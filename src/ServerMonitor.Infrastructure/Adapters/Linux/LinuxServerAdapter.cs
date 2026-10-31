using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Application.Models;
using ServerMonitor.Domain;
using ServerMonitor.Domain.Entities;
using ServerMonitor.Infrastructure.Remote;
using ServerMonitor.Infrastructure.Remote.Ssh;
using ServerMonitor.Infrastructure.Security;

namespace ServerMonitor.Infrastructure.Adapters.Linux;

/// <summary>Linux over SSH. Reads /proc, systemd and journald; never runs user-supplied commands.</summary>
public sealed class LinuxServerAdapter(SshConnectionPool ssh, ILogger<LinuxServerAdapter> logger) : IServerAdapter
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    public OperatingSystemFamily Family => OperatingSystemFamily.Linux;

    public IReadOnlyList<LogSourceDescriptor> LogSources { get; } =
    [
        new("journal", "Journal (journalctl)"),
        new("syslog", "Syslog"),
        new("auth", "Auth log"),
        new("kernel", "Kernel"),
        new("nginx-access", "Nginx access"),
        new("nginx-error", "Nginx error"),
        new("apache-access", "Apache access"),
        new("apache-error", "Apache error")
    ];

    public IReadOnlyList<(string Name, string Display)> WellKnownServices { get; } =
    [
        ("nginx", "Nginx"), ("apache2", "Apache (Debian)"), ("httpd", "Apache (RHEL)"), ("mysql", "MySQL"),
        ("mariadb", "MariaDB"), ("postgresql", "PostgreSQL"), ("redis-server", "Redis (Debian)"), ("redis", "Redis (RHEL)"),
        ("docker", "Docker"), ("ssh", "SSH (Debian)"), ("sshd", "SSH (RHEL)"), ("php8.3-fpm", "PHP-FPM 8.3"), ("php-fpm", "PHP-FPM (RHEL)")
    ];

    public async Task<ConnectionTestResult> TestConnectionAsync(Server server, bool acceptPresentedKey, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var (fp, output) = await ssh.TestAsync(server, LinuxScripts.Identify, acceptPresentedKey, ct);
            var s = LinuxParsers.Sections(output);
            var os = s.GetValueOrDefault("os")?.FirstOrDefault()?.Trim();
            var host = s.GetValueOrDefault("hostname")?.FirstOrDefault()?.Trim();
            var pinned = server.TrustedHostKeyFingerprint is not null;
            return new ConnectionTestResult(true, ConnectionState.Connected,
                pinned ? "Connected. Host key matches the pinned fingerprint." : "Connected. Host key is NOT pinned yet — verify the fingerprint and trust it.",
                fp, os, host, sw.Elapsed.TotalMilliseconds);
        }
        catch (RemoteConnectionException ex)
        {
            return new ConnectionTestResult(false, ex.State, ex.Message, ex.PresentedFingerprint, ElapsedMs: sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Connection test failed for server {ServerId}", server.Id);
            return new ConnectionTestResult(false, ConnectionState.Error, ex.Message, ElapsedMs: sw.Elapsed.TotalMilliseconds);
        }
    }

    public async Task<HostSample> CollectMetricsAsync(Server server, CancellationToken ct)
    {
        var result = await ssh.ExecuteAsync(server, LinuxScripts.Metrics, DefaultTimeout, ct);
        return LinuxParsers.ParseMetrics(server.Id, result.StdOut, DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(Server server, int top, CancellationToken ct)
    {
        var result = await ssh.ExecuteAsync(server, LinuxScripts.Processes, DefaultTimeout, ct);
        return LinuxParsers.ParseProcesses(result.StdOut, Math.Clamp(top, 1, 1000));
    }

    public async Task<IReadOnlyList<ServiceStatusInfo>> GetServicesAsync(Server server, IReadOnlyCollection<string> serviceNames, CancellationToken ct)
    {
        var valid = serviceNames.Where(InputValidation.IsValidLinuxUnit).Distinct().ToList();
        if (valid.Count == 0) return [];
        var quoted = valid.Select(n => InputValidation.ShellQuote(n.Contains('.') ? n : n + ".service"));
        var result = await ssh.ExecuteAsync(server, LinuxScripts.Services(quoted), DefaultTimeout, ct);
        var services = LinuxParsers.ParseSystemctlShow(result.StdOut).ToList();

        // Attach recent error lines for failed units only (keeps polling cheap).
        for (var i = 0; i < services.Count; i++)
        {
            if (services[i].State != ServiceState.Failed) continue;
            var errors = await ssh.ExecuteAsync(server, LinuxScripts.ServiceErrors(InputValidation.ShellQuote(services[i].Name + ".service")), DefaultTimeout, ct);
            var text = errors.StdOut.Trim();
            if (text.Length > 0) services[i] = services[i] with { LastError = text.Length > 900 ? text[^900..] : text };
        }
        return services;
    }

    public async Task<ServiceControlResult> RestartServiceAsync(Server server, string serviceName, CancellationToken ct)
    {
        var unit = InputValidation.RequireLinuxUnit(serviceName);
        var quoted = InputValidation.ShellQuote(unit.Contains('.') ? unit : unit + ".service");
        var result = await ssh.ExecuteAsync(server, LinuxScripts.RestartService(quoted), TimeSpan.FromSeconds(60), ct);
        return result.ExitCode == 0
            ? new ServiceControlResult(true, $"{unit} restarted.")
            : new ServiceControlResult(false, $"Restart failed (exit {result.ExitCode}): {(result.StdOut + result.StdErr).Trim()}");
    }

    public async Task<SecurityScanResult> ScanSecurityAsync(Server server, DateTimeOffset since, CancellationToken ct)
    {
        var result = await ssh.ExecuteAsync(server, LinuxScripts.Security(since.ToUnixTimeSeconds()), TimeSpan.FromSeconds(60), ct);
        return LinuxParsers.ParseSecurity(result.StdOut, since, DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<LogEntry>> ReadLogsAsync(Server server, string sourceKey, DateTimeOffset? since, int maxEntries, CancellationToken ct)
    {
        var script = LinuxScripts.Logs(sourceKey, Math.Clamp(maxEntries, 1, 2000), since?.ToUnixTimeSeconds())
                     ?? throw new ArgumentException($"Unknown log source '{sourceKey}'.");
        var result = await ssh.ExecuteAsync(server, script, TimeSpan.FromSeconds(30), ct);
        return LinuxParsers.ParseLogs(sourceKey, result.StdOut, since, DateTimeOffset.UtcNow);
    }
}
