namespace ServerMonitor.Infrastructure.Adapters.Linux;

/// <summary>
/// The complete set of commands the Linux adapter can run. Nothing from the frontend is ever
/// executed: scripts are constants, and the only interpolated values are integers or names that
/// passed <see cref="Security.InputValidation"/> and are additionally single-quoted.
/// All scripts are read-only except <see cref="RestartService"/>, which relies on a narrowly
/// scoped sudoers rule (see README).
/// </summary>
internal static class LinuxScripts
{
    private const string Prelude = "export LC_ALL=C TZ=UTC; ";

    /// <summary>Identification used by "Test connection".</summary>
    public const string Identify =
        Prelude +
        "echo @@hostname; hostname; " +
        "echo @@os; ( . /etc/os-release 2>/dev/null && echo \"$PRETTY_NAME\" ) || uname -sr; " +
        "echo @@kernel; uname -r; echo @@end";

    /// <summary>
    /// Resource sample. CPU, disk I/O and network counters are read twice, one second apart,
    /// so every poll returns real rates without depending on the previous poll.
    /// </summary>
    public const string Metrics =
        Prelude +
        "echo @@hostname; hostname; " +
        "echo @@os; ( . /etc/os-release 2>/dev/null && echo \"$PRETTY_NAME\" ) || uname -sr; " +
        "echo @@stat1; grep '^cpu' /proc/stat; " +
        "echo @@net1; cat /proc/net/dev; " +
        "echo @@disk1; cat /proc/diskstats; " +
        "sleep 1; " +
        "echo @@stat2; grep '^cpu' /proc/stat; " +
        "echo @@net2; cat /proc/net/dev; " +
        "echo @@disk2; cat /proc/diskstats; " +
        "echo @@meminfo; cat /proc/meminfo; " +
        "echo @@loadavg; cat /proc/loadavg; " +
        "echo @@uptime; cat /proc/uptime; " +
        "echo @@df; df -P -B1 -T -x tmpfs -x devtmpfs -x squashfs -x overlay -x efivarfs 2>/dev/null; " +
        "echo @@cpufreq; grep -m1 -i 'cpu MHz' /proc/cpuinfo 2>/dev/null; cat /sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq 2>/dev/null; " +
        "echo @@temp; for z in /sys/class/thermal/thermal_zone*; do [ -r \"$z/temp\" ] && echo \"$(cat $z/type 2>/dev/null) $(cat $z/temp 2>/dev/null)\"; done; " +
        "echo @@tcp; cat /proc/net/tcp /proc/net/tcp6 2>/dev/null | awk '$4 ~ /^[0-9A-F][0-9A-F]$/ {print $2, $4}'; " +
        "echo @@procs; ls -d /proc/[0-9]* 2>/dev/null | wc -l; " +
        "echo @@filenr; cat /proc/sys/fs/file-nr 2>/dev/null; " +
        "echo @@sessions; who 2>/dev/null | wc -l; " +
        "echo @@ifstate; for i in /sys/class/net/*; do echo \"$(basename $i) $(cat $i/operstate 2>/dev/null) $(cat $i/speed 2>/dev/null)\"; done; " +
        "echo @@end";

    /// <summary>Per-process CPU sampled over one second from /proc/[pid]/stat (instantaneous, unlike `ps pcpu`).</summary>
    public const string Processes =
        Prelude +
        "echo @@hz; getconf CLK_TCK; echo @@pagesize; getconf PAGESIZE; " +
        "echo @@memtotal; grep MemTotal /proc/meminfo; " +
        "echo @@a; cat /proc/[0-9]*/stat 2>/dev/null; " +
        "sleep 1; " +
        "echo @@b; cat /proc/[0-9]*/stat 2>/dev/null; " +
        "echo @@users; ps -eo pid=,user:32= 2>/dev/null; echo @@end";

    public static string Services(IEnumerable<string> quotedUnits) =>
        Prelude +
        "systemctl show --no-pager -p Id,LoadState,ActiveState,SubState,UnitFileState,Description,StateChangeTimestamp,Result -- " +
        string.Join(' ', quotedUnits);

    public static string ServiceErrors(string quotedUnit) =>
        Prelude + "journalctl -u " + quotedUnit + " -p err -n 5 --no-pager -o cat 2>/dev/null | tail -n 5";

    /// <summary>Requires a sudoers entry such as: monitor ALL=(root) NOPASSWD: /usr/bin/systemctl restart nginx.service</summary>
    public static string RestartService(string quotedUnit) =>
        Prelude + "sudo -n /usr/bin/systemctl restart " + quotedUnit + " 2>&1 || systemctl restart " + quotedUnit + " 2>&1";

    public static string Security(long sinceUnix) =>
        Prelude +
        $"L=$(journalctl -t sshd -t sshd-session --since @{sinceUnix} --no-pager -o short-iso 2>/dev/null | grep -v '^-- '); " +
        "[ -z \"$L\" ] && L=$(grep -h 'sshd' /var/log/auth.log /var/log/secure 2>/dev/null | tail -n 5000); " +
        "echo @@failed; printf '%s\\n' \"$L\" | grep -E 'Failed (password|publickey)|Invalid user|authentication failure' | tail -n 500; " +
        "echo @@accepted; printf '%s\\n' \"$L\" | grep -E 'Accepted (password|publickey|keyboard-interactive)' | tail -n 200; " +
        "echo @@firewall; for s in ufw firewalld nftables iptables; do echo \"$s $(systemctl is-active $s 2>/dev/null)\"; done; " +
        "grep -h '^ENABLED=' /etc/ufw/ufw.conf 2>/dev/null; " +
        "echo @@updates; if [ -x /usr/lib/update-notifier/apt-check ]; then /usr/lib/update-notifier/apt-check 2>&1 | tail -n1; " +
        "elif command -v apt >/dev/null 2>&1; then echo \"$(apt list --upgradable 2>/dev/null | grep -c upgradable);\"; " +
        "elif command -v dnf >/dev/null 2>&1; then echo \"$(dnf -q check-update --cacheonly 2>/dev/null | grep -c '^[A-Za-z0-9]');$(dnf -q updateinfo list --security --cacheonly 2>/dev/null | wc -l)\"; fi; " +
        "echo @@reboot; [ -f /var/run/reboot-required ] && echo yes || echo no; " +
        "echo @@crypt; lsblk -rno TYPE 2>/dev/null | grep -c '^crypt'; " +
        "echo @@listen; cat /proc/net/tcp /proc/net/tcp6 2>/dev/null | awk '$4==\"0A\" {print $2}'; " +
        "echo @@end";

    /// <summary>Log sources: fixed key → fixed command. `N` and `S` are integers produced server-side.</summary>
    public static string? Logs(string sourceKey, int maxLines, long? sinceUnix)
    {
        var since = sinceUnix is null ? "" : $" --since @{sinceUnix}";
        var j = $"journalctl --no-pager -o json -n {maxLines}{since}";
        return sourceKey switch
        {
            "journal" => Prelude + j + " 2>/dev/null",
            "syslog" => Prelude + $"(tail -n {maxLines} /var/log/syslog 2>/dev/null || tail -n {maxLines} /var/log/messages 2>/dev/null) || {j} 2>/dev/null",
            "auth" => Prelude + $"{j} SYSLOG_FACILITY=4 SYSLOG_FACILITY=10 2>/dev/null || tail -n {maxLines} /var/log/auth.log /var/log/secure 2>/dev/null",
            "kernel" => Prelude + $"journalctl --no-pager -o json -k -n {maxLines}{since} 2>/dev/null",
            "nginx-access" => Prelude + $"tail -n {maxLines} /var/log/nginx/access.log 2>&1",
            "nginx-error" => Prelude + $"tail -n {maxLines} /var/log/nginx/error.log 2>&1",
            "apache-access" => Prelude + $"(tail -n {maxLines} /var/log/apache2/access.log || tail -n {maxLines} /var/log/httpd/access_log) 2>&1",
            "apache-error" => Prelude + $"(tail -n {maxLines} /var/log/apache2/error.log || tail -n {maxLines} /var/log/httpd/error_log) 2>&1",
            _ => null
        };
    }
}
