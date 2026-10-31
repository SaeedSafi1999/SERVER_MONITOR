using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ServerMonitor.Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef` to create migrations without starting the web host.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MonitorDbContext>
{
    public MonitorDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("SM_DESIGN_CONNECTION")
                 ?? "Host=localhost;Database=servermonitor;Username=postgres;Password=postgres";
        var options = new DbContextOptionsBuilder<MonitorDbContext>().UseNpgsql(cs).Options;
        return new MonitorDbContext(options);
    }
}
