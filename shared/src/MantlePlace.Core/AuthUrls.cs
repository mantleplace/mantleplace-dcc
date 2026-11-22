using System.Globalization;

namespace MantlePlace.Core;

public static class AuthUrls
{
    public static string BuildLoopbackRedirectUri(int port, string callbackPath)
    {
        ArgumentNullException.ThrowIfNull(callbackPath);

        string path = callbackPath.StartsWith('/') ? callbackPath : "/" + callbackPath;
        return string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}{path}");
    }

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

    public static string PercentEncode(string value)
        => Uri.EscapeDataString(value ?? string.Empty);

    public static string? NormaliseBaseUrl(string? baseUrl)
    {
        string trimmed = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        return Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? parsed) && parsed.Host.Length > 0
            ? trimmed
            : null;
    }
}
