namespace ServerMonitor.Domain.Entities;

/// <summary>
/// A monitored host. Credentials are stored only as ciphertext (see ICredentialProtector);
/// the plain values never leave the backend.
/// </summary>
public class Server
{
    public int Id { get; set; }
    public required string Name { get; set; }
    /// <summary>IP address or DNS hostname.</summary>
    public required string Host { get; set; }
    public OperatingSystemFamily OperatingSystem { get; set; }
    /// <summary>Free-form OS description reported by the host (e.g. "Ubuntu 24.04.1 LTS").</summary>
    public string? OsVersion { get; set; }
    public ConnectionProtocol Protocol { get; set; }
    /// <summary>SSH port (Linux) or WinRM port (Windows).</summary>
    public int ManagementPort { get; set; } = 22;
    /// <summary>Optional RDP port for Windows hosts (checked as a TCP port, never connected to).</summary>
    public int? RdpPort { get; set; }

    public required string Username { get; set; }
    public AuthenticationMethod AuthMethod { get; set; }
    public WinRmAuthScheme WinRmAuth { get; set; }
    public string? EncryptedPassword { get; set; }
    public string? EncryptedPrivateKey { get; set; }
    public string? EncryptedPrivateKeyPassphrase { get; set; }

    /// <summary>Pinned SSH host key fingerprint ("SHA256:...") — required before collection runs.</summary>
    public string? TrustedHostKeyFingerprint { get; set; }
    /// <summary>Pinned WinRM HTTPS certificate SHA-256 thumbprint.</summary>
    public string? TrustedCertificateThumbprint { get; set; }

    public int MonitoringIntervalSeconds { get; set; } = 30;
    public bool IsMonitoringEnabled { get; set; } = true;
    /// <summary>Comma separated tags.</summary>
    public string Tags { get; set; } = "";
    public string? Location { get; set; }
    public string? Description { get; set; }

    /// <summary>Optional URL for HTTP/HTTPS health check (TTFB, TLS timing, certificate expiry).</summary>
    public string? HealthCheckUrl { get; set; }
    public int? HealthCheckExpectedStatus { get; set; }
    /// <summary>TCP port used for the TCP-connect latency probe. Defaults to ManagementPort.</summary>
    public int? TcpProbePort { get; set; }
    public bool IcmpEnabled { get; set; } = true;
    /// <summary>Allows Operators to restart services on this host (sudo -n / service control rights required).</summary>
    public bool AllowServiceControl { get; set; }

    public int? GroupId { get; set; }
    public ServerGroup? Group { get; set; }

    // Latest state (denormalised for fast dashboard queries).
    public HealthStatus Status { get; set; } = HealthStatus.Unknown;
    public ConnectionState ConnectionState { get; set; } = ConnectionState.Unknown;
    public string? LastError { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? LastCollectionAttemptAt { get; set; }
    public DateTimeOffset? StatusChangedAt { get; set; }

    // Latest TLS certificate seen by the HTTP probe.
    public string? CertificateSubject { get; set; }
    public string? CertificateIssuer { get; set; }
    public DateTimeOffset? CertificateNotAfter { get; set; }
    public bool? CertificateValid { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<MonitoredService> Services { get; set; } = [];
    public List<MonitoredPort> Ports { get; set; } = [];

    public IEnumerable<string> TagList =>
        Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool IsWindows => OperatingSystem == OperatingSystemFamily.Windows;
}

public class ServerGroup
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public List<Server> Servers { get; set; } = [];
}
