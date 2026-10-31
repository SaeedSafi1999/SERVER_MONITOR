using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;
using SshAuth = Renci.SshNet.AuthenticationMethod;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Domain;
using ServerMonitor.Domain.Entities;

namespace ServerMonitor.Infrastructure.Remote.Ssh;

/// <summary>
/// Keeps one authenticated SSH connection per server and reuses it across polls
/// (avoids a full key exchange every 30s). A connection is rebuilt when the server's
/// connection settings change or the socket drops.
/// Host keys are verified against the pinned fingerprint on every handshake.
/// </summary>
public sealed class SshConnectionPool(ICredentialProtector protector, ILogger<SshConnectionPool> logger) : IDisposable
{
    private sealed class Entry(SshClient client, string configHash)
    {
        public SshClient Client { get; } = client;
        public string ConfigHash { get; } = configHash;
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<int, Entry> _entries = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _connectLocks = new();
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Runs a fixed script on the host. Commands are serialised per host to keep the load predictable.</summary>
    public async Task<CommandResult> ExecuteAsync(Server server, string script, TimeSpan timeout, CancellationToken ct)
    {
        var entry = await GetConnectedAsync(server, ct);
        await entry.Gate.WaitAsync(ct);
        try
        {
            using var cmd = entry.Client.CreateCommand(script);
            cmd.CommandTimeout = timeout;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            try
            {
                await cmd.ExecuteAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new RemoteCommandException($"Command timed out after {timeout.TotalSeconds:0}s");
            }
            catch (SshConnectionException ex)
            {
                Evict(server.Id);
                throw new RemoteConnectionException(ConnectionState.Unreachable, "SSH connection lost: " + ex.Message, ex);
            }
            return new CommandResult(cmd.Result ?? "", cmd.Error ?? "", cmd.ExitStatus);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    /// <summary>
    /// Opens a fresh connection for the "Test connection" button.
    /// When <paramref name="acceptPresentedKey"/> is true an unpinned key is accepted (and returned so the admin can pin it).
    /// </summary>
    public async Task<(string Fingerprint, string Output)> TestAsync(Server server, string probeScript, bool acceptPresentedKey, CancellationToken ct)
    {
        string? presented = null;
        var rejected = false;
        using var client = CreateClient(server, fp =>
        {
            presented = fp;
            var ok = server.TrustedHostKeyFingerprint is not null
                ? FixedTimeEquals(fp, server.TrustedHostKeyFingerprint)
                : acceptPresentedKey;
            rejected = !ok;
            return ok;
        });
        await ConnectAsync(client, server, () => presented, () => rejected, ct);
        using var cmd = client.CreateCommand(probeScript);
        cmd.CommandTimeout = TimeSpan.FromSeconds(15);
        await cmd.ExecuteAsync(ct);
        client.Disconnect();
        return (presented ?? "", cmd.Result ?? "");
    }

    public void Evict(int serverId)
    {
        if (_entries.TryRemove(serverId, out var entry))
        {
            try { entry.Client.Disconnect(); } catch { /* ignore */ }
            entry.Client.Dispose();
        }
    }

    private async Task<Entry> GetConnectedAsync(Server server, CancellationToken ct)
    {
        if (server.TrustedHostKeyFingerprint is null)
            throw new RemoteConnectionException(ConnectionState.HostKeyNotTrusted,
                "Host key not trusted yet. Run 'Test connection' and pin the host key first.");

        var hash = ConfigHash(server);
        if (_entries.TryGetValue(server.Id, out var existing) && existing.ConfigHash == hash && existing.Client.IsConnected)
            return existing;

        var connectLock = _connectLocks.GetOrAdd(server.Id, _ => new SemaphoreSlim(1, 1));
        await connectLock.WaitAsync(ct);
        try
        {
            if (_entries.TryGetValue(server.Id, out existing) && existing.ConfigHash == hash && existing.Client.IsConnected)
                return existing;
            Evict(server.Id);

            string? presented = null;
            var rejected = false;
            var client = CreateClient(server, fp =>
            {
                presented = fp;
                rejected = !FixedTimeEquals(fp, server.TrustedHostKeyFingerprint!);
                return !rejected;
            });
            try
            {
                await ConnectAsync(client, server, () => presented, () => rejected, ct);
            }
            catch
            {
                client.Dispose();
                throw;
            }
            var entry = new Entry(client, hash);
            _entries[server.Id] = entry;
            return entry;
        }
        finally
        {
            connectLock.Release();
        }
    }

    private SshClient CreateClient(Server server, Func<string, bool> verifyHostKey)
    {
        var methods = new List<SshAuth>();
        if (server.AuthMethod == Domain.AuthenticationMethod.PrivateKey && server.EncryptedPrivateKey is not null)
        {
            var keyText = protector.Unprotect(server.EncryptedPrivateKey);
            var passphrase = server.EncryptedPrivateKeyPassphrase is null ? null : protector.Unprotect(server.EncryptedPrivateKeyPassphrase);
            using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(keyText));
            var keyFile = passphrase is null ? new PrivateKeyFile(keyStream) : new PrivateKeyFile(keyStream, passphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(server.Username, keyFile));
        }
        else if (server.EncryptedPassword is not null)
        {
            methods.Add(new PasswordAuthenticationMethod(server.Username, protector.Unprotect(server.EncryptedPassword)));
        }
        else
        {
            throw new RemoteConnectionException(ConnectionState.AuthenticationFailed, "No credentials configured.");
        }

        var info = new ConnectionInfo(server.Host, server.ManagementPort, server.Username, methods.ToArray())
        {
            Timeout = ConnectTimeout,
            RetryAttempts = 1
        };
        var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        client.HostKeyReceived += (_, e) =>
        {
            var fp = "SHA256:" + e.FingerPrintSHA256;
            e.CanTrust = verifyHostKey(fp);
            if (!e.CanTrust) logger.LogWarning("Host key for server {ServerId} rejected (presented {Fingerprint})", server.Id, fp);
        };
        return client;
    }

    private static async Task ConnectAsync(SshClient client, Server server, Func<string?> presented, Func<bool> hostKeyRejected, CancellationToken ct)
    {
        try
        {
            await client.ConnectAsync(ct);
        }
        catch (Exception ex) when (hostKeyRejected() && ex is not OperationCanceledException)
        {
            var fp = presented();
            var state = server.TrustedHostKeyFingerprint is null ? ConnectionState.HostKeyNotTrusted : ConnectionState.HostKeyMismatch;
            var msg = state == ConnectionState.HostKeyMismatch
                ? $"HOST KEY MISMATCH — presented {fp}, pinned {server.TrustedHostKeyFingerprint}. Possible MITM; collection blocked."
                : $"Host key {fp} is not trusted yet.";
            throw new RemoteConnectionException(state, msg, ex, fp);
        }
        catch (SshAuthenticationException ex)
        {
            throw new RemoteConnectionException(ConnectionState.AuthenticationFailed, "Authentication failed: " + ex.Message, ex, presented());
        }
        catch (Exception ex) when (ex is SocketException or SshOperationTimeoutException or TimeoutException or SshConnectionException)
        {
            throw new RemoteConnectionException(ConnectionState.Unreachable, "Cannot reach host: " + ex.Message, ex, presented());
        }
        catch (Exception ex) when (ex is SshException or InvalidOperationException)
        {
            throw new RemoteConnectionException(ConnectionState.Error, ex.Message, ex, presented());
        }
    }

    private static string ConfigHash(Server s)
    {
        var raw = string.Join('|', s.Host, s.ManagementPort, s.Username, s.AuthMethod, s.EncryptedPassword, s.EncryptedPrivateKey,
            s.EncryptedPrivateKeyPassphrase, s.TrustedHostKeyFingerprint);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public void Dispose()
    {
        foreach (var id in _entries.Keys) Evict(id);
    }
}
