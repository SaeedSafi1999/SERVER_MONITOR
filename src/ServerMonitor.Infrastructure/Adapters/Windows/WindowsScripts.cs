namespace ServerMonitor.Infrastructure.Adapters.Windows;

/// <summary>
/// Fixed PowerShell scripts (Windows PowerShell 5.1 compatible). Performance data is read from
/// locale-independent raw CIM counters twice, one second apart; rates are computed in C#.
/// Dates are always emitted as ISO-8601 UTC strings.
/// </summary>
internal static class WindowsScripts
{
    private const string Prelude = "$ErrorActionPreference='SilentlyContinue';$ProgressPreference='SilentlyContinue';";

    public const string Identify =
        Prelude +
        "$o=Get-CimInstance Win32_OperatingSystem;" +
        "[pscustomobject]@{host=$env:COMPUTERNAME;os=$o.Caption+' '+$o.Version}|ConvertTo-Json -Compress";

    public const string Metrics =
        Prelude + """
function S {
 [pscustomobject]@{
  cpu=@(Get-CimInstance Win32_PerfRawData_PerfOS_Processor|select Name,PercentProcessorTime,Timestamp_Sys100NS)
  disk=@(Get-CimInstance Win32_PerfRawData_PerfDisk_PhysicalDisk|?{$_.Name -ne '_Total'}|select Name,DiskReadBytesPersec,DiskWriteBytesPersec,AvgDisksecPerRead,AvgDisksecPerRead_Base,AvgDisksecPerWrite,AvgDisksecPerWrite_Base,Timestamp_PerfTime,Frequency_PerfTime)
  net=@(Get-CimInstance Win32_PerfRawData_Tcpip_NetworkInterface|select Name,BytesReceivedPersec,BytesSentPersec,Timestamp_PerfTime,Frequency_PerfTime)
 }
}
$a=S;Start-Sleep -Milliseconds 1000;$b=S
$o=Get-CimInstance Win32_OperatingSystem
$p=@(Get-CimInstance Win32_Processor)
$sy=Get-CimInstance Win32_PerfRawData_PerfOS_System
$ld=@(Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3'|select DeviceID,FileSystem,Size,FreeSpace)
$pf=@(Get-CimInstance Win32_PageFileUsage|select AllocatedBaseSize,CurrentUsage)
$tc=@(Get-NetTCPConnection|group State|%{[pscustomobject]@{s=[string]$_.Name;c=$_.Count}})
$ls=@(Get-NetTCPConnection -State Listen|select @{n='a';e={[string]$_.LocalAddress}},@{n='p';e={[int]$_.LocalPort}} -Unique)
$ad=@(Get-NetAdapter|%{$st=Get-NetAdapterStatistics -Name $_.Name;[pscustomobject]@{n=$_.Name;d=$_.InterfaceDescription;up=([string]$_.Status -eq 'Up');sp=[int64]$_.Speed;rx=[int64]$st.ReceivedBytes;tx=[int64]$st.SentBytes;re=[int64]$st.ReceivedPacketErrors;te=[int64]$st.OutboundPacketErrors;rd=[int64]$st.ReceivedDiscardedPackets;td=[int64]$st.OutboundDiscardedPackets}})
$h=(Get-Process|Measure-Object HandleCount -Sum).Sum
$q=@(quser 2>$null).Count;if($q -gt 0){$q--}
$tz=$null;$t=Get-CimInstance -Namespace root/wmi MSAcpi_ThermalZoneTemperature;if($t){$tz=(@($t)[0].CurrentTemperature/10)-273.15}
[pscustomobject]@{a=$a;b=$b;host=$env:COMPUTERNAME;os=$o.Caption+' '+$o.Version;memTotalKb=[int64]$o.TotalVisibleMemorySize;memFreeKb=[int64]$o.FreePhysicalMemory;
boot=$o.LastBootUpTime.ToUniversalTime().ToString('o');cores=($p|Measure-Object NumberOfLogicalProcessors -Sum).Sum;mhz=($p|Measure-Object CurrentClockSpeed -Average).Average;
procs=[int]$sy.Processes;threads=[int]$sy.Threads;queue=[int]$sy.ProcessorQueueLength;disks=$ld;page=$pf;tcp=$tc;listen=$ls;adapters=$ad;handles=[int64]$h;sessions=$q;temp=$tz}|ConvertTo-Json -Compress -Depth 5
""";

    public const string Processes =
        Prelude + """
function S{@(Get-CimInstance Win32_PerfRawData_PerfProc_Process|?{$_.Name -ne '_Total' -and $_.Name -ne 'Idle'}|select IDProcess,Name,PercentProcessorTime,Timestamp_Sys100NS,WorkingSetPrivate,ThreadCount)}
$a=S;Start-Sleep -Milliseconds 1000;$b=S
$o=Get-CimInstance Win32_OperatingSystem
[pscustomobject]@{a=$a;b=$b;memTotalKb=[int64]$o.TotalVisibleMemorySize}|ConvertTo-Json -Compress -Depth 4
""";

    /// <param name="wqlFilter">Pre-validated "Name='a' OR Name='b'" filter (names restricted to [A-Za-z0-9_.-$]).</param>
    public static string Services(string wqlFilter) =>
        Prelude +
        $"ConvertTo-Json -Compress -InputObject @(Get-CimInstance Win32_Service -Filter \"{wqlFilter}\"|select Name,DisplayName,State,StartMode,ExitCode)";

    public static string RestartService(string quotedName) =>
        "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';" +
        $"try{{Restart-Service -Name {quotedName} -Force;(Get-Service -Name {quotedName}).Status.ToString()}}catch{{Write-Output ('ERROR: '+$_.Exception.Message);exit 1}}";

    public static string Security(DateTimeOffset since) =>
        Prelude + $"$since=[DateTime]::Parse('{since.UtcDateTime:o}').ToUniversalTime();" + """
$f=@(Get-WinEvent -FilterHashtable @{LogName='Security';Id=4625;StartTime=$since} -MaxEvents 500|%{[pscustomobject]@{t=$_.TimeCreated.ToUniversalTime().ToString('o');u=[string]$_.Properties[5].Value;ip=[string]$_.Properties[19].Value;lt=[string]$_.Properties[10].Value}})
$s=@(Get-WinEvent -FilterHashtable @{LogName='Security';Id=4624;StartTime=$since} -MaxEvents 2000|?{$_.Properties[8].Value -in 2,10,11}|select -First 200|%{[pscustomobject]@{t=$_.TimeCreated.ToUniversalTime().ToString('o');u=[string]$_.Properties[5].Value;ip=[string]$_.Properties[18].Value;lt=[string]$_.Properties[8].Value}})
$secReadable=$true;try{Get-WinEvent -LogName Security -MaxEvents 1 -ErrorAction Stop|Out-Null}catch{$secReadable=$false}
$fw=@(Get-NetFirewallProfile|%{[pscustomobject]@{n=[string]$_.Name;e=[bool]$_.Enabled}})
$upd=$null;$sec=$null
try{$us=New-Object -ComObject Microsoft.Update.Session;$r=$us.CreateUpdateSearcher().Search("IsInstalled=0 and IsHidden=0 and Type='Software'");$upd=$r.Updates.Count;$sec=@($r.Updates|?{$_.MsrcSeverity -in 'Critical','Important'}).Count}catch{}
$rb=Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'
$bl=$null;try{$bl=@(Get-BitLockerVolume -ErrorAction Stop|%{[string]$_.MountPoint+'='+[string]$_.ProtectionStatus}) -join ', '}catch{}
$ls=@(Get-NetTCPConnection -State Listen|select @{n='a';e={[string]$_.LocalAddress}},@{n='p';e={[int]$_.LocalPort}} -Unique)
[pscustomobject]@{failed=$f;success=$s;secReadable=$secReadable;fw=$fw;updates=$upd;secUpdates=$sec;reboot=$rb;bitlocker=$bl;listen=$ls}|ConvertTo-Json -Compress -Depth 4
""";

    /// <summary>Allow-listed event logs only.</summary>
    public static string? Logs(string sourceKey, int maxEvents, DateTimeOffset? since)
    {
        var log = sourceKey switch
        {
            "system" => "System",
            "application" => "Application",
            "security" => "Security",
            "setup" => "Setup",
            _ => null
        };
        if (log is null) return null;
        var start = since is null ? "" : $";StartTime=[DateTime]::Parse('{since.Value.UtcDateTime:o}').ToUniversalTime()";
        return Prelude +
               $"ConvertTo-Json -Compress -InputObject @(Get-WinEvent -FilterHashtable @{{LogName='{log}'{start}}} -MaxEvents {maxEvents}|" +
               "%{[pscustomobject]@{t=$_.TimeCreated.ToUniversalTime().ToString('o');l=[int]$_.Level;p=[string]$_.ProviderName;i=[int]$_.Id;m=[string]$_.Message}})";
    }
}
