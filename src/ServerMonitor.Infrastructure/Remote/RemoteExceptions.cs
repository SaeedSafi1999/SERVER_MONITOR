using ServerMonitor.Domain;

namespace ServerMonitor.Infrastructure.Remote;

/// <summary>A connection-level failure with a classified <see cref="ConnectionState"/>.</summary>
public class RemoteConnectionException(ConnectionState state, string message, Exception? inner = null, string? presentedFingerprint = null)
    : Exception(message, inner)
{
    public ConnectionState State { get; } = state;
    /// <summary>Fingerprint/thumbprint presented by the host (for the "trust this key" flow).</summary>
    public string? PresentedFingerprint { get; } = presentedFingerprint;

    /// <summary>Authentication / trust errors should not be retried — retrying only locks accounts.</summary>
    public bool IsTransient => State is ConnectionState.Unreachable or ConnectionState.Error;
}

/// <summary>The remote command ran but reported failure.</summary>
public class RemoteCommandException(string message, int? exitCode = null) : Exception(message)
{
    public int? ExitCode { get; } = exitCode;
}

public sealed record CommandResult(string StdOut, string StdErr, int? ExitCode);
