using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>
/// The two URLs the browser flow needs (<c>HPS-06</c>, <c>HPS-19</c>).
/// </summary>
public static class AuthUrls
{
    /// <summary>
    /// <c>http://127.0.0.1:{port}{path}</c> — the literal loopback IP, never <c>localhost</c>.
    /// </summary>
    /// <remarks>
    /// <see href="https://www.rfc-editor.org/rfc/rfc8252#section-8.3">RFC 8252 §8.3</see>: the
    /// literal address forces the loopback interface and side-steps DNS and hosts-file surprises,
    /// including a corporate resolver that answers <c>localhost</c> with something else.
    /// </remarks>
    public static string BuildLoopbackRedirectUri(int port, string callbackPath)
    {
        ArgumentNullException.ThrowIfNull(callbackPath);

        string path = callbackPath.StartsWith('/') ? callbackPath : "/" + callbackPath;
        return string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}{path}");
    }

    /// <summary>
    /// The system-browser URL, with the query in the order corpus
    /// <c>auth.pkceVectors.authorizeQueryOrder</c> pins.
    /// </summary>
    /// <remarks>
    /// Parameter order is not semantically required by OAuth, but it is pinned so two hosts produce
    /// byte-identical URLs — which is what makes a captured URL from one host a usable reproduction
    /// for the other.
    /// </remarks>
    public static string BuildAuthorizeUrl(
        string webLoginUrl,
        string redirectUri,
        string codeChallenge,
        string state)
    {
        ArgumentNullException.ThrowIfNull(webLoginUrl);

        string baseUrl = webLoginUrl.Trim();
        char separator = baseUrl.Contains('?', StringComparison.Ordinal) ? '&' : '?';

        return baseUrl
            + separator
            + "response_type=code"
            + "&code_challenge=" + PercentEncode(codeChallenge)
            + "&code_challenge_method=" + PkceCodes.ChallengeMethod
            + "&redirect_uri=" + PercentEncode(redirectUri)
            + "&state=" + PercentEncode(state);
    }

    /// <summary>
    /// Percent-encodes for a query value, leaving only the RFC 3986 unreserved set
    /// <c>A-Za-z0-9-._~</c>.
    /// </summary>
    public static string PercentEncode(string value)
        => Uri.EscapeDataString(value ?? string.Empty);

    /// <summary>
    /// Trims a base URL's trailing slashes so path concatenation cannot produce a double slash, and
    /// refuses one with no host (<c>HPS-19</c>).
    /// </summary>
    /// <remarks>
    /// A scheme with no authority — <c>"https:"</c> — concatenates into the hostless
    /// <c>"https:/auth/v1/token"</c>, which fails at DNS time with a message that names neither the
    /// misconfiguration nor the field it came from.
    /// </remarks>
    public static string? NormaliseBaseUrl(string? baseUrl)
    {
        string trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        return Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? parsed) && parsed.Host.Length > 0
            ? trimmed
            : null;
    }
}
