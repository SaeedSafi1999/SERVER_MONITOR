using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Infrastructure.Adapters;
using ServerMonitor.Infrastructure.Adapters.Linux;
using ServerMonitor.Infrastructure.Adapters.Windows;
using ServerMonitor.Infrastructure.Alerting;
using ServerMonitor.Infrastructure.Monitoring;
using ServerMonitor.Infrastructure.Persistence;
using ServerMonitor.Infrastructure.Probes;
using ServerMonitor.Infrastructure.Remote.Ssh;
using ServerMonitor.Infrastructure.Remote.WinRm;
using ServerMonitor.Infrastructure.Security;
using ServerMonitor.Infrastructure.Services;

namespace ServerMonitor.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddServerMonitorInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var cs = config.GetConnectionString("Monitor")
                 ?? throw new InvalidOperationException("ConnectionStrings:Monitor is not configured.");
        // Transient contexts: Blazor Server circuits are long-lived, so every service instance gets its own
        // short unit of work instead of sharing one DbContext across concurrent UI events.
        services.AddDbContext<MonitorDbContext>(o => o.UseNpgsql(cs, npg => npg.EnableRetryOnFailure(3)),
            ServiceLifetime.Transient, ServiceLifetime.Singleton);
        services.AddTransient<IMonitorDbContext>(sp => sp.GetRequiredService<MonitorDbContext>());

        // Secrets at rest: Data Protection key ring outside the database.
        var keysPath = config["DataProtection:KeysPath"] ?? Path.Combine(AppContext.BaseDirectory, "keys");
        var dp = services.AddDataProtection().SetApplicationName("ServerMonitor").PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        var certPath = config["DataProtection:CertificatePath"];
        if (!string.IsNullOrEmpty(certPath))
        {
            var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(certPath, config["DataProtection:CertificatePassword"]);
            dp.ProtectKeysWithCertificate(cert);
        }
        services.AddSingleton<ICredentialProtector, CredentialProtector>();
        services.AddSingleton<ChannelConfigSerializer>();

        // Remote access
        services.AddSingleton<SshConnectionPool>();
        services.AddSingleton<WinRmClient>();
        services.AddSingleton<LinuxServerAdapter>();
        services.AddSingleton<WindowsServerAdapter>();
        services.AddSingleton<IServerAdapterFactory, ServerAdapterFactory>();
        services.AddSingleton<INetworkProber, NetworkProber>();

        // Monitoring runtime
        services.AddSingleton<ILiveStateCache, LiveStateCache>();
        services.AddSingleton<ISettingsProvider, SettingsProvider>();
        services.AddSingleton<AlertEngine>();
        services.AddSingleton<INotificationQueue, NotificationQueue>();
        services.AddScoped<ServerCollector>();
        services.AddSingleton<MonitoringScheduler>();
        services.AddSingleton<ICollectionTrigger>(sp => sp.GetRequiredService<MonitoringScheduler>());
        services.AddHostedService(sp => sp.GetRequiredService<MonitoringScheduler>());
        services.AddHostedService<RetentionWorker>();
        services.AddHostedService<NotificationWorker>();

        // Notifications
        services.AddHttpClient("notifications", c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton<INotificationSender, EmailNotificationSender>();
        services.AddSingleton<INotificationSender, TelegramNotificationSender>();
        services.AddSingleton<INotificationSender, WebhookNotificationSender>();

        // Application services
        services.AddTransient<IAuditService, AuditService>();
        services.AddTransient<ServerManagementService>();
        services.AddTransient<MetricsQueryService>();
        services.AddTransient<AlertManagementService>();
        return services;
    }
}
