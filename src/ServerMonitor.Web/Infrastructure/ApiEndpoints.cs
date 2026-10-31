using System.ComponentModel.DataAnnotations;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Domain;
using ServerMonitor.Infrastructure.Persistence;
using ServerMonitor.Infrastructure.Services;

namespace ServerMonitor.Web.Infrastructure;

public static class ApiEndpoints
{
    private const string CsrfHeader = "X-CSRF";

    public static void MapServerMonitorApi(this WebApplication app)
    {
        // ---------------- account ----------------
        app.MapPost("/account/logout", async (SignInManager<ApplicationUser> signIn, IAuditService audit, [FromForm] string? returnUrl) =>
        {
            await audit.WriteAsync("auth.logout");
            await signIn.SignOutAsync();
            return Results.LocalRedirect("/login");
        }).RequireAuthorization();

        // ---------------- REST API v1 ----------------
        var api = app.MapGroup("/api/v1").RequireAuthorization().RequireRateLimiting("api");

        // State-changing calls must carry a custom header: browsers cannot add it cross-site without a
        // CORS preflight (and CORS is not enabled), which blocks CSRF against cookie-authenticated callers.
        api.AddEndpointFilter(async (ctx, next) =>
        {
            var method = ctx.HttpContext.Request.Method;
            if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !ctx.HttpContext.Request.Headers.ContainsKey(CsrfHeader))
                return Results.Problem($"Missing {CsrfHeader} header.", statusCode: 400);
            try
            {
                return await next(ctx);
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(ex.Message, statusCode: 403);
            }
            catch (ValidationException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["error"] = [ex.Message] });
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });

        api.MapGet("/servers", async (ServerManagementService svc, ILiveStateCache cache) =>
            (await svc.ListAsync()).Select(s => new
            {
                s.Id, s.Name, s.Host, OperatingSystem = s.OperatingSystem.ToString(), Protocol = s.Protocol.ToString(), s.ManagementPort,
                Status = s.Status.ToString(), ConnectionState = s.ConnectionState.ToString(), s.LastSeenAt, s.IsMonitoringEnabled,
                s.MonitoringIntervalSeconds, Tags = s.TagList, s.Location, Group = s.Group?.Name, s.OsVersion
                // credentials are intentionally never serialised
            }));

        api.MapGet("/servers/{id:int}/live", (int id, ILiveStateCache cache) =>
            cache.Get(id) is { } s ? Results.Ok(LiveDto.From(s)) : Results.NotFound());

        api.MapGet("/servers/{id:int}/metrics", async (int id, DateTimeOffset? from, DateTimeOffset? to, MetricsQueryService q) =>
        {
            var (f, t) = Range(from, to);
            var series = await q.MetricSeriesAsync(id, f, t, Enum.GetValues<MetricSeries>());
            return Results.Ok(series.ToDictionary(k => k.Key.ToString(), v => v.Value));
        });

        api.MapGet("/servers/{id:int}/latency", async (int id, ProbeType? type, DateTimeOffset? from, DateTimeOffset? to, MetricsQueryService q) =>
        {
            var (f, t) = Range(from, to);
            var (series, loss, stats) = await q.LatencyAsync(id, type ?? ProbeType.Icmp, f, t);
            return Results.Ok(new { stats, stats.PacketLossPercent, series, loss });
        });

        api.MapGet("/servers/{id:int}/http", async (int id, DateTimeOffset? from, DateTimeOffset? to, MetricsQueryService q) =>
        {
            var (f, t) = Range(from, to);
            return Results.Ok(await q.HttpPhasesAsync(id, f, t));
        });

        api.MapGet("/servers/{id:int}/availability", async (int id, DateTimeOffset? from, DateTimeOffset? to, MetricsQueryService q) =>
        {
            var (f, t) = Range(from, to, TimeSpan.FromDays(30));
            var (report, segments) = await q.AvailabilityAsync(id, f, t);
            return Results.Ok(new { report, report.SlaMet, segments });
        });

        api.MapGet("/servers/{id:int}/export/metrics.csv", async (int id, DateTimeOffset? from, DateTimeOffset? to, MetricsQueryService q) =>
        {
            var (f, t) = Range(from, to);
            return Results.File(Encoding.UTF8.GetBytes(await q.ExportMetricsCsvAsync(id, f, t)), "text/csv", $"server-{id}-metrics.csv");
        });

        api.MapGet("/servers/{id:int}/export/probes.csv", async (int id, DateTimeOffset? from, DateTimeOffset? to, MetricsQueryService q) =>
        {
            var (f, t) = Range(from, to);
            return Results.File(Encoding.UTF8.GetBytes(await q.ExportProbesCsvAsync(id, f, t)), "text/csv", $"server-{id}-probes.csv");
        });

        api.MapGet("/alerts", async (AlertStatus? status, AlertSeverity? severity, int? serverId, AlertManagementService svc) =>
            (await svc.ListAsync(new AlertFilter(status, severity, serverId))).Select(a => new
            {
                a.Id, a.ServerId, Server = a.Server?.Name, a.Title, a.Message, Severity = a.Severity.ToString(), Status = a.Status.ToString(),
                a.Value, a.Threshold, a.FirstSeenAt, a.LastSeenAt, a.OccurrenceCount, a.AcknowledgedBy, a.ResolvedBy, a.ResolvedAt
            }));

        api.MapPost("/alerts/{id:long}/acknowledge", async (long id, AlertManagementService svc) =>
        {
            await svc.AcknowledgeAsync(id);
            return Results.NoContent();
        });

        api.MapPost("/alerts/{id:long}/resolve", async (long id, AlertManagementService svc) =>
        {
            await svc.ResolveAsync(id);
            return Results.NoContent();
        });

        api.MapGet("/reports/sla", async (DateTimeOffset? from, DateTimeOffset? to, MetricsQueryService q) =>
        {
            var (f, t) = Range(from, to, TimeSpan.FromDays(30));
            return (await q.SlaReportAsync(f, t)).Select(r => new { r.Server.Id, r.Server.Name, r.Report, r.Report.SlaMet });
        });

        api.MapGet("/reports/sla.csv", async (DateTimeOffset? from, DateTimeOffset? to, MetricsQueryService q) =>
        {
            var (f, t) = Range(from, to, TimeSpan.FromDays(30));
            var sb = new StringBuilder("server,uptime_percent,sla_target,sla_met,downtime_seconds,incidents,mttr_seconds,mtbf_seconds,unknown_seconds\n");
            foreach (var (server, r) in await q.SlaReportAsync(f, t))
                sb.AppendLine(string.Join(',', MetricsQueryService.Csv(server.Name), r.UptimePercent.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture),
                    r.SlaTarget.ToString(System.Globalization.CultureInfo.InvariantCulture), r.SlaMet, (long)r.Downtime.TotalSeconds, r.Incidents,
                    (long?)r.Mttr?.TotalSeconds, (long?)r.Mtbf?.TotalSeconds, (long)r.Unknown.TotalSeconds));
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "sla-report.csv");
        });

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
    }

    private static (DateTimeOffset From, DateTimeOffset To) Range(DateTimeOffset? from, DateTimeOffset? to, TimeSpan? defaultSpan = null)
    {
        var t = to?.ToUniversalTime() ?? DateTimeOffset.UtcNow;
        var f = from?.ToUniversalTime() ?? t - (defaultSpan ?? TimeSpan.FromHours(1));
        if (f >= t) throw new ValidationException("'from' must be before 'to'.");
        if (t - f > TimeSpan.FromDays(400)) throw new ValidationException("Range too large (max 400 days).");
        return (f, t);
    }
}
