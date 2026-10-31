using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Domain;
using ServerMonitor.Domain.Entities;
using ServerMonitor.Infrastructure.Alerting;
using ServerMonitor.Infrastructure.Monitoring;

namespace ServerMonitor.Infrastructure.Services;

public sealed record AlertFilter(AlertStatus? Status = null, AlertSeverity? Severity = null, int? ServerId = null, DateTimeOffset? From = null, DateTimeOffset? To = null, int Take = 200);

public sealed class AlertManagementService(
    IMonitorDbContext db,
    IAuditService audit,
    ICurrentUser user,
    AlertEngine engine,
    IRealtimePublisher realtime,
    ChannelConfigSerializer channelConfigs,
    IEnumerable<INotificationSender> senders,
    ISettingsProvider settingsProvider)
{
    public async Task<List<Alert>> ListAsync(AlertFilter f, CancellationToken ct = default)
    {
        var q = db.Alerts.AsNoTracking().Include(a => a.Server).AsQueryable();
        if (f.Status is not null) q = q.Where(a => a.Status == f.Status);
        if (f.Severity is not null) q = q.Where(a => a.Severity == f.Severity);
        if (f.ServerId is not null) q = q.Where(a => a.ServerId == f.ServerId);
        if (f.From is not null) q = q.Where(a => a.LastSeenAt >= f.From);
        if (f.To is not null) q = q.Where(a => a.FirstSeenAt <= f.To);
        return await q.OrderBy(a => a.Status).ThenByDescending(a => a.Severity).ThenByDescending(a => a.LastSeenAt).Take(Math.Clamp(f.Take, 1, 2000)).ToListAsync(ct);
    }

    public Task<int> OpenCountAsync(CancellationToken ct = default) => db.Alerts.CountAsync(a => a.Status != AlertStatus.Resolved, ct);

    public async Task AcknowledgeAsync(long id, CancellationToken ct = default)
    {
        user.Demand(Roles.Admin, Roles.Operator);
        var a = await db.Alerts.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        if (a.Status != AlertStatus.Open) return;
        a.Status = AlertStatus.Acknowledged;
        a.AcknowledgedAt = DateTimeOffset.UtcNow;
        a.AcknowledgedBy = user.UserName;
        await db.SaveChangesAsync(ct);
        await realtime.AlertChangedAsync(a, ct);
        await audit.WriteAsync("alert.acknowledge", "Alert", id.ToString(), a.Title, ct);
    }

    public async Task ResolveAsync(long id, CancellationToken ct = default)
    {
        user.Demand(Roles.Admin, Roles.Operator);
        var a = await db.Alerts.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException();
        if (a.Status == AlertStatus.Resolved) return;
        a.Status = AlertStatus.Resolved;
        a.ResolvedAt = DateTimeOffset.UtcNow;
        a.ResolvedBy = user.UserName;
        await db.SaveChangesAsync(ct);
        await realtime.AlertChangedAsync(a, ct);
        await audit.WriteAsync("alert.resolve", "Alert", id.ToString(), a.Title, ct);
    }

    // ---------------- rules ----------------

    public Task<List<AlertRule>> RulesAsync(CancellationToken ct = default) =>
        db.AlertRules.AsNoTracking().Include(r => r.Server).OrderBy(r => r.ServerId).ThenBy(r => r.Metric).ThenBy(r => r.Severity).ToListAsync(ct);

    public async Task SaveRuleAsync(AlertRule input, CancellationToken ct = default)
    {
        user.Demand(Roles.Admin);
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 128) throw new ValidationException("Rule name is required.");
        if (input.ConsecutiveBreaches is < 1 or > 100) throw new ValidationException("Consecutive breaches must be 1–100.");
        if (input.CooldownMinutes is < 0 or > 10080) throw new ValidationException("Cooldown must be 0–10080 minutes.");
        var rule = input.Id == 0 ? new AlertRule { Name = input.Name } : await db.AlertRules.FirstOrDefaultAsync(r => r.Id == input.Id, ct) ?? throw new KeyNotFoundException();
        rule.Name = input.Name.Trim();
        rule.ServerId = input.ServerId;
        rule.Metric = input.Metric;
        rule.Operator = input.Operator;
        rule.Threshold = input.Threshold;
        rule.Severity = input.Severity;
        rule.ConsecutiveBreaches = input.ConsecutiveBreaches;
        rule.CooldownMinutes = input.CooldownMinutes;
        rule.IsEnabled = input.IsEnabled;
        if (input.Id == 0) db.AlertRules.Add(rule);
        await db.SaveChangesAsync(ct);
        engine.InvalidateRules();
        await audit.WriteAsync(input.Id == 0 ? "rule.create" : "rule.update", "AlertRule", rule.Id.ToString(),
            $"{rule.Name}: {rule.Metric} {rule.Operator} {rule.Threshold} ({rule.Severity})", ct);
    }

    public async Task DeleteRuleAsync(int id, CancellationToken ct = default)
    {
        user.Demand(Roles.Admin);
        var rule = await db.AlertRules.FindAsync([id], ct);
        if (rule is null) return;
        db.AlertRules.Remove(rule);
        await db.SaveChangesAsync(ct);
        engine.InvalidateRules();
        await audit.WriteAsync("rule.delete", "AlertRule", id.ToString(), rule.Name, ct);
    }

    // ---------------- channels ----------------

    public Task<List<NotificationChannel>> ChannelsAsync(CancellationToken ct = default) =>
        db.NotificationChannels.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);

    public T ReadChannelConfig<T>(NotificationChannel c) where T : new() => channelConfigs.Read<T>(c);

    public async Task SaveChannelAsync<T>(NotificationChannel input, T config, CancellationToken ct = default)
    {
        user.Demand(Roles.Admin);
        if (string.IsNullOrWhiteSpace(input.Name)) throw new ValidationException("Channel name is required.");
        if (config is WebhookChannelConfig w && (!Uri.TryCreate(w.Url, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http")))
            throw new ValidationException("Webhook URL must be an absolute http(s) URL.");
        var ch = input.Id == 0 ? new NotificationChannel { Name = input.Name, EncryptedConfig = "" } : await db.NotificationChannels.FirstOrDefaultAsync(c => c.Id == input.Id, ct) ?? throw new KeyNotFoundException();
        ch.Name = input.Name.Trim();
        ch.Type = input.Type;
        ch.IsEnabled = input.IsEnabled;
        ch.MinimumSeverity = input.MinimumSeverity;
        ch.NotifyOnResolve = input.NotifyOnResolve;
        // Secrets are write-only in the UI: an empty secret on update means "keep the stored one".
        if (input.Id != 0 && !string.IsNullOrEmpty(ch.EncryptedConfig))
        {
            switch (config)
            {
                case EmailChannelConfig e when string.IsNullOrEmpty(e.Password):
                    e.Password = channelConfigs.Read<EmailChannelConfig>(ch).Password; break;
                case TelegramChannelConfig t when string.IsNullOrEmpty(t.BotToken):
                    t.BotToken = channelConfigs.Read<TelegramChannelConfig>(ch).BotToken; break;
                case WebhookChannelConfig wh when string.IsNullOrEmpty(wh.Secret):
                    wh.Secret = channelConfigs.Read<WebhookChannelConfig>(ch).Secret; break;
            }
        }
        ch.EncryptedConfig = channelConfigs.Write(config);
        if (input.Id == 0) db.NotificationChannels.Add(ch);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(input.Id == 0 ? "channel.create" : "channel.update", "NotificationChannel", ch.Id.ToString(), $"{ch.Name} ({ch.Type})", ct);
    }

    public async Task DeleteChannelAsync(int id, CancellationToken ct = default)
    {
        user.Demand(Roles.Admin);
        var ch = await db.NotificationChannels.FindAsync([id], ct);
        if (ch is null) return;
        db.NotificationChannels.Remove(ch);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("channel.delete", "NotificationChannel", id.ToString(), ch.Name, ct);
    }

    public async Task<string> TestChannelAsync(int id, CancellationToken ct = default)
    {
        user.Demand(Roles.Admin);
        var ch = await db.NotificationChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new KeyNotFoundException();
        var sender = senders.First(s => s.Type == ch.Type);
        try
        {
            await sender.SendAsync(ch, new NotificationMessage("Test notification", "This is a test message from ServerMonitor.", AlertSeverity.Info, null, false, DateTimeOffset.UtcNow), ct);
            await audit.WriteAsync("channel.test", "NotificationChannel", id.ToString(), "OK", ct);
            return "Test notification sent.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await audit.WriteAsync("channel.test", "NotificationChannel", id.ToString(), "Failed: " + ex.Message, ct);
            return "Failed: " + ex.Message;
        }
    }

    // ---------------- settings ----------------

    public async Task<SystemSettings> GetSettingsAsync(CancellationToken ct = default) =>
        await db.SystemSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? new SystemSettings();

    public async Task SaveSettingsAsync(SystemSettings input, CancellationToken ct = default)
    {
        user.Demand(Roles.Admin);
        Validator.ValidateObject(input, new ValidationContext(input), true);
        var s = await db.SystemSettings.FirstOrDefaultAsync(ct);
        if (s is null) { s = new SystemSettings(); db.SystemSettings.Add(s); }
        s.MetricRetentionDays = Math.Clamp(input.MetricRetentionDays, 1, 3650);
        s.ProbeRetentionDays = Math.Clamp(input.ProbeRetentionDays, 1, 3650);
        s.AlertRetentionDays = Math.Clamp(input.AlertRetentionDays, 7, 3650);
        s.SecurityEventRetentionDays = Math.Clamp(input.SecurityEventRetentionDays, 1, 3650);
        s.AuditRetentionDays = Math.Clamp(input.AuditRetentionDays, 30, 3650);
        s.SlaTargetPercent = Math.Clamp(input.SlaTargetPercent, 50, 100);
        s.IcmpPacketsPerRound = Math.Clamp(input.IcmpPacketsPerRound, 1, 20);
        s.ProbeTimeoutMs = Math.Clamp(input.ProbeTimeoutMs, 200, 30000);
        s.MaxConcurrentCollections = Math.Clamp(input.MaxConcurrentCollections, 1, 256);
        s.SecurityScanIntervalMinutes = Math.Clamp(input.SecurityScanIntervalMinutes, 1, 1440);
        s.CertificateWarningDays = Math.Clamp(input.CertificateWarningDays, 1, 365);
        await db.SaveChangesAsync(ct);
        settingsProvider.Invalidate();
        await audit.WriteAsync("settings.update", "SystemSettings", "1", null, ct);
    }

    // ---------------- audit / security events ----------------

    public Task<List<AuditLog>> AuditAsync(string? search, int take, CancellationToken ct = default)
    {
        user.Demand(Roles.Admin);
        var q = db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(a => a.Action.Contains(search) || (a.UserName != null && a.UserName.Contains(search)) || (a.Details != null && a.Details.Contains(search)));
        return q.OrderByDescending(a => a.Timestamp).Take(Math.Clamp(take, 1, 2000)).ToListAsync(ct);
    }

    public Task<List<SecurityEvent>> SecurityEventsAsync(int? serverId, SecurityEventType? type, DateTimeOffset from, int take, CancellationToken ct = default)
    {
        var q = db.SecurityEvents.AsNoTracking().Where(e => e.Timestamp >= from);
        if (serverId is not null) q = q.Where(e => e.ServerId == serverId);
        if (type is not null) q = q.Where(e => e.Type == type);
        return q.OrderByDescending(e => e.Timestamp).Take(Math.Clamp(take, 1, 5000)).ToListAsync(ct);
    }
}
