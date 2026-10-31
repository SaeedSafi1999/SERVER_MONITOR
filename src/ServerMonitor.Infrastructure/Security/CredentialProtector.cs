using Microsoft.AspNetCore.DataProtection;
using ServerMonitor.Application.Abstractions;

namespace ServerMonitor.Infrastructure.Security;

/// <summary>
/// AES-256-CBC + HMACSHA256 authenticated encryption via ASP.NET Core Data Protection.
/// The key ring lives outside the database (mounted volume, optionally wrapped with an X.509
/// certificate) so a database dump alone never reveals server credentials.
/// </summary>
public sealed class CredentialProtector(IDataProtectionProvider provider) : ICredentialProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("ServerMonitor.Credentials.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);
}
