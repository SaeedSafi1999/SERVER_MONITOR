using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace ServerMonitor.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "text", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EntityId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Details = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "NotificationChannels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    MinimumSeverity = table.Column<int>(type: "integer", nullable: false),
                    EncryptedConfig = table.Column<string>(type: "text", nullable: false),
                    NotifyOnResolve = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationChannels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ServerGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ServerGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SystemSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MetricRetentionDays = table.Column<int>(type: "integer", nullable: false),
                    ProbeRetentionDays = table.Column<int>(type: "integer", nullable: false),
                    AlertRetentionDays = table.Column<int>(type: "integer", nullable: false),
                    SecurityEventRetentionDays = table.Column<int>(type: "integer", nullable: false),
                    AuditRetentionDays = table.Column<int>(type: "integer", nullable: false),
                    SlaTargetPercent = table.Column<double>(type: "double precision", nullable: false),
                    IcmpPacketsPerRound = table.Column<int>(type: "integer", nullable: false),
                    ProbeTimeoutMs = table.Column<int>(type: "integer", nullable: false),
                    MaxConcurrentCollections = table.Column<int>(type: "integer", nullable: false),
                    SecurityScanIntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    CertificateWarningDays = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SystemSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<string>(type: "text", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<string>(type: "text", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    RoleId = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "text", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Servers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    OperatingSystem = table.Column<int>(type: "integer", nullable: false),
                    OsVersion = table.Column<string>(type: "text", nullable: true),
                    Protocol = table.Column<int>(type: "integer", nullable: false),
                    ManagementPort = table.Column<int>(type: "integer", nullable: false),
                    RdpPort = table.Column<int>(type: "integer", nullable: true),
                    Username = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    AuthMethod = table.Column<int>(type: "integer", nullable: false),
                    WinRmAuth = table.Column<int>(type: "integer", nullable: false),
                    EncryptedPassword = table.Column<string>(type: "text", nullable: true),
                    EncryptedPrivateKey = table.Column<string>(type: "text", nullable: true),
                    EncryptedPrivateKeyPassphrase = table.Column<string>(type: "text", nullable: true),
                    TrustedHostKeyFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TrustedCertificateThumbprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    MonitoringIntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    IsMonitoringEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    Tags = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Location = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    HealthCheckUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    HealthCheckExpectedStatus = table.Column<int>(type: "integer", nullable: true),
                    TcpProbePort = table.Column<int>(type: "integer", nullable: true),
                    IcmpEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AllowServiceControl = table.Column<bool>(type: "boolean", nullable: false),
                    GroupId = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ConnectionState = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastCollectionAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StatusChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CertificateSubject = table.Column<string>(type: "text", nullable: true),
                    CertificateIssuer = table.Column<string>(type: "text", nullable: true),
                    CertificateNotAfter = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CertificateValid = table.Column<bool>(type: "boolean", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Servers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Servers_ServerGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "ServerGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "AlertRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<int>(type: "integer", nullable: true),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Metric = table.Column<int>(type: "integer", nullable: false),
                    Operator = table.Column<int>(type: "integer", nullable: false),
                    Threshold = table.Column<double>(type: "double precision", nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    ConsecutiveBreaches = table.Column<int>(type: "integer", nullable: false),
                    CooldownMinutes = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertRules_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Alerts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<int>(type: "integer", nullable: false),
                    RuleId = table.Column<int>(type: "integer", nullable: true),
                    Metric = table.Column<int>(type: "integer", nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Value = table.Column<double>(type: "double precision", nullable: true),
                    Threshold = table.Column<double>(type: "double precision", nullable: true),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OccurrenceCount = table.Column<int>(type: "integer", nullable: false),
                    LastNotifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolvedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Alerts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Alerts_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MetricSnapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<int>(type: "integer", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CpuPercent = table.Column<double>(type: "double precision", nullable: false),
                    CpuCores = table.Column<int>(type: "integer", nullable: false),
                    PerCoreJson = table.Column<string>(type: "text", nullable: true),
                    Load1 = table.Column<double>(type: "double precision", nullable: true),
                    Load5 = table.Column<double>(type: "double precision", nullable: true),
                    Load15 = table.Column<double>(type: "double precision", nullable: true),
                    CpuTemperatureC = table.Column<double>(type: "double precision", nullable: true),
                    CpuFrequencyMhz = table.Column<double>(type: "double precision", nullable: true),
                    MemTotal = table.Column<long>(type: "bigint", nullable: false),
                    MemUsed = table.Column<long>(type: "bigint", nullable: false),
                    MemFree = table.Column<long>(type: "bigint", nullable: false),
                    MemAvailable = table.Column<long>(type: "bigint", nullable: false),
                    SwapTotal = table.Column<long>(type: "bigint", nullable: false),
                    SwapUsed = table.Column<long>(type: "bigint", nullable: false),
                    DiskTotal = table.Column<long>(type: "bigint", nullable: false),
                    DiskUsed = table.Column<long>(type: "bigint", nullable: false),
                    DiskReadBytesPerSec = table.Column<double>(type: "double precision", nullable: false),
                    DiskWriteBytesPerSec = table.Column<double>(type: "double precision", nullable: false),
                    DiskReadLatencyMs = table.Column<double>(type: "double precision", nullable: true),
                    DiskWriteLatencyMs = table.Column<double>(type: "double precision", nullable: true),
                    NetRxBytesPerSec = table.Column<double>(type: "double precision", nullable: false),
                    NetTxBytesPerSec = table.Column<double>(type: "double precision", nullable: false),
                    NetRxTotalBytes = table.Column<long>(type: "bigint", nullable: false),
                    NetTxTotalBytes = table.Column<long>(type: "bigint", nullable: false),
                    NetErrors = table.Column<long>(type: "bigint", nullable: false),
                    NetDrops = table.Column<long>(type: "bigint", nullable: false),
                    TcpConnections = table.Column<int>(type: "integer", nullable: false),
                    TcpEstablished = table.Column<int>(type: "integer", nullable: false),
                    ProcessCount = table.Column<int>(type: "integer", nullable: false),
                    ThreadCount = table.Column<int>(type: "integer", nullable: false),
                    OpenFileDescriptors = table.Column<long>(type: "bigint", nullable: true),
                    ActiveSessions = table.Column<int>(type: "integer", nullable: true),
                    UptimeSeconds = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MetricSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MetricSnapshots_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MonitoredPorts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<int>(type: "integer", nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    Protocol = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    ServiceName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ExpectOpen = table.Column<bool>(type: "boolean", nullable: false),
                    IsOpen = table.Column<bool>(type: "boolean", nullable: true),
                    ResponseTimeMs = table.Column<double>(type: "double precision", nullable: true),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastChangedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoredPorts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MonitoredPorts_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MonitoredServices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    IsCustom = table.Column<bool>(type: "boolean", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AlertWhenStopped = table.Column<bool>(type: "boolean", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    SubState = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    StartupType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastStateChangeAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoredServices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MonitoredServices_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProbeResults",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<int>(type: "integer", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Success = table.Column<bool>(type: "boolean", nullable: false),
                    LatencyMs = table.Column<double>(type: "double precision", nullable: true),
                    DnsMs = table.Column<double>(type: "double precision", nullable: true),
                    TcpConnectMs = table.Column<double>(type: "double precision", nullable: true),
                    TlsHandshakeMs = table.Column<double>(type: "double precision", nullable: true),
                    TtfbMs = table.Column<double>(type: "double precision", nullable: true),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    Error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProbeResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProbeResults_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SecurityEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<int>(type: "integer", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Severity = table.Column<int>(type: "integer", nullable: false),
                    UserName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SourceIp = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SecurityEvents_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StatusPeriods",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ServerId = table.Column<int>(type: "integer", nullable: false),
                    IsUp = table.Column<bool>(type: "boolean", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StatusPeriods", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StatusPeriods_Servers_ServerId",
                        column: x => x.ServerId,
                        principalTable: "Servers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "AlertRules",
                columns: new[] { "Id", "ConsecutiveBreaches", "CooldownMinutes", "IsEnabled", "Metric", "Name", "Operator", "ServerId", "Severity", "Threshold" },
                values: new object[,]
                {
                    { 1, 3, 15, true, 0, "CPU high", 0, null, 1, 85.0 },
                    { 2, 3, 15, true, 0, "CPU critical", 0, null, 2, 95.0 },
                    { 3, 2, 15, true, 1, "Memory high", 0, null, 1, 90.0 },
                    { 4, 1, 15, true, 2, "Disk high", 0, null, 2, 90.0 },
                    { 5, 2, 15, true, 3, "Ping latency", 0, null, 1, 150.0 },
                    { 6, 2, 15, true, 4, "Packet loss", 0, null, 1, 5.0 },
                    { 7, 2, 15, true, 5, "HTTP slow", 0, null, 1, 500.0 },
                    { 8, 2, 15, true, 6, "Server offline", 0, null, 2, 0.0 },
                    { 9, 1, 15, true, 7, "Service stopped", 0, null, 2, 0.0 },
                    { 10, 1, 720, true, 8, "Certificate expiring", 2, null, 1, 14.0 },
                    { 11, 2, 15, true, 9, "Port closed", 0, null, 1, 0.0 },
                    { 12, 1, 60, true, 12, "Brute force", 0, null, 1, 20.0 }
                });

            migrationBuilder.InsertData(
                table: "SystemSettings",
                columns: new[] { "Id", "AlertRetentionDays", "AuditRetentionDays", "CertificateWarningDays", "IcmpPacketsPerRound", "MaxConcurrentCollections", "MetricRetentionDays", "ProbeRetentionDays", "ProbeTimeoutMs", "SecurityEventRetentionDays", "SecurityScanIntervalMinutes", "SlaTargetPercent" },
                values: new object[] { 1, 180, 365, 21, 4, 16, 14, 14, 3000, 90, 5, 99.900000000000006 });

            migrationBuilder.CreateIndex(
                name: "IX_AlertRules_ServerId",
                table: "AlertRules",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_Fingerprint_Status",
                table: "Alerts",
                columns: new[] { "Fingerprint", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_ServerId",
                table: "Alerts",
                column: "ServerId");

            migrationBuilder.CreateIndex(
                name: "IX_Alerts_Status_LastSeenAt",
                table: "Alerts",
                columns: new[] { "Status", "LastSeenAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_Timestamp",
                table: "AuditLogs",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_MetricSnapshots_ServerId_Timestamp",
                table: "MetricSnapshots",
                columns: new[] { "ServerId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_MetricSnapshots_Timestamp",
                table: "MetricSnapshots",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoredPorts_ServerId_Port_Protocol",
                table: "MonitoredPorts",
                columns: new[] { "ServerId", "Port", "Protocol" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MonitoredServices_ServerId_Name",
                table: "MonitoredServices",
                columns: new[] { "ServerId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProbeResults_ServerId_Type_Timestamp",
                table: "ProbeResults",
                columns: new[] { "ServerId", "Type", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_ProbeResults_Timestamp",
                table: "ProbeResults",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_Fingerprint",
                table: "SecurityEvents",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_ServerId_Timestamp",
                table: "SecurityEvents",
                columns: new[] { "ServerId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_ServerGroups_Name",
                table: "ServerGroups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Servers_GroupId",
                table: "Servers",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_Servers_Name",
                table: "Servers",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StatusPeriods_ServerId_StartedAt",
                table: "StatusPeriods",
                columns: new[] { "ServerId", "StartedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertRules");

            migrationBuilder.DropTable(
                name: "Alerts");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "MetricSnapshots");

            migrationBuilder.DropTable(
                name: "MonitoredPorts");

            migrationBuilder.DropTable(
                name: "MonitoredServices");

            migrationBuilder.DropTable(
                name: "NotificationChannels");

            migrationBuilder.DropTable(
                name: "ProbeResults");

            migrationBuilder.DropTable(
                name: "SecurityEvents");

            migrationBuilder.DropTable(
                name: "StatusPeriods");

            migrationBuilder.DropTable(
                name: "SystemSettings");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "Servers");

            migrationBuilder.DropTable(
                name: "ServerGroups");
        }
    }
}
