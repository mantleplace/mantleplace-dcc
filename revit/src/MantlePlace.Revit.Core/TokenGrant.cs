using System.Text.Json;

namespace MantlePlace.Revit.Core;

/// <summary>One successful grant response, already normalised.</summary>
public sealed class TokenGrant
{
    public required string AccessToken { get; init; }

    /// <summary>Empty when the response did not re-issue one — see <see cref="TokenGrants.ChooseRefreshToken"/>.</summary>
    public string RefreshToken { get; init; } = string.Empty;

    /// <summary>Always positive: an absent or non-positive <c>expires_in</c> is replaced.</summary>
    public required int ExpiresInSeconds { get; init; }

    public string UserId { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;
}

/// <summary>
/// ⛔<c>HPS-10</c>, <c>HPS-11</c> and <c>HPS-48</c>: reading a grant response without degrading the
/// session.
/// </summary>
public static class TokenGrants
{
    /// <summary>Substituted when the server states no usable lifetime.</summary>
    public const int DefaultAccessTokenLifetimeSeconds = 3600;

    /// <summary>
    /// Seconds a token is considered expired EARLY, so it does not lapse mid-request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A constant with no override, deliberately. The reference host's pure logic takes this as a
    /// parameter defaulting to 60 and its shim passes <c>0</c>, silently disabling the skew.
    /// Host #2 does not reproduce that: with no parameter there is nowhere for a caller to
    /// put a zero, so the bug is unrepresentable rather than merely absent.
    /// </para>
    /// <para>
    /// <c>HPS-11</c> says "at least 60 seconds". If a longer skew is ever wanted it is a change to
    /// this line, reviewed once, and not a per-call decision made by whoever is writing a request.
    /// </para>
    /// </remarks>
    public const int ExpirySkewSeconds = 60;

    /// <summary>
    /// Parses a grant response body.
    /// </summary>
    /// <returns><c>null</c> on success, or the message to show. On success <paramref name="grant"/> is set.</returns>
    public static string? TryParse(string body, out TokenGrant? grant)
    {
        grant = null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body ?? string.Empty);
        }
        catch (JsonException)
        {
            return "Invalid JSON in the sign-in response.";
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return "Invalid JSON in the sign-in response.";
            }

            if (DescribeError(root) is { } failure)
            {
                return failure;
            }

            string accessToken = root.Str("access_token");
            if (accessToken.Length == 0)
            {
                // A grant with no access token is a failure however cheerful the rest of the body
                // is. Returning success here would leave every later request unauthenticated with
                // no visible cause — the corpus calls this out because a parser that returns true
                // with an empty access token satisfies every other assertion in the file.
                return "The sign-in response carried no access token.";
            }

            // HPS-10: `now + 0` marks a freshly minted session as already expired, and the plugin
            // signs the user out between one call and the next.
            int expiresIn = root.OptionalInt("expires_in") is { } stated && stated > 0
                ? stated
                : DefaultAccessTokenLifetimeSeconds;

            JsonElement? user = root.Object("user");

            grant = new TokenGrant
            {
                AccessToken = accessToken,
                RefreshToken = root.Str("refresh_token"),
                ExpiresInSeconds = expiresIn,
                UserId = user?.Str("id") ?? string.Empty,
                Email = user?.Str("email") ?? string.Empty,
            };
            return null;
        }
    }

    /// <summary>
    /// ⛔<c>HPS-10</c>: a response that omits <c>refresh_token</c> keeps the prior one.
    /// </summary>
    /// <remarks>
    /// Wiping the cached token on a refresh that simply did not re-issue one costs the curator their
    /// session at the next restart — hours later, with nothing to connect it to.
    /// </remarks>
    public static string ChooseRefreshToken(string? issued, string? prior)
        => string.IsNullOrEmpty(issued) ? prior ?? string.Empty : issued;

    /// <summary>
    /// <c>HPS-11</c>: <c>expired ⇔ (now + skew) &gt;= expiresAt</c>. The boundary is inclusive.
    /// </summary>
    public static bool IsExpired(DateTimeOffset now, DateTimeOffset expiresAt)
        => now.AddSeconds(ExpirySkewSeconds) >= expiresAt;

    /// <summary>When a token minted now, for this many seconds, lapses.</summary>
    public static DateTimeOffset ExpiryFrom(DateTimeOffset now, int expiresInSeconds)
        => now.AddSeconds(expiresInSeconds);

    /// <summary>
    /// Reduces a grant error body to one message, or <c>null</c> when the body states no error.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="PlatformErrors"/> rather than carrying its own copy of the order.
    /// Two parsers with two orders is how two hosts came to show different text for the same 410
    /// once, and the
    /// cheapest way not to repeat it is to have only one implementation to get wrong.
    /// </remarks>
    public static string? DescribeError(JsonElement root)
        => PlatformErrors.TryRead(root, out PlatformError error) ? error.Message : null;
}
