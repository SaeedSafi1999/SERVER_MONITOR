using ServerMonitor.Application.Abstractions;
using ServerMonitor.Domain.Entities;

namespace ServerMonitor.Infrastructure.Services;

public sealed class AuditService(IMonitorDbContext db, ICurrentUser user) : IAuditService
{
    public async Task WriteAsync(string action, string? entityType = null, string? entityId = null, string? details = null, CancellationToken ct = default)
    {
        db.AuditLogs.Add(new AuditLog
        {
            Timestamp = DateTimeOffset.UtcNow,
            UserName = user.UserName ?? "system",
            IpAddress = user.IpAddress,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Details = details is { Length: > 4000 } ? details[..4000] : details
        });
        await db.SaveChangesAsync(ct);
    }
}
