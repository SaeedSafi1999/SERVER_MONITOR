namespace ServerMonitor.Domain.Entities;

/// <summary>
/// Threshold rule. ServerId == null means the rule applies to every server that has no
/// server-specific rule for the same metric.
/// </summary>
public class AlertRule
{
    public int Id { get; set; }
    public int? ServerId { get; set; }
    public Server? Server { get; set; }
    public required string Name { get; set; }
    public AlertMetric Metric { get; set; }
    public ComparisonOperator Operator { get; set; } = ComparisonOperator.GreaterThan;
    public double Threshold { get; set; }
    public AlertSeverity Severity { get; set; } = AlertSeverity.Warning;
    /// <summary>Number of consecutive evaluations that must breach before the alert fires (flap protection).</summary>
    public int ConsecutiveBreaches { get; set; } = 2;
    /// <summary>Minimum minutes between repeated notifications for the same open alert.</summary>
    public int CooldownMinutes { get; set; } = 15;
    public bool IsEnabled { get; set; } = true;
}

public class Alert
{
    public long Id { get; set; }
    public int ServerId { get; set; }
    public Server? Server { get; set; }
    public int? RuleId { get; set; }
    public AlertMetric Metric { get; set; }
    public AlertSeverity Severity { get; set; }
    public AlertStatus Status { get; set; } = AlertStatus.Open;
    /// <summary>Deduplication key: one non-resolved alert per fingerprint.</summary>
    public required string Fingerprint { get; set; }
    public required string Title { get; set; }
    public string? Message { get; set; }
    public double? Value { get; set; }
    public double? Threshold { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public int OccurrenceCount { get; set; } = 1;
    public DateTimeOffset? LastNotifiedAt { get; set; }
    public string? AcknowledgedBy { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

public class NotificationChannel
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public NotificationChannelType Type { get; set; }
    public bool IsEnabled { get; set; } = true;
    public AlertSeverity MinimumSeverity { get; set; } = AlertSeverity.Warning;
    /// <summary>Channel settings (SMTP password, bot token, webhook secret...) encrypted as a JSON blob.</summary>
    public required string EncryptedConfig { get; set; }
    public bool NotifyOnResolve { get; set; } = true;
}
