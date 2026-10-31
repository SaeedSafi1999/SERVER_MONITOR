using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Domain.Entities;

namespace ServerMonitor.Infrastructure.Persistence;

public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
}

public class MonitorDbContext(DbContextOptions<MonitorDbContext> options)
    : IdentityDbContext<ApplicationUser>(options), IMonitorDbContext
{
    public DbSet<Server> Servers => Set<Server>();
    public DbSet<ServerGroup> ServerGroups => Set<ServerGroup>();
    public DbSet<MetricSnapshot> MetricSnapshots => Set<MetricSnapshot>();
    public DbSet<ProbeResult> ProbeResults => Set<ProbeResult>();
    public DbSet<StatusPeriod> StatusPeriods => Set<StatusPeriod>();
    public DbSet<MonitoredPort> MonitoredPorts => Set<MonitoredPort>();
    public DbSet<MonitoredService> MonitoredServices => Set<MonitoredService>();
    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();
    public DbSet<AlertRule> AlertRules => Set<AlertRule>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<NotificationChannel> NotificationChannels => Set<NotificationChannel>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<SystemSettings> SystemSettings => Set<SystemSettings>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Server>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.Host).HasMaxLength(255);
            e.Property(x => x.Username).HasMaxLength(128);
            e.Property(x => x.Tags).HasMaxLength(512);
            e.Property(x => x.Location).HasMaxLength(128);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.HealthCheckUrl).HasMaxLength(2048);
            e.Property(x => x.TrustedHostKeyFingerprint).HasMaxLength(128);
            e.Property(x => x.TrustedCertificateThumbprint).HasMaxLength(128);
            e.Property(x => x.LastError).HasMaxLength(2000);
            e.HasIndex(x => x.Name).IsUnique();
            e.HasOne(x => x.Group).WithMany(g => g.Servers).HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.SetNull);
            e.Ignore(x => x.TagList);
            e.Ignore(x => x.IsWindows);
        });

        b.Entity<ServerGroup>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(128);
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<MetricSnapshot>(e =>
        {
            e.HasOne<Server>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ServerId, x.Timestamp });
            e.HasIndex(x => x.Timestamp);
            e.Ignore(x => x.MemPercent);
            e.Ignore(x => x.SwapPercent);
            e.Ignore(x => x.DiskPercent);
        });

        b.Entity<ProbeResult>(e =>
        {
            e.HasOne<Server>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ServerId, x.Type, x.Timestamp });
            e.HasIndex(x => x.Timestamp);
            e.Property(x => x.Error).HasMaxLength(500);
        });

        b.Entity<StatusPeriod>(e =>
        {
            e.HasOne<Server>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ServerId, x.StartedAt });
            e.Property(x => x.Reason).HasMaxLength(500);
        });

        b.Entity<MonitoredPort>(e =>
        {
            e.HasOne(x => x.Server).WithMany(s => s.Ports).HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ServerId, x.Port, x.Protocol }).IsUnique();
            e.Property(x => x.Protocol).HasMaxLength(8);
            e.Property(x => x.ServiceName).HasMaxLength(64);
            e.Property(x => x.LastError).HasMaxLength(500);
        });

        b.Entity<MonitoredService>(e =>
        {
            e.HasOne(x => x.Server).WithMany(s => s.Services).HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ServerId, x.Name }).IsUnique();
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.DisplayName).HasMaxLength(256);
            e.Property(x => x.SubState).HasMaxLength(64);
            e.Property(x => x.StartupType).HasMaxLength(64);
            e.Property(x => x.LastError).HasMaxLength(1000);
        });

        b.Entity<SecurityEvent>(e =>
        {
            e.HasOne<Server>().WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.ServerId, x.Timestamp });
            e.HasIndex(x => x.Fingerprint).IsUnique();
            e.Property(x => x.Fingerprint).HasMaxLength(64);
            e.Property(x => x.Message).HasMaxLength(2000);
            e.Property(x => x.UserName).HasMaxLength(128);
            e.Property(x => x.SourceIp).HasMaxLength(64);
        });

        b.Entity<AlertRule>(e =>
        {
            e.HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(128);
        });

        b.Entity<Alert>(e =>
        {
            e.HasOne(x => x.Server).WithMany().HasForeignKey(x => x.ServerId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.Fingerprint, x.Status });
            e.HasIndex(x => new { x.Status, x.LastSeenAt });
            e.Property(x => x.Fingerprint).HasMaxLength(200);
            e.Property(x => x.Title).HasMaxLength(300);
            e.Property(x => x.Message).HasMaxLength(2000);
            e.Property(x => x.AcknowledgedBy).HasMaxLength(256);
            e.Property(x => x.ResolvedBy).HasMaxLength(256);
        });

        b.Entity<NotificationChannel>(e => e.Property(x => x.Name).HasMaxLength(128));

        b.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => x.Timestamp);
            e.Property(x => x.Action).HasMaxLength(128);
            e.Property(x => x.UserName).HasMaxLength(256);
            e.Property(x => x.EntityType).HasMaxLength(64);
            e.Property(x => x.EntityId).HasMaxLength(64);
            e.Property(x => x.Details).HasMaxLength(4000);
            e.Property(x => x.IpAddress).HasMaxLength(64);
        });

        b.Entity<SystemSettings>().HasData(new SystemSettings { Id = 1 });

        // Sensible global default thresholds (editable in the UI).
        b.Entity<AlertRule>().HasData(
            new AlertRule { Id = 1, Name = "CPU high", Metric = Domain.AlertMetric.CpuPercent, Threshold = 85, Severity = Domain.AlertSeverity.Warning, ConsecutiveBreaches = 3 },
            new AlertRule { Id = 2, Name = "CPU critical", Metric = Domain.AlertMetric.CpuPercent, Threshold = 95, Severity = Domain.AlertSeverity.Critical, ConsecutiveBreaches = 3 },
            new AlertRule { Id = 3, Name = "Memory high", Metric = Domain.AlertMetric.MemoryPercent, Threshold = 90, Severity = Domain.AlertSeverity.Warning },
            new AlertRule { Id = 4, Name = "Disk high", Metric = Domain.AlertMetric.DiskPercent, Threshold = 90, Severity = Domain.AlertSeverity.Critical, ConsecutiveBreaches = 1 },
            new AlertRule { Id = 5, Name = "Ping latency", Metric = Domain.AlertMetric.PingLatencyMs, Threshold = 150, Severity = Domain.AlertSeverity.Warning },
            new AlertRule { Id = 6, Name = "Packet loss", Metric = Domain.AlertMetric.PacketLossPercent, Threshold = 5, Severity = Domain.AlertSeverity.Warning },
            new AlertRule { Id = 7, Name = "HTTP slow", Metric = Domain.AlertMetric.HttpResponseTimeMs, Threshold = 500, Severity = Domain.AlertSeverity.Warning },
            new AlertRule { Id = 8, Name = "Server offline", Metric = Domain.AlertMetric.ServerOffline, Threshold = 0, Severity = Domain.AlertSeverity.Critical, ConsecutiveBreaches = 2 },
            new AlertRule { Id = 9, Name = "Service stopped", Metric = Domain.AlertMetric.ServiceStopped, Threshold = 0, Severity = Domain.AlertSeverity.Critical, ConsecutiveBreaches = 1 },
            new AlertRule { Id = 10, Name = "Certificate expiring", Metric = Domain.AlertMetric.CertificateExpiryDays, Operator = Domain.ComparisonOperator.LessThan, Threshold = 14, Severity = Domain.AlertSeverity.Warning, ConsecutiveBreaches = 1, CooldownMinutes = 720 },
            new AlertRule { Id = 11, Name = "Port closed", Metric = Domain.AlertMetric.PortClosed, Threshold = 0, Severity = Domain.AlertSeverity.Warning, ConsecutiveBreaches = 2 },
            new AlertRule { Id = 12, Name = "Brute force", Metric = Domain.AlertMetric.FailedLoginsPerInterval, Threshold = 20, Severity = Domain.AlertSeverity.Warning, ConsecutiveBreaches = 1, CooldownMinutes = 60 }
        );
    }
}
