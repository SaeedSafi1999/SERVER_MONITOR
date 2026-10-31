using System.Globalization;
using System.Text.Json;
using ServerMonitor.Application.Models;
using ServerMonitor.Domain;
using ServerMonitor.Domain.Entities;

namespace ServerMonitor.Infrastructure.Adapters.Windows;

/// <summary>Parses JSON emitted by <see cref="WindowsScripts"/>. Pure functions, unit-tested.</summary>
public static class WindowsParsers
{
    /// <summary>PowerShell 5.1 collapses one-element arrays to objects and omits empty ones; normalise.</summary>
    public static IEnumerable<JsonElement> Array(JsonElement e, string property)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(property, out var v)) return [];
        return AsArray(v);
    }

    public static IEnumerable<JsonElement> AsArray(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Array => v.EnumerateArray().ToList(),
        JsonValueKind.Object => [v],
        _ => []
    };

    private static double D(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0
        } : 0;

    private static long L(JsonElement e, string p) => (long)D(e, p);
    private static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool B(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? Date(JsonElement e, string p) =>
        DateTimeOffset.TryParse(Str(e, p), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

    /// <summary>Perf counter instance names replace these characters in adapter descriptions.</summary>
    public static string NormalizeInstance(string s) =>
        s.Replace('(', '[').Replace(')', ']').Replace('#', '_').Replace('/', '_').Replace('\\', '_').Trim().ToLowerInvariant();

    public static HostSample ParseMetrics(int serverId, string json, DateTimeOffset timestamp)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var a = r.GetProperty("a");
        var b = r.GetProperty("b");

        // CPU: PERF_100NSEC_TIMER_INV — raw value accumulates idle time.
        var cpuA = Array(a, "cpu").ToDictionary(x => Str(x, "Name") ?? "", x => (Idle: D(x, "PercentProcessorTime"), Ts: D(x, "Timestamp_Sys100NS")));
        double Usage(string name, JsonElement x)
        {
            if (!cpuA.TryGetValue(name, out var prev)) return 0;
            var dt = D(x, "Timestamp_Sys100NS") - prev.Ts;
            if (dt <= 0) return 0;
            return Math.Clamp(100.0 * (1 - (D(x, "PercentProcessorTime") - prev.Idle) / dt), 0, 100);
        }
        double total = 0;
        var perCore = new List<(int Index, double Value)>();
        foreach (var x in Array(b, "cpu"))
        {
            var name = Str(x, "Name") ?? "";
            var u = Math.Round(Usage(name, x), 1);
            if (name == "_Total") total = u;
            else if (int.TryParse(name.Split(',').Last(), out var idx)) perCore.Add((idx, u));
        }
        var cores = perCore.OrderBy(c => c.Index).Select(c => c.Value).ToArray();

        // Disk I/O per physical disk
        var diskA = Array(a, "disk").ToDictionary(x => Str(x, "Name") ?? "");
        var devices = new List<DiskDeviceIo>();
        foreach (var x in Array(b, "disk"))
        {
            var name = Str(x, "Name") ?? "";
            if (!diskA.TryGetValue(name, out var p)) continue;
            var freq = D(x, "Frequency_PerfTime");
            var secs = freq > 0 ? (D(x, "Timestamp_PerfTime") - D(p, "Timestamp_PerfTime")) / freq : 0;
            if (secs <= 0) continue;
            double Rate(string k) => Math.Max(0, (D(x, k) - D(p, k)) / secs);
            double? Latency(string k)
            {
                var dBase = D(x, k + "_Base") - D(p, k + "_Base");
                if (dBase <= 0 || freq <= 0) return null;
                return 1000.0 * ((D(x, k) - D(p, k)) / freq) / dBase;
            }
            devices.Add(new DiskDeviceIo(name, Rate("DiskReadBytesPersec"), Rate("DiskWriteBytesPersec"), Latency("AvgDisksecPerRead"), Latency("AvgDisksecPerWrite")));
        }

        // Network rates from perf counters; totals/state from Get-NetAdapter.
        var netA = Array(a, "net").ToDictionary(x => Str(x, "Name") ?? "");
        var netRates = new Dictionary<string, (double Rx, double Tx)>();
        foreach (var x in Array(b, "net"))
        {
            var name = Str(x, "Name") ?? "";
            if (!netA.TryGetValue(name, out var p)) continue;
            var freq = D(x, "Frequency_PerfTime");
            var secs = freq > 0 ? (D(x, "Timestamp_PerfTime") - D(p, "Timestamp_PerfTime")) / freq : 0;
            if (secs <= 0) continue;
            netRates[NormalizeInstance(name)] = (Math.Max(0, (D(x, "BytesReceivedPersec") - D(p, "BytesReceivedPersec")) / secs),
                Math.Max(0, (D(x, "BytesSentPersec") - D(p, "BytesSentPersec")) / secs));
        }
        var interfaces = new List<NetworkInterfaceInfo>();
        foreach (var x in Array(r, "adapters"))
        {
            var desc = NormalizeInstance(Str(x, "d") ?? "");
            var rate = netRates.GetValueOrDefault(desc);
            var speed = L(x, "sp");
            interfaces.Add(new NetworkInterfaceInfo(Str(x, "n") ?? desc, B(x, "up"), L(x, "rx"), L(x, "tx"), rate.Rx, rate.Tx,
                L(x, "re"), L(x, "te"), L(x, "rd"), L(x, "td"), speed > 0 ? speed / 1_000_000 : null));
        }

        // Volumes
        var partitions = Array(r, "disks").Select(x =>
        {
            var size = L(x, "Size");
            var free = L(x, "FreeSpace");
            var id = Str(x, "DeviceID") ?? "?";
            return new DiskPartitionInfo(id + "\\", id, Str(x, "FileSystem") ?? "", size, size - free, free);
        }).Where(p => p.TotalBytes > 0).ToList();

        // TCP
        var tcpStates = Array(r, "tcp").ToDictionary(x => (Str(x, "s") ?? "?").ToUpperInvariant(), x => (int)L(x, "c"));
        var listening = Array(r, "listen").Select(x => new ListeningPortInfo("TCP", Str(x, "a") ?? "", (int)L(x, "p")))
            .DistinctBy(l => (l.Address, l.Port)).OrderBy(l => l.Port).ToList();

        var memTotal = L(r, "memTotalKb") * 1024;
        var memFree = L(r, "memFreeKb") * 1024;
        var pageFiles = Array(r, "page").ToList();
        var boot = Date(r, "boot");
        var physical = interfaces.Where(i => !i.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)).ToList();

        var snapshot = new MetricSnapshot
        {
            ServerId = serverId,
            Timestamp = timestamp,
            CpuPercent = total,
            CpuCores = (int)L(r, "cores") is > 0 and var c ? c : cores.Length,
            PerCoreJson = JsonSerializer.Serialize(cores),
            // Windows has no load average; processor queue length is the closest equivalent.
            Load1 = D(r, "queue"),
            CpuFrequencyMhz = D(r, "mhz") is > 0 and var mhz ? mhz : null,
            CpuTemperatureC = r.TryGetProperty("temp", out var t) && t.ValueKind == JsonValueKind.Number ? Math.Round(t.GetDouble(), 1) : null,
            MemTotal = memTotal,
            MemFree = memFree,
            MemAvailable = memFree,
            MemUsed = Math.Max(0, memTotal - memFree),
            SwapTotal = pageFiles.Sum(p => L(p, "AllocatedBaseSize")) * 1024 * 1024,
            SwapUsed = pageFiles.Sum(p => L(p, "CurrentUsage")) * 1024 * 1024,
            DiskTotal = partitions.Sum(p => p.TotalBytes),
            DiskUsed = partitions.Sum(p => p.UsedBytes),
            DiskReadBytesPerSec = devices.Sum(d => d.ReadBytesPerSec),
            DiskWriteBytesPerSec = devices.Sum(d => d.WriteBytesPerSec),
            DiskReadLatencyMs = AverageOrNull(devices.Select(d => d.ReadLatencyMs)),
            DiskWriteLatencyMs = AverageOrNull(devices.Select(d => d.WriteLatencyMs)),
            NetRxBytesPerSec = netRates.Values.Sum(v => v.Rx),
            NetTxBytesPerSec = netRates.Values.Sum(v => v.Tx),
            NetRxTotalBytes = physical.Sum(i => i.RxBytes),
            NetTxTotalBytes = physical.Sum(i => i.TxBytes),
            NetErrors = physical.Sum(i => i.RxErrors + i.TxErrors),
            NetDrops = physical.Sum(i => i.RxDrops + i.TxDrops),
            TcpConnections = tcpStates.Where(k => k.Key != "LISTEN").Sum(k => k.Value),
            TcpEstablished = tcpStates.GetValueOrDefault("ESTABLISHED"),
            ProcessCount = (int)L(r, "procs"),
            ThreadCount = (int)L(r, "threads"),
            OpenFileDescriptors = L(r, "handles"),
            ActiveSessions = (int)L(r, "sessions"),
            UptimeSeconds = boot is null ? 0 : (long)(timestamp - boot.Value).TotalSeconds
        };

        return new HostSample
        {
            Snapshot = snapshot,
            Hostname = Str(r, "host"),
            OsVersion = Str(r, "os"),
            PerCoreUsage = cores,
            Partitions = partitions,
            DiskDevices = devices,
            Interfaces = interfaces,
            TcpStates = tcpStates,
            ListeningPorts = listening
        };
    }

    private static double? AverageOrNull(IEnumerable<double?> values)
    {
        var v = values.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        return v.Count == 0 ? null : Math.Round(v.Average(), 3);
    }

    public static IReadOnlyList<ProcessInfo> ParseProcesses(string json, int top)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var memTotal = L(r, "memTotalKb") * 1024;
        var a = Array(r, "a").GroupBy(x => (int)L(x, "IDProcess")).ToDictionary(g => g.Key, g => g.First());
        var list = new List<ProcessInfo>();
        foreach (var x in Array(r, "b"))
        {
            var pid = (int)L(x, "IDProcess");
            if (pid == 0) continue;
            double cpu = 0;
            if (a.TryGetValue(pid, out var p))
            {
                var dt = D(x, "Timestamp_Sys100NS") - D(p, "Timestamp_Sys100NS");
                if (dt > 0) cpu = Math.Max(0, 100.0 * (D(x, "PercentProcessorTime") - D(p, "PercentProcessorTime")) / dt);
            }
            var ws = L(x, "WorkingSetPrivate");
            var name = Str(x, "Name") ?? "?";
            var hash = name.IndexOf('#');
            list.Add(new ProcessInfo(pid, hash > 0 ? name[..hash] : name, null, Math.Round(cpu, 1),
                memTotal > 0 ? Math.Round(100.0 * ws / memTotal, 2) : 0, ws, (int)L(x, "ThreadCount")));
        }
        return list.OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.RssBytes).Take(top).ToList();
    }

    public static IReadOnlyList<ServiceStatusInfo> ParseServices(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = JsonDocument.Parse(json);
        return AsArray(doc.RootElement).Select(x =>
        {
            var state = (Str(x, "State") ?? "") switch
            {
                "Running" => ServiceState.Running,
                "Stopped" => ServiceState.Stopped,
                "Start Pending" => ServiceState.Starting,
                "Stop Pending" => ServiceState.Stopping,
                "Paused" => ServiceState.Stopped,
                _ => ServiceState.Unknown
            };
            var exit = L(x, "ExitCode");
            return new ServiceStatusInfo(Str(x, "Name") ?? "?", Str(x, "DisplayName"), state, Str(x, "State"), Str(x, "StartMode"), null,
                exit is 0 or 1077 ? null : $"Exit code {exit}");
        }).ToList();
    }

    public static SecurityScanResult ParseSecurity(string json, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        static LoginRecord Login(JsonElement x, bool ok)
        {
            var ip = Str(x, "ip");
            if (ip is "-" or "") ip = null;
            var type = Str(x, "lt");
            return new LoginRecord(Date(x, "t") ?? DateTimeOffset.UtcNow, Str(x, "u"), ip, ok,
                ok ? $"Logon type {type} succeeded for {Str(x, "u")}" : $"Logon failed for {Str(x, "u")} (type {type}) from {ip ?? "local"}");
        }

        var fw = Array(r, "fw").ToList();
        var warnings = new List<string>();
        if (r.TryGetProperty("secReadable", out var sr) && sr.ValueKind == JsonValueKind.False)
            warnings.Add("Security event log is not readable — add the monitoring account to 'Event Log Readers'.");

        return new SecurityScanResult
        {
            FailedLogins = Array(r, "failed").Select(x => Login(x, false)).ToList(),
            SuccessfulLogins = Array(r, "success").Select(x => Login(x, true)).ToList(),
            FirewallActive = fw.Count == 0 ? null : fw.All(x => B(x, "e")),
            FirewallDetails = fw.Count == 0 ? "Unknown" : string.Join("; ", fw.Select(x => $"{Str(x, "n")}: {(B(x, "e") ? "on" : "OFF")}")),
            PendingUpdates = r.TryGetProperty("updates", out var u) && u.ValueKind == JsonValueKind.Number ? u.GetInt32() : null,
            PendingSecurityUpdates = r.TryGetProperty("secUpdates", out var su) && su.ValueKind == JsonValueKind.Number ? su.GetInt32() : null,
            RebootRequired = B(r, "reboot"),
            DiskEncryption = Str(r, "bitlocker") is { Length: > 0 } bl ? "BitLocker: " + bl : "Unknown (requires admin rights)",
            ListeningPorts = Array(r, "listen").Select(x => new ListeningPortInfo("TCP", Str(x, "a") ?? "", (int)L(x, "p")))
                .DistinctBy(l => (l.Address, l.Port)).OrderBy(l => l.Port).ToList(),
            Warnings = warnings,
            ScannedAt = now
        };
    }

    public static IReadOnlyList<LogEntry> ParseLogs(string sourceKey, string json, DateTimeOffset? since)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        using var doc = JsonDocument.Parse(json);
        return AsArray(doc.RootElement).Select(x =>
        {
            var level = (int)L(x, "l") switch
            {
                1 => LogLevelKind.Critical,
                2 => LogLevelKind.Error,
                3 => LogLevelKind.Warning,
                5 => LogLevelKind.Debug,
                _ => LogLevelKind.Info
            };
            return new LogEntry(Date(x, "t") ?? DateTimeOffset.UtcNow, level, $"{Str(x, "p")} ({L(x, "i")})", Str(x, "m") ?? "");
        }).Where(e => since is null || e.Timestamp > since).OrderBy(e => e.Timestamp).ToList();
    }
}
