using System.Globalization;
using System.Net;
using System.Net.Sockets;
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
    /// Asks the OS for a free loopback port. Injected so the retry loop below is testable without
    /// racing a real socket.
    /// </summary>
    /// <returns>A port number that was free at the instant it was probed.</returns>
    public delegate int EphemeralPortProbe();

    /// <summary>
    /// Binds a loopback port chosen by the OS (<c>HPS-06</c>). This is the default path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why not a fixed list.</b> On Windows, Hyper-V/WinNAT reserve ~100-port blocks that move
    /// across reboots, and a bind into one is refused even though nothing is listening — HTTP.SYS
    /// reports it as "the process cannot access the file because it is being used by another
    /// process", which is how a reserved range gets misread as a second copy of this plugin. A
    /// hardcoded range cannot dodge a moving target: 51000-51009 sat entirely inside one such block
    /// and took sign-in down outright. The OS ephemeral allocator skips its own exclusions by
    /// construction, so asking it for a port cannot land in one.
    /// </para>
    /// <para>
    /// It also removes the other failure this range had: two hosts signing in at once. A curator
    /// with Revit and Unreal open — or two Revit sessions — drew from one finite list, and the
    /// Unreal editor holds its port for the life of the process. Ports the OS hands out are
    /// distinct per socket, so no coordination between hosts is needed or possible to get wrong.
    /// </para>
    /// <para>
    /// <b>Why a probe and a retry rather than binding port 0 directly.</b> <see cref="HttpListener"/>
    /// takes a URI prefix, which needs a literal port; there is no "bind 0 and tell me what you
    /// got". So we open a throwaway socket on port 0, note the port, close it, and hand that number
    /// to the real listener. Between those two steps another process could take it. That window is
    /// tiny and self-correcting: a retry re-probes and the allocator has already moved on, so the
    /// second attempt is a different port rather than a re-run of the same race.
    /// </para>
    /// </remarks>
    /// <returns><c>null</c> when no port bound — the caller must NOT open a browser.</returns>
    public static LoopbackRedirectListener? StartEphemeral(string callbackPath, int attempts = 5)
        => StartEphemeral(callbackPath, ProbeEphemeralPort, attempts);

    /// <summary>As <see cref="StartEphemeral(string,int)"/>, with the probe supplied. For tests.</summary>
    public static LoopbackRedirectListener? StartEphemeral(
        string callbackPath,
        EphemeralPortProbe probe,
        int attempts = 5)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        string path = NormalisePath(callbackPath);

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            int port;
            try
            {
                port = probe();
            }
            catch (SocketException)
            {
                // No socket to be had at all. A retry is cheap and the next one may succeed.
                continue;
            }

            LoopbackRedirectListener? listener = TryBind(port, path);
            if (listener is not null)
            {
                return listener;
            }
        }

        return null;
    }

    /// <summary>
    /// Binds the first free port in <paramref name="ports"/>.
    /// </summary>
    /// <remarks>
    /// The explicit-list path, used only when a machine configures <c>loopbackPorts</c> — a site
    /// that has allow-listed specific ports and needs them honoured. Unconfigured machines take
    /// <see cref="StartEphemeral(string,int)"/> instead, which is why this list no longer has a
    /// default.
    /// </remarks>
    /// <returns><c>null</c> when none bound — the caller must NOT open a browser.</returns>
    public static LoopbackRedirectListener? Start(IReadOnlyList<int> ports, string callbackPath)
    {
        ArgumentNullException.ThrowIfNull(ports);

        string path = NormalisePath(callbackPath);

        foreach (int port in ports)
        {
            LoopbackRedirectListener? listener = TryBind(port, path);
            if (listener is not null)
            {
                return listener;
            }
        }

        return null;
    }

    /// <summary>The one place a port becomes a bound listener. Both entry points go through it.</summary>
    private static LoopbackRedirectListener? TryBind(int port, string path)
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
            // In use, reserved by the OS, or refused by policy. Indistinguishable here on purpose:
            // the caller's job is to try something else, not to explain Windows.
            listener.Close();
            return null;
        }

        return new LoopbackRedirectListener(
            listener,
            port,
            AuthUrls.BuildLoopbackRedirectUri(port, path),
            path);
    }

    /// <summary>Opens a socket on port 0, notes what the OS assigned, and lets it go.</summary>
    private static int ProbeEphemeralPort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);

        // Must be set before Start(). .NET leaves this false, which sets SO_REUSEADDR, and on
        // Windows that lets another socket share the port -- so the probe would report a port it
        // does not actually hold, which is the one thing a probe must not do.
        probe.ExclusiveAddressUse = true;
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private static string NormalisePath(string callbackPath)
        => callbackPath.StartsWith('/') ? callbackPath : "/" + callbackPath;

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
