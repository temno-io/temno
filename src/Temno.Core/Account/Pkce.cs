using System.Security.Cryptography;
using System.Text;

namespace Temno.Core.Account;

public static class Pkce
{
    public static string NewVerifier()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    public static string ChallengeS256(string verifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifier);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(hash);
    }

    public static string Base64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
