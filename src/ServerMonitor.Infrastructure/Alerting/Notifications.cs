using System.Net;
using System.Net.Mail;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Domain;
using ServerMonitor.Domain.Entities;

namespace ServerMonitor.Infrastructure.Alerting;

public interface INotificationQueue
{
    void Enqueue(NotificationMessage message);
    IAsyncEnumerable<NotificationMessage> ReadAllAsync(CancellationToken ct);
}

/// <summary>Bounded in-memory queue so a slow SMTP server never stalls metric collection.</summary>
public sealed class NotificationQueue : INotificationQueue
{
    private readonly Channel<NotificationMessage> _channel = Channel.CreateBounded<NotificationMessage>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });

    public void Enqueue(NotificationMessage message) => _channel.Writer.TryWrite(message);

    public IAsyncEnumerable<NotificationMessage> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}

/// <summary>Delivers queued notifications to every enabled channel whose minimum severity matches.</summary>
public sealed class NotificationWorker(INotificationQueue queue, IServiceScopeFactory scopes, IEnumerable<INotificationSender> senders, ILogger<NotificationWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<IMonitorDbContext>();
                var channels = await db.NotificationChannels.AsNoTracking()
                    .Where(c => c.IsEnabled && c.MinimumSeverity <= message.Severity)
                    .ToListAsync(stoppingToken);
                foreach (var channel in channels.Where(c => !message.IsResolution || c.NotifyOnResolve))
                {
                    var sender = senders.FirstOrDefault(s => s.Type == channel.Type);
                    if (sender is null) continue;
                    try
                    {
                        await Monitoring.RetryPolicy.ExecuteAsync(async ct => { await sender.SendAsync(channel, message, ct); return true; },
                            3, TimeSpan.FromSeconds(2), stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Notification via channel {Channel} ({Type}) failed", channel.Name, channel.Type);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Notification dispatch failed");
            }
        }
    }
}

// ---------- Channel configurations (stored encrypted as JSON) ----------

public sealed class EmailChannelConfig
{
    public string SmtpHost { get; set; } = "";
    public int SmtpPort { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public sealed class TelegramChannelConfig
{
    public string BotToken { get; set; } = "";
    public string ChatId { get; set; } = "";
}

public sealed class WebhookChannelConfig
{
    public string Url { get; set; } = "";
    /// <summary>When set, the body is signed: X-ServerMonitor-Signature: sha256=HEX(HMAC(secret, body)).</summary>
    public string? Secret { get; set; }
}

public sealed class ChannelConfigSerializer(ICredentialProtector protector)
{
    public T Read<T>(NotificationChannel channel) where T : new() =>
        JsonSerializer.Deserialize<T>(protector.Unprotect(channel.EncryptedConfig)) ?? new T();

    public string Write<T>(T config) => protector.Protect(JsonSerializer.Serialize(config));
}

public sealed class EmailNotificationSender(ChannelConfigSerializer configs) : INotificationSender
{
    public NotificationChannelType Type => NotificationChannelType.Email;

    public async Task SendAsync(NotificationChannel channel, NotificationMessage message, CancellationToken ct)
    {
        var cfg = configs.Read<EmailChannelConfig>(channel);
        using var client = new SmtpClient(cfg.SmtpHost, cfg.SmtpPort) { EnableSsl = cfg.UseSsl, DeliveryMethod = SmtpDeliveryMethod.Network };
        if (!string.IsNullOrEmpty(cfg.Username)) client.Credentials = new NetworkCredential(cfg.Username, cfg.Password);
        using var mail = new MailMessage { From = new MailAddress(cfg.From), Subject = $"[{message.Severity}] {message.Title}", Body = Format(message), IsBodyHtml = false };
        foreach (var to in cfg.To.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) mail.To.Add(to);
        await client.SendMailAsync(mail, ct);
    }

    private static string Format(NotificationMessage m) =>
        $"{m.Title}\n\n{m.Body}\n\nServer: {m.ServerName}\nSeverity: {m.Severity}\nTime (UTC): {m.Timestamp:yyyy-MM-dd HH:mm:ss}";
}

public sealed class TelegramNotificationSender(ChannelConfigSerializer configs, IHttpClientFactory httpFactory) : INotificationSender
{
    public NotificationChannelType Type => NotificationChannelType.Telegram;

    public async Task SendAsync(NotificationChannel channel, NotificationMessage message, CancellationToken ct)
    {
        var cfg = configs.Read<TelegramChannelConfig>(channel);
        var icon = message.IsResolution ? "✅" : message.Severity switch { AlertSeverity.Critical => "🔴", AlertSeverity.Warning => "🟠", _ => "🔵" };
        var text = $"{icon} <b>{WebUtility.HtmlEncode(message.Title)}</b>\n{WebUtility.HtmlEncode(message.Body)}\n<i>{message.Timestamp:yyyy-MM-dd HH:mm:ss} UTC</i>";
        var http = httpFactory.CreateClient("notifications");
        using var response = await http.PostAsJsonAsync($"https://api.telegram.org/bot{cfg.BotToken}/sendMessage",
            new { chat_id = cfg.ChatId, text, parse_mode = "HTML", disable_web_page_preview = true }, ct);
        if (!response.IsSuccessStatusCode)
            throw new IOException($"Telegram API returned {(int)response.StatusCode}"); // token intentionally not logged
    }
}

public sealed class WebhookNotificationSender(ChannelConfigSerializer configs, IHttpClientFactory httpFactory) : INotificationSender
{
    public NotificationChannelType Type => NotificationChannelType.Webhook;

    public async Task SendAsync(NotificationChannel channel, NotificationMessage message, CancellationToken ct)
    {
        var cfg = configs.Read<WebhookChannelConfig>(channel);
        var body = JsonSerializer.Serialize(new
        {
            title = message.Title, body = message.Body, severity = message.Severity.ToString(), server = message.ServerName,
            resolved = message.IsResolution, timestamp = message.Timestamp
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, cfg.Url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (!string.IsNullOrEmpty(cfg.Secret))
        {
            var sig = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(cfg.Secret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            request.Headers.Add("X-ServerMonitor-Signature", "sha256=" + sig);
        }
        var http = httpFactory.CreateClient("notifications");
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new IOException($"Webhook returned {(int)response.StatusCode}");
    }
}
