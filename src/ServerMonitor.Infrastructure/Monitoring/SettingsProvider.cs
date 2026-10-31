using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Domain.Entities;

namespace ServerMonitor.Infrastructure.Monitoring;

public interface ISettingsProvider
{
    Task<SystemSettings> GetAsync(CancellationToken ct = default);
    void Invalidate();
}

/// <summary>Caches the single SystemSettings row for 60 seconds (or until invalidated by the settings page).</summary>
public sealed class SettingsProvider(IServiceScopeFactory scopes) : ISettingsProvider
{
    private SystemSettings? _cached;
    private DateTimeOffset _loadedAt;

    public async Task<SystemSettings> GetAsync(CancellationToken ct = default)
    {
        var cached = _cached;
        if (cached is not null && DateTimeOffset.UtcNow - _loadedAt < TimeSpan.FromSeconds(60)) return cached;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IMonitorDbContext>();
        var settings = await db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new SystemSettings();
        _cached = settings;
        _loadedAt = DateTimeOffset.UtcNow;
        return settings;
    }

    public void Invalidate() => _cached = null;
}
