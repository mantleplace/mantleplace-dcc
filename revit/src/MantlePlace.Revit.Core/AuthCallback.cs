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
    /// <summary>
    /// The platform's branded completion page, which the success page hands off to when it can.
    /// </summary>
    /// <remarks>
    /// It carries the real typeface and lockup and names the signed-in email — which this listener
    /// cannot know, because it never sees a session. <c>host</c> is the manifest's <c>hostId</c>,
    /// the same vocabulary <c>MantlePlaceEndpoints.WebLoginUrl</c> uses.
    /// </remarks>
    public const string DoneUrl = "https://mantle.place/auth/native/done?host=revit";

    public static string Success()
        => Page("Signed in", "You're signed in to Mantle Place.", HandOffScript);

    public static string Error(string message)
        => Page("Sign-in failed", "Sign-in did not complete: " + HtmlEscape(message), script: null);

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
    /// Probes <see cref="DoneUrl"/> and navigates to it only if something answered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>&lt;meta http-equiv="refresh"&gt;</c> would navigate unconditionally, and with no
    /// network the browser would land on its own "can't reach this site" page — so the branded page
    /// below would have been rendered and then thrown away in exactly the case it exists for. The
    /// probe inverts that: no response, no navigation, and the curator keeps looking at a complete
    /// page on brand. The hand-off is an upgrade, never a dependency, which is what keeps this
    /// inside <c>HPS-08</c>'s "small self-contained HTML page" rather than beside it.
    /// </para>
    /// <para>
    /// <c>no-cors</c> because the done page is another origin and nothing here reads the response —
    /// an opaque resolution is the entire signal. This markup ships on the success page only: the
    /// failure page interpolates a string the authorization server wrote, and keeping that string
    /// off any page that executes anything leaves <see cref="HtmlEscape"/> as the only thing that
    /// has to be right about it.
    /// </para>
    /// </remarks>
    private const string HandOffScript =
        "<script>(function(){if(!window.fetch){return}var d=\"" + DoneUrl + "\";"
        + "fetch(d,{mode:\"no-cors\",cache:\"no-store\"}).then(function(){location.replace(d)},"
        + "function(){});})();</script>";

    /// <summary>
    /// The brand ground, as <c>MantlePlacePalette</c> states it for the Unreal host — one palette
    /// across both. Dark is the fix, not a preference: the platform's own hand-off page is dark, and
    /// arriving from it onto default white read as something having gone wrong.
    /// </summary>
    /// <remarks>
    /// The font stack stays on system faces. The brand typeface would be a network request, and
    /// <c>HPS-08</c>'s whole point is that this page renders with no network at all.
    /// </remarks>
    private const string Stylesheet =
        ":root{color-scheme:dark}"
        + "html,body{height:100%}"
        + "body{margin:0;display:flex;align-items:center;justify-content:center;box-sizing:border-box;"
        + "padding:2rem;background:#0B0C10;color:#fff;"
        + "font-family:system-ui,-apple-system,\"Segoe UI\",Roboto,sans-serif}"
        + "main{max-width:26rem;text-align:center}"
        + "hr{width:2rem;height:2px;border:0;margin:0 auto 1.5rem;background:#FF7110}"
        + ".wordmark{margin:0 0 1.75rem;font-size:.75rem;letter-spacing:.18em;text-transform:uppercase;"
        + "color:rgba(255,255,255,.65)}"
        + "h1{margin:0 0 .625rem;font-size:1.375rem;font-weight:600;letter-spacing:-.01em}"
        + ".body{margin:0;font-size:.9375rem;line-height:1.55;color:rgba(255,255,255,.7)}"
        + ".close{margin:1rem 0 0;font-size:.875rem;line-height:1.5;color:rgba(255,255,255,.4)}";

    /// <summary>
    /// One inline document, no external references. The browser may have no network by the time it
    /// renders this, and a page that reaches for a stylesheet renders as unstyled text.
    /// </summary>
    /// <param name="title">Plain text; escaped here.</param>
    /// <param name="bodyHtml">Already-escaped markup. Callers escape, because only they know which
    /// part of the sentence came from the authorization server.</param>
    /// <param name="script">Trailing markup that is ours alone, or <c>null</c>. Never rendered on a
    /// page carrying a string the authorization server wrote.</param>
    private static string Page(string title, string bodyHtml, string? script)
    {
        StringBuilder html = new();
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        html.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        // &middot; rather than the character itself, so this file stays ASCII and the separator
        // cannot arrive as mojibake in a tab title. <title> is RCDATA, so the entity is parsed.
        html.Append("<title>").Append(HtmlEscape(title)).Append(" &middot; Mantle Place</title>");
        html.Append("<style>").Append(Stylesheet).Append("</style></head><body><main>");
        html.Append("<hr>");
        html.Append("<p class=\"wordmark\">Mantle Place</p>");
        html.Append("<h1>").Append(HtmlEscape(title)).Append("</h1>");
        html.Append("<p class=\"body\">").Append(bodyHtml).Append("</p>");
        // Its own paragraph, not a sentence appended to the one above: on the failure page the one
        // above ends with an error_description the authorization server wrote, which carries no
        // promise of a full stop — run together, the two read as one broken sentence.
        //
        // And it is on BOTH pages, not just success: HPS-08 asks each of them to tell the user to
        // close the tab, and the failure page is the one where a curator left staring at a dead tab
        // is most likely to go looking for a button that is not there.
        html.Append("<p class=\"close\">You can close this tab and return to Revit.</p>");
        html.Append("</main>").Append(script ?? string.Empty).Append("</body></html>");
        return html.ToString();
    }
}
