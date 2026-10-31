namespace ServerMonitor.Domain.Entities;

public class AuditLog
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? UserName { get; set; }
    public required string Action { get; set; }
    public string? EntityType { get; set; }
    public string? EntityId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
}

/// <summary>Single-row table with runtime-editable settings.</summary>
public class SystemSettings
{
    public int Id { get; set; } = 1;
    public int MetricRetentionDays { get; set; } = 14;
    public int ProbeRetentionDays { get; set; } = 14;
    public int AlertRetentionDays { get; set; } = 180;
    public int SecurityEventRetentionDays { get; set; } = 90;
    public int AuditRetentionDays { get; set; } = 365;
    public double SlaTargetPercent { get; set; } = 99.9;
    public int IcmpPacketsPerRound { get; set; } = 4;
    public int ProbeTimeoutMs { get; set; } = 3000;
    public int MaxConcurrentCollections { get; set; } = 16;
    public int SecurityScanIntervalMinutes { get; set; } = 5;
    public int CertificateWarningDays { get; set; } = 21;
}
