using System.Text.Json;

namespace MantlePlace.Core;

public enum ExportRefusalKind
{
    SignInNeeded,

    WebMovedFirst,

    RateLimited,

    RetryLater,

    Rejected,
}

public readonly record struct ExportRefusal(
    ExportRefusalKind Kind,
    int Status,
    string Code,
    string Message,
    int RetryAfterMs)
{
    public bool IsFault => Kind != ExportRefusalKind.WebMovedFirst;
}

public static class ExportRefusals
{
    public const int DefaultRetryAfterMs = 1000;

    public const string InvalidTransitionCode = "invalid_transition";

    public static ExportRefusal Classify(int status, string? body)
    {
        PlatformError? error = PlatformErrors.FromBody(body ?? string.Empty);
        string code = error?.Code is { Length: > 0 } stated ? stated : CodeOf(body);
        string message = PlatformErrors.Refusal(status, body ?? string.Empty)
                         ?? $"mantle.place refused this request (HTTP {status}).";

        if (status == 401)
        {
            return new ExportRefusal(ExportRefusalKind.SignInNeeded, status, code, message, 0);
        }

        if (status == 409 && string.Equals(code, InvalidTransitionCode, StringComparison.Ordinal))
        {
            return new ExportRefusal(
                ExportRefusalKind.WebMovedFirst,
                status,
                code,
                "The web moved this export first.",
                0);
        }

        if (status == 429)
        {
            return new ExportRefusal(
                ExportRefusalKind.RateLimited, status, code, message, RetryAfter(body));
        }

        return new ExportRefusal(
            status >= 500 ? ExportRefusalKind.RetryLater : ExportRefusalKind.Rejected,
            status,
            code,
            message,
            0);
    }

    private static string CodeOf(string? body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body ?? string.Empty);
            return document.RootElement.Str("code");
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static int RetryAfter(string? body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body ?? string.Empty);
            int? value = document.RootElement.OptionalInt("retryAfterMs");
            return value is > 0 ? value.Value : DefaultRetryAfterMs;
        }
        catch (JsonException)
        {
            return DefaultRetryAfterMs;
        }
    }
}
