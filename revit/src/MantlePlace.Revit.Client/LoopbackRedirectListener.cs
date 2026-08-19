using System.Globalization;
using System.Net;
using System.Text;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Client;

/// <summary>The result of one browser round-trip.</summary>
/// <param name="Outcome">What the callback turned out to be, or <c>null</c> when none arrived.</param>
/// <param name="Callback">The parsed callback, when one arrived.</param>
/// <param name="Message">The reason, when <paramref name="Outcome"/> is not <c>Code</c>.</param>
public readonly record struct LoopbackResult(CallbackOutcome? Outcome, AuthCallback? Callback, string Message)
{
    /// <summary>A sign-in that timed out or was cancelled. Not a failure (<c>HPS-09</c>).</summary>
    public static LoopbackResult Abandoned => new(null, null, string.Empty);

    public bool HasCode => Outcome == CallbackOutcome.Code;
}

/// <summary>
/// The loopback redirect target (<c>HPS-06</c> … <c>HPS-09</c>).
/// </summary>
/// <remarks>
/// <para>
/// Binds <b>before</b> the browser opens. A host that opens the browser first and then discovers no
/// port is free has sent the curator to a page that will redirect into nothing, and the only symptom
/// is a browser tab that hangs.
/// </para>
/// <para>
/// Every decision this makes — which failure a callback is, whether a state matches — lives in
/// <see cref="AuthCallbackQuery"/>. What is left here is genuinely I/O: bind, wait, write a page.
/// </para>
/// </remarks>
public sealed class LoopbackRedirectListener : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _callbackPath;

    private LoopbackRedirectListener(HttpListener listener, int port, string redirectUri, string callbackPath)
    {
        _listener = listener;
        _callbackPath = callbackPath;
        Port = port;
        RedirectUri = redirectUri;
    }

    public int Port { get; }

    /// <summary>What to send as <c>redirect_uri</c>. Bound already, so this cannot go stale.</summary>
    public string RedirectUri { get; }

    /// <summary>
    /// Binds the first free port in <paramref name="ports"/>.
    /// </summary>
    /// <returns><c>null</c> when none bound — the caller must NOT open a browser.</returns>
    public static LoopbackRedirectListener? Start(IReadOnlyList<int> ports, string callbackPath)
    {
        ArgumentNullException.ThrowIfNull(ports);

        string path = callbackPath.StartsWith('/') ? callbackPath : "/" + callbackPath;

        foreach (int port in ports)
        {
            HttpListener listener = new();

            // The prefix is the literal loopback IP, matching the redirect_uri exactly. A prefix of
            // "localhost" or "+" would either not match the redirect or need an admin URL ACL.
            listener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/"));

            try
            {
                listener.Start();
            }
            catch (HttpListenerException)
            {
                listener.Close();
                continue;
            }

            return new LoopbackRedirectListener(
                listener,
                port,
                AuthUrls.BuildLoopbackRedirectUri(port, path),
                path);
        }

        return null;
    }

    /// <summary>
    /// Waits for the redirect, answering every request with a page (<c>HPS-08</c>).
    /// </summary>
    /// <remarks>
    /// Single-consumption: the first request that parses as a callback latches and returns. Later
    /// or duplicate redirects — a curator who refreshed the tab — are served the success page and
    /// otherwise ignored, because by then the loop has already returned.
    /// </remarks>
    /// <returns>
    /// <see cref="LoopbackResult.Abandoned"/> on timeout or cancellation. Timing out is a
    /// cancellation, not a failure: a curator who wandered off returns to a signed-out plugin rather
    /// than a latched error (<c>HPS-09</c>).
    /// </returns>
    public async Task<LoopbackResult> WaitForCallbackAsync(
        string expectedState,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        while (!deadline.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return LoopbackResult.Abandoned;
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                // Disposed out from under us — an explicit Cancel. Fires no completion (HPS-26's
                // sibling rule for this layer): the caller already knows.
                return LoopbackResult.Abandoned;
            }

            string query = context.Request.Url?.Query ?? string.Empty;

            if (!AuthCallbackQuery.TryParse(query, out AuthCallback callback))
            {
                // Something else on the machine found the port. Say so and keep waiting — this is
                // not the curator's sign-in and must not end it.
                Respond(context, HttpStatusCode.NotFound, BrowserPages.Error("This is not a Mantle Place sign-in."));
                continue;
            }

            CallbackOutcome outcome = AuthCallbackQuery.Classify(callback, expectedState, out string message);

            Respond(
                context,
                outcome == CallbackOutcome.Code ? HttpStatusCode.OK : HttpStatusCode.BadRequest,
                outcome == CallbackOutcome.Code ? BrowserPages.Success() : BrowserPages.Error(message));

            return new LoopbackResult(outcome, callback, message);
        }

        return LoopbackResult.Abandoned;
    }

    public void Dispose()
    {
        try
        {
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>The path the redirect is expected on, for a caller that wants to log it.</summary>
    public string CallbackPath => _callbackPath;

    private static void Respond(HttpListenerContext context, HttpStatusCode status, string html)
    {
        try
        {
            byte[] body = Encoding.UTF8.GetBytes(html);
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength64 = body.Length;
            context.Response.OutputStream.Write(body, 0, body.Length);
            context.Response.OutputStream.Close();
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException)
        {
            // The browser hung up. The sign-in itself already succeeded or failed on its own terms;
            // throwing here would convert "we could not draw you a page" into "sign-in failed".
        }
    }
}
