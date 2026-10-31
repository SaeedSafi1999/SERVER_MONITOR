using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Infrastructure;
using ServerMonitor.Infrastructure.Persistence;
using ServerMonitor.Web.Components;
using ServerMonitor.Web.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// ---------------- UI ----------------
builder.Services.AddRazorComponents().AddInteractiveServerComponents(o =>
{
    o.DetailedErrors = builder.Environment.IsDevelopment();
    o.DisconnectedCircuitMaxRetained = 50;
});
builder.Services.AddSignalR(o => o.MaximumReceiveMessageSize = 64 * 1024);
builder.Services.AddHttpContextAccessor();
builder.Services.AddCascadingAuthenticationState();

// ---------------- Monitoring core ----------------
builder.Services.AddServerMonitorInfrastructure(builder.Configuration);
builder.Services.AddSingleton<LiveUpdates>();
builder.Services.AddSingleton<IRealtimePublisher, RealtimePublisher>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<CurrentUser>());
builder.Services.AddScoped<CircuitHandler, UserCircuitHandler>();
builder.Services.AddScoped<ToastService>();

// ---------------- Identity / auth ----------------
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(o =>
    {
        o.Password.RequiredLength = 12;
        o.Password.RequireNonAlphanumeric = true;
        o.Password.RequireUppercase = true;
        o.Password.RequireLowercase = true;
        o.Password.RequireDigit = true;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.Lockout.AllowedForNewUsers = true;
        o.User.RequireUniqueEmail = true;
        o.SignIn.RequireConfirmedAccount = false;
    })
    .AddEntityFrameworkStores<MonitorDbContext>()
    .AddDefaultTokenProviders();

// Role/permission changes and "sign out everywhere" take effect within a minute.
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));
builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "sm.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
    o.LoginPath = "/login";
    o.AccessDeniedPath = "/access-denied";
    // APIs get status codes instead of login redirects.
    o.Events.OnRedirectToLogin = ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/hubs")) ctx.Response.StatusCode = 401;
        else ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    };
    o.Events.OnRedirectToAccessDenied = ctx =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.StatusCode = 403;
        else ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    };
});
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("Operator", p => p.RequireRole(Roles.Admin, Roles.Operator));
    o.AddPolicy("Admin", p => p.RequireRole(Roles.Admin));
});
builder.Services.AddAntiforgery(o => o.Cookie.Name = "sm.csrf");

// ---------------- Rate limiting ----------------
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("api", ctx => RateLimitPartition.GetTokenBucketLimiter(
        ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new TokenBucketRateLimiterOptions { TokenLimit = 120, TokensPerPeriod = 60, ReplenishmentPeriod = TimeSpan.FromSeconds(30), QueueLimit = 0 }));
});

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Trust only explicitly configured proxies (e.g. the reverse proxy container).
    foreach (var proxy in builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
        if (System.Net.IPAddress.TryParse(proxy, out var ip)) o.KnownProxies.Add(ip);
});

builder.Services.AddHsts(o => { o.MaxAge = TimeSpan.FromDays(180); o.IncludeSubDomains = true; });

var app = builder.Build();

await DbInitializer.InitializeAsync(app.Services, app.Configuration, app.Logger);

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
if (app.Configuration.GetValue("Https:Redirect", true)) app.UseHttpsRedirection();

// Security headers (CSP allows the pinned chart CDN and Blazor's websocket).
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    h["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; style-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net https://fonts.googleapis.com; " +
        "font-src 'self' https://fonts.gstatic.com https://cdn.jsdelivr.net; img-src 'self' data:; connect-src 'self' ws: wss:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next();
});

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.MapHub<MonitoringHub>("/hubs/monitoring");
app.MapServerMonitorApi();

app.Run();
