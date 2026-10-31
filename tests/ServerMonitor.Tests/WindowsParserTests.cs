using ServerMonitor.Domain;
using ServerMonitor.Infrastructure.Adapters.Windows;
using ServerMonitor.Infrastructure.Remote.WinRm;

namespace ServerMonitor.Tests;

public class WindowsParserTests
{
    // Raw CIM counters one second apart (Timestamp_Sys100NS += 10,000,000; PerfTime freq 10 MHz).
    private const string MetricsJson = """
{"a":{"cpu":[{"Name":"0","PercentProcessorTime":1000000000,"Timestamp_Sys100NS":5000000000},{"Name":"1","PercentProcessorTime":1000000000,"Timestamp_Sys100NS":5000000000},{"Name":"_Total","PercentProcessorTime":1000000000,"Timestamp_Sys100NS":5000000000}],
      "disk":{"Name":"0 C:","DiskReadBytesPersec":1000,"DiskWriteBytesPersec":2000,"AvgDisksecPerRead":0,"AvgDisksecPerRead_Base":0,"AvgDisksecPerWrite":0,"AvgDisksecPerWrite_Base":0,"Timestamp_PerfTime":100000000,"Frequency_PerfTime":10000000},
      "net":[{"Name":"Intel[R] Ethernet Connection","BytesReceivedPersec":5000,"BytesSentPersec":1000,"Timestamp_PerfTime":100000000,"Frequency_PerfTime":10000000}]},
 "b":{"cpu":[{"Name":"0","PercentProcessorTime":1002500000,"Timestamp_Sys100NS":5010000000},{"Name":"1","PercentProcessorTime":1007500000,"Timestamp_Sys100NS":5010000000},{"Name":"_Total","PercentProcessorTime":1005000000,"Timestamp_Sys100NS":5010000000}],
      "disk":{"Name":"0 C:","DiskReadBytesPersec":5096,"DiskWriteBytesPersec":10192,"AvgDisksecPerRead":40000,"AvgDisksecPerRead_Base":10,"AvgDisksecPerWrite":100000,"AvgDisksecPerWrite_Base":20,"Timestamp_PerfTime":110000000,"Frequency_PerfTime":10000000},
      "net":[{"Name":"Intel[R] Ethernet Connection","BytesReceivedPersec":105000,"BytesSentPersec":51000,"Timestamp_PerfTime":110000000,"Frequency_PerfTime":10000000}]},
 "host":"WIN-01","os":"Microsoft Windows Server 2022 Standard 10.0.20348","memTotalKb":16000000,"memFreeKb":4000000,
 "boot":"2026-10-30T12:00:00.0000000Z","cores":2,"mhz":2600,"procs":150,"threads":2000,"queue":1,
 "disks":{"DeviceID":"C:","FileSystem":"NTFS","Size":100000000000,"FreeSpace":25000000000},
 "page":[{"AllocatedBaseSize":2048,"CurrentUsage":512}],
 "tcp":[{"s":"Established","c":40},{"s":"Listen","c":12},{"s":"TimeWait","c":5}],
 "listen":[{"a":"0.0.0.0","p":3389},{"a":"0.0.0.0","p":5986}],
 "adapters":{"n":"Ethernet","d":"Intel(R) Ethernet Connection","up":true,"sp":1000000000,"rx":9000000,"tx":3000000,"re":0,"te":1,"rd":2,"td":0},
 "handles":50000,"sessions":2,"temp":null}
""";

    [Fact]
    public void Computes_rates_from_raw_counters()
    {
        var now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var s = WindowsParsers.ParseMetrics(3, MetricsJson, now);
        var m = s.Snapshot;
        Assert.Equal(50, m.CpuPercent);                 // idle 0.5 s of 1 s
        Assert.Equal(new[] { 75.0, 25.0 }, s.PerCoreUsage);
        Assert.Equal(2, m.CpuCores);
        Assert.Equal(4096, m.DiskReadBytesPerSec);
        Assert.Equal(8192, m.DiskWriteBytesPerSec);
        Assert.Equal(0.4, m.DiskReadLatencyMs!.Value, 6);   // (40000/1e7 s)/10 = 0.4 ms
        Assert.Equal(0.5, m.DiskWriteLatencyMs!.Value, 6);  // (100000/1e7 s)/20
        Assert.Equal(100000, m.NetRxBytesPerSec);       // single-object arrays handled
        Assert.Equal(50000, m.NetTxBytesPerSec);
        Assert.Equal(12_000_000L * 1024, m.MemUsed);
        Assert.Equal(75_000_000_000L, m.DiskUsed);
        Assert.Equal(512L * 1024 * 1024, m.SwapUsed);
        Assert.Equal(40, m.TcpEstablished);
        Assert.Equal(45, m.TcpConnections);
        Assert.Equal(86400, m.UptimeSeconds);
        Assert.Equal(1000, s.Interfaces[0].SpeedMbps);
        Assert.Equal(100000, s.Interfaces[0].RxBytesPerSec); // matched by normalised description
        Assert.Equal(2, s.ListeningPorts.Count);
    }

    [Fact]
    public void Parses_services_including_single_object_json()
    {
        var single = WindowsParsers.ParseServices("""{"Name":"W3SVC","DisplayName":"World Wide Web Publishing Service","State":"Running","StartMode":"Auto","ExitCode":0}""");
        Assert.Equal(ServiceState.Running, single.Single().State);
        var many = WindowsParsers.ParseServices("""[{"Name":"MSSQLSERVER","State":"Stopped","StartMode":"Auto","ExitCode":1067},{"Name":"DNS","State":"Running","ExitCode":0}]""");
        Assert.Equal(ServiceState.Stopped, many[0].State);
        Assert.Equal("Exit code 1067", many[0].LastError);
        Assert.Empty(WindowsParsers.ParseServices(""));
    }

    [Fact]
    public void Parses_security_scan()
    {
        var json = """{"failed":{"t":"2026-10-31T11:00:00.0000000Z","u":"Administrator","ip":"203.0.113.5","lt":"3"},"success":[],"secReadable":true,"fw":[{"n":"Domain","e":true},{"n":"Public","e":false}],"updates":4,"secUpdates":1,"reboot":false,"bitlocker":"C:=On","listen":[{"a":"::","p":445}]}""";
        var r = WindowsParsers.ParseSecurity(json, DateTimeOffset.UtcNow);
        Assert.Single(r.FailedLogins);
        Assert.Equal("Administrator", r.FailedLogins[0].User);
        Assert.False(r.FirewallActive);
        Assert.Contains("Public: OFF", r.FirewallDetails);
        Assert.Equal(4, r.PendingUpdates);
        Assert.Equal("BitLocker: C:=On", r.DiskEncryption);
    }

    [Fact]
    public void Powershell_command_line_stays_under_cmd_limit()
    {
        var args = WinRmClient.BuildPowerShellArgs(typeof(WindowsParsers).Assembly
            .GetType("ServerMonitor.Infrastructure.Adapters.Windows.WindowsScripts")!
            .GetField("Metrics")!.GetValue(null)!.ToString()!);
        Assert.True(args.Length < 8000, $"length {args.Length}");
        Assert.StartsWith("-NoLogo -NoProfile -NonInteractive", args);
    }
}
