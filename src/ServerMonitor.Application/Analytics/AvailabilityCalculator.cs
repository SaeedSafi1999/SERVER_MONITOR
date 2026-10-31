using ServerMonitor.Domain.Entities;

namespace ServerMonitor.Application.Analytics;

public sealed record AvailabilityReport(
    DateTimeOffset From, DateTimeOffset To,
    TimeSpan Monitored, TimeSpan Uptime, TimeSpan Downtime, TimeSpan Unknown,
    int Incidents, TimeSpan? Mttr, TimeSpan? Mtbf, double UptimePercent, double SlaTarget)
{
    public bool SlaMet => UptimePercent >= SlaTarget;
    /// <summary>Downtime still allowed in the window to stay within SLA (negative = budget exceeded).</summary>
    public TimeSpan ErrorBudgetRemaining =>
        TimeSpan.FromSeconds((To - From).TotalSeconds * (1 - SlaTarget / 100.0) - Downtime.TotalSeconds);
}

public static class AvailabilityCalculator
{
    /// <summary>
    /// Computes availability for [from, to) from up/down periods.
    /// Time not covered by any period (monitoring disabled / before the server was added) is "unknown"
    /// and excluded from the uptime percentage rather than counted as downtime.
    /// MTTR = mean duration of down periods; MTBF = mean up-time between failures (up time / incidents).
    /// </summary>
    public static AvailabilityReport Compute(IEnumerable<StatusPeriod> periods, DateTimeOffset from, DateTimeOffset to, double slaTarget, DateTimeOffset? now = null)
    {
        var clockNow = now ?? DateTimeOffset.UtcNow;
        var end = to > clockNow ? clockNow : to;
        TimeSpan up = TimeSpan.Zero, down = TimeSpan.Zero;
        var downDurations = new List<TimeSpan>();

        foreach (var p in periods.OrderBy(p => p.StartedAt))
        {
            var start = p.StartedAt < from ? from : p.StartedAt;
            var stop = p.EndedAt ?? clockNow;
            if (stop > end) stop = end;
            if (stop <= start) continue;

            var span = stop - start;
            if (p.IsUp) up += span;
            else
            {
                down += span;
                downDurations.Add(span);
            }
        }

        var monitored = up + down;
        var total = end > from ? end - from : TimeSpan.Zero;
        var unknown = total - monitored < TimeSpan.Zero ? TimeSpan.Zero : total - monitored;
        var uptimePct = monitored.TotalSeconds > 0 ? 100.0 * up.TotalSeconds / monitored.TotalSeconds : 100.0;
        var incidents = downDurations.Count;

        TimeSpan? mttr = incidents > 0 ? TimeSpan.FromSeconds(downDurations.Average(d => d.TotalSeconds)) : null;
        TimeSpan? mtbf = incidents > 0 ? TimeSpan.FromSeconds(up.TotalSeconds / incidents) : null;

        return new AvailabilityReport(from, to, monitored, up, down, unknown, incidents, mttr, mtbf, uptimePct, slaTarget);
    }
}
