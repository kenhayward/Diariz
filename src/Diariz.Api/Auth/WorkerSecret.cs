using System.Security.Cryptography;
using System.Text;

namespace Diariz.Api.Auth;

/// <summary>The one check behind every <c>internal/*</c> worker callback's <c>X-Worker-Secret</c> header. Kept in
/// one place so the four callback controllers cannot drift apart, and so the two rules below hold everywhere:
/// <list type="bullet">
/// <item>a blank configured secret authorises nothing - an unset secret must fail closed, never compare equal to
/// a blank header;</item>
/// <item>the comparison is fixed-time over the bytes, so response timing says nothing about how much of a
/// guess was right.</item>
/// </list></summary>
public static class WorkerSecret
{
    public const string HeaderName = "X-Worker-Secret";

    public static bool Matches(string? presented, string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured) || presented is null) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(configured));
    }
}
