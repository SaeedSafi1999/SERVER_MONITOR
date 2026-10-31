using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Application.Models;
using ServerMonitor.Domain;
using ServerMonitor.Domain.Entities;
using ServerMonitor.Infrastructure.Remote;
using ServerMonitor.Infrastructure.Remote.WinRm;
using ServerMonitor.Infrastructure.Security;

namespace ServerMonitor.Infrastructure.Adapters.Windows;

/// <summary>Windows Server over WinRM (WS-Management) running fixed PowerShell/CIM queries.</summary>
public sealed class WindowsServerAdapter(WinRmClient winrm, ILogger<WindowsServerAdapter> logger) : IServerAdapter
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(45);

    public OperatingSystemFamily Family => OperatingSystemFamily.Windows;

    public IReadOnlyList<LogSourceDescriptor> LogSources { get; } =
    [
        new("system", "System"),
        new("application", "Application"),
        new("security", "Security"),
        new("setup", "Setup")
    ];

    public IReadOnlyList<(string Name, string Display)> WellKnownServices { get; } =
    [
        ("W3SVC", "IIS (World Wide Web Publishing)"), ("WAS", "IIS Windows Process Activation"), ("MSSQLSERVER", "SQL Server (default instance)"),
        ("SQLSERVERAGENT", "SQL Server Agent"), ("TermService", "Remote Desktop (RDP)"), ("DNS", "DNS Server"), ("DHCPServer", "DHCP Server"),
        ("WinRM", "Windows Remote Management"), ("EventLog", "Windows Event Log"), ("wuauserv", "Windows Update"), ("MpsSvc", "Windows Defender Firewall")
    ];

    public async Task<ConnectionTestResult> TestConnectionAsync(Server server, bool acceptPresentedKey, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var outcome = await winrm.TestAsync(server, WindowsScripts.Identify, acceptPresentedKey, ct);
            using var doc = JsonDocument.Parse(outcome.Output);
            var os = doc.RootElement.TryGetProperty("os", out var o) ? o.GetString() : null;
            var host = doc.RootElement.TryGetProperty("host", out var h) ? h.GetString() : null;
            var msg = server.Protocol == ConnectionProtocol.WinRmHttp
                ? "Connected over plain HTTP. Switch to WinRM HTTPS for production."
                : server.TrustedCertificateThumbprint is null ? "Connected. Certificate is NOT pinned yet — verify and trust it." : "Connected. Certificate matches the pinned thumbprint.";
            return new ConnectionTestResult(true, ConnectionState.Connected, msg, outcome.Thumbprint, os, host, sw.Elapsed.TotalMilliseconds);
        }
        catch (RemoteConnectionException ex)
        {
            var fp = ex.PresentedFingerprint;
            if (fp is null && server.Protocol == ConnectionProtocol.WinRmHttps)
                fp = await WinRmClient.GetCertificateThumbprintAsync(server.Host, server.ManagementPort, ct);
            return new ConnectionTestResult(false, ex.State, ex.Message, fp, ElapsedMs: sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "WinRM connection test failed for server {ServerId}", server.Id);
            return new ConnectionTestResult(false, ConnectionState.Error, ex.Message, ElapsedMs: sw.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<string> RunAsync(Server server, string script, TimeSpan timeout, CancellationToken ct)
    {
        EnsureTrusted(server);
        var result = await winrm.RunPowerShellAsync(server, script, timeout, ct);
        if (result.ExitCode is not (null or 0) && string.IsNullOrWhiteSpace(result.StdOut))
            throw new RemoteCommandException("PowerShell failed: " + result.StdErr, result.ExitCode);
        return result.StdOut.Trim();
    }

    private static void EnsureTrusted(Server server)
    {
        if (server.Protocol == ConnectionProtocol.WinRmHttps && server.TrustedCertificateThumbprint is null)
            throw new RemoteConnectionException(ConnectionState.HostKeyNotTrusted,
                "WinRM certificate not trusted yet. Run 'Test connection' and pin the certificate first.");
    }

    public async Task<HostSample> CollectMetricsAsync(Server server, CancellationToken ct)
    {
        var json = await RunAsync(server, WindowsScripts.Metrics, DefaultTimeout, ct);
        return WindowsParsers.ParseMetrics(server.Id, json, DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(Server server, int top, CancellationToken ct)
    {
        var json = await RunAsync(server, WindowsScripts.Processes, DefaultTimeout, ct);
        return WindowsParsers.ParseProcesses(json, Math.Clamp(top, 1, 1000));
    }

    public async Task<IReadOnlyList<ServiceStatusInfo>> GetServicesAsync(Server server, IReadOnlyCollection<string> serviceNames, CancellationToken ct)
    {
        var valid = serviceNames.Where(InputValidation.IsValidWindowsService).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (valid.Count == 0) return [];
        // Names are restricted to [A-Za-z0-9_.-$] so they cannot break out of the WQL string literal.
        var filter = string.Join(" OR ", valid.Select(n => $"Name='{n}'"));
        var json = await RunAsync(server, WindowsScripts.Services(filter), DefaultTimeout, ct);
        var found = WindowsParsers.ParseServices(json).ToList();
        foreach (var missing in valid.Where(v => !found.Any(f => f.Name.Equals(v, StringComparison.OrdinalIgnoreCase))))
            found.Add(new ServiceStatusInfo(missing, null, ServiceState.NotFound, null, null, null, "Service not installed"));
        return found;
    }

    public async Task<ServiceControlResult> RestartServiceAsync(Server server, string serviceName, CancellationToken ct)
    {
        var name = InputValidation.RequireWindowsService(serviceName);
        EnsureTrusted(server);
        var result = await winrm.RunPowerShellAsync(server, WindowsScripts.RestartService(InputValidation.PsQuote(name)), TimeSpan.FromSeconds(90), ct);
        var output = result.StdOut.Trim();
        return result.ExitCode is null or 0 && !output.StartsWith("ERROR", StringComparison.Ordinal)
            ? new ServiceControlResult(true, $"{name} restarted (status: {output}).")
            : new ServiceControlResult(false, $"Restart failed: {output} {result.StdErr}".Trim());
    }

    public async Task<SecurityScanResult> ScanSecurityAsync(Server server, DateTimeOffset since, CancellationToken ct)
    {
        var json = await RunAsync(server, WindowsScripts.Security(since), TimeSpan.FromMinutes(3), ct);
        return WindowsParsers.ParseSecurity(json, DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<LogEntry>> ReadLogsAsync(Server server, string sourceKey, DateTimeOffset? since, int maxEntries, CancellationToken ct)
    {
        var script = WindowsScripts.Logs(sourceKey, Math.Clamp(maxEntries, 1, 2000), since)
                     ?? throw new ArgumentException($"Unknown log source '{sourceKey}'.");
        var json = await RunAsync(server, script, TimeSpan.FromSeconds(60), ct);
        return WindowsParsers.ParseLogs(sourceKey, json, since);
    }
}
