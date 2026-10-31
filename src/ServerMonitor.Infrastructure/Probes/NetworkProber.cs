using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Application.Models;
using ServerMonitor.Domain;
using ServerMonitor.Domain.Entities;

namespace ServerMonitor.Infrastructure.Probes;

/// <summary>
/// Latency probes executed from the monitoring host. Each method is measured independently:
/// ICMP echo RTT, raw TCP handshake time, and a hand-rolled HTTP/1.1 exchange that times
/// DNS, TCP connect, TLS handshake, time-to-first-byte and total response separately.
/// </summary>
public sealed class NetworkProber(ILogger<NetworkProber> logger) : INetworkProber
{
    public async Task<IReadOnlyList<ProbeResult>> PingAsync(Server server, int count, int timeoutMs, CancellationToken ct)
    {
        var results = new List<ProbeResult>(count);
        using var ping = new Ping();
        var buffer = new byte[32];
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var ts = DateTimeOffset.UtcNow;
            try
            {
                var reply = await ping.SendPingAsync(server.Host, TimeSpan.FromMilliseconds(timeoutMs), buffer, new PingOptions(128, true), ct);
                var ok = reply.Status == IPStatus.Success;
                results.Add(new ProbeResult
                {
                    ServerId = server.Id, Timestamp = ts, Type = ProbeType.Icmp, Success = ok,
                    // RoundtripTime is integer ms; sub-millisecond replies report 0, which we keep as a real sample.
                    LatencyMs = ok ? reply.RoundtripTime : null,
                    Error = ok ? null : reply.Status.ToString()
                });
            }
            catch (PingException ex)
            {
                results.Add(new ProbeResult { ServerId = server.Id, Timestamp = ts, Type = ProbeType.Icmp, Success = false, Error = Short(ex.InnerException?.Message ?? ex.Message) });
            }
            if (i < count - 1) await Task.Delay(200, ct);
        }
        return results;
    }

    public async Task<ProbeResult> TcpConnectAsync(Server server, int port, int timeoutMs, CancellationToken ct)
    {
        var ts = DateTimeOffset.UtcNow;
        var result = new ProbeResult { ServerId = server.Id, Timestamp = ts, Type = ProbeType.Tcp };
        try
        {
            var (addresses, dnsMs) = await ResolveAsync(server.Host, timeoutMs, ct);
            result.DnsMs = dnsMs;
            var (socket, connectMs) = await ConnectAsync(addresses, port, timeoutMs, ct);
            socket.Dispose();
            result.TcpConnectMs = connectMs;
            result.LatencyMs = connectMs;
            result.Success = true;
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            result.Success = false;
            result.Error = Short(ex is SocketException se ? se.SocketErrorCode.ToString() : ex.Message);
        }
        return result;
    }

    /// <summary>Simple TCP open/closed check used by the port monitor.</summary>
    public static async Task<(bool Open, double? Ms, string? Error)> CheckPortAsync(string host, int port, int timeoutMs, CancellationToken ct)
    {
        try
        {
            var (addresses, _) = await ResolveAsync(host, timeoutMs, ct);
            var (socket, ms) = await ConnectAsync(addresses, port, timeoutMs, ct);
            socket.Dispose();
            return (true, ms, null);
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return (false, null, ex is SocketException se ? se.SocketErrorCode.ToString() : "Timeout");
        }
    }

    public async Task<(ProbeResult Result, HttpProbeSummary Summary)> HttpCheckAsync(Server server, string url, int timeoutMs, CancellationToken ct)
    {
        var ts = DateTimeOffset.UtcNow;
        var result = new ProbeResult { ServerId = server.Id, Timestamp = ts, Type = ProbeType.Http };
        string? certSubject = null, certIssuer = null;
        DateTimeOffset? certNotAfter = null;
        bool? certValid = null;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeoutMs * 3);
        var token = timeoutCts.Token;
        var total = Stopwatch.StartNew();
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new ArgumentException("Health check URL must be an absolute http(s) URL.");

            var (addresses, dnsMs) = await ResolveAsync(uri.IdnHost, timeoutMs, token);
            result.DnsMs = dnsMs;

            var (socket, connectMs) = await ConnectAsync(addresses, uri.Port, timeoutMs, token);
            result.TcpConnectMs = connectMs;
            Stream stream = new NetworkStream(socket, ownsSocket: true);

            if (uri.Scheme == "https")
            {
                var ssl = new SslStream(stream, false, (_, cert, chain, errors) =>
                {
                    if (cert is X509Certificate2 c2)
                    {
                        certSubject = c2.Subject;
                        certIssuer = c2.Issuer;
                        certNotAfter = new DateTimeOffset(c2.NotAfter.ToUniversalTime());
                    }
                    certValid = errors == SslPolicyErrors.None;
                    // Measure even when the certificate is invalid; validity is reported separately.
                    return true;
                });
                var tls = Stopwatch.StartNew();
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = uri.IdnHost,
                    ApplicationProtocols = [SslApplicationProtocol.Http11]
                }, token);
                result.TlsHandshakeMs = tls.Elapsed.TotalMilliseconds;
                stream = ssl;
            }

            await using (stream)
            {
                var request = $"GET {uri.PathAndQuery} HTTP/1.1\r\nHost: {uri.Authority}\r\nUser-Agent: ServerMonitor-HealthCheck/1.0\r\nAccept: */*\r\nConnection: close\r\n\r\n";
                var requestStart = Stopwatch.StartNew();
                await stream.WriteAsync(Encoding.ASCII.GetBytes(request), token);
                await stream.FlushAsync(token);

                var buffer = new byte[16 * 1024];
                var first = await stream.ReadAsync(buffer, token);
                result.TtfbMs = requestStart.Elapsed.TotalMilliseconds;
                if (first == 0) throw new IOException("Connection closed before any response bytes.");

                var head = Encoding.ASCII.GetString(buffer, 0, Math.Min(first, 64));
                var parts = head.Split(' ', 3);
                if (parts.Length >= 2 && int.TryParse(parts[1], out var status)) result.StatusCode = status;

                // Drain the body (capped at 5 MB) so "total" covers the full response.
                long read = first;
                int n;
                while (read < 5_000_000 && (n = await stream.ReadAsync(buffer, token)) > 0) read += n;
            }

            result.LatencyMs = total.Elapsed.TotalMilliseconds;
            var expected = server.HealthCheckExpectedStatus;
            result.Success = expected is null ? result.StatusCode is >= 200 and < 400 : result.StatusCode == expected;
            if (!result.Success) result.Error = $"HTTP {result.StatusCode}";
        }
        catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or ArgumentException or System.Security.Authentication.AuthenticationException
                                       || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            result.Success = false;
            result.LatencyMs = null;
            result.Error = Short(ex is SocketException se ? se.SocketErrorCode.ToString() : ex is OperationCanceledException ? "Timeout" : ex.Message);
            logger.LogDebug(ex, "HTTP probe failed for server {ServerId}", server.Id);
        }

        var summary = new HttpProbeSummary(ts, result.Success, result.StatusCode, result.DnsMs, result.TcpConnectMs, result.TlsHandshakeMs,
            result.TtfbMs, result.LatencyMs, result.Error, certSubject, certIssuer, certNotAfter, certValid);
        return (result, summary);
    }

    private static async Task<(IPAddress[] Addresses, double Ms)> ResolveAsync(string host, int timeoutMs, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var ip)) return ([ip], 0);
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var addresses = await Dns.GetHostAddressesAsync(host, cts.Token);
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
        return (addresses, sw.Elapsed.TotalMilliseconds);
    }

    private static async Task<(Socket Socket, double Ms)> ConnectAsync(IPAddress[] addresses, int port, int timeoutMs, CancellationToken ct)
    {
        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        var sw = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cts.Token);
            return (socket, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new TimeoutException($"TCP connect to {address}:{port} timed out after {timeoutMs} ms");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static string Short(string s) => s.Length > 480 ? s[..480] : s;
}
