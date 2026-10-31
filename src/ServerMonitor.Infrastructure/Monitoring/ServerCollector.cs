using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Application.Analytics;
using ServerMonitor.Application.Models;
using ServerMonitor.Domain;
using ServerMonitor.Domain.Entities;
using ServerMonitor.Infrastructure.Alerting;
using ServerMonitor.Infrastructure.Probes;
using ServerMonitor.Infrastructure.Remote;

namespace ServerMonitor.Infrastructure.Monitoring;

/// <summary>
/// Runs one collection cycle for one server: network probes, resource metrics, services, ports,
/// persistence, availability tracking, alert evaluation and real-time push. Scoped per cycle.
/// </summary>
public sealed class ServerCollector(
    IMonitorDbContext db,
    IServerAdapterFactory adapters,
    INetworkProber prober,
    ILiveStateCache cache,
    IRealtimePublisher realtime,
    ISettingsProvider settingsProvider,
    AlertEngine alerts,
    ILogger<ServerCollector> logger)
{
    /// <returns>True when the host is reachable (management channel or any probe). Drives scheduler backoff.</returns>
    public async Task<bool> CollectAsync(int serverId, CancellationToken ct)
    {
        var server = await db.Servers.Include(s => s.Services).Include(s => s.Ports).FirstOrDefaultAsync(s => s.Id == serverId, ct);
        if (server is null || !server.IsMonitoringEnabled) return true;

        var settings = await settingsProvider.GetAsync(ct);
        var adapter = adapters.For(server);
        var state = cache.GetOrAdd(server);
        var now = DateTimeOffset.UtcNow;
        server.LastCollectionAttemptAt = now;

        // 1. Network probes run concurrently with the agentless metric collection.
        var probeTask = RunProbesAsync(server, settings, ct);
        HostSample? sample = null;
        Exception? collectError = null;
        try
        {
            sample = await RetryPolicy.ExecuteAsync(c => adapter.CollectMetricsAsync(server, c), 2, TimeSpan.FromSeconds(1), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            collectError = ex;
        }
        var probes = await probeTask;

        // 2. Connection state
        if (sample is not null)
        {
            server.ConnectionState = ConnectionState.Connected;
            server.LastError = null;
            server.LastSeenAt = sample.Snapshot.Timestamp;
            if (!string.IsNullOrWhiteSpace(sample.OsVersion)) server.OsVersion = sample.OsVersion;
            db.MetricSnapshots.Add(sample.Snapshot);
        }
        else
        {
            server.ConnectionState = collectError is RemoteConnectionException rce ? rce.State : ConnectionState.Error;
            server.LastError = Truncate(collectError?.Message, 2000);
            logger.LogWarning("Metric collection failed for {Server}: {Error}", server.Name, collectError?.Message);
        }

        // 3. Services and ports (only meaningful when the host answers).
        var observations = new List<Observation>();
        if (sample is not null) await CollectServicesAsync(server, adapter, observations, ct);
        await CheckPortsAsync(server, settings, observations, ct);

        // 4. Availability: down only when the management channel AND every network probe fail.
        // (e.g. an expired SSH password makes the host "Warning", not "Offline", as long as it still answers ping/TCP).
        var isUp = sample is not null || probes.AnyProbeSucceeded;
        await TrackAvailabilityAsync(server, isUp, server.LastError, now, ct);

        // 5. Alert observations
        observations.Add(new Observation(AlertMetric.ServerOffline, isUp ? 0 : 1));
        if (sample is not null)
        {
            var snap = sample.Snapshot;
            observations.Add(new Observation(AlertMetric.CpuPercent, snap.CpuPercent));
            observations.Add(new Observation(AlertMetric.MemoryPercent, Math.Round(snap.MemPercent, 2)));
            if (snap.SwapTotal > 0) observations.Add(new Observation(AlertMetric.SwapPercent, Math.Round(snap.SwapPercent, 2)));
            if (snap.Load1 is not null && snap.CpuCores > 0 && server.OperatingSystem == OperatingSystemFamily.Linux)
                observations.Add(new Observation(AlertMetric.LoadPerCore, Math.Round(snap.Load1.Value / snap.CpuCores, 2)));
            foreach (var p in sample.Partitions)
                observations.Add(new Observation(AlertMetric.DiskPercent, Math.Round(p.UsedPercent, 2), "disk:" + p.Mount, p.Mount));
        }
        if (probes.Icmp is { } icmp)
        {
            observations.Add(new Observation(AlertMetric.PacketLossPercent, Math.Round(icmp.PacketLossPercent, 2)));
            if (icmp.AvgMs is { } avg) observations.Add(new Observation(AlertMetric.PingLatencyMs, Math.Round(avg, 2)));
        }
        if (probes.Http is { } http)
        {
            if (http.TotalMs is { } total) observations.Add(new Observation(AlertMetric.HttpResponseTimeMs, Math.Round(total, 1)));
            if (http.CertNotAfter is { } notAfter)
                observations.Add(new Observation(AlertMetric.CertificateExpiryDays, Math.Round((notAfter - now).TotalDays, 1), "cert", http.CertSubject));
            server.CertificateSubject = http.CertSubject ?? server.CertificateSubject;
            server.CertificateIssuer = http.CertIssuer ?? server.CertificateIssuer;
            server.CertificateNotAfter = http.CertNotAfter ?? server.CertificateNotAfter;
            server.CertificateValid = http.CertValid ?? server.CertificateValid;
        }

        await db.SaveChangesAsync(ct);
        await SaveSecurityEventsAsync(ct);
        var openCount = await alerts.EvaluateAsync(db, server, observations, ct);

        // 6. Health
        var openAlerts = await db.Alerts.AsNoTracking().Where(a => a.ServerId == server.Id && a.Status != AlertStatus.Resolved).ToListAsync(ct);
        var health = !isUp ? HealthStatus.Offline : AlertEngine.HealthFrom(openAlerts);
        if (isUp && health == HealthStatus.Healthy && server.ConnectionState != ConnectionState.Connected) health = HealthStatus.Warning;
        if (health != server.Status) server.StatusChangedAt = now;
        server.Status = health;
        await db.SaveChangesAsync(ct);

        // 7. Live cache + push
        state.Status = server.Status;
        state.ConnectionState = server.ConnectionState;
        state.LastError = server.LastError;
        state.LastSeenAt = server.LastSeenAt;
        state.OsVersion = server.OsVersion;
        state.MonitoringEnabled = server.IsMonitoringEnabled;
        state.OpenAlerts = openCount;
        if (sample is not null) state.Sample = sample;
        if (probes.Icmp is not null) state.Icmp = probes.Icmp;
        if (probes.Tcp is not null) state.Tcp = probes.Tcp;
        if (probes.Http is not null) state.Http = probes.Http;
        await realtime.ServerUpdatedAsync(state, ct);

        // Back off only for hosts that are completely unreachable; a reachable host with an
        // auth/trust problem keeps its normal cadence so latency and SLA data stay accurate.
        return isUp;
    }

    private sealed record ProbeOutcome(LatencySummary? Icmp, LatencySummary? Tcp, HttpProbeSummary? Http)
    {
        public bool AnyProbeRan => Icmp is not null || Tcp is not null || Http is not null;
        public bool AnyProbeSucceeded => Icmp is { Received: > 0 } || Tcp is { Received: > 0 } || Http is { Success: true } || Http is { StatusCode: not null };
    }

    private async Task<ProbeOutcome> RunProbesAsync(Server server, SystemSettings settings, CancellationToken ct)
    {
        var timeout = settings.ProbeTimeoutMs;
        var icmpTask = server.IcmpEnabled ? prober.PingAsync(server, Math.Clamp(settings.IcmpPacketsPerRound, 1, 20), timeout, ct) : Task.FromResult<IReadOnlyList<ProbeResult>>([]);
        var tcpTask = prober.TcpConnectAsync(server, server.TcpProbePort ?? server.ManagementPort, timeout, ct);
        var httpTask = string.IsNullOrWhiteSpace(server.HealthCheckUrl)
            ? Task.FromResult<(ProbeResult, HttpProbeSummary)?>(null)
            : WrapHttp(prober.HttpCheckAsync(server, server.HealthCheckUrl, timeout, ct));

        IReadOnlyList<ProbeResult> icmp = [];
        ProbeResult? tcp = null;
        (ProbeResult Result, HttpProbeSummary Summary)? http = null;
        try { icmp = await icmpTask; } catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "ICMP probe error"); }
        try { tcp = await tcpTask; } catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "TCP probe error"); }
        try { http = await httpTask; } catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogDebug(ex, "HTTP probe error"); }

        db.ProbeResults.AddRange(icmp);
        if (tcp is not null) db.ProbeResults.Add(tcp);
        if (http is not null) db.ProbeResults.Add(http.Value.Result);

        LatencySummary? icmpSummary = null;
        if (icmp.Count > 0)
        {
            var ok = icmp.Where(p => p.Success && p.LatencyMs.HasValue).Select(p => p.LatencyMs!.Value).ToList();
            var stats = LatencyStatistics.Compute(ok, icmp.Count - ok.Count);
            icmpSummary = new LatencySummary(icmp[0].Timestamp, stats.Current, stats.Min, stats.Max, stats.Average, stats.Jitter,
                stats.PacketLossPercent, icmp.Count, ok.Count, icmp.FirstOrDefault(p => !p.Success)?.Error);
        }
        LatencySummary? tcpSummary = tcp is null ? null :
            new LatencySummary(tcp.Timestamp, tcp.LatencyMs, tcp.LatencyMs, tcp.LatencyMs, tcp.LatencyMs, null, tcp.Success ? 0 : 100, 1, tcp.Success ? 1 : 0, tcp.Error);

        return new ProbeOutcome(icmpSummary, tcpSummary, http?.Summary);
    }

    private static async Task<(ProbeResult, HttpProbeSummary)?> WrapHttp(Task<(ProbeResult, HttpProbeSummary)> task) => await task;

    private async Task CollectServicesAsync(Server server, IServerAdapter adapter, List<Observation> observations, CancellationToken ct)
    {
        var monitored = server.Services.Where(s => s.IsEnabled).ToList();
        if (monitored.Count == 0) return;
        try
        {
            var statuses = await adapter.GetServicesAsync(server, monitored.Select(s => s.Name).ToList(), ct);
            var now = DateTimeOffset.UtcNow;
            foreach (var svc in monitored)
            {
                var st = statuses.FirstOrDefault(x => x.Name.Equals(svc.Name, StringComparison.OrdinalIgnoreCase));
                var newState = st?.State ?? ServiceState.NotFound;
                if (svc.State != newState)
                {
                    if (svc.State != ServiceState.Unknown)
                    {
                        // Any state change is recorded as a security-relevant event (unexpected stop/start).
                        AddSecurityEvent(server.Id, now, SecurityEventType.ServiceStateChanged,
                            newState == ServiceState.Running ? AlertSeverity.Info : AlertSeverity.Warning,
                            $"Service {svc.Name} changed {svc.State} → {newState}", null, null);
                    }
                    svc.State = newState;
                    svc.LastStateChangeAt = st?.Since ?? now;
                }
                else if (st?.Since is not null) svc.LastStateChangeAt = st.Since;
                svc.SubState = st?.SubState;
                svc.StartupType = st?.StartupType;
                svc.DisplayName ??= st?.DisplayName;
                svc.LastError = Truncate(st?.LastError, 1000);
                svc.LastCheckedAt = now;
                if (svc.AlertWhenStopped)
                    observations.Add(new Observation(AlertMetric.ServiceStopped, newState is ServiceState.Running or ServiceState.Starting ? 0 : 1, "svc:" + svc.Name, svc.Name));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Service check failed for {Server}", server.Name);
        }
    }

    private async Task CheckPortsAsync(Server server, SystemSettings settings, List<Observation> observations, CancellationToken ct)
    {
        var ports = server.Ports.Where(p => p.IsEnabled).ToList();
        if (ports.Count == 0) return;
        var results = await Task.WhenAll(ports.Select(p => NetworkProber.CheckPortAsync(server.Host, p.Port, settings.ProbeTimeoutMs, ct)));
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < ports.Count; i++)
        {
            var port = ports[i];
            var (open, ms, error) = results[i];
            if (port.IsOpen != open) port.LastChangedAt = now;
            port.IsOpen = open;
            port.ResponseTimeMs = ms is null ? null : Math.Round(ms.Value, 2);
            port.LastError = error;
            port.LastCheckedAt = now;
            if (port.ExpectOpen)
                observations.Add(new Observation(AlertMetric.PortClosed, open ? 0 : 1, "port:" + port.Port, $"{port.Port}/{port.Protocol}{(port.ServiceName is null ? "" : " (" + port.ServiceName + ")")}"));
        }
    }

    private async Task TrackAvailabilityAsync(Server server, bool isUp, string? reason, DateTimeOffset now, CancellationToken ct)
    {
        var current = await db.StatusPeriods.Where(p => p.ServerId == server.Id && p.EndedAt == null)
            .OrderByDescending(p => p.StartedAt).FirstOrDefaultAsync(ct);
        if (current is not null && current.IsUp == isUp) return;
        if (current is not null) current.EndedAt = now;
        db.StatusPeriods.Add(new StatusPeriod { ServerId = server.Id, IsUp = isUp, StartedAt = now, Reason = isUp ? null : Truncate(reason, 500) });
    }

    // ------------------------------------------------------------------ security

    /// <summary>Security scan (lower frequency). Persists new events and evaluates brute-force alerts.</summary>
    public async Task ScanSecurityAsync(int serverId, DateTimeOffset since, CancellationToken ct)
    {
        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == serverId, ct);
        if (server is null || !server.IsMonitoringEnabled) return;
        var adapter = adapters.For(server);
        var state = cache.GetOrAdd(server);
        var previous = state.Security;
        var result = await adapter.ScanSecurityAsync(server, since, ct);
        var now = DateTimeOffset.UtcNow;

        foreach (var f in result.FailedLogins)
        {
            var suspicious = result.FailedLogins.Count(x => x.SourceIp == f.SourceIp && f.SourceIp != null) >= 10 || f.User is "root" or "Administrator" or "admin";
            AddSecurityEvent(server.Id, f.Timestamp, suspicious ? SecurityEventType.SuspiciousLogin : SecurityEventType.FailedLogin,
                suspicious ? AlertSeverity.Warning : AlertSeverity.Info, f.Message, f.User, f.SourceIp);
        }
        foreach (var s in result.SuccessfulLogins)
            AddSecurityEvent(server.Id, s.Timestamp, SecurityEventType.SuccessfulLogin, AlertSeverity.Info, s.Message, s.User, s.SourceIp);

        if (result.FirewallActive == false && previous?.FirewallActive != false)
            AddSecurityEvent(server.Id, now, SecurityEventType.FirewallDisabled, AlertSeverity.Warning, "Firewall is not active: " + result.FirewallDetails, null, null);

        if (previous is not null)
        {
            var before = previous.ListeningPorts.Select(p => p.Port).ToHashSet();
            foreach (var p in result.ListeningPorts.Where(p => !before.Contains(p.Port)).DistinctBy(p => p.Port))
                AddSecurityEvent(server.Id, now, SecurityEventType.NewListeningPort, AlertSeverity.Warning, $"New listening port {p.Port}/{p.Protocol} on {p.Address}", null, null);
        }
        if (result.PendingSecurityUpdates is > 0 && previous?.PendingSecurityUpdates != result.PendingSecurityUpdates)
            AddSecurityEvent(server.Id, now, SecurityEventType.UpdatesPending, AlertSeverity.Info, $"{result.PendingSecurityUpdates} security update(s) pending ({result.PendingUpdates} total)", null, null);

        await SaveSecurityEventsAsync(ct);

        state.Security = result;
        var observations = new List<Observation> { new(AlertMetric.FailedLoginsPerInterval, result.FailedLogins.Count) };
        state.OpenAlerts = await alerts.EvaluateAsync(db, server, observations, ct);
        await realtime.ServerUpdatedAsync(state, ct);
    }

    private readonly List<SecurityEvent> _pendingEvents = [];

    private void AddSecurityEvent(int serverId, DateTimeOffset ts, SecurityEventType type, AlertSeverity severity, string message, string? user, string? ip)
    {
        var fp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{serverId}|{type}|{ts:O}|{message}")))[..64];
        if (_pendingEvents.Any(e => e.Fingerprint == fp)) return;
        _pendingEvents.Add(new SecurityEvent
        {
            ServerId = serverId, Timestamp = ts, Type = type, Severity = severity, Message = Truncate(message, 2000)!,
            UserName = Truncate(user, 128), SourceIp = Truncate(ip, 64), Fingerprint = fp
        });
    }

    private async Task SaveSecurityEventsAsync(CancellationToken ct)
    {
        if (_pendingEvents.Count == 0) return;
        var fps = _pendingEvents.Select(e => e.Fingerprint).ToList();
        var existing = await db.SecurityEvents.Where(e => fps.Contains(e.Fingerprint)).Select(e => e.Fingerprint).ToListAsync(ct);
        db.SecurityEvents.AddRange(_pendingEvents.Where(e => !existing.Contains(e.Fingerprint)));
        _pendingEvents.Clear();
        await db.SaveChangesAsync(ct);
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
