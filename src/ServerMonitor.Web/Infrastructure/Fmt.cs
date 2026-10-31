using System.Globalization;
using ServerMonitor.Domain;

namespace ServerMonitor.Web.Infrastructure;

/// <summary>Display formatting helpers.</summary>
public static class Fmt
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static string Bytes(double? bytes)
    {
        if (bytes is null) return "—";
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var v = bytes.Value;
        var i = 0;
        while (Math.Abs(v) >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return v.ToString(i == 0 ? "0" : "0.##", C) + " " + units[i];
    }

    public static string Rate(double? bytesPerSec)
    {
        if (bytesPerSec is null) return "—";
        var bits = bytesPerSec.Value * 8;
        string[] units = ["bps", "Kbps", "Mbps", "Gbps", "Tbps"];
        var i = 0;
        while (bits >= 1000 && i < units.Length - 1) { bits /= 1000; i++; }
        return bits.ToString("0.##", C) + " " + units[i];
    }

    public static string Pct(double? v) => v is null ? "—" : v.Value.ToString("0.#", C) + "%";
    public static string Ms(double? v) => v is null ? "—" : v.Value < 10 ? v.Value.ToString("0.##", C) + " ms" : v.Value.ToString("0.#", C) + " ms";
    public static string Num(double? v, string format = "0.##") => v is null ? "—" : v.Value.ToString(format, C);

    public static string Duration(TimeSpan? t)
    {
        if (t is null) return "—";
        var v = t.Value;
        if (v.TotalSeconds < 60) return $"{v.TotalSeconds:0}s";
        if (v.TotalMinutes < 60) return $"{(int)v.TotalMinutes}m {v.Seconds}s";
        if (v.TotalHours < 24) return $"{(int)v.TotalHours}h {v.Minutes}m";
        return $"{(int)v.TotalDays}d {v.Hours}h";
    }

    public static string Uptime(long seconds) => seconds <= 0 ? "—" : Duration(TimeSpan.FromSeconds(seconds));

    public static string Ago(DateTimeOffset? t)
    {
        if (t is null) return "never";
        var d = DateTimeOffset.UtcNow - t.Value;
        if (d.TotalSeconds < 5) return "just now";
        if (d.TotalSeconds < 60) return $"{d.TotalSeconds:0}s ago";
        if (d.TotalMinutes < 60) return $"{d.TotalMinutes:0}m ago";
        if (d.TotalHours < 24) return $"{d.TotalHours:0}h ago";
        return $"{d.TotalDays:0}d ago";
    }

    public static string Time(DateTimeOffset? t) => t is null ? "—" : t.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", C);

    public static string StatusClass(HealthStatus s) => s switch
    {
        HealthStatus.Healthy => "healthy",
        HealthStatus.Warning => "warning",
        HealthStatus.Critical => "critical",
        HealthStatus.Offline => "offline",
        _ => "unknown"
    };

    public static string SeverityClass(AlertSeverity s) => s switch
    {
        AlertSeverity.Critical => "critical",
        AlertSeverity.Warning => "warning",
        _ => "info"
    };

    public static string LevelClass(double? pct, double warn = 75, double crit = 90) =>
        pct is null ? "unknown" : pct >= crit ? "critical" : pct >= warn ? "warning" : "healthy";

    public static string ServiceClass(ServiceState s) => s switch
    {
        ServiceState.Running => "healthy",
        ServiceState.Starting or ServiceState.Stopping => "warning",
        ServiceState.Stopped or ServiceState.Failed => "critical",
        _ => "offline"
    };
}
