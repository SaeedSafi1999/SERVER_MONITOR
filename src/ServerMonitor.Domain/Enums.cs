namespace ServerMonitor.Domain;

public enum OperatingSystemFamily
{
    Linux = 0,
    Windows = 1
}

/// <summary>How the collector talks to the monitored host.</summary>
public enum ConnectionProtocol
{
    /// <summary>SSH (Linux). Host key is pinned on first trusted connection.</summary>
    Ssh = 0,
    /// <summary>WinRM over HTTPS (port 5986). Recommended for Windows.</summary>
    WinRmHttps = 1,
    /// <summary>WinRM over HTTP (port 5985). Requires AllowUnencrypted + Basic on the target; not recommended.</summary>
    WinRmHttp = 2
}

public enum AuthenticationMethod
{
    Password = 0,
    PrivateKey = 1
}

public enum WinRmAuthScheme
{
    Negotiate = 0,
    Basic = 1
}

/// <summary>Overall health derived from latest metrics + alerts.</summary>
public enum HealthStatus
{
    Unknown = 0,
    Healthy = 1,
    Warning = 2,
    Critical = 3,
    Offline = 4
}

public enum ConnectionState
{
    Unknown = 0,
    Connected = 1,
    AuthenticationFailed = 2,
    Unreachable = 3,
    HostKeyNotTrusted = 4,
    HostKeyMismatch = 5,
    Error = 6,
    Disabled = 7
}

public enum ProbeType
{
    Icmp = 0,
    Tcp = 1,
    Http = 2
}

public enum ServiceState
{
    Unknown = 0,
    Running = 1,
    Stopped = 2,
    Failed = 3,
    NotFound = 4,
    Starting = 5,
    Stopping = 6
}

public enum AlertSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

public enum AlertStatus
{
    Open = 0,
    Acknowledged = 1,
    Resolved = 2
}

public enum AlertMetric
{
    CpuPercent = 0,
    MemoryPercent = 1,
    DiskPercent = 2,
    PingLatencyMs = 3,
    PacketLossPercent = 4,
    HttpResponseTimeMs = 5,
    ServerOffline = 6,
    ServiceStopped = 7,
    CertificateExpiryDays = 8,
    PortClosed = 9,
    SwapPercent = 10,
    LoadPerCore = 11,
    FailedLoginsPerInterval = 12
}

public enum ComparisonOperator
{
    GreaterThan = 0,
    GreaterThanOrEqual = 1,
    LessThan = 2,
    LessThanOrEqual = 3
}

public enum NotificationChannelType
{
    Email = 0,
    Telegram = 1,
    Webhook = 2
}

public enum SecurityEventType
{
    FailedLogin = 0,
    SuccessfulLogin = 1,
    SuspiciousLogin = 2,
    FirewallDisabled = 3,
    ServiceStateChanged = 4,
    NewListeningPort = 5,
    CertificateExpiring = 6,
    UpdatesPending = 7,
    AuthenticationFailure = 8,
    Other = 99
}
