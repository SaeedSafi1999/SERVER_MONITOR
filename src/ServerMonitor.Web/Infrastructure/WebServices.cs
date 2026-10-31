using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.SignalR;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Application.Models;
using ServerMonitor.Domain.Entities;

namespace ServerMonitor.Web.Infrastructure;

/// <summary>
/// Resolves the calling user for both HTTP requests (API) and Blazor circuits
/// (where HttpContext is not available after the initial request).
/// </summary>
public sealed class CurrentUser(IHttpContextAccessor http) : ICurrentUser
{
    private ClaimsPrincipal? _circuitUser;
    private string? _circuitIp;

    public void SetCircuitUser(ClaimsPrincipal user, string? ip)
    {
        _circuitUser = user;
        _circuitIp ??= ip;
    }

    private ClaimsPrincipal? Principal => _circuitUser ?? http.HttpContext?.User;

    public string? UserName => Principal?.Identity?.IsAuthenticated == true ? Principal.Identity.Name : null;

    public string? IpAddress => _circuitIp ?? http.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public bool IsInRole(string role) => Principal?.IsInRole(role) == true;
}

/// <summary>Copies the authenticated user (and client IP) into the circuit's scoped CurrentUser.</summary>
public sealed class UserCircuitHandler(AuthenticationStateProvider auth, CurrentUser currentUser, IHttpContextAccessor http) : CircuitHandler, IDisposable
{
    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var state = await auth.GetAuthenticationStateAsync();
        currentUser.SetCircuitUser(state.User, http.HttpContext?.Connection.RemoteIpAddress?.ToString());
        auth.AuthenticationStateChanged += OnChanged;
    }

    private async void OnChanged(Task<AuthenticationState> task)
    {
        try
        {
            currentUser.SetCircuitUser((await task).User, null);
        }
        catch
        {
            // ignored: next request re-resolves the user
        }
    }

    public void Dispose() => auth.AuthenticationStateChanged -= OnChanged;
}

/// <summary>In-process fan-out for Blazor Server components (they already run on the server).</summary>
public sealed class LiveUpdates
{
    public event Action<ServerLiveState>? ServerUpdated;
    public event Action<Alert>? AlertChanged;

    public void RaiseServer(ServerLiveState s) => ServerUpdated?.Invoke(s);
    public void RaiseAlert(Alert a) => AlertChanged?.Invoke(a);
}

/// <summary>SignalR hub for real-time consumers (external dashboards, integrations, browser clients).</summary>
[Authorize]
public sealed class MonitoringHub : Hub
{
    public Task SubscribeServer(int serverId) => Groups.AddToGroupAsync(Context.ConnectionId, $"server-{serverId}");
    public Task UnsubscribeServer(int serverId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, $"server-{serverId}");
}

/// <summary>Publishes collector updates to SignalR clients and to Blazor circuits. Never includes credentials.</summary>
public sealed class RealtimePublisher(IHubContext<MonitoringHub> hub, LiveUpdates live, ILogger<RealtimePublisher> logger) : IRealtimePublisher
{
    public async Task ServerUpdatedAsync(ServerLiveState state, CancellationToken ct = default)
    {
        live.RaiseServer(state);
        try
        {
            var dto = LiveDto.From(state);
            await hub.Clients.All.SendAsync("serverUpdated", dto, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "SignalR broadcast failed");
        }
    }

    public async Task AlertChangedAsync(Alert alert, CancellationToken ct = default)
    {
        live.RaiseAlert(alert);
        try
        {
            await hub.Clients.All.SendAsync("alertChanged", new
            {
                alert.Id, alert.ServerId, alert.Title, alert.Message, Severity = alert.Severity.ToString(), Status = alert.Status.ToString(),
                alert.Value, alert.Threshold, alert.FirstSeenAt, alert.LastSeenAt
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "SignalR broadcast failed");
        }
    }
}

public static class LiveDto
{
    public static object From(ServerLiveState s) => new
    {
        s.ServerId, s.Name, s.Host, OperatingSystem = s.OperatingSystem.ToString(), Status = s.Status.ToString(),
        ConnectionState = s.ConnectionState.ToString(), s.LastError, s.LastSeenAt, s.OsVersion, s.OpenAlerts,
        Metrics = s.Sample?.Snapshot is { } m ? new
        {
            m.Timestamp, m.CpuPercent, m.CpuCores, m.Load1, m.Load5, m.Load15, MemPercent = Math.Round(m.MemPercent, 2), m.MemTotal, m.MemUsed,
            DiskPercent = Math.Round(m.DiskPercent, 2), m.NetRxBytesPerSec, m.NetTxBytesPerSec, m.DiskReadBytesPerSec, m.DiskWriteBytesPerSec,
            m.TcpConnections, m.ProcessCount, m.UptimeSeconds
        } : null,
        s.Icmp, s.Tcp, s.Http
    };
}

public sealed class ToastService
{
    public sealed record Toast(Guid Id, string Message, string Kind, DateTimeOffset At);

    public event Action? Changed;
    public List<Toast> Items { get; } = [];

    public void Success(string message) => Add(message, "success");
    public void Error(string message) => Add(message, "error");
    public void Info(string message) => Add(message, "info");
    public void Warning(string message) => Add(message, "warning");

    private void Add(string message, string kind)
    {
        var toast = new Toast(Guid.NewGuid(), message, kind, DateTimeOffset.UtcNow);
        lock (Items) Items.Add(toast);
        Changed?.Invoke();
        _ = Task.Delay(kind == "error" ? 8000 : 4500).ContinueWith(_ => Dismiss(toast.Id));
    }

    public void Dismiss(Guid id)
    {
        lock (Items) Items.RemoveAll(t => t.Id == id);
        Changed?.Invoke();
    }
}
