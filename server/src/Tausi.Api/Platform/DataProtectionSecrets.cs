using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Tausi.Application.Mpesa;

namespace Tausi.Api.Platform;

/// <summary>
/// M-Pesa secrets in the database, encrypted with the server's data-protection keys (data\keys). A
/// backup restored on another PC can't read them: Settings → M-Pesa asks for them again.
/// </summary>
public sealed class DataProtectionSecrets(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("Tausi.Mpesa.Secrets.v1");

    public string Protect(string plain) => _protector.Protect(plain);

    public string? Unprotect(string protectedText)
    {
        try
        {
            return _protector.Unprotect(protectedText);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
