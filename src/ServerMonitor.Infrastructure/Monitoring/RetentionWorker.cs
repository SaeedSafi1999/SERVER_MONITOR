using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServerMonitor.Domain;
using ServerMonitor.Infrastructure.Persistence;

namespace ServerMonitor.Infrastructure.Monitoring;

/// <summary>Deletes historical data older than the configured retention, in batches, once per hour.</summary>
public sealed class RetentionWorker(IServiceScopeFactory scopes, ISettingsProvider settingsProvider, ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await PurgeAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Retention purge failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task PurgeAsync(CancellationToken ct)
    {
        var s = await settingsProvider.GetAsync(ct);
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MonitorDbContext>();
        var now = DateTimeOffset.UtcNow;

        var metricCut = now.AddDays(-Math.Max(1, s.MetricRetentionDays));
        var probeCut = now.AddDays(-Math.Max(1, s.ProbeRetentionDays));
        var alertCut = now.AddDays(-Math.Max(7, s.AlertRetentionDays));
        var secCut = now.AddDays(-Math.Max(1, s.SecurityEventRetentionDays));
        var auditCut = now.AddDays(-Math.Max(30, s.AuditRetentionDays));

        var metrics = await db.MetricSnapshots.Where(m => m.Timestamp < metricCut).ExecuteDeleteAsync(ct);
        var probes = await db.ProbeResults.Where(p => p.Timestamp < probeCut).ExecuteDeleteAsync(ct);
        var alerts = await db.Alerts.Where(a => a.Status == AlertStatus.Resolved && a.ResolvedAt < alertCut).ExecuteDeleteAsync(ct);
        var sec = await db.SecurityEvents.Where(e => e.Timestamp < secCut).ExecuteDeleteAsync(ct);
        var audit = await db.AuditLogs.Where(a => a.Timestamp < auditCut).ExecuteDeleteAsync(ct);
        // Availability periods are tiny and drive long-range SLA reports: keep 2 years.
        var periods = await db.StatusPeriods.Where(p => p.EndedAt != null && p.EndedAt < now.AddYears(-2)).ExecuteDeleteAsync(ct);

        if (metrics + probes + alerts + sec + audit + periods > 0)
            logger.LogInformation("Retention purge removed {Metrics} metrics, {Probes} probes, {Alerts} alerts, {Sec} security events, {Audit} audit rows, {Periods} periods",
                metrics, probes, alerts, sec, audit, periods);
    }
}
