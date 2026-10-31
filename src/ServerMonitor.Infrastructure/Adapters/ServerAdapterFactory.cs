using ServerMonitor.Application.Abstractions;
using ServerMonitor.Domain;
using ServerMonitor.Domain.Entities;
using ServerMonitor.Infrastructure.Adapters.Linux;
using ServerMonitor.Infrastructure.Adapters.Windows;

namespace ServerMonitor.Infrastructure.Adapters;

public sealed class ServerAdapterFactory(LinuxServerAdapter linux, WindowsServerAdapter windows) : IServerAdapterFactory
{
    public IServerAdapter For(Server server) => For(server.OperatingSystem);

    public IServerAdapter For(OperatingSystemFamily family) => family switch
    {
        OperatingSystemFamily.Linux => linux,
        OperatingSystemFamily.Windows => windows,
        _ => throw new NotSupportedException($"No adapter for {family}")
    };
}
