using System.Text.Json;

namespace MantlePlace.Core;

public sealed class TokenGrant
{
    public required string AccessToken { get; init; }

    public string RefreshToken { get; init; } = string.Empty;

    public required int ExpiresInSeconds { get; init; }

    public string UserId { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;
}

public static class TokenGrants
{
    public const int DefaultAccessTokenLifetimeSeconds = 3600;

    public const int ExpirySkewSeconds = 60;

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
                return "The sign-in response carried no access token.";
            }

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

    public static string ChooseRefreshToken(string? issued, string? prior)
        => string.IsNullOrEmpty(issued) ? prior ?? string.Empty : issued;

    public static bool IsExpired(DateTimeOffset now, DateTimeOffset expiresAt)
        => now.AddSeconds(ExpirySkewSeconds) >= expiresAt;

    public static DateTimeOffset ExpiryFrom(DateTimeOffset now, int expiresInSeconds)
        => now.AddSeconds(expiresInSeconds);

    private static readonly string[] DefinitiveCodes =
    [
        "invalid_grant",
        "invalid_refresh_token",
        "refresh_token_not_found",
        "refresh_token_already_used",
    ];

    public static bool IsDefinitiveRejection(int httpStatus, string? body)
    {
        if (httpStatus is not (400 or 401) || string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (string key in (string[])["error", "error_code", "code"])
            {
                if (document.RootElement.TryGetProperty(key, out JsonElement value)
                    && value.ValueKind == JsonValueKind.String
                    && value.GetString() is { Length: > 0 } code
                    && Array.Exists(DefinitiveCodes, c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static string? DescribeError(JsonElement root)
        => PlatformErrors.TryRead(root, out PlatformError error) ? error.Message : null;
}
