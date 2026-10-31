using System.Collections.Concurrent;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Application.Models;
using ServerMonitor.Domain.Entities;

namespace ServerMonitor.Infrastructure.Monitoring;

public sealed class LiveStateCache : ILiveStateCache
{
    private readonly ConcurrentDictionary<int, ServerLiveState> _states = new();

    public ServerLiveState? Get(int serverId) => _states.GetValueOrDefault(serverId);

    public IReadOnlyCollection<ServerLiveState> All() => _states.Values.OrderBy(s => s.Name).ToList();

    public ServerLiveState GetOrAdd(Server server) =>
        _states.AddOrUpdate(server.Id,
            _ => Create(server),
            (_, existing) => existing.Name == server.Name && existing.Host == server.Host && existing.OperatingSystem == server.OperatingSystem
                ? Sync(existing, server)
                : Sync(Create(server, existing), server));

    public void Remove(int serverId) => _states.TryRemove(serverId, out _);

    private static ServerLiveState Create(Server s, ServerLiveState? previous = null) => new()
    {
        ServerId = s.Id,
        Name = s.Name,
        Host = s.Host,
        OperatingSystem = s.OperatingSystem,
        Status = s.Status,
        ConnectionState = s.ConnectionState,
        LastError = s.LastError,
        LastSeenAt = s.LastSeenAt,
        OsVersion = s.OsVersion,
        Sample = previous?.Sample,
        Icmp = previous?.Icmp,
        Tcp = previous?.Tcp,
        Http = previous?.Http,
        Security = previous?.Security,
        OpenAlerts = previous?.OpenAlerts ?? 0
    };

    private static ServerLiveState Sync(ServerLiveState state, Server s)
    {
        state.MonitoringEnabled = s.IsMonitoringEnabled;
        return state;
    }
}
