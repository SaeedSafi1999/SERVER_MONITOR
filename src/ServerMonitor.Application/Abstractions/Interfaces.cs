using Microsoft.EntityFrameworkCore;
using ServerMonitor.Application.Models;
using ServerMonitor.Domain;
using ServerMonitor.Domain.Entities;

namespace ServerMonitor.Application.Abstractions;

public interface IMonitorDbContext
{
    DbSet<Server> Servers { get; }
    DbSet<ServerGroup> ServerGroups { get; }
    DbSet<MetricSnapshot> MetricSnapshots { get; }
    DbSet<ProbeResult> ProbeResults { get; }
    DbSet<StatusPeriod> StatusPeriods { get; }
    DbSet<MonitoredPort> MonitoredPorts { get; }
    DbSet<MonitoredService> MonitoredServices { get; }
    DbSet<SecurityEvent> SecurityEvents { get; }
    DbSet<AlertRule> AlertRules { get; }
    DbSet<Alert> Alerts { get; }
    DbSet<NotificationChannel> NotificationChannels { get; }
    DbSet<AuditLog> AuditLogs { get; }
    DbSet<SystemSettings> SystemSettings { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Encrypts secrets at rest. Plain text never leaves the backend.</summary>
public interface ICredentialProtector
{
    string Protect(string plaintext);
    string Unprotect(string ciphertext);
}

/// <summary>
/// OS-specific adapter. Implementations only execute a fixed, server-side set of read-only
/// commands; user-supplied values (service names, log sources) are validated against allow-lists.
/// </summary>
public interface IServerAdapter
{
    OperatingSystemFamily Family { get; }
    IReadOnlyList<LogSourceDescriptor> LogSources { get; }
    /// <summary>Default services suggested when a server of this OS is added.</summary>
    IReadOnlyList<(string Name, string Display)> WellKnownServices { get; }

    /// <param name="acceptPresentedKey">When true, a host key/certificate that is not yet pinned is accepted for this test only.</param>
    Task<ConnectionTestResult> TestConnectionAsync(Server server, bool acceptPresentedKey, CancellationToken ct);
    Task<HostSample> CollectMetricsAsync(Server server, CancellationToken ct);
    Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync(Server server, int top, CancellationToken ct);
    Task<IReadOnlyList<ServiceStatusInfo>> GetServicesAsync(Server server, IReadOnlyCollection<string> serviceNames, CancellationToken ct);
    Task<ServiceControlResult> RestartServiceAsync(Server server, string serviceName, CancellationToken ct);
    Task<SecurityScanResult> ScanSecurityAsync(Server server, DateTimeOffset since, CancellationToken ct);
    Task<IReadOnlyList<LogEntry>> ReadLogsAsync(Server server, string sourceKey, DateTimeOffset? since, int maxEntries, CancellationToken ct);
}

public interface IServerAdapterFactory
{
    IServerAdapter For(Server server);
    IServerAdapter For(OperatingSystemFamily family);
}

public interface INetworkProber
{
    Task<IReadOnlyList<ProbeResult>> PingAsync(Server server, int count, int timeoutMs, CancellationToken ct);
    Task<ProbeResult> TcpConnectAsync(Server server, int port, int timeoutMs, CancellationToken ct);
    Task<(ProbeResult Result, HttpProbeSummary Summary)> HttpCheckAsync(Server server, string url, int timeoutMs, CancellationToken ct);
}

/// <summary>Holds the latest state of every server in memory (fast dashboard rendering).</summary>
public interface ILiveStateCache
{
    ServerLiveState? Get(int serverId);
    IReadOnlyCollection<ServerLiveState> All();
    ServerLiveState GetOrAdd(Server server);
    void Remove(int serverId);
}

/// <summary>Real-time fan-out. Implemented in the web layer over SignalR + in-process events for Blazor circuits.</summary>
public interface IRealtimePublisher
{
    Task ServerUpdatedAsync(ServerLiveState state, CancellationToken ct = default);
    Task AlertChangedAsync(Alert alert, CancellationToken ct = default);
}

public interface INotificationSender
{
    NotificationChannelType Type { get; }
    Task SendAsync(NotificationChannel channel, NotificationMessage message, CancellationToken ct);
}

public sealed record NotificationMessage(string Title, string Body, AlertSeverity Severity, string? ServerName, bool IsResolution, DateTimeOffset Timestamp);

public interface IAuditService
{
    Task WriteAsync(string action, string? entityType = null, string? entityId = null, string? details = null, CancellationToken ct = default);
}

/// <summary>Who is calling (for audit + authorisation inside services).</summary>
public interface ICurrentUser
{
    string? UserName { get; }
    string? IpAddress { get; }
    bool IsInRole(string role);
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string Operator = "Operator";
    public const string Viewer = "Viewer";
    public const string AdminOrOperator = Admin + "," + Operator;
    public static readonly string[] All = [Admin, Operator, Viewer];
}
