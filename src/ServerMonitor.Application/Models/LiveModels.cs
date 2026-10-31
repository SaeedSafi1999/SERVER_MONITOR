using ServerMonitor.Domain;
using ServerMonitor.Domain.Entities;

namespace ServerMonitor.Application.Models;

public sealed record DiskPartitionInfo(string Mount, string Device, string FileSystem, long TotalBytes, long UsedBytes, long FreeBytes)
{
    public double UsedPercent => TotalBytes > 0 ? 100.0 * UsedBytes / TotalBytes : 0;
}

public sealed record DiskDeviceIo(string Device, double ReadBytesPerSec, double WriteBytesPerSec, double? ReadLatencyMs, double? WriteLatencyMs);

public sealed record NetworkInterfaceInfo(
    string Name, bool IsUp, long RxBytes, long TxBytes, double RxBytesPerSec, double TxBytesPerSec,
    long RxErrors, long TxErrors, long RxDrops, long TxDrops, long? SpeedMbps);

public sealed record ProcessInfo(int Pid, string Name, string? User, double CpuPercent, double MemPercent, long RssBytes, int Threads);

public sealed record ServiceStatusInfo(string Name, string? DisplayName, ServiceState State, string? SubState, string? StartupType, DateTimeOffset? Since, string? LastError);

public sealed record ListeningPortInfo(string Protocol, string Address, int Port);

public sealed record LoginRecord(DateTimeOffset Timestamp, string? User, string? SourceIp, bool Success, string Message);

/// <summary>Everything one metric poll returns. Only <see cref="Snapshot"/> is persisted; the rest lives in the live cache.</summary>
public sealed class HostSample
{
    public required MetricSnapshot Snapshot { get; init; }
    public string? OsVersion { get; init; }
    public string? Hostname { get; init; }
    public double[] PerCoreUsage { get; init; } = [];
    public IReadOnlyList<DiskPartitionInfo> Partitions { get; init; } = [];
    public IReadOnlyList<DiskDeviceIo> DiskDevices { get; init; } = [];
    public IReadOnlyList<NetworkInterfaceInfo> Interfaces { get; init; } = [];
    public IReadOnlyDictionary<string, int> TcpStates { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<ListeningPortInfo> ListeningPorts { get; init; } = [];
}

public sealed class SecurityScanResult
{
    public IReadOnlyList<LoginRecord> FailedLogins { get; init; } = [];
    public IReadOnlyList<LoginRecord> SuccessfulLogins { get; init; } = [];
    public bool? FirewallActive { get; init; }
    public string? FirewallDetails { get; init; }
    public int? PendingUpdates { get; init; }
    public int? PendingSecurityUpdates { get; init; }
    public bool? RebootRequired { get; init; }
    public string? DiskEncryption { get; init; }
    public IReadOnlyList<ListeningPortInfo> ListeningPorts { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public DateTimeOffset ScannedAt { get; init; } = DateTimeOffset.UtcNow;
}

public enum LogLevelKind { Unknown, Debug, Info, Notice, Warning, Error, Critical }

public sealed record LogEntry(DateTimeOffset Timestamp, LogLevelKind Level, string Source, string Message, string? Raw = null);

/// <summary>Log sources are a closed allow-list per OS: the frontend sends only the key.</summary>
public sealed record LogSourceDescriptor(string Key, string DisplayName);

public sealed record ConnectionTestResult(
    bool Success,
    ConnectionState State,
    string Message,
    string? PresentedFingerprint = null,
    string? OsVersion = null,
    string? Hostname = null,
    double? ElapsedMs = null);

public sealed record ServiceControlResult(bool Success, string Message);

/// <summary>Latest in-memory view of a server, pushed to the UI on every update.</summary>
public sealed class ServerLiveState
{
    public int ServerId { get; init; }
    public required string Name { get; init; }
    public required string Host { get; init; }
    public OperatingSystemFamily OperatingSystem { get; init; }
    public HealthStatus Status { get; set; }
    public ConnectionState ConnectionState { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public bool MonitoringEnabled { get; set; }
    public string? OsVersion { get; set; }
    public HostSample? Sample { get; set; }
    public LatencySummary? Icmp { get; set; }
    public LatencySummary? Tcp { get; set; }
    public HttpProbeSummary? Http { get; set; }
    public int OpenAlerts { get; set; }
    public SecurityScanResult? Security { get; set; }
}

/// <summary>Result of one probe round (several ICMP echoes or one TCP/HTTP attempt).</summary>
public sealed record LatencySummary(DateTimeOffset Timestamp, double? CurrentMs, double? MinMs, double? MaxMs, double? AvgMs, double? JitterMs, double PacketLossPercent, int Sent, int Received, string? Error);

public sealed record HttpProbeSummary(
    DateTimeOffset Timestamp, bool Success, int? StatusCode, double? DnsMs, double? TcpConnectMs,
    double? TlsHandshakeMs, double? TtfbMs, double? TotalMs, string? Error,
    string? CertSubject, string? CertIssuer, DateTimeOffset? CertNotAfter, bool? CertValid);
