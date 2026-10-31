using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Domain;
using ServerMonitor.Domain.Entities;

namespace ServerMonitor.Infrastructure.Alerting;

/// <summary>A measured value for one metric (and optional subject: a mount, service or port).</summary>
public sealed record Observation(AlertMetric Metric, double Value, string? Subject = null, string? SubjectLabel = null);

/// <summary>
/// Threshold evaluation with flap protection (N consecutive breaches), deduplication
/// (one unresolved alert per rule+server+subject fingerprint), notification cooldown and
/// automatic resolution when the condition clears.
/// </summary>
public sealed class AlertEngine(INotificationQueue notifications, IRealtimePublisher realtime, ILogger<AlertEngine> logger)
{
    private readonly ConcurrentDictionary<string, int> _breachCounters = new();
    private List<AlertRule>? _rules;
    private DateTimeOffset _rulesLoadedAt;

    public void InvalidateRules() => _rules = null;

    public async Task<int> EvaluateAsync(IMonitorDbContext db, Server server, IReadOnlyCollection<Observation> observations, CancellationToken ct)
    {
        var rules = await GetRulesAsync(db, ct);
        var now = DateTimeOffset.UtcNow;
        var observedMetrics = observations.Select(o => o.Metric).ToHashSet();

        // Server-specific rules override global rules for the same metric.
        var applicable = rules
            .Where(r => r.IsEnabled && (r.ServerId == server.Id || r.ServerId == null))
            .GroupBy(r => r.Metric)
            .SelectMany(g => g.Any(r => r.ServerId == server.Id) ? g.Where(r => r.ServerId == server.Id) : g)
            .Where(r => observedMetrics.Contains(r.Metric))
            .ToList();

        var openAlerts = await db.Alerts.Where(a => a.ServerId == server.Id && a.Status != AlertStatus.Resolved).ToListAsync(ct);
        var changed = new List<Alert>();

        foreach (var rule in applicable)
        {
            foreach (var obs in observations.Where(o => o.Metric == rule.Metric))
            {
                var fingerprint = $"r{rule.Id}:s{server.Id}:{obs.Subject ?? "-"}";
                var breached = Compare(obs.Value, rule.Operator, rule.Threshold);
                var existing = openAlerts.FirstOrDefault(a => a.Fingerprint == fingerprint);

                if (breached)
                {
                    var count = _breachCounters.AddOrUpdate(fingerprint, 1, (_, c) => c + 1);
                    if (count < rule.ConsecutiveBreaches && existing is null) continue;

                    if (existing is null)
                    {
                        existing = new Alert
                        {
                            ServerId = server.Id,
                            RuleId = rule.Id,
                            Metric = rule.Metric,
                            Severity = rule.Severity,
                            Fingerprint = fingerprint,
                            Title = Title(server, rule, obs),
                            Message = Describe(rule, obs),
                            Value = obs.Value,
                            Threshold = rule.Threshold,
                            FirstSeenAt = now,
                            LastSeenAt = now,
                            LastNotifiedAt = now
                        };
                        db.Alerts.Add(existing);
                        openAlerts.Add(existing);
                        changed.Add(existing);
                        notifications.Enqueue(new NotificationMessage(existing.Title, existing.Message ?? "", rule.Severity, server.Name, false, now));
                        logger.LogInformation("Alert raised: {Title}", existing.Title);
                    }
                    else
                    {
                        existing.LastSeenAt = now;
                        existing.Value = obs.Value;
                        existing.Message = Describe(rule, obs);
                        existing.OccurrenceCount++;
                        // Re-notify only after the cooldown and only while nobody has acknowledged it.
                        if (existing.Status == AlertStatus.Open &&
                            (existing.LastNotifiedAt is null || now - existing.LastNotifiedAt >= TimeSpan.FromMinutes(rule.CooldownMinutes)))
                        {
                            existing.LastNotifiedAt = now;
                            notifications.Enqueue(new NotificationMessage("[Reminder] " + existing.Title, existing.Message ?? "", rule.Severity, server.Name, false, now));
                        }
                    }
                }
                else
                {
                    _breachCounters.TryRemove(fingerprint, out _);
                    if (existing is not null)
                    {
                        existing.Status = AlertStatus.Resolved;
                        existing.ResolvedAt = now;
                        existing.ResolvedBy = "system";
                        existing.Value = obs.Value;
                        openAlerts.Remove(existing);
                        changed.Add(existing);
                        notifications.Enqueue(new NotificationMessage("[Resolved] " + existing.Title, Describe(rule, obs), rule.Severity, server.Name, true, now));
                    }
                }
            }
        }

        await db.SaveChangesAsync(ct);
        foreach (var alert in changed) await realtime.AlertChangedAsync(alert, ct);
        return openAlerts.Count;
    }

    /// <summary>Open alert severity summary used to derive server health.</summary>
    public static HealthStatus HealthFrom(IEnumerable<Alert> openAlerts)
    {
        var worst = openAlerts.Select(a => (AlertSeverity?)a.Severity).Max();
        return worst switch
        {
            AlertSeverity.Critical => HealthStatus.Critical,
            AlertSeverity.Warning => HealthStatus.Warning,
            _ => HealthStatus.Healthy
        };
    }

    private async Task<List<AlertRule>> GetRulesAsync(IMonitorDbContext db, CancellationToken ct)
    {
        var rules = _rules;
        if (rules is not null && DateTimeOffset.UtcNow - _rulesLoadedAt < TimeSpan.FromSeconds(30)) return rules;
        rules = await db.AlertRules.AsNoTracking().ToListAsync(ct);
        _rules = rules;
        _rulesLoadedAt = DateTimeOffset.UtcNow;
        return rules;
    }

    public static bool Compare(double value, ComparisonOperator op, double threshold) => op switch
    {
        ComparisonOperator.GreaterThan => value > threshold,
        ComparisonOperator.GreaterThanOrEqual => value >= threshold,
        ComparisonOperator.LessThan => value < threshold,
        ComparisonOperator.LessThanOrEqual => value <= threshold,
        _ => false
    };

    private static string Title(Server server, AlertRule rule, Observation obs) =>
        $"{server.Name}: {rule.Name}{(obs.SubjectLabel is null ? "" : " — " + obs.SubjectLabel)}";

    private static string Describe(AlertRule rule, Observation obs) => rule.Metric switch
    {
        AlertMetric.ServerOffline => obs.Value > 0 ? "Server is not reachable (management connection and network probes failed)." : "Server is reachable again.",
        AlertMetric.ServiceStopped => obs.Value > 0 ? $"Service {obs.SubjectLabel} is not running." : $"Service {obs.SubjectLabel} is running.",
        AlertMetric.PortClosed => obs.Value > 0 ? $"Port {obs.SubjectLabel} is closed or filtered." : $"Port {obs.SubjectLabel} is open.",
        AlertMetric.CertificateExpiryDays => $"TLS certificate expires in {obs.Value:0.#} days (threshold {Op(rule.Operator)} {rule.Threshold}).",
        _ => $"{rule.Metric} = {obs.Value:0.##}{Unit(rule.Metric)} (threshold {Op(rule.Operator)} {rule.Threshold}{Unit(rule.Metric)})."
    };

    private static string Op(ComparisonOperator op) => op switch
    {
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.GreaterThanOrEqual => "≥",
        ComparisonOperator.LessThan => "<",
        _ => "≤"
    };

    public static string Unit(AlertMetric m) => m switch
    {
        AlertMetric.CpuPercent or AlertMetric.MemoryPercent or AlertMetric.DiskPercent or AlertMetric.PacketLossPercent or AlertMetric.SwapPercent => "%",
        AlertMetric.PingLatencyMs or AlertMetric.HttpResponseTimeMs => " ms",
        AlertMetric.CertificateExpiryDays => " days",
        _ => ""
    };
}
