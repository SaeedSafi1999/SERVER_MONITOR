using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ServerMonitor.Application.Models;
using ServerMonitor.Domain;
using ServerMonitor.Domain.Entities;

namespace ServerMonitor.Infrastructure.Adapters.Linux;

/// <summary>Pure parsers for Linux /proc and tool output. No I/O — covered by unit tests.</summary>
public static partial class LinuxParsers
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Splits script output on "@@name" marker lines.</summary>
    public static Dictionary<string, List<string>> Sections(string output)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<string>? current = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                current = [];
                result[line[2..].Trim()] = current;
                continue;
            }
            current?.Add(line);
        }
        return result;
    }

    private static List<string> Get(Dictionary<string, List<string>> s, string key) => s.TryGetValue(key, out var v) ? v : [];

    // ---------------- CPU ----------------

    public readonly record struct CpuTimes(long Idle, long Total);

    /// <summary>Parses "cpu"/"cpuN" lines of /proc/stat. Key "cpu" is the aggregate.</summary>
    public static Dictionary<string, CpuTimes> ParseProcStat(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, CpuTimes>();
        foreach (var line in lines)
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5 || !parts[0].StartsWith("cpu", StringComparison.Ordinal)) continue;
            var v = parts.Skip(1).Select(p => long.TryParse(p, out var n) ? n : 0).ToArray();
            // user nice system idle iowait irq softirq steal [guest guest_nice] — guest is already counted in user.
            long total = v.Take(Math.Min(8, v.Length)).Sum();
            long idle = v[3] + (v.Length > 4 ? v[4] : 0);
            result[parts[0]] = new CpuTimes(idle, total);
        }
        return result;
    }

    public static double CpuUsage(CpuTimes a, CpuTimes b)
    {
        var dTotal = b.Total - a.Total;
        var dIdle = b.Idle - a.Idle;
        if (dTotal <= 0) return 0;
        return Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
    }

    // ---------------- Memory ----------------

    public static Dictionary<string, long> ParseMeminfo(IEnumerable<string> lines)
    {
        var d = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            var rest = line[(idx + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (rest.Length == 0 || !long.TryParse(rest[0], out var value)) continue;
            var multiplier = rest.Length > 1 && rest[1] == "kB" ? 1024 : 1;
            d[line[..idx]] = value * multiplier;
        }
        return d;
    }

    // ---------------- Network ----------------

    public readonly record struct NetCounters(long RxBytes, long RxErrs, long RxDrop, long TxBytes, long TxErrs, long TxDrop);

    public static Dictionary<string, NetCounters> ParseNetDev(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, NetCounters>();
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var name = line[..colon].Trim();
            if (name.Contains('|') || name.Length == 0) continue;
            var f = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => long.TryParse(x, out var n) ? n : 0).ToArray();
            if (f.Length < 12) continue;
            result[name] = new NetCounters(f[0], f[2], f[3], f[8], f[10], f[11]);
        }
        return result;
    }

    // ---------------- Disk ----------------

    public readonly record struct DiskCounters(long ReadsCompleted, long SectorsRead, long MsReading, long WritesCompleted, long SectorsWritten, long MsWriting);

    [GeneratedRegex(@"^(sd[a-z]+|vd[a-z]+|xvd[a-z]+|hd[a-z]+|nvme\d+n\d+|mmcblk\d+)$")]
    private static partial Regex WholeDiskRegex();

    /// <summary>Parses /proc/diskstats keeping only whole physical/virtual disks (no partitions, loop, dm, ram).</summary>
    public static Dictionary<string, DiskCounters> ParseDiskStats(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, DiskCounters>();
        foreach (var line in lines)
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 14 || !WholeDiskRegex().IsMatch(p[2])) continue;
            long L(int i) => long.TryParse(p[i], out var n) ? n : 0;
            result[p[2]] = new DiskCounters(L(3), L(5), L(6), L(7), L(9), L(10));
        }
        return result;
    }

    public static IReadOnlyList<DiskPartitionInfo> ParseDf(IEnumerable<string> lines)
    {
        var list = new List<DiskPartitionInfo>();
        var seenDevices = new HashSet<string>();
        foreach (var line in lines.Skip(1))
        {
            // Filesystem Type 1-blocks Used Available Capacity Mounted-on (mount may contain spaces)
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 7) continue;
            if (!long.TryParse(p[2], out var total) || !long.TryParse(p[3], out var used) || !long.TryParse(p[4], out var avail)) continue;
            if (total == 0) continue;
            var mount = string.Join(' ', p.Skip(6));
            // Bind mounts and btrfs subvolumes report the same device several times; count once.
            if (!seenDevices.Add(p[0])) continue;
            list.Add(new DiskPartitionInfo(mount, p[0], p[1], total, used, avail));
        }
        return list;
    }

    // ---------------- TCP ----------------

    private static readonly Dictionary<string, string> TcpStateNames = new()
    {
        ["01"] = "ESTABLISHED", ["02"] = "SYN_SENT", ["03"] = "SYN_RECV", ["04"] = "FIN_WAIT1", ["05"] = "FIN_WAIT2",
        ["06"] = "TIME_WAIT", ["07"] = "CLOSE", ["08"] = "CLOSE_WAIT", ["09"] = "LAST_ACK", ["0A"] = "LISTEN", ["0B"] = "CLOSING"
    };

    /// <summary>Input lines are "local_address state" from /proc/net/tcp{,6}.</summary>
    public static (Dictionary<string, int> States, List<ListeningPortInfo> Listening) ParseTcp(IEnumerable<string> lines)
    {
        var states = new Dictionary<string, int>();
        var listening = new List<ListeningPortInfo>();
        var seen = new HashSet<string>();
        foreach (var line in lines)
        {
            var p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2) continue;
            var name = TcpStateNames.GetValueOrDefault(p[1], p[1]);
            states[name] = states.GetValueOrDefault(name) + 1;
            if (p[1] == "0A" && DecodeProcAddress(p[0]) is { } ep && seen.Add(ep.Address + ":" + ep.Port))
                listening.Add(new ListeningPortInfo("TCP", ep.Address, ep.Port));
        }
        return (states, listening.OrderBy(l => l.Port).ToList());
    }

    public static List<ListeningPortInfo> ParseListening(IEnumerable<string> localAddresses)
    {
        var seen = new HashSet<string>();
        var list = new List<ListeningPortInfo>();
        foreach (var raw in localAddresses)
        {
            if (DecodeProcAddress(raw.Trim()) is { } ep && seen.Add(ep.Address + ":" + ep.Port))
                list.Add(new ListeningPortInfo("TCP", ep.Address, ep.Port));
        }
        return list.OrderBy(l => l.Port).ToList();
    }

    /// <summary>Decodes "0100007F:0016" (IPv4, little-endian) or 32-hex-digit IPv6 addresses.</summary>
    public static (string Address, int Port)? DecodeProcAddress(string value)
    {
        var colon = value.LastIndexOf(':');
        if (colon < 0) return null;
        if (!int.TryParse(value[(colon + 1)..], NumberStyles.HexNumber, Inv, out var port)) return null;
        var hex = value[..colon];
        try
        {
            if (hex.Length == 8)
            {
                var bytes = Convert.FromHexString(hex);
                Array.Reverse(bytes);
                return (new IPAddress(bytes).ToString(), port);
            }
            if (hex.Length == 32)
            {
                var bytes = Convert.FromHexString(hex);
                // Stored as four little-endian 32-bit words.
                for (var i = 0; i < 16; i += 4) Array.Reverse(bytes, i, 4);
                var ip = new IPAddress(bytes);
                return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4().ToString() : ip.ToString(), port);
            }
        }
        catch (FormatException) { }
        return null;
    }

    // ---------------- Full metric sample ----------------

    public static HostSample ParseMetrics(int serverId, string output, DateTimeOffset timestamp)
    {
        var s = Sections(output);
        if (!s.ContainsKey("end"))
            throw new FormatException("Metric script output was truncated.");

        // CPU
        var stat1 = ParseProcStat(Get(s, "stat1"));
        var stat2 = ParseProcStat(Get(s, "stat2"));
        var cpu = stat1.TryGetValue("cpu", out var c1) && stat2.TryGetValue("cpu", out var c2) ? CpuUsage(c1, c2) : 0;
        var cores = stat2.Keys.Where(k => k != "cpu")
            .OrderBy(k => int.TryParse(k.AsSpan(3), out var n) ? n : 0)
            .Select(k => stat1.TryGetValue(k, out var a) ? Math.Round(CpuUsage(a, stat2[k]), 1) : 0)
            .ToArray();

        // Memory
        var mem = ParseMeminfo(Get(s, "meminfo"));
        long M(string k) => mem.GetValueOrDefault(k);
        var memTotal = M("MemTotal");
        var memAvail = mem.ContainsKey("MemAvailable") ? M("MemAvailable") : M("MemFree") + M("Buffers") + M("Cached");

        // Load / uptime
        double? load1 = null, load5 = null, load15 = null;
        var threads = 0;
        var loadParts = Get(s, "loadavg").FirstOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (loadParts.Length >= 4)
        {
            load1 = double.Parse(loadParts[0], Inv);
            load5 = double.Parse(loadParts[1], Inv);
            load15 = double.Parse(loadParts[2], Inv);
            // "running/total" scheduling entities = total thread count.
            var slash = loadParts[3].IndexOf('/');
            if (slash > 0) int.TryParse(loadParts[3][(slash + 1)..], out threads);
        }
        var uptime = (long)double.Parse(Get(s, "uptime").FirstOrDefault()?.Split(' ')[0] ?? "0", Inv);

        // Disk
        var partitions = ParseDf(Get(s, "df"));
        var d1 = ParseDiskStats(Get(s, "disk1"));
        var d2 = ParseDiskStats(Get(s, "disk2"));
        var devices = new List<DiskDeviceIo>();
        foreach (var (dev, b) in d2)
        {
            if (!d1.TryGetValue(dev, out var a)) continue;
            var reads = b.ReadsCompleted - a.ReadsCompleted;
            var writes = b.WritesCompleted - a.WritesCompleted;
            devices.Add(new DiskDeviceIo(dev,
                Math.Max(0, (b.SectorsRead - a.SectorsRead) * 512.0),
                Math.Max(0, (b.SectorsWritten - a.SectorsWritten) * 512.0),
                reads > 0 ? (double)(b.MsReading - a.MsReading) / reads : null,
                writes > 0 ? (double)(b.MsWriting - a.MsWriting) / writes : null));
        }

        // Network
        var n1 = ParseNetDev(Get(s, "net1"));
        var n2 = ParseNetDev(Get(s, "net2"));
        var ifState = Get(s, "ifstate").Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length >= 2).ToDictionary(p => p[0], p => (Up: p[1] is "up" or "unknown", Speed: p.Length > 2 && long.TryParse(p[2], out var sp) && sp > 0 ? sp : (long?)null));
        var interfaces = new List<NetworkInterfaceInfo>();
        foreach (var (name, b) in n2)
        {
            var a = n1.GetValueOrDefault(name, b);
            var st = ifState.GetValueOrDefault(name, (Up: true, Speed: null));
            interfaces.Add(new NetworkInterfaceInfo(name, st.Up, b.RxBytes, b.TxBytes,
                Math.Max(0, b.RxBytes - a.RxBytes), Math.Max(0, b.TxBytes - a.TxBytes),
                b.RxErrs, b.TxErrs, b.RxDrop, b.TxDrop, st.Speed));
        }
        var physical = interfaces.Where(i => i.Name != "lo").ToList();

        // TCP
        var (tcpStates, listening) = ParseTcp(Get(s, "tcp"));

        // Misc
        double? temp = null;
        var temps = Get(s, "temp").Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Where(p => p.Length == 2 && long.TryParse(p[1], out _)).ToList();
        var cpuZone = temps.FirstOrDefault(p => p[0].Contains("pkg", StringComparison.OrdinalIgnoreCase) || p[0].Contains("cpu", StringComparison.OrdinalIgnoreCase) || p[0].Contains("soc", StringComparison.OrdinalIgnoreCase))
                      ?? temps.FirstOrDefault();
        if (cpuZone is not null) temp = long.Parse(cpuZone[1], Inv) / 1000.0;

        double? freq = null;
        foreach (var line in Get(s, "cpufreq"))
        {
            if (line.Contains("MHz", StringComparison.OrdinalIgnoreCase))
            {
                var v = line.Split(':').LastOrDefault()?.Trim();
                if (double.TryParse(v, NumberStyles.Float, Inv, out var mhz)) { freq = mhz; break; }
            }
            else if (long.TryParse(line.Trim(), out var khz)) { freq = khz / 1000.0; break; }
        }

        long? openFiles = null;
        var fileNr = Get(s, "filenr").FirstOrDefault()?.Split((char[])['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (fileNr is { Length: >= 1 } && long.TryParse(fileNr[0], out var of)) openFiles = of;

        int.TryParse(Get(s, "procs").FirstOrDefault()?.Trim(), out var procCount);
        int.TryParse(Get(s, "sessions").FirstOrDefault()?.Trim(), out var sessions);

        var snapshot = new MetricSnapshot
        {
            ServerId = serverId,
            Timestamp = timestamp,
            CpuPercent = Math.Round(cpu, 2),
            CpuCores = cores.Length,
            PerCoreJson = JsonSerializer.Serialize(cores),
            Load1 = load1, Load5 = load5, Load15 = load15,
            CpuTemperatureC = temp,
            CpuFrequencyMhz = freq,
            MemTotal = memTotal,
            MemAvailable = memAvail,
            MemFree = M("MemFree"),
            MemUsed = Math.Max(0, memTotal - memAvail),
            SwapTotal = M("SwapTotal"),
            SwapUsed = Math.Max(0, M("SwapTotal") - M("SwapFree")),
            DiskTotal = partitions.Sum(p => p.TotalBytes),
            DiskUsed = partitions.Sum(p => p.UsedBytes),
            DiskReadBytesPerSec = devices.Sum(d => d.ReadBytesPerSec),
            DiskWriteBytesPerSec = devices.Sum(d => d.WriteBytesPerSec),
            DiskReadLatencyMs = AverageOrNull(devices.Select(d => d.ReadLatencyMs)),
            DiskWriteLatencyMs = AverageOrNull(devices.Select(d => d.WriteLatencyMs)),
            NetRxBytesPerSec = physical.Sum(i => i.RxBytesPerSec),
            NetTxBytesPerSec = physical.Sum(i => i.TxBytesPerSec),
            NetRxTotalBytes = physical.Sum(i => i.RxBytes),
            NetTxTotalBytes = physical.Sum(i => i.TxBytes),
            NetErrors = physical.Sum(i => i.RxErrors + i.TxErrors),
            NetDrops = physical.Sum(i => i.RxDrops + i.TxDrops),
            TcpConnections = tcpStates.Where(k => k.Key != "LISTEN").Sum(k => k.Value),
            TcpEstablished = tcpStates.GetValueOrDefault("ESTABLISHED"),
            ProcessCount = procCount,
            ThreadCount = threads,
            OpenFileDescriptors = openFiles,
            ActiveSessions = sessions,
            UptimeSeconds = uptime
        };

        return new HostSample
        {
            Snapshot = snapshot,
            Hostname = Get(s, "hostname").FirstOrDefault()?.Trim(),
            OsVersion = Get(s, "os").FirstOrDefault()?.Trim(),
            PerCoreUsage = cores,
            Partitions = partitions,
            DiskDevices = devices,
            Interfaces = interfaces.OrderBy(i => i.Name).ToList(),
            TcpStates = tcpStates,
            ListeningPorts = listening
        };
    }

    private static double? AverageOrNull(IEnumerable<double?> values)
    {
        var v = values.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        return v.Count == 0 ? null : Math.Round(v.Average(), 3);
    }

    // ---------------- Processes ----------------

    public static IReadOnlyList<ProcessInfo> ParseProcesses(string output, int top)
    {
        var s = Sections(output);
        var hz = long.TryParse(Get(s, "hz").FirstOrDefault()?.Trim(), out var h) && h > 0 ? h : 100;
        var page = long.TryParse(Get(s, "pagesize").FirstOrDefault()?.Trim(), out var ps) && ps > 0 ? ps : 4096;
        var memTotal = ParseMeminfo(Get(s, "memtotal")).GetValueOrDefault("MemTotal");
        var a = ParseStatLines(Get(s, "a"));
        var b = ParseStatLines(Get(s, "b"));
        var users = Get(s, "users").Select(l => l.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(p => p.Length == 2 && int.TryParse(p[0], out _))
            .GroupBy(p => int.Parse(p[0])).ToDictionary(g => g.Key, g => g.First()[1]);

        var list = new List<ProcessInfo>(b.Count);
        foreach (var (pid, nb) in b)
        {
            var cpuTicks = a.TryGetValue(pid, out var na) ? nb.Ticks - na.Ticks : 0;
            var cpu = Math.Max(0, 100.0 * cpuTicks / hz); // sampled over one second
            var rss = nb.RssPages * page;
            list.Add(new ProcessInfo(pid, nb.Name, users.GetValueOrDefault(pid), Math.Round(cpu, 1),
                memTotal > 0 ? Math.Round(100.0 * rss / memTotal, 2) : 0, rss, nb.Threads));
        }
        return list.OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.RssBytes).Take(top).ToList();
    }

    private readonly record struct ProcStat(string Name, long Ticks, int Threads, long RssPages);

    private static Dictionary<int, ProcStat> ParseStatLines(IEnumerable<string> lines)
    {
        var d = new Dictionary<int, ProcStat>();
        foreach (var line in lines)
        {
            var open = line.IndexOf('(');
            var close = line.LastIndexOf(')');
            if (open < 0 || close < open) continue;
            if (!int.TryParse(line.AsSpan(0, open).Trim(), out var pid)) continue;
            var name = line[(open + 1)..close];
            var f = line[(close + 2)..].Split(' ');
            // after ")": [0]=state ... utime=[11] stime=[12] num_threads=[17] rss=[21]
            if (f.Length < 22) continue;
            long L(int i) => long.TryParse(f[i], out var n) ? n : 0;
            d[pid] = new ProcStat(name, L(11) + L(12), (int)L(17), L(21));
        }
        return d;
    }

    // ---------------- Services ----------------

    public static IReadOnlyList<ServiceStatusInfo> ParseSystemctlShow(string output)
    {
        var list = new List<ServiceStatusInfo>();
        foreach (var block in output.Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = block.Split('\n').Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
            if (!kv.TryGetValue("Id", out var id)) continue;
            var load = kv.GetValueOrDefault("LoadState");
            var active = kv.GetValueOrDefault("ActiveState");
            var state = load == "not-found" ? ServiceState.NotFound : active switch
            {
                "active" => ServiceState.Running,
                "reloading" => ServiceState.Running,
                "inactive" => ServiceState.Stopped,
                "failed" => ServiceState.Failed,
                "activating" => ServiceState.Starting,
                "deactivating" => ServiceState.Stopping,
                _ => ServiceState.Unknown
            };
            var name = id.EndsWith(".service", StringComparison.Ordinal) ? id[..^8] : id;
            var result = kv.GetValueOrDefault("Result");
            list.Add(new ServiceStatusInfo(name, kv.GetValueOrDefault("Description"), state, kv.GetValueOrDefault("SubState"),
                kv.GetValueOrDefault("UnitFileState"), ParseSystemdTimestamp(kv.GetValueOrDefault("StateChangeTimestamp")),
                result is null or "success" ? null : "Result: " + result));
        }
        return list;
    }

    /// <summary>Parses "Thu 2026-10-29 08:15:02 UTC" (TZ=UTC is forced by the script).</summary>
    public static DateTimeOffset? ParseSystemdTimestamp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var p = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (p.Length < 3) return null;
        return DateTimeOffset.TryParseExact(p[1] + " " + p[2], "yyyy-MM-dd HH:mm:ss", Inv,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt) ? dt : null;
    }

    // ---------------- Security ----------------

    [GeneratedRegex(@"(?:Failed \w+ for (?:invalid user )?(?<user>\S+)|Invalid user (?<user>\S+)|user=(?<user>\S+)).*?(?:from|rhost=) ?(?<ip>[0-9a-fA-F:.]+)")]
    private static partial Regex FailedLoginRegex();

    [GeneratedRegex(@"Accepted (?<method>\S+) for (?<user>\S+) from (?<ip>[0-9a-fA-F:.]+)")]
    private static partial Regex AcceptedLoginRegex();

    public static SecurityScanResult ParseSecurity(string output, DateTimeOffset since, DateTimeOffset now)
    {
        var s = Sections(output);
        var failed = new List<LoginRecord>();
        foreach (var line in Get(s, "failed"))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var ts = ParseLogTimestamp(line, now);
            if (ts is not null && ts < since) continue;
            var m = FailedLoginRegex().Match(line);
            failed.Add(new LoginRecord(ts ?? now, m.Success ? m.Groups["user"].Value : null, m.Success ? m.Groups["ip"].Value : null, false, Trim(line)));
        }
        var accepted = new List<LoginRecord>();
        foreach (var line in Get(s, "accepted"))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var ts = ParseLogTimestamp(line, now);
            if (ts is not null && ts < since) continue;
            var m = AcceptedLoginRegex().Match(line);
            accepted.Add(new LoginRecord(ts ?? now, m.Success ? m.Groups["user"].Value : null, m.Success ? m.Groups["ip"].Value : null, true, Trim(line)));
        }

        bool? firewall = null;
        var fwDetails = new List<string>();
        foreach (var line in Get(s, "firewall"))
        {
            var p = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            if (p[0].StartsWith("ENABLED=", StringComparison.Ordinal))
            {
                var enabled = p[0].EndsWith("yes", StringComparison.OrdinalIgnoreCase);
                fwDetails.Add("ufw.conf ENABLED=" + (enabled ? "yes" : "no"));
                if (enabled) firewall = true;
                continue;
            }
            var st = p.Length > 1 ? p[1].Trim() : "unknown";
            if (st == "active")
            {
                fwDetails.Add($"{p[0]}: active");
                if (p[0] != "ufw") firewall = true; // ufw service being active does not mean rules are enabled
            }
            else if (firewall is null && st is "inactive" or "failed") firewall = false;
        }

        int? updates = null, secUpdates = null;
        var upd = Get(s, "updates").LastOrDefault(l => l.Contains(';'))?.Trim();
        if (upd is not null)
        {
            var p = upd.Split(';');
            if (int.TryParse(p[0], out var u)) updates = u;
            if (p.Length > 1 && int.TryParse(p[1], out var su)) secUpdates = su;
        }

        var cryptCount = int.TryParse(Get(s, "crypt").FirstOrDefault()?.Trim(), out var cc) ? cc : 0;

        return new SecurityScanResult
        {
            FailedLogins = failed,
            SuccessfulLogins = accepted,
            FirewallActive = firewall,
            FirewallDetails = fwDetails.Count > 0 ? string.Join("; ", fwDetails) : "No firewall service detected",
            PendingUpdates = updates,
            PendingSecurityUpdates = secUpdates,
            RebootRequired = Get(s, "reboot").FirstOrDefault()?.Trim() == "yes",
            DiskEncryption = cryptCount > 0 ? $"LUKS ({cryptCount} mapped device(s))" : "No dm-crypt devices detected",
            ListeningPorts = ParseListening(Get(s, "listen")),
            ScannedAt = now
        };
    }

    private static string Trim(string line) => line.Length > 500 ? line[..500] : line;

    /// <summary>Supports ISO (journal short-iso / rsyslog high precision) and classic "Oct 31 12:00:01" syslog timestamps.</summary>
    public static DateTimeOffset? ParseLogTimestamp(string line, DateTimeOffset now)
    {
        var first = line.Split(' ', 2)[0];
        if (first.Length >= 19 && char.IsDigit(first[0]) &&
            DateTimeOffset.TryParse(first, Inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var iso))
            return iso;
        if (line.Length >= 15 && DateTime.TryParseExact(line[..15].Replace("  ", " 0"), "MMM dd HH:mm:ss", Inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var classic))
        {
            var dt = new DateTimeOffset(now.Year, classic.Month, classic.Day, classic.Hour, classic.Minute, classic.Second, TimeSpan.Zero);
            return dt > now.AddDays(1) ? dt.AddYears(-1) : dt;
        }
        return null;
    }

    // ---------------- Logs ----------------

    public static IReadOnlyList<LogEntry> ParseLogs(string sourceKey, string output, DateTimeOffset? since, DateTimeOffset now)
    {
        var list = new List<LogEntry>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("-- ", StringComparison.Ordinal)) continue;
            LogEntry? entry = line.StartsWith('{') ? ParseJournalJson(line, sourceKey) : ParseTextLine(line, sourceKey, now);
            if (entry is null) continue;
            if (since is not null && entry.Timestamp <= since) continue;
            list.Add(entry);
        }
        return list;
    }

    private static LogEntry? ParseJournalJson(string line, string source)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            var ts = r.TryGetProperty("__REALTIME_TIMESTAMP", out var t) && long.TryParse(t.GetString(), out var us)
                ? DateTimeOffset.FromUnixTimeMilliseconds(us / 1000) : DateTimeOffset.UtcNow;
            var prio = r.TryGetProperty("PRIORITY", out var p) && int.TryParse(p.GetString(), out var pr) ? pr : 6;
            var ident = r.TryGetProperty("SYSLOG_IDENTIFIER", out var id) ? id.GetString() : r.TryGetProperty("_COMM", out var cm) ? cm.GetString() : null;
            string msg;
            if (r.TryGetProperty("MESSAGE", out var m))
            {
                msg = m.ValueKind switch
                {
                    JsonValueKind.String => m.GetString() ?? "",
                    // Non-UTF8 messages are exported as a byte array.
                    JsonValueKind.Array => System.Text.Encoding.UTF8.GetString(m.EnumerateArray().Select(e => (byte)e.GetInt32()).ToArray()),
                    _ => ""
                };
            }
            else msg = "";
            var level = prio switch
            {
                <= 2 => LogLevelKind.Critical,
                3 => LogLevelKind.Error,
                4 => LogLevelKind.Warning,
                5 => LogLevelKind.Notice,
                6 => LogLevelKind.Info,
                _ => LogLevelKind.Debug
            };
            return new LogEntry(ts, level, ident ?? source, msg);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static LogEntry ParseTextLine(string line, string source, DateTimeOffset now)
    {
        var ts = ParseLogTimestamp(line, now) ?? ParseAccessLogTimestamp(line) ?? now;
        return new LogEntry(ts, GuessLevel(line), source, line);
    }

    [GeneratedRegex(@"\[(?<t>\d{2}/\w{3}/\d{4}:\d{2}:\d{2}:\d{2} [+\-]\d{4})\]")]
    private static partial Regex AccessLogTimeRegex();

    private static DateTimeOffset? ParseAccessLogTimestamp(string line)
    {
        var m = AccessLogTimeRegex().Match(line);
        if (m.Success && DateTimeOffset.TryParseExact(m.Groups["t"].Value, "dd/MMM/yyyy:HH:mm:ss zzz", Inv, DateTimeStyles.None, out var dt))
            return dt.ToUniversalTime();
        // nginx error log: 2026/10/31 12:00:00 [error]
        if (line.Length >= 19 && DateTimeOffset.TryParseExact(line[..19], "yyyy/MM/dd HH:mm:ss", Inv, DateTimeStyles.AssumeUniversal, out var ng))
            return ng;
        return null;
    }

    public static LogLevelKind GuessLevel(string line)
    {
        var l = line.AsSpan();
        if (l.Contains("emerg", StringComparison.OrdinalIgnoreCase) || l.Contains("crit", StringComparison.OrdinalIgnoreCase) || l.Contains("panic", StringComparison.OrdinalIgnoreCase)) return LogLevelKind.Critical;
        if (l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("[err", StringComparison.OrdinalIgnoreCase) || l.Contains("fail", StringComparison.OrdinalIgnoreCase)) return LogLevelKind.Error;
        if (l.Contains("warn", StringComparison.OrdinalIgnoreCase)) return LogLevelKind.Warning;
        if (l.Contains("notice", StringComparison.OrdinalIgnoreCase)) return LogLevelKind.Notice;
        if (l.Contains("debug", StringComparison.OrdinalIgnoreCase)) return LogLevelKind.Debug;
        // HTTP access logs: classify by status code
        var m = Regex.Match(line, "\" (?<code>[1-5]\\d\\d) ");
        if (m.Success) return m.Groups["code"].Value[0] switch { '5' => LogLevelKind.Error, '4' => LogLevelKind.Warning, _ => LogLevelKind.Info };
        return LogLevelKind.Info;
    }
}
