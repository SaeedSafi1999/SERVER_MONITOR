using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Application.Models;
using ServerMonitor.Domain;
using ServerMonitor.Domain.Entities;
using ServerMonitor.Infrastructure.Monitoring;
using ServerMonitor.Infrastructure.Remote.Ssh;
using ServerMonitor.Infrastructure.Security;

namespace ServerMonitor.Infrastructure.Services;

/// <summary>Edit model for the server form. Secrets are write-only: empty means "keep the stored value".</summary>
public sealed class ServerEditModel : IValidatableObject
{
    public int Id { get; set; }
    [Required, StringLength(128, MinimumLength = 1)] public string Name { get; set; } = "";
    [Required, StringLength(255)] public string Host { get; set; } = "";
    public OperatingSystemFamily OperatingSystem { get; set; } = OperatingSystemFamily.Linux;
    public ConnectionProtocol Protocol { get; set; } = ConnectionProtocol.Ssh;
    [Range(1, 65535)] public int ManagementPort { get; set; } = 22;
    [Range(1, 65535)] public int? RdpPort { get; set; }
    [Required, StringLength(128)] public string Username { get; set; } = "";
    public AuthenticationMethod AuthMethod { get; set; } = AuthenticationMethod.Password;
    public WinRmAuthScheme WinRmAuth { get; set; } = WinRmAuthScheme.Negotiate;
    [StringLength(512)] public string? NewPassword { get; set; }
    [StringLength(16384)] public string? NewPrivateKey { get; set; }
    [StringLength(512)] public string? NewPrivateKeyPassphrase { get; set; }
    public bool HasStoredPassword { get; set; }
    public bool HasStoredPrivateKey { get; set; }
    [Range(10, 3600)] public int MonitoringIntervalSeconds { get; set; } = 30;
    public bool IsMonitoringEnabled { get; set; } = true;
    [StringLength(512)] public string Tags { get; set; } = "";
    [StringLength(128)] public string? Location { get; set; }
    [StringLength(2000)] public string? Description { get; set; }
    [StringLength(2048)] public string? HealthCheckUrl { get; set; }
    [Range(100, 599)] public int? HealthCheckExpectedStatus { get; set; }
    [Range(1, 65535)] public int? TcpProbePort { get; set; }
    public bool IcmpEnabled { get; set; } = true;
    public bool AllowServiceControl { get; set; }
    public int? GroupId { get; set; }
    public string? TrustedHostKeyFingerprint { get; set; }
    public string? TrustedCertificateThumbprint { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        if (!InputValidation.IsValidHost(Host))
            yield return new ValidationResult("Host must be an IP address or a valid DNS name.", [nameof(Host)]);
        if (OperatingSystem == OperatingSystemFamily.Linux && Protocol != ConnectionProtocol.Ssh)
            yield return new ValidationResult("Linux servers are monitored over SSH.", [nameof(Protocol)]);
        if (OperatingSystem == OperatingSystemFamily.Windows && Protocol == ConnectionProtocol.Ssh)
            yield return new ValidationResult("Windows servers are monitored over WinRM.", [nameof(Protocol)]);
        if (!string.IsNullOrWhiteSpace(HealthCheckUrl) &&
            (!Uri.TryCreate(HealthCheckUrl, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")))
            yield return new ValidationResult("Health check URL must be an absolute http(s) URL.", [nameof(HealthCheckUrl)]);
        if (Username.IndexOfAny(['\r', '\n', '\0']) >= 0)
            yield return new ValidationResult("Invalid username.", [nameof(Username)]);
    }
}

/// <summary>Server CRUD, credential encryption, connection tests and trust pinning. All writes are audited.</summary>
public sealed class ServerManagementService(
    IMonitorDbContext db,
    ICredentialProtector protector,
    IServerAdapterFactory adapters,
    IAuditService audit,
    ILiveStateCache cache,
    ICollectionTrigger trigger,
    SshConnectionPool sshPool,
    ICurrentUser user)
{
    public Task<List<Server>> ListAsync(CancellationToken ct = default) =>
        db.Servers.AsNoTracking().Include(s => s.Group).OrderBy(s => s.Name).ToListAsync(ct);

    public Task<Server?> GetAsync(int id, CancellationToken ct = default) =>
        db.Servers.AsNoTracking().Include(s => s.Group).Include(s => s.Services).Include(s => s.Ports).FirstOrDefaultAsync(s => s.Id == id, ct);

    public Task<List<MonitoredService>> AllServicesAsync(CancellationToken ct = default) =>
        db.MonitoredServices.AsNoTracking().Include(s => s.Server).OrderBy(s => s.Server!.Name).ThenBy(s => s.Name).ToListAsync(ct);

    public Task<List<MonitoredPort>> AllPortsAsync(CancellationToken ct = default) =>
        db.MonitoredPorts.AsNoTracking().Include(p => p.Server).OrderBy(p => p.Server!.Name).ThenBy(p => p.Port).ToListAsync(ct);

    public Task<List<ServerGroup>> GroupsAsync(CancellationToken ct = default) =>
        db.ServerGroups.AsNoTracking().OrderBy(g => g.Name).ToListAsync(ct);

    public async Task<ServerGroup> CreateGroupAsync(string name, string? description, CancellationToken ct = default)
    {
        user.Demand(Roles.Admin);
        name = name.Trim();
        if (name.Length is 0 or > 128) throw new ValidationException("Group name is required (max 128 characters).");
        if (await db.ServerGroups.AnyAsync(g => g.Name == name, ct)) throw new ValidationException("A group with this name already exists.");
        var g = new ServerGroup { Name = name, Description = description };
        db.ServerGroups.Add(g);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("group.create", "ServerGroup", g.Id.ToString(), name, ct);
        return g;
    }

    public async Task DeleteGroupAsync(int id, CancellationToken ct = default)
    {
        user.Demand(Roles.Admin);
        var g = await db.ServerGroups.FindAsync([id], ct);
        if (g is null) return;
        db.ServerGroups.Remove(g);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("group.delete", "ServerGroup", id.ToString(), g.Name, ct);
    }

    public static ServerEditModel ToEditModel(Server s) => new()
    {
        Id = s.Id, Name = s.Name, Host = s.Host, OperatingSystem = s.OperatingSystem, Protocol = s.Protocol,
        ManagementPort = s.ManagementPort, RdpPort = s.RdpPort, Username = s.Username, AuthMethod = s.AuthMethod, WinRmAuth = s.WinRmAuth,
        HasStoredPassword = s.EncryptedPassword is not null, HasStoredPrivateKey = s.EncryptedPrivateKey is not null,
        MonitoringIntervalSeconds = s.MonitoringIntervalSeconds, IsMonitoringEnabled = s.IsMonitoringEnabled, Tags = s.Tags,
        Location = s.Location, Description = s.Description, HealthCheckUrl = s.HealthCheckUrl, HealthCheckExpectedStatus = s.HealthCheckExpectedStatus,
        TcpProbePort = s.TcpProbePort, IcmpEnabled = s.IcmpEnabled, AllowServiceControl = s.AllowServiceControl, GroupId = s.GroupId,
        TrustedHostKeyFingerprint = s.TrustedHostKeyFingerprint, TrustedCertificateThumbprint = s.TrustedCertificateThumbprint
    };

    public async Task<Server> SaveAsync(ServerEditModel m, CancellationToken ct = default)
    {
        user.Demand(Roles.Admin);
        Validator.ValidateObject(m, new ValidationContext(m), validateAllProperties: true);
        var name = m.Name.Trim();
        if (await db.Servers.AnyAsync(s => s.Name == name && s.Id != m.Id, ct))
            throw new ValidationException("A server with this name already exists.");

        var isNew = m.Id == 0;
        var server = isNew
            ? new Server { Name = name, Host = m.Host.Trim(), Username = m.Username.Trim() }
            : await db.Servers.FirstOrDefaultAsync(s => s.Id == m.Id, ct) ?? throw new KeyNotFoundException("Server not found");

        var connectionChanged = !isNew && (server.Host != m.Host.Trim() || server.ManagementPort != m.ManagementPort || server.Protocol != m.Protocol);

        server.Name = name;
        server.Host = m.Host.Trim();
        server.OperatingSystem = m.OperatingSystem;
        server.Protocol = m.Protocol;
        server.ManagementPort = m.ManagementPort;
        server.RdpPort = m.RdpPort;
        server.Username = m.Username.Trim();
        server.AuthMethod = m.OperatingSystem == OperatingSystemFamily.Windows ? AuthenticationMethod.Password : m.AuthMethod;
        server.WinRmAuth = m.WinRmAuth;
        server.MonitoringIntervalSeconds = m.MonitoringIntervalSeconds;
        server.IsMonitoringEnabled = m.IsMonitoringEnabled;
        server.Tags = string.Join(", ", m.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct());
        server.Location = m.Location;
        server.Description = m.Description;
        server.HealthCheckUrl = string.IsNullOrWhiteSpace(m.HealthCheckUrl) ? null : m.HealthCheckUrl.Trim();
        server.HealthCheckExpectedStatus = m.HealthCheckExpectedStatus;
        server.TcpProbePort = m.TcpProbePort;
        server.IcmpEnabled = m.IcmpEnabled;
        server.AllowServiceControl = m.AllowServiceControl;
        server.GroupId = m.GroupId;
        server.UpdatedAt = DateTimeOffset.UtcNow;

        // Write-only secrets: only replaced when a new value is supplied.
        if (!string.IsNullOrEmpty(m.NewPassword)) server.EncryptedPassword = protector.Protect(m.NewPassword);
        if (!string.IsNullOrWhiteSpace(m.NewPrivateKey))
        {
            ValidatePrivateKey(m.NewPrivateKey, m.NewPrivateKeyPassphrase);
            server.EncryptedPrivateKey = protector.Protect(m.NewPrivateKey.Trim() + "\n");
            server.EncryptedPrivateKeyPassphrase = string.IsNullOrEmpty(m.NewPrivateKeyPassphrase) ? null : protector.Protect(m.NewPrivateKeyPassphrase);
        }
        if (server.EncryptedPassword is null && server.EncryptedPrivateKey is null)
            throw new ValidationException("A password or private key is required.");

        // The pinned identity belongs to the old endpoint: changing the endpoint requires re-trusting it.
        if (connectionChanged)
        {
            server.TrustedHostKeyFingerprint = null;
            server.TrustedCertificateThumbprint = null;
        }
        if (isNew)
        {
            db.Servers.Add(server);
            await db.SaveChangesAsync(ct);
            // Sensible default port checks.
            foreach (var (port, svc) in DefaultPorts(server))
                db.MonitoredPorts.Add(new MonitoredPort { ServerId = server.Id, Port = port, ServiceName = svc });
        }
        await db.SaveChangesAsync(ct);

        sshPool.Evict(server.Id);
        cache.GetOrAdd(server);
        trigger.ServersChanged();
        trigger.RequestImmediate(server.Id);
        await audit.WriteAsync(isNew ? "server.create" : "server.update", "Server", server.Id.ToString(),
            $"{server.Name} ({server.Host}:{server.ManagementPort}, {server.Protocol}){(string.IsNullOrEmpty(m.NewPassword) && string.IsNullOrEmpty(m.NewPrivateKey) ? "" : " — credentials changed")}", ct);
        return server;
    }

    private static IEnumerable<(int, string)> DefaultPorts(Server s) => s.OperatingSystem == OperatingSystemFamily.Linux
        ? [(s.ManagementPort, "SSH")]
        : [(s.ManagementPort, "WinRM"), (s.RdpPort ?? 3389, "RDP")];

    private static void ValidatePrivateKey(string key, string? passphrase)
    {
        try
        {
            using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(key.Trim() + "\n"));
            _ = string.IsNullOrEmpty(passphrase) ? new Renci.SshNet.PrivateKeyFile(ms) : new Renci.SshNet.PrivateKeyFile(ms, passphrase);
        }
        catch (Exception ex)
        {
            throw new ValidationException("The private key could not be parsed (wrong format or passphrase): " + ex.Message);
        }
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        user.Demand(Roles.Admin);
        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (server is null) return;
        db.Servers.Remove(server);
        await db.SaveChangesAsync(ct);
        sshPool.Evict(id);
        cache.Remove(id);
        trigger.ServersChanged();
        await audit.WriteAsync("server.delete", "Server", id.ToString(), server.Name, ct);
    }

    public async Task SetMonitoringAsync(int id, bool enabled, CancellationToken ct = default)
    {
        user.Demand(Roles.Admin);
        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new KeyNotFoundException();
        server.IsMonitoringEnabled = enabled;
        if (!enabled) server.ConnectionState = ConnectionState.Disabled;
        await db.SaveChangesAsync(ct);
        cache.GetOrAdd(server).MonitoringEnabled = enabled;
        trigger.ServersChanged();
        await audit.WriteAsync(enabled ? "server.monitoring.enable" : "server.monitoring.disable", "Server", id.ToString(), server.Name, ct);
    }

    /// <summary>Tests the connection with stored credentials. Accepts an unpinned key only for this test.</summary>
    public async Task<ConnectionTestResult> TestConnectionAsync(int id, CancellationToken ct = default)
    {
        user.Demand(Roles.Admin, Roles.Operator);
        var server = await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new KeyNotFoundException();
        var result = await adapters.For(server).TestConnectionAsync(server, acceptPresentedKey: true, ct);
        await audit.WriteAsync("server.test-connection", "Server", id.ToString(), $"{result.State}: {result.Message}", ct);
        return result;
    }

    /// <summary>Pins the presented SSH host key / WinRM certificate after an administrator verified it.</summary>
    public async Task TrustIdentityAsync(int id, string fingerprint, CancellationToken ct = default)
    {
        user.Demand(Roles.Admin);
        if (string.IsNullOrWhiteSpace(fingerprint) || fingerprint.Length > 128) throw new ValidationException("Invalid fingerprint.");
        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new KeyNotFoundException();
        if (server.Protocol == ConnectionProtocol.Ssh) server.TrustedHostKeyFingerprint = fingerprint;
        else server.TrustedCertificateThumbprint = fingerprint;
        server.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        sshPool.Evict(id);
        trigger.RequestImmediate(id);
        await audit.WriteAsync("server.trust-identity", "Server", id.ToString(), fingerprint, ct);
    }

    public async Task ClearTrustAsync(int id, CancellationToken ct = default)
    {
        user.Demand(Roles.Admin);
        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new KeyNotFoundException();
        server.TrustedHostKeyFingerprint = null;
        server.TrustedCertificateThumbprint = null;
        await db.SaveChangesAsync(ct);
        sshPool.Evict(id);
        await audit.WriteAsync("server.clear-trust", "Server", id.ToString(), null, ct);
    }

    // ---------------- services & ports ----------------

    public async Task AddServiceAsync(int serverId, string name, string? displayName, bool isCustom, CancellationToken ct = default)
    {
        user.Demand(Roles.Admin);
        var server = await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == serverId, ct) ?? throw new KeyNotFoundException();
        name = name.Trim();
        var valid = server.OperatingSystem == OperatingSystemFamily.Linux ? InputValidation.IsValidLinuxUnit(name) : InputValidation.IsValidWindowsService(name);
        if (!valid) throw new ValidationException("Invalid service name. Allowed characters: letters, digits, '.', '-', '_', '@', ':' (Linux) or '$' (Windows).");
        if (await db.MonitoredServices.AnyAsync(s => s.ServerId == serverId && s.Name == name, ct)) return;
        db.MonitoredServices.Add(new MonitoredService { ServerId = serverId, Name = name, DisplayName = displayName, IsCustom = isCustom });
        await db.SaveChangesAsync(ct);
        trigger.RequestImmediate(serverId);
        await audit.WriteAsync("service.add", "Server", serverId.ToString(), name, ct);
    }

    public async Task RemoveServiceAsync(int serviceId, CancellationToken ct = default)
    {
        user.Demand(Roles.Admin);
        var svc = await db.MonitoredServices.FindAsync([serviceId], ct);
        if (svc is null) return;
        db.MonitoredServices.Remove(svc);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("service.remove", "Server", svc.ServerId.ToString(), svc.Name, ct);
    }

    public async Task<ServiceControlResult> RestartServiceAsync(int serviceId, CancellationToken ct = default)
    {
        user.Demand(Roles.Admin, Roles.Operator);
        var svc = await db.MonitoredServices.Include(s => s.Server).FirstOrDefaultAsync(s => s.Id == serviceId, ct) ?? throw new KeyNotFoundException();
        var server = svc.Server!;
        if (!server.AllowServiceControl)
            return new ServiceControlResult(false, "Service control is disabled for this server.");
        var result = await adapters.For(server).RestartServiceAsync(server, svc.Name, ct);
        await audit.WriteAsync("service.restart", "Server", server.Id.ToString(), $"{svc.Name}: {(result.Success ? "OK" : result.Message)}", ct);
        trigger.RequestImmediate(server.Id);
        return result;
    }

    public async Task AddPortAsync(int serverId, int port, string? serviceName, bool expectOpen, CancellationToken ct = default)
    {
        user.Demand(Roles.Admin);
        if (port is < 1 or > 65535) throw new ValidationException("Port must be between 1 and 65535.");
        if (await db.MonitoredPorts.AnyAsync(p => p.ServerId == serverId && p.Port == port, ct)) return;
        var count = await db.MonitoredPorts.CountAsync(p => p.ServerId == serverId, ct);
        // Explicit per-port checks only; this is not a port scanner.
        if (count >= 64) throw new ValidationException("At most 64 monitored ports per server.");
        db.MonitoredPorts.Add(new MonitoredPort { ServerId = serverId, Port = port, ServiceName = serviceName?.Trim(), ExpectOpen = expectOpen });
        await db.SaveChangesAsync(ct);
        trigger.RequestImmediate(serverId);
        await audit.WriteAsync("port.add", "Server", serverId.ToString(), port.ToString(), ct);
    }

    public async Task RemovePortAsync(int portId, CancellationToken ct = default)
    {
        user.Demand(Roles.Admin);
        var p = await db.MonitoredPorts.FindAsync([portId], ct);
        if (p is null) return;
        db.MonitoredPorts.Remove(p);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("port.remove", "Server", p.ServerId.ToString(), p.Port.ToString(), ct);
    }
}
