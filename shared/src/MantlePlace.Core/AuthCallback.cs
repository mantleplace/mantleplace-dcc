using System.Text;

namespace MantlePlace.Core;

public sealed class AuthCallback
{
    public string Code { get; init; } = string.Empty;

    public string State { get; init; } = string.Empty;

    public string Error { get; init; } = string.Empty;

    public string ErrorDescription { get; init; } = string.Empty;
}

public enum CallbackOutcome
{
    Code,

    ServerError,

    StateMismatch,

    MissingCode,
}

public static class AuthCallbackQuery
{
    private static readonly string[] RecognisedKeys = ["code", "state", "error", "error_description"];

    public static bool TryParse(string rawQuery, out AuthCallback callback)
    {
        callback = new AuthCallback();
        if (rawQuery is null)
        {
            return false;
        }

        string query = rawQuery.Trim();

        int question = query.IndexOf('?', StringComparison.Ordinal);
        if (question >= 0)
        {
            query = query[(question + 1)..];
        }

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

    public static bool IsStateValid(string? expected, string? received)
        => !string.IsNullOrEmpty(expected) && string.Equals(expected, received, StringComparison.Ordinal);

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

    private static string FormDecode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}

public static class BrowserPages
{
    public const string DoneUrl = "https://mantle.place/auth/native/done?host=revit";

    public static string Success()
        => Page("Signed in", "You're signed in to Mantle Place.", HandOffScript);

    public static string Error(string message)
        => Page("Sign-in failed", "Sign-in did not complete: " + HtmlEscape(message), script: null);

    public static string HtmlEscape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }

    private const string HandOffScript =
        "<script>(function(){if(!window.fetch){return}var d=\"" + DoneUrl + "\";"
        + "fetch(d,{mode:\"no-cors\",cache:\"no-store\"}).then(function(){location.replace(d)},"
        + "function(){});})();</script>";

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

    private static string Page(string title, string bodyHtml, string? script)
    {
        StringBuilder html = new();
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        html.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.Append("<title>").Append(HtmlEscape(title)).Append(" &middot; Mantle Place</title>");
        html.Append("<style>").Append(Stylesheet).Append("</style></head><body><main>");
        html.Append("<hr>");
        html.Append("<p class=\"wordmark\">Mantle Place</p>");
        html.Append("<h1>").Append(HtmlEscape(title)).Append("</h1>");
        html.Append("<p class=\"body\">").Append(bodyHtml).Append("</p>");
        html.Append("<p class=\"close\">You can close this tab and return to Revit.</p>");
        html.Append("</main>").Append(script ?? string.Empty).Append("</body></html>");
        return html.ToString();
    }
}
