using System.Text.Json;

namespace MantlePlace.Revit.Core;

/// <summary>What a refused export call means to the plugin.</summary>
public enum ExportRefusalKind
{
    /// <summary>A 401: refresh the access token and retry once; if that fails, sign in again.</summary>
    SignInNeeded,

    /// <summary>
    /// A <c>409 invalid_transition</c>: the web moved the export first (cancelled it, say). Not a
    /// fault: stop work on the export and close anything open for it.
    /// </summary>
    WebMovedFirst,

    /// <summary>A 429: back off by <see cref="ExportRefusal.RetryAfterMs"/>.</summary>
    RateLimited,

    /// <summary>A 5xx, or an unknown code in that class: the platform may answer later.</summary>
    RetryLater,

    /// <summary>Any other 4xx: the same request will not succeed, so it is not retried.</summary>
    Rejected,
}

/// <summary>A non-2xx from an export route, reduced to what the plugin does about it.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="Status">The HTTP status.</param>
/// <param name="Code">The refusal <c>code</c>, or empty (a 401 has none).</param>
/// <param name="Message">A sentence for the log or the curator.</param>
/// <param name="RetryAfterMs">With <see cref="ExportRefusalKind.RateLimited"/>, how long to wait.</param>
public readonly record struct ExportRefusal(
    ExportRefusalKind Kind,
    int Status,
    string Code,
    string Message,
    int RetryAfterMs)
{
    /// <summary>Every fault but the web having moved the export first.</summary>
    public bool IsFault => Kind != ExportRefusalKind.WebMovedFirst;
}

/// <summary>
/// The contract's refusal handling (the platform export contract, section 5), decided from a status
/// and a body.
/// </summary>
/// <remarks>
/// <para>
/// A 401 is told by its status alone: it carries no <c>code</c>. <c>409 invalid_transition</c> is the
/// web having moved the export first. A 429 carries <c>retryAfterMs</c>. Every other code, known or
/// not, is treated by its HTTP status class (section 7): retry later on 5xx and 429, do not retry on
/// other 4xx. The class is the rule and the code only names it, so a code the platform adds later
/// is handled the day it appears.
/// </para>
/// </remarks>
public static class ExportRefusals
{
    /// <summary>The wait when a 429 states none, or states one that is not a positive number.</summary>
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

    // The code is its own field: a body that states a code and no prose still names its refusal.
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
