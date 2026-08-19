namespace MantlePlace.Revit.Core;

/// <summary>
/// base64url per <see href="https://www.rfc-editor.org/rfc/rfc4648#section-5">RFC 4648 §5</see>:
/// the standard alphabet with <c>+</c>→<c>-</c>, <c>/</c>→<c>_</c>, and all <c>=</c> padding stripped.
/// </summary>
/// <remarks>
/// Its own type because two unrelated things depend on it being exactly right — the PKCE verifier
/// and the challenge (<c>HPS-04</c>) — and because the failure is invisible locally. An encoder that
/// leaves <c>+</c>, <c>/</c> or <c>=</c> in place produces a verifier the authorization server
/// rejects only at the exchange step, long after the flow looks correct. Pinned by corpus
/// <c>auth.pkceVectors.base64url</c>.
/// </remarks>
public static class Base64Url
{
    /// <summary>Encodes bytes. Never returns padding.</summary>
    public static string Encode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
