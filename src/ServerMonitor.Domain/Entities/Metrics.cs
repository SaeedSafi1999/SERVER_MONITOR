namespace ServerMonitor.Domain.Entities;

/// <summary>
/// One resource sample collected from a host. Rates (bytes/s, CPU %) are measured over a
/// one-second window on the host itself, so a single poll yields correct values.
/// </summary>
public class MetricSnapshot
{
    public long Id { get; set; }
    public int ServerId { get; set; }
    public DateTimeOffset Timestamp { get; set; }

    // CPU
    public double CpuPercent { get; set; }
    public int CpuCores { get; set; }
    /// <summary>JSON array of per-core usage percentages.</summary>
    public string? PerCoreJson { get; set; }
    public double? Load1 { get; set; }
    public double? Load5 { get; set; }
    public double? Load15 { get; set; }
    public double? CpuTemperatureC { get; set; }
    public double? CpuFrequencyMhz { get; set; }

    // Memory (bytes)
    public long MemTotal { get; set; }
    public long MemUsed { get; set; }
    public long MemFree { get; set; }
    public long MemAvailable { get; set; }
    public long SwapTotal { get; set; }
    public long SwapUsed { get; set; }

    // Disk (bytes, all local filesystems summed)
    public long DiskTotal { get; set; }
    public long DiskUsed { get; set; }
    public double DiskReadBytesPerSec { get; set; }
    public double DiskWriteBytesPerSec { get; set; }
    public double? DiskReadLatencyMs { get; set; }
    public double? DiskWriteLatencyMs { get; set; }

    // Network
    public double NetRxBytesPerSec { get; set; }
    public double NetTxBytesPerSec { get; set; }
    public long NetRxTotalBytes { get; set; }
    public long NetTxTotalBytes { get; set; }
    public long NetErrors { get; set; }
    public long NetDrops { get; set; }

    // System
    public int TcpConnections { get; set; }
    public int TcpEstablished { get; set; }
    public int ProcessCount { get; set; }
    public int ThreadCount { get; set; }
    public long? OpenFileDescriptors { get; set; }
    public int? ActiveSessions { get; set; }
    public long UptimeSeconds { get; set; }

    public double MemPercent => MemTotal > 0 ? 100.0 * MemUsed / MemTotal : 0;
    public double SwapPercent => SwapTotal > 0 ? 100.0 * SwapUsed / SwapTotal : 0;
    public double DiskPercent => DiskTotal > 0 ? 100.0 * DiskUsed / DiskTotal : 0;
}

/// <summary>
/// A single network probe measurement taken from the monitoring host.
/// ICMP: one row per echo request (lost packets have Success=false) so that percentiles,
/// jitter and loss are computed from real samples. TCP/HTTP: one row per attempt.
/// </summary>
public class ProbeResult
{
    public long Id { get; set; }
    public int ServerId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public ProbeType Type { get; set; }
    public bool Success { get; set; }
    /// <summary>ICMP: round-trip time. TCP: connect time. HTTP: total response time.</summary>
    public double? LatencyMs { get; set; }
    public double? DnsMs { get; set; }
    public double? TcpConnectMs { get; set; }
    public double? TlsHandshakeMs { get; set; }
    public double? TtfbMs { get; set; }
    public int? StatusCode { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// A contiguous period during which a server had a given availability state.
/// Used for uptime %, MTTR, MTBF and SLA reports.
/// </summary>
public class StatusPeriod
{
    public long Id { get; set; }
    public int ServerId { get; set; }
    public bool IsUp { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? Reason { get; set; }
}
