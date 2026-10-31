using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServerMonitor.Application.Abstractions;

namespace ServerMonitor.Infrastructure.Monitoring;

/// <summary>Lets the UI ask for an immediate poll (after editing a server or pressing "Refresh").</summary>
public interface ICollectionTrigger
{
    void RequestImmediate(int serverId);
    void ServersChanged();
}

/// <summary>
/// Drives all data collection independently of the UI. Each server is polled at its own interval;
/// a global semaphore caps concurrent remote sessions, a per-server flag prevents overlapping
/// polls, and failing servers back off exponentially so a dead host is not hammered.
/// </summary>
public sealed class MonitoringScheduler(IServiceScopeFactory scopes, ISettingsProvider settingsProvider, ILogger<MonitoringScheduler> logger)
    : BackgroundService, ICollectionTrigger
{
    private sealed class ServerSchedule
    {
        public int ServerId;
        public TimeSpan Interval;
        public DateTimeOffset NextMetrics;
        public DateTimeOffset NextSecurity;
        public DateTimeOffset LastSecurityScan;
        public int Failures;
        public int Running; // 0/1 via Interlocked
    }

    private readonly ConcurrentDictionary<int, ServerSchedule> _schedules = new();
    private volatile bool _reload = true;
    private DateTimeOffset _lastReload;
    private SemaphoreSlim _concurrency = new(16, 16);
    private int _concurrencyLimit = 16;

    public void RequestImmediate(int serverId)
    {
        if (_schedules.TryGetValue(serverId, out var s)) s.NextMetrics = DateTimeOffset.MinValue;
        else _reload = true;
    }

    public void ServersChanged() => _reload = true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the web host a moment to finish migrations/startup.
        await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        logger.LogInformation("Monitoring scheduler started");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                if (_reload || DateTimeOffset.UtcNow - _lastReload > TimeSpan.FromSeconds(30))
                    await ReloadAsync(stoppingToken);
                Dispatch(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Scheduler tick failed");
            }
        }
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        _reload = false;
        _lastReload = DateTimeOffset.UtcNow;
        var settings = await settingsProvider.GetAsync(ct);
        if (settings.MaxConcurrentCollections != _concurrencyLimit && settings.MaxConcurrentCollections > 0)
        {
            _concurrencyLimit = settings.MaxConcurrentCollections;
            _concurrency = new SemaphoreSlim(_concurrencyLimit, _concurrencyLimit);
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IMonitorDbContext>();
        var servers = await db.Servers.AsNoTracking().Where(s => s.IsMonitoringEnabled)
            .Select(s => new { s.Id, s.MonitoringIntervalSeconds }).ToListAsync(ct);
        var ids = servers.Select(s => s.Id).ToHashSet();
        foreach (var removed in _schedules.Keys.Where(k => !ids.Contains(k))) _schedules.TryRemove(removed, out _);

        var now = DateTimeOffset.UtcNow;
        foreach (var s in servers)
        {
            var interval = TimeSpan.FromSeconds(Math.Clamp(s.MonitoringIntervalSeconds, 10, 3600));
            _schedules.AddOrUpdate(s.Id,
                _ => new ServerSchedule
                {
                    ServerId = s.Id, Interval = interval,
                    // Spread first polls so 100 servers do not connect in the same second.
                    NextMetrics = now.AddMilliseconds(Random.Shared.Next(0, 3000)),
                    NextSecurity = now.AddSeconds(Random.Shared.Next(20, 60)),
                    LastSecurityScan = now.AddHours(-1)
                },
                (_, existing) => { existing.Interval = interval; return existing; });
        }
    }

    private void Dispatch(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var schedule in _schedules.Values)
        {
            if (schedule.NextMetrics > now && schedule.NextSecurity > now) continue;
            if (Interlocked.CompareExchange(ref schedule.Running, 1, 0) != 0) continue;
            _ = RunAsync(schedule, ct);
        }
    }

    private async Task RunAsync(ServerSchedule schedule, CancellationToken ct)
    {
        try
        {
            await _concurrency.WaitAsync(ct);
            try
            {
                var now = DateTimeOffset.UtcNow;
                if (schedule.NextMetrics <= now)
                {
                    var cycleStart = DateTimeOffset.UtcNow;
                    using var scope = scopes.CreateScope();
                    var collector = scope.ServiceProvider.GetRequiredService<ServerCollector>();
                    bool ok;
                    try
                    {
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(TimeSpan.FromMinutes(2));
                        ok = await collector.CollectAsync(schedule.ServerId, cts.Token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        logger.LogError(ex, "Collection cycle failed for server {ServerId}", schedule.ServerId);
                        ok = false;
                    }
                    schedule.Failures = ok ? 0 : schedule.Failures + 1;
                    // Fixed-rate: the interval is measured from cycle start, so sample spacing does not drift
                    // by the cycle's own duration. A cycle longer than the interval simply runs back-to-back.
                    var next = cycleStart + RetryPolicy.BackoffInterval(schedule.Interval, schedule.Failures);
                    schedule.NextMetrics = next > DateTimeOffset.UtcNow ? next : DateTimeOffset.UtcNow;
                }

                if (schedule.NextSecurity <= DateTimeOffset.UtcNow && schedule.Failures == 0)
                {
                    var settings = await settingsProvider.GetAsync(ct);
                    var since = schedule.LastSecurityScan;
                    var started = DateTimeOffset.UtcNow;
                    try
                    {
                        using var scope = scopes.CreateScope();
                        var collector = scope.ServiceProvider.GetRequiredService<ServerCollector>();
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        cts.CancelAfter(TimeSpan.FromMinutes(4));
                        await collector.ScanSecurityAsync(schedule.ServerId, since, cts.Token);
                        schedule.LastSecurityScan = started;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        logger.LogWarning(ex, "Security scan failed for server {ServerId}", schedule.ServerId);
                    }
                    schedule.NextSecurity = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(Math.Max(1, settings.SecurityScanIntervalMinutes));
                }
            }
            finally
            {
                _concurrency.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.Exchange(ref schedule.Running, 0);
        }
    }
}
