namespace ServerMonitor.Domain.Entities;

/// <summary>A TCP port explicitly configured by an administrator. No range scanning is ever performed.</summary>
public class MonitoredPort
{
    public int Id { get; set; }
    public int ServerId { get; set; }
    public Server? Server { get; set; }
    public int Port { get; set; }
    public string Protocol { get; set; } = "TCP";
    public string? ServiceName { get; set; }
    public bool IsEnabled { get; set; } = true;
    /// <summary>When true, a closed port raises a PortClosed alert.</summary>
    public bool ExpectOpen { get; set; } = true;

    public bool? IsOpen { get; set; }
    public double? ResponseTimeMs { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset? LastChangedAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>A system service (systemd unit or Windows service) whose state is tracked.</summary>
public class MonitoredService
{
    public int Id { get; set; }
    public int ServerId { get; set; }
    public Server? Server { get; set; }
    /// <summary>systemd unit name (e.g. "nginx") or Windows service name (e.g. "W3SVC"). Validated against a strict pattern.</summary>
    public required string Name { get; set; }
    public string? DisplayName { get; set; }
    public bool IsCustom { get; set; }
    public bool IsEnabled { get; set; } = true;
    /// <summary>When true, a stopped state raises a ServiceStopped alert.</summary>
    public bool AlertWhenStopped { get; set; } = true;

    public ServiceState State { get; set; } = ServiceState.Unknown;
    public string? SubState { get; set; }
    public string? StartupType { get; set; }
    public DateTimeOffset? LastStateChangeAt { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public string? LastError { get; set; }
}

public class SecurityEvent
{
    public long Id { get; set; }
    public int ServerId { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public SecurityEventType Type { get; set; }
    public AlertSeverity Severity { get; set; }
    public string? UserName { get; set; }
    public string? SourceIp { get; set; }
    public required string Message { get; set; }
    /// <summary>Hash of (server, type, timestamp, message) to avoid storing the same log line twice.</summary>
    public required string Fingerprint { get; set; }
}
