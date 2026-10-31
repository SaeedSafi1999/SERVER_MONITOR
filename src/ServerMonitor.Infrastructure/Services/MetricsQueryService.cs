using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Application.Analytics;
using ServerMonitor.Domain;
using ServerMonitor.Domain.Entities;

namespace ServerMonitor.Infrastructure.Services;

public enum MetricSeries
{
    Cpu, Memory, Disk, Swap, Load, NetRx, NetTx, DiskRead, DiskWrite, TcpConnections, Processes, Temperature
}

public sealed record SeriesPoint(DateTimeOffset Time, double? Value);
public sealed record SeriesSummary(double? Min, double? Max, double? Avg, double? Last);
public sealed record Series(string Name, IReadOnlyList<SeriesPoint> Points, SeriesSummary Summary);

public sealed record HttpPhaseStats(LatencyStats Dns, LatencyStats Tcp, LatencyStats Tls, LatencyStats Ttfb, LatencyStats Total, int Attempts, int Failures);

public sealed record AvailabilitySegment(DateTimeOffset From, DateTimeOffset To, bool? IsUp);

/// <summary>Read-side queries for charts and statistics. Long ranges are downsampled into time buckets.</summary>
public sealed class MetricsQueryService(IMonitorDbContext db, Monitoring.ISettingsProvider settings)
{
    public static TimeSpan BucketFor(DateTimeOffset from, DateTimeOffset to, int maxPoints = 300)
    {
        var seconds = Math.Max(1, (to - from).TotalSeconds / maxPoints);
        // Round to friendly bucket sizes.
        int[] steps = [10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400];
        return TimeSpan.FromSeconds(steps.FirstOrDefault(s => s >= seconds, 86400));
    }

    private static Func<MetricSnapshot, double?> Selector(MetricSeries s) => s switch
    {
        MetricSeries.Cpu => m => m.CpuPercent,
        MetricSeries.Memory => m => m.MemTotal > 0 ? 100.0 * m.MemUsed / m.MemTotal : null,
        MetricSeries.Disk => m => m.DiskTotal > 0 ? 100.0 * m.DiskUsed / m.DiskTotal : null,
        MetricSeries.Swap => m => m.SwapTotal > 0 ? 100.0 * m.SwapUsed / m.SwapTotal : 0,
        MetricSeries.Load => m => m.Load1,
        MetricSeries.NetRx => m => m.NetRxBytesPerSec,
        MetricSeries.NetTx => m => m.NetTxBytesPerSec,
        MetricSeries.DiskRead => m => m.DiskReadBytesPerSec,
        MetricSeries.DiskWrite => m => m.DiskWriteBytesPerSec,
        MetricSeries.TcpConnections => m => m.TcpConnections,
        MetricSeries.Processes => m => m.ProcessCount,
        MetricSeries.Temperature => m => m.CpuTemperatureC,
        _ => _ => null
    };

    public async Task<List<MetricSnapshot>> RawSnapshotsAsync(int serverId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        await db.MetricSnapshots.AsNoTracking()
            .Where(m => m.ServerId == serverId && m.Timestamp >= from && m.Timestamp <= to)
            .OrderBy(m => m.Timestamp).ToListAsync(ct);

    public async Task<Dictionary<MetricSeries, Series>> MetricSeriesAsync(int serverId, DateTimeOffset from, DateTimeOffset to, IEnumerable<MetricSeries> which, int maxPoints = 300, CancellationToken ct = default)
    {
        var raw = await RawSnapshotsAsync(serverId, from, to, ct);
        var bucket = BucketFor(from, to, maxPoints);
        return which.ToDictionary(s => s, s => Build(s.ToString(), raw.Select(m => (m.Timestamp, Selector(s)(m))), bucket));
    }

    /// <summary>One series per server for the comparison chart.</summary>
    public async Task<List<Series>> CompareAsync(IReadOnlyCollection<int> serverIds, MetricSeries series, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var names = await db.Servers.AsNoTracking().Where(s => serverIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var raw = await db.MetricSnapshots.AsNoTracking()
            .Where(m => serverIds.Contains(m.ServerId) && m.Timestamp >= from && m.Timestamp <= to)
            .OrderBy(m => m.Timestamp).ToListAsync(ct);
        var bucket = BucketFor(from, to);
        var sel = Selector(series);
        return raw.GroupBy(m => m.ServerId)
            .Select(g => Build(names.GetValueOrDefault(g.Key, "#" + g.Key), g.Select(m => (m.Timestamp, sel(m))), bucket))
            .ToList();
    }

    public static Series Build(string name, IEnumerable<(DateTimeOffset Time, double? Value)> raw, TimeSpan bucket)
    {
        var list = raw.Where(r => r.Value.HasValue).ToList();
        var points = list
            .GroupBy(r => r.Time.UtcTicks / bucket.Ticks)
            .OrderBy(g => g.Key)
            .Select(g => new SeriesPoint(new DateTimeOffset(g.Key * bucket.Ticks, TimeSpan.Zero), Math.Round(g.Average(x => x.Value!.Value), 3)))
            .ToList();
        var values = list.Select(r => r.Value!.Value).ToList();
        var summary = values.Count == 0
            ? new SeriesSummary(null, null, null, null)
            : new SeriesSummary(values.Min(), values.Max(), Math.Round(values.Average(), 3), values[^1]);
        return new Series(name, points, summary);
    }

    // ---------------- Latency ----------------

    public async Task<(Series Series, Series Loss, LatencyStats Stats)> LatencyAsync(int serverId, ProbeType type, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var raw = await db.ProbeResults.AsNoTracking()
            .Where(p => p.ServerId == serverId && p.Type == type && p.Timestamp >= from && p.Timestamp <= to)
            .OrderBy(p => p.Timestamp)
            .Select(p => new { p.Timestamp, p.Success, p.LatencyMs })
            .ToListAsync(ct);
        var ok = raw.Where(r => r.Success && r.LatencyMs.HasValue).Select(r => r.LatencyMs!.Value).ToList();
        var stats = LatencyStatistics.Compute(ok, raw.Count - ok.Count);
        var bucket = BucketFor(from, to);
        var series = Build(type.ToString(), raw.Where(r => r.Success).Select(r => (r.Timestamp, r.LatencyMs)), bucket);
        var loss = Build("Loss %", raw.GroupBy(r => r.Timestamp.UtcTicks / bucket.Ticks)
            .Select(g => (new DateTimeOffset(g.Key * bucket.Ticks, TimeSpan.Zero), (double?)(100.0 * g.Count(x => !x.Success) / g.Count()))), bucket);
        return (series, loss, stats);
    }

    public async Task<HttpPhaseStats> HttpPhasesAsync(int serverId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var raw = await db.ProbeResults.AsNoTracking()
            .Where(p => p.ServerId == serverId && p.Type == ProbeType.Http && p.Timestamp >= from && p.Timestamp <= to)
            .OrderBy(p => p.Timestamp)
            .Select(p => new { p.Success, p.DnsMs, p.TcpConnectMs, p.TlsHandshakeMs, p.TtfbMs, p.LatencyMs })
            .ToListAsync(ct);
        return new HttpPhaseStats(
            LatencyStatistics.Compute(raw.Where(r => r.DnsMs.HasValue).Select(r => r.DnsMs!.Value).ToList()),
            LatencyStatistics.Compute(raw.Where(r => r.TcpConnectMs.HasValue).Select(r => r.TcpConnectMs!.Value).ToList()),
            LatencyStatistics.Compute(raw.Where(r => r.TlsHandshakeMs.HasValue).Select(r => r.TlsHandshakeMs!.Value).ToList()),
            LatencyStatistics.Compute(raw.Where(r => r.TtfbMs.HasValue).Select(r => r.TtfbMs!.Value).ToList()),
            LatencyStatistics.Compute(raw.Where(r => r.Success && r.LatencyMs.HasValue).Select(r => r.LatencyMs!.Value).ToList(), raw.Count(r => !r.Success)),
            raw.Count, raw.Count(r => !r.Success));
    }

    // ---------------- Availability ----------------

    public async Task<(AvailabilityReport Report, List<AvailabilitySegment> Segments)> AvailabilityAsync(int serverId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var periods = await db.StatusPeriods.AsNoTracking()
            .Where(p => p.ServerId == serverId && p.StartedAt < to && (p.EndedAt == null || p.EndedAt > from))
            .OrderBy(p => p.StartedAt).ToListAsync(ct);
        var target = (await settings.GetAsync(ct)).SlaTargetPercent;
        var report = AvailabilityCalculator.Compute(periods, from, to, target);
        var now = DateTimeOffset.UtcNow;
        var segments = periods.Select(p => new AvailabilitySegment(p.StartedAt < from ? from : p.StartedAt, (p.EndedAt ?? now) > to ? to : p.EndedAt ?? now, p.IsUp)).ToList();
        return (report, segments);
    }

    public async Task<List<(Server Server, AvailabilityReport Report)>> SlaReportAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var servers = await db.Servers.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
        var periods = await db.StatusPeriods.AsNoTracking()
            .Where(p => p.StartedAt < to && (p.EndedAt == null || p.EndedAt > from)).ToListAsync(ct);
        var target = (await settings.GetAsync(ct)).SlaTargetPercent;
        return servers.Select(s => (s, AvailabilityCalculator.Compute(periods.Where(p => p.ServerId == s.Id), from, to, target))).ToList();
    }

    // ---------------- Export ----------------

    public async Task<string> ExportMetricsCsvAsync(int serverId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var raw = await RawSnapshotsAsync(serverId, from, to, ct);
        var sb = new StringBuilder();
        sb.AppendLine("timestamp_utc,cpu_percent,cpu_cores,load1,load5,load15,mem_total,mem_used,mem_available,swap_total,swap_used,disk_total,disk_used,disk_read_bps,disk_write_bps,net_rx_bps,net_tx_bps,net_rx_total,net_tx_total,net_errors,tcp_connections,tcp_established,processes,threads,uptime_s");
        var c = CultureInfo.InvariantCulture;
        foreach (var m in raw)
        {
            sb.AppendLine(string.Join(',',
                m.Timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", c), m.CpuPercent.ToString(c), m.CpuCores, m.Load1?.ToString(c), m.Load5?.ToString(c), m.Load15?.ToString(c),
                m.MemTotal, m.MemUsed, m.MemAvailable, m.SwapTotal, m.SwapUsed, m.DiskTotal, m.DiskUsed,
                m.DiskReadBytesPerSec.ToString("0.##", c), m.DiskWriteBytesPerSec.ToString("0.##", c), m.NetRxBytesPerSec.ToString("0.##", c), m.NetTxBytesPerSec.ToString("0.##", c),
                m.NetRxTotalBytes, m.NetTxTotalBytes, m.NetErrors, m.TcpConnections, m.TcpEstablished, m.ProcessCount, m.ThreadCount, m.UptimeSeconds));
        }
        return sb.ToString();
    }

    public async Task<string> ExportProbesCsvAsync(int serverId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var raw = await db.ProbeResults.AsNoTracking()
            .Where(p => p.ServerId == serverId && p.Timestamp >= from && p.Timestamp <= to)
            .OrderBy(p => p.Timestamp).ToListAsync(ct);
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("timestamp_utc,type,success,latency_ms,dns_ms,tcp_connect_ms,tls_ms,ttfb_ms,status_code,error\n");
        foreach (var p in raw)
            sb.AppendLine(string.Join(',', p.Timestamp.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", c), p.Type, p.Success,
                p.LatencyMs?.ToString("0.###", c), p.DnsMs?.ToString("0.###", c), p.TcpConnectMs?.ToString("0.###", c), p.TlsHandshakeMs?.ToString("0.###", c),
                p.TtfbMs?.ToString("0.###", c), p.StatusCode, Csv(p.Error)));
        return sb.ToString();
    }

    public static string Csv(string? s) =>
        s is null ? "" : s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
