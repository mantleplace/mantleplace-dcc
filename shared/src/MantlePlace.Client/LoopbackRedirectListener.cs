using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MantlePlace.Core;

namespace MantlePlace.Client;

public readonly record struct LoopbackResult(CallbackOutcome? Outcome, AuthCallback? Callback, string Message)
{
    public static LoopbackResult Abandoned => new(null, null, string.Empty);

    public bool HasCode => Outcome == CallbackOutcome.Code;
}

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

    public string RedirectUri { get; }

    public delegate int EphemeralPortProbe();

    public static LoopbackRedirectListener? StartEphemeral(string callbackPath, int attempts = 5)
        => StartEphemeral(callbackPath, ProbeEphemeralPort, attempts);

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

    private static LoopbackRedirectListener? TryBind(int port, string path)
    {
        HttpListener listener = new();

        listener.Prefixes.Add(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}/"));

        try
        {
            listener.Start();
        }
        catch (HttpListenerException)
        {
            listener.Close();
            return null;
        }

        return new LoopbackRedirectListener(
            listener,
            port,
            AuthUrls.BuildLoopbackRedirectUri(port, path),
            path);
    }

    private static int ProbeEphemeralPort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);

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
                return LoopbackResult.Abandoned;
            }

            string query = context.Request.Url?.Query ?? string.Empty;

            if (!AuthCallbackQuery.TryParse(query, out AuthCallback callback))
            {
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
        }
    }
}
