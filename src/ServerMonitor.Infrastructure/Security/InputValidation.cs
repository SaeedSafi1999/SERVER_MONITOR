using System.Text.RegularExpressions;

namespace ServerMonitor.Infrastructure.Security;

/// <summary>
/// Strict allow-list validation for any value that is ever interpolated into a remote command.
/// Anything not matching is rejected before a connection is opened.
/// </summary>
public static partial class InputValidation
{
    // systemd unit names: letters, digits, ':', '-', '_', '.', '@' (no spaces, quotes, shell metacharacters).
    [GeneratedRegex(@"^[A-Za-z0-9:_.@\-]{1,128}$")]
    private static partial Regex LinuxUnitRegex();

    // Windows service names: letters, digits, '_', '-', '.', '$' (MSSQL$INSTANCE). No spaces or quotes.
    [GeneratedRegex(@"^[A-Za-z0-9_.\-$]{1,128}$")]
    private static partial Regex WindowsServiceRegex();

    [GeneratedRegex(@"^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9\-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex HostnameRegex();

    public static bool IsValidLinuxUnit(string? name) => name is not null && LinuxUnitRegex().IsMatch(name) && !name.StartsWith('-');

    public static bool IsValidWindowsService(string? name) => name is not null && WindowsServiceRegex().IsMatch(name) && !name.StartsWith('-');

    public static bool IsValidHost(string? host) =>
        !string.IsNullOrWhiteSpace(host) &&
        (System.Net.IPAddress.TryParse(host, out _) || HostnameRegex().IsMatch(host));

    public static string RequireLinuxUnit(string name) =>
        IsValidLinuxUnit(name) ? name : throw new ArgumentException($"Invalid service name '{name}'.");

    public static string RequireWindowsService(string name) =>
        IsValidWindowsService(name) ? name : throw new ArgumentException($"Invalid service name '{name}'.");

    /// <summary>Wraps a pre-validated token in single quotes for POSIX shells (defence in depth).</summary>
    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";

    /// <summary>Wraps a pre-validated token in a PowerShell single-quoted literal.</summary>
    public static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";
}
