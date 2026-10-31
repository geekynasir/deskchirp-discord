using System.Security.Cryptography;
using System.Text;

namespace DeveloperPresence.Core.T3;

/// <summary>
/// RFC 7636 Proof Key for Code Exchange (PKCE) with S256 challenge generation.
/// </summary>
public static class PkceUtility
{
    public static (string Verifier, string Challenge) GeneratePkce()
    {
        // 32 cryptographically random bytes -> 43 characters base64url
        byte[] bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        string verifier = Base64UrlEncode(bytes);

        // SHA-256 of ASCII verifier bytes
        byte[] verifierBytes = Encoding.ASCII.GetBytes(verifier);
        byte[] hash = SHA256.HashData(verifierBytes);
        string challenge = Base64UrlEncode(hash);

        return (verifier, challenge);
    }

    public static string GenerateState()
    {
        byte[] bytes = new byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    public static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
