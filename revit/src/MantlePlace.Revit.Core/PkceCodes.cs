using System.Security.Cryptography;
using System.Text;

namespace MantlePlace.Revit.Core;

/// <summary>
/// ⛔<c>HPS-04</c>: the PKCE code verifier and its <c>S256</c> challenge
/// (<see href="https://www.rfc-editor.org/rfc/rfc7636">RFC 7636</see>).
/// </summary>
/// <remarks>
/// PKCE is the one place in this flow where a plausible-looking implementation is silently
/// insecure, so both halves are pinned by vectors rather than by reading: the RFC 7636 Appendix B
/// pair proves <c>S256</c> end to end, and the base64url triples catch the padding and <c>+</c>
/// / <c>/</c> substitution bugs. <c>plain</c> is never used and is not implemented here.
/// </remarks>
public static class PkceCodes
{
    /// <summary>CSPRNG bytes behind a verifier. RFC 7636 §4.1 allows 32–96; the floor is the rule.</summary>
    public const int VerifierEntropyBytes = 32;

    /// <summary>What <see cref="VerifierEntropyBytes"/> base64url-encodes to, with padding stripped.</summary>
    public const int VerifierEncodedLength = 43;

    /// <summary>The only challenge method this host offers.</summary>
    public const string ChallengeMethod = "S256";

    /// <summary>Mints a fresh verifier from cryptographically secure randomness.</summary>
    public static string MakeCodeVerifier()
        => Base64Url.Encode(RandomNumberGenerator.GetBytes(VerifierEntropyBytes));

    /// <summary><c>base64url(SHA256(utf8(verifier)))</c>.</summary>
    public static string MakeCodeChallengeS256(string codeVerifier)
    {
        ArgumentNullException.ThrowIfNull(codeVerifier);
        return Base64Url.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(codeVerifier)));
    }

    /// <summary>
    /// Mints the CSRF <c>state</c>. Same entropy source and encoding as the verifier — it is
    /// compared for equality and never parsed, so there is nothing to gain from a second scheme.
    /// </summary>
    public static string MakeState()
        => Base64Url.Encode(RandomNumberGenerator.GetBytes(VerifierEntropyBytes));
}
