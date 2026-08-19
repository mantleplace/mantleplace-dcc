using System.Text;

namespace MantlePlace.Revit.Core;

/// <summary>What the browser handed back on the loopback redirect.</summary>
public sealed class AuthCallback
{
    public string Code { get; init; } = string.Empty;

    public string State { get; init; } = string.Empty;

    public string Error { get; init; } = string.Empty;

    public string ErrorDescription { get; init; } = string.Empty;
}

/// <summary>What a parsed callback turned out to be (<c>HPS-08</c> failure precedence).</summary>
public enum CallbackOutcome
{
    /// <summary>An authorization code, with a state that matched.</summary>
    Code,

    /// <summary>The authorization server said no.</summary>
    ServerError,

    /// <summary>Possible CSRF. The code, if any, is discarded.</summary>
    StateMismatch,

    /// <summary>Neither an error nor a code — something else reached the port.</summary>
    MissingCode,
}

/// <summary>
/// Parsing and validating the loopback callback (<c>HPS-07</c>, <c>HPS-08</c>).
/// </summary>
public static class AuthCallbackQuery
{
    /// <summary>
    /// Keys this host reads. Anything else in the query is ignored, and a query carrying NONE of
    /// these parsed nothing — pinned by corpus <c>auth.callbackQueryVectors.recognisedKeys</c>.
    /// </summary>
    private static readonly string[] RecognisedKeys = ["code", "state", "error", "error_description"];

    /// <summary>
    /// Parses a raw query string, or a whole redirect URL.
    /// </summary>
    /// <returns>
    /// <c>false</c> when the input carried no recognised key at all — an empty query, or somebody
    /// else's request arriving on our port.
    /// </returns>
    public static bool TryParse(string rawQuery, out AuthCallback callback)
    {
        callback = new AuthCallback();
        if (rawQuery is null)
        {
            return false;
        }

        string query = rawQuery.Trim();

        // Tolerate a full URL ("http://127.0.0.1:51000/callback?code=..") as well as a bare query.
        int question = query.IndexOf('?', StringComparison.Ordinal);
        if (question >= 0)
        {
            query = query[(question + 1)..];
        }

        // Everything from the first '#' is the fragment and never reaches the server anyway.
        int hash = query.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            query = query[..hash];
        }

        string code = string.Empty;
        string state = string.Empty;
        string error = string.Empty;
        string errorDescription = string.Empty;
        bool sawRecognisedKey = false;

        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            string key = equals < 0 ? pair : pair[..equals];
            string value = equals < 0 ? string.Empty : pair[(equals + 1)..];

            if (Array.IndexOf(RecognisedKeys, key) < 0)
            {
                continue;
            }

            sawRecognisedKey = true;
            string decoded = FormDecode(value);

            switch (key)
            {
                case "code":
                    code = decoded;
                    break;
                case "state":
                    state = decoded;
                    break;
                case "error":
                    error = decoded;
                    break;
                default:
                    errorDescription = decoded;
                    break;
            }
        }

        if (!sawRecognisedKey)
        {
            return false;
        }

        callback = new AuthCallback
        {
            Code = code,
            State = state,
            Error = error,
            ErrorDescription = errorDescription,
        };
        return true;
    }

    /// <summary>
    /// ⛔<c>HPS-07</c>: case-sensitive equality, and an empty expected state is never valid.
    /// </summary>
    /// <remarks>
    /// The empty-expected case is the one that matters. The loopback port is reachable by anything
    /// on the machine, so a host that treats <c>"" == ""</c> as a match accepts any callback that
    /// arrives — including one an attacker sent, carrying their authorization code.
    /// </remarks>
    public static bool IsStateValid(string? expected, string? received)
        => !string.IsNullOrEmpty(expected) && string.Equals(expected, received, StringComparison.Ordinal);

    /// <summary>
    /// Decides what a callback is, in the <c>HPS-08</c> precedence: an explicit <c>error</c>, then a
    /// state mismatch, then a missing code.
    /// </summary>
    /// <remarks>
    /// The precedence is not cosmetic. Checking state first would report "possible CSRF" for an
    /// ordinary "user declined" — sending a curator to look for an attack that is not there — and
    /// checking the code first would report "no code" for the same thing, which is true and useless.
    /// Pure so that all three orderings are assertable without a browser.
    /// </remarks>
    public static CallbackOutcome Classify(AuthCallback callback, string expectedState, out string message)
    {
        ArgumentNullException.ThrowIfNull(callback);

        if (callback.Error.Length > 0)
        {
            message = callback.ErrorDescription.Length > 0 ? callback.ErrorDescription : callback.Error;
            return CallbackOutcome.ServerError;
        }

        if (!IsStateValid(expectedState, callback.State))
        {
            message = "The sign-in response did not match this request. Nothing was signed in — "
                + "start sign-in again from Revit.";
            return CallbackOutcome.StateMismatch;
        }

        if (callback.Code.Length == 0)
        {
            message = "The sign-in response carried no authorization code.";
            return CallbackOutcome.MissingCode;
        }

        message = string.Empty;
        return CallbackOutcome.Code;
    }

    /// <summary>
    /// <c>application/x-www-form-urlencoded</c> decoding: <c>+</c> is a space, then percent-escapes.
    /// </summary>
    /// <remarks>
    /// The order is load-bearing and the reverse is wrong: unescaping first would turn a literal
    /// plus encoded as <c>%2B</c> into <c>+</c> and then into a space.
    /// </remarks>
    private static string FormDecode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            // A malformed escape is somebody else's malformed request, not a reason to throw out of
            // an HTTP handler that still owes the browser a page (HPS-08).
            return value;
        }
    }
}

/// <summary>
/// The two self-contained pages the browser is shown when the redirect lands (<c>HPS-08</c>).
/// </summary>
public static class BrowserPages
{
    public static string Success()
        => Page("Signed in", "You're signed in to Mantle Place. You can close this tab and return to Revit.");

    public static string Error(string message)
        => Page("Sign-in failed", "Sign-in did not complete: " + HtmlEscape(message));

    /// <summary>
    /// Escapes <c>&amp;</c>, <c>&lt;</c> and <c>&gt;</c>, in that order.
    /// </summary>
    /// <remarks>
    /// The order is the whole rule. Escaping <c>&lt;</c> first would turn it into <c>&amp;lt;</c>
    /// and the later ampersand pass would double-escape it to <c>&amp;amp;lt;</c>. The message can
    /// contain an <c>error_description</c> the authorization server wrote, so it is not ours to
    /// trust.
    /// </remarks>
    public static string HtmlEscape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    /// <summary>
    /// One inline document, no external references. The browser may have no network by the time it
    /// renders this, and a page that reaches for a stylesheet renders as unstyled text.
    /// </summary>
    /// <param name="title">Plain text; escaped here.</param>
    /// <param name="bodyHtml">Already-escaped markup. Callers escape, because only they know which
    /// part of the sentence came from the authorization server.</param>
    private static string Page(string title, string bodyHtml)
    {
        StringBuilder html = new();
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        html.Append("<title>").Append(HtmlEscape(title)).Append("</title>");
        html.Append("<style>body{font-family:system-ui,sans-serif;margin:4rem auto;max-width:32rem;");
        html.Append("padding:0 1rem;color:#111}h1{font-size:1.25rem}</style></head><body>");
        html.Append("<h1>").Append(HtmlEscape(title)).Append("</h1>");
        html.Append("<p>").Append(bodyHtml).Append("</p>");
        html.Append("</body></html>");
        return html.ToString();
    }
}
