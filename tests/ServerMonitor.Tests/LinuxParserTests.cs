using ServerMonitor.Domain;
using ServerMonitor.Infrastructure.Adapters.Linux;
using ServerMonitor.Infrastructure.Security;

namespace ServerMonitor.Tests;

public class LinuxParserTests
{
    private const string MetricsOutput = """
@@hostname
web-01
@@os
Ubuntu 24.04.1 LTS
@@stat1
cpu  1000 0 500 8000 500 0 0 0 0 0
cpu0 500 0 250 4000 250 0 0 0 0 0
cpu1 500 0 250 4000 250 0 0 0 0 0
@@net1
Inter-|   Receive                                                |  Transmit
 face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
    lo: 5000 50 0 0 0 0 0 0 5000 50 0 0 0 0 0 0
  eth0: 100000 1000 1 2 0 0 0 0 200000 2000 3 4 0 0 0 0
@@disk1
   8       0 sda 1000 0 80000 500 2000 0 160000 1000 0 0 0 0 0 0 0 0 0
   8       1 sda1 900 0 70000 400 1800 0 150000 900 0 0 0 0 0 0 0 0 0
   7       0 loop0 10 0 20 0 0 0 0 0 0 0 0 0 0 0 0 0 0
@@stat2
cpu  1100 0 550 8300 550 0 0 0 0 0
cpu0 600 0 300 4100 260 0 0 0 0 0
cpu1 500 0 250 4200 290 0 0 0 0 0
@@net2
Inter-|   Receive                                                |  Transmit
 face |bytes    packets errs drop fifo frame compressed multicast|bytes    packets errs drop fifo colls carrier compressed
    lo: 6000 60 0 0 0 0 0 0 6000 60 0 0 0 0 0 0
  eth0: 150000 1500 1 2 0 0 0 0 225000 2500 3 4 0 0 0 0
@@disk2
   8       0 sda 1010 0 80200 520 2010 0 160400 1030 0 0 0 0 0 0 0 0 0
   8       1 sda1 905 0 70100 410 1805 0 150200 920 0 0 0 0 0 0 0 0 0
@@meminfo
MemTotal:        8000000 kB
MemFree:         1000000 kB
MemAvailable:    6000000 kB
Buffers:          100000 kB
Cached:          2000000 kB
SwapTotal:       2000000 kB
SwapFree:        1500000 kB
@@loadavg
0.52 0.61 0.70 2/345 12345
@@uptime
123456.78 400000.00
@@df
Filesystem     Type  1-blocks        Used   Available Capacity Mounted on
/dev/sda1      ext4  100000000000 40000000000 55000000000      43% /
/dev/sda1      ext4  100000000000 40000000000 55000000000      43% /var/lib/docker
/dev/sdb1      xfs   50000000000  10000000000 40000000000      20% /data disk
@@cpufreq
cpu MHz		: 2399.998
@@temp
acpitz 27800
x86_pkg_temp 45000
@@tcp
0100007F:0016 0A
00000000:01BB 0A
0100007F:9C40 01
0100007F:9C41 01
0100007F:9C42 06
00000000000000000000000000000000:0050 0A
@@procs
212
@@filenr
2048	0	9223372036854775807
@@sessions
3
@@ifstate
eth0 up 1000
lo unknown
@@end
""";

    [Fact]
    public void Parses_cpu_memory_disk_network_from_two_samples()
    {
        var s = LinuxParsers.ParseMetrics(7, MetricsOutput, DateTimeOffset.UnixEpoch);
        var m = s.Snapshot;

        // aggregate: total delta = (1100+550+8300+550) - (1000+500+8000+500) = 500; idle delta = (8300+550)-(8000+500) = 350
        Assert.Equal(30, m.CpuPercent, 2);
        Assert.Equal(2, m.CpuCores);
        Assert.Equal(2, s.PerCoreUsage.Length);
        // cpu0: total 1260-1000... (600+300+4100+260) - (500+250+4000+250) = 260, idle (4360-4250)=110 -> 57.7%
        Assert.Equal(57.7, s.PerCoreUsage[0], 1);

        Assert.Equal(8_000_000L * 1024, m.MemTotal);
        Assert.Equal(6_000_000L * 1024, m.MemAvailable);
        Assert.Equal(2_000_000L * 1024, m.MemUsed);
        Assert.Equal(500_000L * 1024, m.SwapUsed);

        // Duplicate /dev/sda1 (bind mount) counted once; mount with space preserved.
        Assert.Equal(2, s.Partitions.Count);
        Assert.Equal("/data disk", s.Partitions[1].Mount);
        Assert.Equal(150_000_000_000L, m.DiskTotal);
        // sda: 200 sectors read, 400 sectors written in 1 s; partitions/loop ignored
        Assert.Equal(200 * 512, m.DiskReadBytesPerSec);
        Assert.Equal(400 * 512, m.DiskWriteBytesPerSec);
        Assert.Equal(2.0, m.DiskReadLatencyMs);   // 20 ms / 10 reads
        Assert.Equal(3.0, m.DiskWriteLatencyMs);  // 30 ms / 10 writes

        // lo excluded from totals
        Assert.Equal(50000, m.NetRxBytesPerSec);
        Assert.Equal(25000, m.NetTxBytesPerSec);
        Assert.Equal(150000, m.NetRxTotalBytes);
        Assert.Equal(1 + 3, m.NetErrors);

        Assert.Equal(0.52, m.Load1);
        Assert.Equal(345, m.ThreadCount);
        Assert.Equal(123456, m.UptimeSeconds);
        Assert.Equal(212, m.ProcessCount);
        Assert.Equal(2048, m.OpenFileDescriptors);
        Assert.Equal(3, m.ActiveSessions);
        Assert.Equal(45.0, m.CpuTemperatureC);
        Assert.Equal(2399.998, m.CpuFrequencyMhz!.Value, 3);

        Assert.Equal(2, m.TcpEstablished);
        Assert.Equal(3, m.TcpConnections); // LISTEN excluded
        Assert.Equal(new[] { 22, 80, 443 }, s.ListeningPorts.Select(p => p.Port).ToArray());
        Assert.Equal("127.0.0.1", s.ListeningPorts.First(p => p.Port == 22).Address);
        Assert.Equal("Ubuntu 24.04.1 LTS", s.OsVersion);
        Assert.Equal(1000, s.Interfaces.First(i => i.Name == "eth0").SpeedMbps);
    }

    [Fact]
    public void Truncated_output_is_rejected()
    {
        Assert.Throws<FormatException>(() => LinuxParsers.ParseMetrics(1, "@@hostname\nx\n", DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Decodes_ipv6_proc_address()
    {
        // ::1 port 5432
        var r = LinuxParsers.DecodeProcAddress("00000000000000000000000001000000:1538");
        Assert.Equal(("::1", 5432), r);
    }

    [Fact]
    public void Parses_processes_with_spaces_in_names()
    {
        var output = """
@@hz
100
@@pagesize
4096
@@memtotal
MemTotal: 1000000 kB
@@a
1 (systemd) S 0 1 1 0 -1 4194560 0 0 0 0 100 50 0 0 20 0 1 0 1 0 2000 0 0
42 (Web Content) S 1 42 42 0 -1 0 0 0 0 0 1000 200 0 0 20 0 12 0 5 0 50000 0 0
@@b
1 (systemd) S 0 1 1 0 -1 4194560 0 0 0 0 101 50 0 0 20 0 1 0 1 0 2000 0 0
42 (Web Content) S 1 42 42 0 -1 0 0 0 0 0 1050 210 0 0 20 0 12 0 5 0 50000 0 0
@@users
    1 root
   42 alice
@@end
""";
        var p = LinuxParsers.ParseProcesses(output, 10);
        Assert.Equal("Web Content", p[0].Name);
        Assert.Equal(60, p[0].CpuPercent);   // 60 ticks / 100 Hz over 1 s
        Assert.Equal(12, p[0].Threads);
        Assert.Equal(50000L * 4096, p[0].RssBytes);
        Assert.Equal("alice", p[0].User);
        Assert.Equal(1, p[1].CpuPercent);
    }

    [Fact]
    public void Parses_systemctl_show_blocks()
    {
        var output = "Id=nginx.service\nLoadState=loaded\nActiveState=active\nSubState=running\nUnitFileState=enabled\nDescription=A high performance web server\nStateChangeTimestamp=Thu 2026-10-29 08:15:02 UTC\nResult=success\n\n" +
                     "Id=redis.service\nLoadState=not-found\nActiveState=inactive\nSubState=dead\n\n" +
                     "Id=php8.3-fpm.service\nLoadState=loaded\nActiveState=failed\nSubState=failed\nResult=exit-code\n";
        var s = LinuxParsers.ParseSystemctlShow(output);
        Assert.Equal(3, s.Count);
        Assert.Equal(ServiceState.Running, s[0].State);
        Assert.Equal("nginx", s[0].Name);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 8, 15, 2, TimeSpan.Zero), s[0].Since);
        Assert.Equal(ServiceState.NotFound, s[1].State);
        Assert.Equal(ServiceState.Failed, s[2].State);
        Assert.Equal("Result: exit-code", s[2].LastError);
    }

    [Fact]
    public void Parses_security_scan()
    {
        var now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
        var output = """
@@failed
2026-10-31T11:50:01+00:00 web-01 sshd[123]: Failed password for invalid user admin from 203.0.113.9 port 51234 ssh2
2026-10-31T11:50:05+00:00 web-01 sshd[123]: Failed password for root from 203.0.113.9 port 51235 ssh2
2026-10-30T11:50:05+00:00 web-01 sshd[123]: Failed password for root from 198.51.100.1 port 51235 ssh2
@@accepted
2026-10-31T11:55:00+00:00 web-01 sshd[200]: Accepted publickey for deploy from 192.0.2.10 port 40000 ssh2
@@firewall
ufw active
firewalld inactive
nftables inactive
iptables inactive
ENABLED=yes
@@updates
12;3
@@reboot
yes
@@crypt
1
@@listen
00000000:0016
@@end
""";
        var r = LinuxParsers.ParseSecurity(output, now.AddHours(-1), now);
        Assert.Equal(2, r.FailedLogins.Count); // the one from yesterday is filtered
        Assert.Equal("admin", r.FailedLogins[0].User);
        Assert.Equal("203.0.113.9", r.FailedLogins[0].SourceIp);
        Assert.Equal("root", r.FailedLogins[1].User);
        Assert.Single(r.SuccessfulLogins);
        Assert.Equal("deploy", r.SuccessfulLogins[0].User);
        Assert.True(r.FirewallActive);
        Assert.Equal(12, r.PendingUpdates);
        Assert.Equal(3, r.PendingSecurityUpdates);
        Assert.True(r.RebootRequired);
        Assert.StartsWith("LUKS", r.DiskEncryption);
        Assert.Equal(22, r.ListeningPorts.Single().Port);
    }

    [Fact]
    public void Parses_journal_json_logs()
    {
        var output = "{\"__REALTIME_TIMESTAMP\":\"1761912000000000\",\"PRIORITY\":\"3\",\"SYSLOG_IDENTIFIER\":\"nginx\",\"MESSAGE\":\"boom\"}\n" +
                     "{\"__REALTIME_TIMESTAMP\":\"1761912001000000\",\"PRIORITY\":\"6\",\"_COMM\":\"cron\",\"MESSAGE\":[104,105]}\n";
        var logs = LinuxParsers.ParseLogs("journal", output, null, DateTimeOffset.UtcNow);
        Assert.Equal(2, logs.Count);
        Assert.Equal(ServerMonitor.Application.Models.LogLevelKind.Error, logs[0].Level);
        Assert.Equal("nginx", logs[0].Source);
        Assert.Equal("hi", logs[1].Message);
    }
}

public class InputValidationTests
{
    [Theory]
    [InlineData("nginx", true)]
    [InlineData("php8.3-fpm", true)]
    [InlineData("getty@tty1", true)]
    [InlineData("nginx; rm -rf /", false)]
    [InlineData("$(reboot)", false)]
    [InlineData("-help", false)]
    [InlineData("a b", false)]
    [InlineData("x'y", false)]
    public void Linux_unit_names(string name, bool valid) => Assert.Equal(valid, InputValidation.IsValidLinuxUnit(name));

    [Theory]
    [InlineData("MSSQL$SQLEXPRESS", true)]
    [InlineData("W3SVC", true)]
    [InlineData("x' OR Name LIKE '%", false)]
    [InlineData("svc;Stop-Computer", false)]
    public void Windows_service_names(string name, bool valid) => Assert.Equal(valid, InputValidation.IsValidWindowsService(name));

    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("fe80::1", true)]
    [InlineData("srv-01.example.com", true)]
    [InlineData("host;whoami", false)]
    [InlineData("-oProxyCommand=x", false)]
    public void Hosts(string host, bool valid) => Assert.Equal(valid, InputValidation.IsValidHost(host));

    [Fact]
    public void Shell_quote_escapes_single_quotes() =>
        Assert.Equal("'a'\"'\"'b'", InputValidation.ShellQuote("a'b"));
}
