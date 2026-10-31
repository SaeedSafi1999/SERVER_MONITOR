using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using ServerMonitor.Application.Abstractions;
using ServerMonitor.Domain;
using ServerMonitor.Domain.Entities;

namespace ServerMonitor.Infrastructure.Remote.WinRm;

/// <summary>
/// Minimal WS-Management (WinRM) shell client: Create shell → Command → Receive → Signal → Delete.
/// Runs PowerShell scripts that are compiled into this application (never user input).
/// HTTPS certificates are pinned by SHA-256 thumbprint (self-signed WinRM listeners are common).
/// </summary>
public sealed class WinRmClient(ICredentialProtector protector, ILogger<WinRmClient> logger)
{
    private static readonly XNamespace S = "http://www.w3.org/2003/05/soap-envelope";
    private static readonly XNamespace A = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
    private static readonly XNamespace W = "http://schemas.dmtf.org/wbem/wsman/1/wsman.xsd";
    private static readonly XNamespace P = "http://schemas.microsoft.com/wbem/wsman/1/wsman.xsd";
    private static readonly XNamespace Rsp = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell";
    private const string ShellUri = "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/cmd";

    /// <summary>cmd.exe limits a command line to 8191 characters.</summary>
    private const int MaxCommandLine = 8000;

    public sealed record TestOutcome(string Output, string? Thumbprint);

    /// <summary>Runs a PowerShell script and returns stdout. Throws on transport/auth errors or non-zero exit.</summary>
    public async Task<CommandResult> RunPowerShellAsync(Server server, string script, TimeSpan timeout, CancellationToken ct, bool acceptUnpinnedCertificate = false)
    {
        string? presented = null;
        using var http = CreateHttpClient(server, timeout, acceptUnpinnedCertificate, t => presented = t);
        var endpoint = Endpoint(server);
        string? shellId = null;
        try
        {
            shellId = await CreateShellAsync(http, endpoint, ct);
            var commandId = await StartCommandAsync(http, endpoint, shellId, BuildPowerShellArgs(script), ct);
            var (stdout, stderr, exit) = await ReceiveAllAsync(http, endpoint, shellId, commandId, timeout, ct);
            await SendAsync(http, endpoint, Envelope(endpoint, "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Signal", shellId,
                new XElement(Rsp + "Signal", new XAttribute("CommandId", commandId),
                    new XElement(Rsp + "Code", "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/signal/terminate"))), ct, throwOnFault: false);
            return new CommandResult(stdout, CleanClixml(stderr), exit);
        }
        catch (HttpRequestException ex) when (ex.InnerException is System.Security.Authentication.AuthenticationException || ex.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            var state = server.TrustedCertificateThumbprint is null ? ConnectionState.HostKeyNotTrusted : ConnectionState.HostKeyMismatch;
            throw new RemoteConnectionException(state,
                state == ConnectionState.HostKeyMismatch
                    ? $"WinRM certificate mismatch — presented {presented}, pinned {server.TrustedCertificateThumbprint}. Possible MITM; collection blocked."
                    : $"WinRM certificate {presented} is not trusted yet.", ex, presented);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new RemoteConnectionException(ConnectionState.AuthenticationFailed, "WinRM authentication failed (401).", ex, presented);
        }
        catch (HttpRequestException ex) when (ex.InnerException is SocketException || ex.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            throw new RemoteConnectionException(ConnectionState.Unreachable, "Cannot reach WinRM endpoint: " + ex.Message, ex, presented);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new RemoteConnectionException(ConnectionState.Unreachable, "WinRM request timed out.", ex, presented);
        }
        finally
        {
            if (shellId is not null)
            {
                try
                {
                    await SendAsync(http, endpoint, Envelope(endpoint, "http://schemas.xmlsoap.org/ws/2004/09/transfer/Delete", shellId, null), CancellationToken.None, throwOnFault: false);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to delete WinRM shell on server {ServerId}", server.Id);
                }
            }
        }
    }

    /// <summary>Connects, runs the script and returns the presented certificate thumbprint (for pinning).</summary>
    public async Task<TestOutcome> TestAsync(Server server, string script, bool acceptUnpinned, CancellationToken ct)
    {
        string? presented = null;
        // First learn the thumbprint with a cheap TLS handshake so it can be returned even on auth failure.
        if (server.Protocol == ConnectionProtocol.WinRmHttps)
            presented = await GetCertificateThumbprintAsync(server.Host, server.ManagementPort, ct);
        var result = await RunPowerShellAsync(server, script, TimeSpan.FromSeconds(30), ct, acceptUnpinned);
        if (result.ExitCode is not (null or 0))
            throw new RemoteCommandException("Test script failed: " + result.StdErr, result.ExitCode);
        return new TestOutcome(result.StdOut, presented);
    }

    public static async Task<string?> GetCertificateThumbprintAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            await tcp.ConnectAsync(host, port, cts.Token);
            string? thumb = null;
            await using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) =>
            {
                if (cert is not null) thumb = Sha256Thumbprint(cert);
                return true; // only reading the certificate; no data is sent over this stream
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cts.Token);
            return thumb;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    public static string Sha256Thumbprint(X509Certificate cert) =>
        Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));

    private HttpClient CreateHttpClient(Server server, TimeSpan timeout, bool acceptUnpinned, Action<string> onPresented)
    {
        var password = server.EncryptedPassword is null ? "" : protector.Unprotect(server.EncryptedPassword);
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                {
                    if (cert is null) return false;
                    var thumb = Sha256Thumbprint(cert);
                    onPresented(thumb);
                    if (server.TrustedCertificateThumbprint is not null)
                        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(thumb), Encoding.ASCII.GetBytes(server.TrustedCertificateThumbprint));
                    // Not pinned: accept a publicly trusted cert, or anything when the admin is explicitly testing.
                    return errors == SslPolicyErrors.None || acceptUnpinned;
                }
            }
        };

        var http = new HttpClient(handler) { Timeout = timeout + TimeSpan.FromSeconds(15) };
        if (server.WinRmAuth == WinRmAuthScheme.Basic)
        {
            if (server.Protocol == ConnectionProtocol.WinRmHttp)
                logger.LogWarning("Server {ServerId} uses Basic auth over plain HTTP — credentials are only base64 encoded.", server.Id);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{server.Username}:{password}")));
        }
        else
        {
            var (domain, user) = SplitDomain(server.Username);
            handler.Credentials = new CredentialCache
            {
                { new Uri(Endpoint(server)), "Negotiate", new NetworkCredential(user, password, domain) },
                { new Uri(Endpoint(server)), "NTLM", new NetworkCredential(user, password, domain) }
            };
            handler.PreAuthenticate = true;
        }
        return http;
    }

    private static (string? Domain, string User) SplitDomain(string username)
    {
        var idx = username.IndexOf('\\');
        return idx > 0 ? (username[..idx], username[(idx + 1)..]) : (null, username);
    }

    private static string Endpoint(Server s) =>
        $"{(s.Protocol == ConnectionProtocol.WinRmHttp ? "http" : "https")}://{(s.Host.Contains(':') ? "[" + s.Host + "]" : s.Host)}:{s.ManagementPort}/wsman";

    /// <summary>
    /// Builds "powershell -EncodedCommand ..." where the encoded command is a tiny bootstrap that
    /// gunzips the real script. Keeps the command line well under cmd.exe's limit.
    /// </summary>
    public static string BuildPowerShellArgs(string script)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            gz.Write(Encoding.UTF8.GetBytes(script));
        var payload = Convert.ToBase64String(ms.ToArray());
        var bootstrap =
            "$ProgressPreference='SilentlyContinue';[Console]::OutputEncoding=[Text.Encoding]::UTF8;" +
            $"$m=New-Object IO.MemoryStream(,[Convert]::FromBase64String('{payload}'));" +
            "$g=New-Object IO.Compression.GZipStream($m,[IO.Compression.CompressionMode]::Decompress);" +
            "$r=New-Object IO.StreamReader($g,[Text.Encoding]::UTF8);$s=$r.ReadToEnd();Invoke-Expression $s";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap));
        var args = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded;
        if (args.Length + 20 > MaxCommandLine)
            throw new InvalidOperationException($"PowerShell script too large for WinRS command line ({args.Length} chars).");
        return args;
    }

    private static XDocument Envelope(string endpoint, string action, string? shellId, XElement? body, IEnumerable<XElement>? options = null)
    {
        var header = new XElement(S + "Header",
            new XElement(A + "To", endpoint),
            new XElement(W + "ResourceURI", new XAttribute(S + "mustUnderstand", "true"), ShellUri),
            new XElement(A + "ReplyTo", new XElement(A + "Address", new XAttribute(S + "mustUnderstand", "true"),
                "http://schemas.xmlsoap.org/ws/2004/08/addressing/role/anonymous")),
            new XElement(A + "Action", new XAttribute(S + "mustUnderstand", "true"), action),
            new XElement(W + "MaxEnvelopeSize", new XAttribute(S + "mustUnderstand", "true"), "512000"),
            new XElement(A + "MessageID", "uuid:" + Guid.NewGuid().ToString().ToUpperInvariant()),
            new XElement(W + "Locale", new XAttribute(XNamespace.Xml + "lang", "en-US"), new XAttribute(S + "mustUnderstand", "false")),
            new XElement(P + "DataLocale", new XAttribute(XNamespace.Xml + "lang", "en-US"), new XAttribute(S + "mustUnderstand", "false")),
            new XElement(W + "OperationTimeout", "PT60S"));
        if (shellId is not null)
            header.Add(new XElement(W + "SelectorSet", new XElement(W + "Selector", new XAttribute("Name", "ShellId"), shellId)));
        if (options is not null)
            header.Add(new XElement(W + "OptionSet", options));

        return new XDocument(new XElement(S + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", S), new XAttribute(XNamespace.Xmlns + "a", A),
            new XAttribute(XNamespace.Xmlns + "w", W), new XAttribute(XNamespace.Xmlns + "p", P),
            new XAttribute(XNamespace.Xmlns + "rsp", Rsp),
            header, new XElement(S + "Body", body)));
    }

    private async Task<string> CreateShellAsync(HttpClient http, string endpoint, CancellationToken ct)
    {
        var body = new XElement(Rsp + "Shell",
            new XElement(Rsp + "InputStreams", "stdin"),
            new XElement(Rsp + "OutputStreams", "stdout stderr"));
        var options = new[]
        {
            new XElement(W + "Option", new XAttribute("Name", "WINRS_NOPROFILE"), "TRUE"),
            new XElement(W + "Option", new XAttribute("Name", "WINRS_CODEPAGE"), "65001")
        };
        var response = await SendAsync(http, endpoint, Envelope(endpoint, "http://schemas.xmlsoap.org/ws/2004/09/transfer/Create", null, body, options), ct);
        var shellId = response.Descendants(Rsp + "ShellId").FirstOrDefault()?.Value
                      ?? response.Descendants(W + "Selector").FirstOrDefault(e => (string?)e.Attribute("Name") == "ShellId")?.Value;
        return shellId ?? throw new RemoteCommandException("WinRM did not return a ShellId.");
    }

    private async Task<string> StartCommandAsync(HttpClient http, string endpoint, string shellId, string arguments, CancellationToken ct)
    {
        var body = new XElement(Rsp + "CommandLine",
            new XElement(Rsp + "Command", "powershell.exe"),
            new XElement(Rsp + "Arguments", arguments));
        var options = new[]
        {
            new XElement(W + "Option", new XAttribute("Name", "WINRS_CONSOLEMODE_STDIN"), "TRUE"),
            new XElement(W + "Option", new XAttribute("Name", "WINRS_SKIP_CMD_SHELL"), "TRUE")
        };
        var response = await SendAsync(http, endpoint, Envelope(endpoint, "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Command", shellId, body, options), ct);
        return response.Descendants(Rsp + "CommandId").FirstOrDefault()?.Value
               ?? throw new RemoteCommandException("WinRM did not return a CommandId.");
    }

    private async Task<(string StdOut, string StdErr, int? Exit)> ReceiveAllAsync(HttpClient http, string endpoint, string shellId, string commandId, TimeSpan timeout, CancellationToken ct)
    {
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        int? exit = null;
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline) throw new RemoteCommandException($"Remote command timed out after {timeout.TotalSeconds:0}s.");

            var body = new XElement(Rsp + "Receive", new XElement(Rsp + "DesiredStream", new XAttribute("CommandId", commandId), "stdout stderr"));
            XDocument response;
            try
            {
                response = await SendAsync(http, endpoint, Envelope(endpoint, "http://schemas.microsoft.com/wbem/wsman/1/windows/shell/Receive", shellId, body), ct);
            }
            catch (WinRmFaultException ex) when (ex.IsOperationTimeout)
            {
                continue; // no output within OperationTimeout — keep waiting
            }

            foreach (var stream in response.Descendants(Rsp + "Stream"))
            {
                if (string.IsNullOrEmpty(stream.Value)) continue;
                var bytes = Convert.FromBase64String(stream.Value);
                ((string?)stream.Attribute("Name") == "stderr" ? stderr : stdout).Write(bytes);
            }

            var state = response.Descendants(Rsp + "CommandState").FirstOrDefault();
            if (state is not null && ((string?)state.Attribute("State"))?.EndsWith("Done", StringComparison.Ordinal) == true)
            {
                if (int.TryParse(state.Element(Rsp + "ExitCode")?.Value, out var code)) exit = code;
                break;
            }
        }
        return (Encoding.UTF8.GetString(stdout.ToArray()), Encoding.UTF8.GetString(stderr.ToArray()), exit);
    }

    private static async Task<XDocument> SendAsync(HttpClient http, string endpoint, XDocument envelope, CancellationToken ct, bool throwOnFault = true)
    {
        using var content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/soap+xml;charset=UTF-8");
        using var response = await http.PostAsync(endpoint, content, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);

        XDocument doc;
        try
        {
            doc = string.IsNullOrWhiteSpace(text) ? new XDocument() : XDocument.Parse(text);
        }
        catch (System.Xml.XmlException)
        {
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"WinRM HTTP {(int)response.StatusCode}", null, response.StatusCode);
            throw;
        }

        var fault = doc.Descendants(S + "Fault").FirstOrDefault();
        if (fault is not null && throwOnFault)
        {
            var code = fault.Descendants(S + "Value").LastOrDefault()?.Value ?? "";
            var reason = fault.Descendants(S + "Text").FirstOrDefault()?.Value
                         ?? fault.Descendants().FirstOrDefault(e => e.Name.LocalName == "Message")?.Value ?? "WinRM fault";
            throw new WinRmFaultException(code, reason.Trim());
        }
        if (!response.IsSuccessStatusCode && fault is null && throwOnFault)
            throw new HttpRequestException($"WinRM HTTP {(int)response.StatusCode}", null, response.StatusCode);
        return doc;
    }

    /// <summary>PowerShell writes errors to stderr as CLIXML; extract the readable text.</summary>
    private static string CleanClixml(string stderr)
    {
        if (!stderr.StartsWith("#< CLIXML", StringComparison.Ordinal)) return stderr.Trim();
        try
        {
            var xml = XDocument.Parse(stderr["#< CLIXML".Length..].Trim());
            var lines = xml.Descendants().Where(e => e.Name.LocalName == "S" && (string?)e.Attribute("S") == "Error").Select(e => e.Value.Replace("_x000D__x000A_", "\n"));
            return string.Concat(lines).Trim();
        }
        catch
        {
            return stderr;
        }
    }
}

public sealed class WinRmFaultException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
    public bool IsOperationTimeout => Code.Contains("TimedOut", StringComparison.OrdinalIgnoreCase);
}

