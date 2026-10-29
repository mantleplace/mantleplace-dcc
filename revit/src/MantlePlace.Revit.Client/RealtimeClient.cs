using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Client;

/// <summary>Where the Realtime client is.</summary>
public enum RealtimeState
{
    /// <summary>Not started, or not configured: nothing is connected.</summary>
    Off,

    /// <summary>Opening the socket.</summary>
    Connecting,

    /// <summary>The socket is open and the join is unanswered.</summary>
    Joining,

    /// <summary>Joined, presence tracked, row changes flowing.</summary>
    Subscribed,

    /// <summary>The socket dropped or the channel failed for a moment; waiting to try again.</summary>
    Retrying,

    /// <summary>The join was refused as <c>Unauthorized</c>. Final: never retried.</summary>
    Refused,

    /// <summary>Stopped: untracked, left and closed.</summary>
    Stopped,
}

/// <summary>
/// The connected host's Realtime client: it joins the curator's private channel on the signed-in
/// session, tracks this Revit's presence there, and hears row changes (the platform export contract,
/// sections 1 to 3). It holds only the socket and the order of things; every frame is
/// <see cref="RealtimeFrames"/> in <c>MantlePlace.Revit.Core</c>.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>Every event is raised on the client's own thread</b>, a thread-pool thread that runs its loop,
/// never the caller's. A subscriber marshals before it touches anything the host owns: in Revit, an
/// <c>ExternalEvent</c> for the document API and the window's dispatcher for UI. A handler that throws
/// is logged, and the loop goes on. Handlers run inside the loop, so a slow one delays the next frame:
/// hand the work off, do not do it there.
/// </para>
/// <para>
/// <b>Off without configuration.</b> With no Realtime location or no publishable key,
/// <see cref="Start"/> returns <c>false</c>, opens nothing, and logs one line saying why, once.
/// </para>
/// <para>
/// <b>Retries.</b> A join refused as <c>Unauthorized</c> is final (<see cref="RealtimeState.Refused"/>).
/// Any other channel error, a dropped socket, or a heartbeat left unanswered closes the socket and opens
/// a new one after <see cref="RealtimeChannel.Backoff"/>, under the same connection id, so the curator
/// sees one Revit that blinked rather than a second one. Every <c>SUBSCRIBED</c> tracks presence again
/// and raises <see cref="Subscribed"/>, which is the host's cue to run the catch-up.
/// </para>
/// <para>
/// <b>Commands</b> (<see cref="SetAuth"/>, <see cref="Track"/>, <see cref="WatchOrder"/>,
/// <see cref="StopWatchingOrder"/>, <see cref="StopAsync"/>) may come from any thread. They are queued to
/// the loop and applied in order, so the socket has one writer.
/// </para>
/// </remarks>
public sealed class RealtimeClient : IConnectedChannel, IAsyncDisposable
{
    /// <summary>The heartbeat interval Supabase's clients use; Realtime drops a socket quiet for longer.</summary>
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(25);

    // How long stop waits for the leave to be answered before it closes anyway: Revit's shutdown must
    // not hang on a platform that has gone quiet.
    private static readonly TimeSpan LeaveWait = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

    private readonly Func<Task<RealtimeValues>>? _resolve;
    private readonly string _connectionId;
    private readonly Action<string> _log;
    private readonly Func<int, TimeSpan> _backoff;
    private readonly TimeSpan _heartbeat;
    private readonly Channel<object> _inbox = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = false });
    private readonly object _gate = new();

    private Task? _loop;
    private bool _stopped;
    private bool _offLogged;
    private int _state = (int)RealtimeState.Off;

    // Set at construction, or by the loop once the published route has answered.
    private string _location;
    private string _publishableKey;

    // Owned by the loop once it runs.
    private string _wireTopic = string.Empty;
    private string _accessToken = string.Empty;
    private string _presence = "{}";
    private string? _orderId;
    private long _ref;
    private int _generation;

    /// <param name="location">The Realtime location (<c>realtimeUrl</c>); empty means off.</param>
    /// <param name="publishableKey">The publishable key (<c>realtimePublishableKey</c>); empty means off.</param>
    /// <param name="connectionId">This launch's connection id: the presence key and the exports filter.</param>
    /// <param name="log">Where the client's one-line notes go.</param>
    /// <param name="backoff">The wait before the nth retry, for tests; <see cref="RealtimeChannel.Backoff"/> when <c>null</c>.</param>
    /// <param name="heartbeatInterval">For tests; <see cref="DefaultHeartbeatInterval"/> when <c>null</c>.</param>
    /// <param name="resolve">
    /// Where the location and key come from when none was given: asked once, on the client's own thread
    /// after <see cref="Start"/>, so Revit's thread never waits on the network. When it answers a reason,
    /// the client logs it and stays off. <c>null</c> keeps the given values as they are.
    /// </param>
    public RealtimeClient(
        string location,
        string publishableKey,
        string connectionId,
        Action<string> log,
        Func<int, TimeSpan>? backoff = null,
        TimeSpan? heartbeatInterval = null,
        Func<Task<RealtimeValues>>? resolve = null)
    {
        _location = location ?? string.Empty;
        _publishableKey = publishableKey ?? string.Empty;
        _resolve = resolve;
        _connectionId = connectionId ?? throw new ArgumentNullException(nameof(connectionId));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _backoff = backoff ?? RealtimeChannel.Backoff;
        _heartbeat = heartbeatInterval ?? DefaultHeartbeatInterval;
    }

    /// <summary>
    /// The client for the endpoints and this launch's connection id: the local override when one is set,
    /// otherwise the location and key the platform publishes (<see cref="RealtimeLocator"/>).
    /// </summary>
    /// <param name="endpoints">The loaded endpoints.</param>
    /// <param name="connectionId">This launch's connection id.</param>
    /// <param name="log">Where the client's one-line notes go.</param>
    /// <param name="routeHandler">For tests: the handler the published route is read through.</param>
    public static RealtimeClient For(
        MantlePlaceEndpoints endpoints,
        string connectionId,
        Action<string> log,
        HttpMessageHandler? routeHandler = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return RealtimeLocator.HasOverride(endpoints)
            ? new RealtimeClient(endpoints.RealtimeUrl, endpoints.RealtimePublishableKey, connectionId, log)
            : new RealtimeClient(
                string.Empty,
                string.Empty,
                connectionId,
                log,
                resolve: () => RealtimeLocator.ResolveAsync(endpoints, routeHandler));
    }

    /// <summary>
    /// Raised on every <c>SUBSCRIBED</c>, after presence is tracked: a catch-up is due. On the client's
    /// thread.
    /// </summary>
    public event EventHandler? Subscribed;

    /// <summary>A row change on a subscribed table. On the client's thread.</summary>
    public event EventHandler<RealtimeRowChange>? RowChanged;

    /// <summary>The join was refused as <c>Unauthorized</c>; the platform's words. On the client's thread.</summary>
    public event EventHandler<string>? Refused;

    /// <summary>Raised whenever <see cref="State"/> changes. On the client's thread.</summary>
    public event EventHandler<RealtimeState>? StateChanged;

    /// <summary>Where the client is. Safe to read from any thread.</summary>
    public RealtimeState State => (RealtimeState)Volatile.Read(ref _state);

    /// <summary>
    /// Opens the socket and joins the curator's channel on this session, tracking
    /// <paramref name="presence"/>. <c>false</c> when it will not connect: not configured, or a token
    /// that names no curator, or a client already stopped. A second call while running changes nothing
    /// and returns <c>true</c>.
    /// </summary>
    public bool Start(string accessToken, ConnectedHost presence)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return false;
            }

            if (_loop is not null)
            {
                return true;
            }

            if (_resolve is null && RealtimeChannel.WhyOff(_location, _publishableKey) is { } off)
            {
                LogOffOnce(off);
                return false;
            }

            if (RealtimeChannel.UserIdOf(accessToken) is not { } userId)
            {
                LogOffOnce("Connected host is off: the session's access token names no curator, so there is no channel to join.");
                return false;
            }

            _wireTopic = RealtimeChannel.WireTopic(userId);
            _accessToken = accessToken;
            _presence = presence.ToJson();
            _loop = Task.Run(RunAsync);
            return true;
        }
    }

    /// <summary>Re-authorises the channel with a refreshed access token; the next join uses it too.</summary>
    public void SetAuth(string accessToken) => _inbox.Writer.TryWrite(new SetAuthCommand(accessToken ?? string.Empty));

    /// <summary>Re-tracks presence when the payload changed (the open document); an unchanged one sends nothing.</summary>
    public void Track(ConnectedHost presence) => _inbox.Writer.TryWrite(new TrackCommand(presence.ToJson()));

    /// <summary>
    /// Watches one order's row while its bundle builds. Realtime fixes a channel's subscriptions at the
    /// join, so this leaves and joins again with the order added, and a catch-up follows the rejoin.
    /// </summary>
    public void WatchOrder(string orderId)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);
        _inbox.Writer.TryWrite(new WatchCommand(orderId));
    }

    /// <summary>Drops the order subscription, by the same leave and rejoin.</summary>
    public void StopWatchingOrder() => _inbox.Writer.TryWrite(new WatchCommand(null));

    /// <summary>
    /// Untracks, leaves and closes, waiting a bounded time for the leave to be answered. Nothing
    /// reconnects afterwards.
    /// </summary>
    public async Task StopAsync()
    {
        Task? loop;
        lock (_gate)
        {
            _stopped = true;
            loop = _loop;
        }

        if (loop is null)
        {
            SetState(RealtimeState.Stopped);
            return;
        }

        _inbox.Writer.TryWrite(StopCommand.Instance);
        await loop.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void LogOffOnce(string line)
    {
        if (_offLogged)
        {
            return;
        }

        _offLogged = true;
        _log(line);
    }

    private async Task RunAsync()
    {
        int attempt = 0;
        try
        {
            if (_resolve is not null)
            {
                SetState(RealtimeState.Connecting);
                Task<RealtimeValues> asking = _resolve();
                if (!await IdleUntilAsync(asking).ConfigureAwait(false))
                {
                    SetState(RealtimeState.Stopped);
                    return;
                }

                RealtimeValues values = await asking.ConfigureAwait(false);
                if (values.WhyNot is not null)
                {
                    // Off for this launch, as an unconfigured install is: the next launch asks again.
                    SetState(RealtimeState.Off);
                    _log(values.WhyNot);
                    return;
                }

                _location = values.Location;
                _publishableKey = values.PublishableKey;
            }

            while (true)
            {
                SetState(RealtimeState.Connecting);
                Session outcome = await SessionAsync(() => attempt = 0).ConfigureAwait(false);
                if (outcome.Ended is RealtimeState.Stopped or RealtimeState.Refused)
                {
                    SetState(outcome.Ended);
                    if (outcome.Ended == RealtimeState.Refused)
                    {
                        _log("Connected host refused: " + outcome.Reason + " Not retried.");
                        Raise(() => Refused?.Invoke(this, outcome.Reason));
                    }

                    return;
                }

                attempt++;
                SetState(RealtimeState.Retrying);
                _log($"Connected host lost the channel ({outcome.Reason}); trying again (attempt {attempt}).");
                if (!await IdleUntilAsync(Task.Delay(_backoff(attempt))).ConfigureAwait(false))
                {
                    SetState(RealtimeState.Stopped);
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The loop is the client: an escape here would leave a Revit silently offline.
            _log("Connected host stopped on an unexpected error: " + ex.Message);
            SetState(RealtimeState.Stopped);
        }
    }

    // One socket's life: open, join, run until it drops, fails, is refused or is stopped.
    private async Task<Session> SessionAsync(Action subscribed)
    {
        int generation = ++_generation;
        using ClientWebSocket socket = new();
        using CancellationTokenSource connectLimit = new(ConnectTimeout);
        Task connect = socket.ConnectAsync(new Uri(RealtimeChannel.SocketUrl(_location, _publishableKey)!), connectLimit.Token);
        if (!await IdleUntilAsync(connect).ConfigureAwait(false))
        {
            await connectLimit.CancelAsync().ConfigureAwait(false);
            await Swallow(connect).ConfigureAwait(false);
            return new Session(RealtimeState.Stopped, string.Empty);
        }

        try
        {
            await connect.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpRequestException)
        {
            return new Session(RealtimeState.Retrying, "the socket did not open: " + ex.Message);
        }

        using CancellationTokenSource sessionEnd = new();
        _ = PumpAsync(socket, generation);
        _ = TickAsync(generation, sessionEnd.Token);

        try
        {
            return await JoinedAsync(socket, generation, subscribed).ConfigureAwait(false);
        }
        finally
        {
            await sessionEnd.CancelAsync().ConfigureAwait(false);
            socket.Abort();
        }
    }

    private async Task<Session> JoinedAsync(ClientWebSocket socket, int generation, Action subscribed)
    {
        string joinRef = NextRef();
        string? heartbeatRef = null;
        bool joined = false;

        if (!await SendAsync(socket, RealtimeFrames.Join(_wireTopic, joinRef, _accessToken, _connectionId, Bindings())).ConfigureAwait(false))
        {
            return new Session(RealtimeState.Retrying, "the join could not be sent");
        }

        SetState(RealtimeState.Joining);

        while (true)
        {
            object item = await _inbox.Reader.ReadAsync().ConfigureAwait(false);
            switch (item)
            {
                case Inbound inbound when inbound.Generation == generation:
                {
                    RealtimeMessage message = RealtimeFrames.Decode(inbound.Frame);
                    if (message.Kind == RealtimeMessageKind.Reply && message.Ref is not null && message.Ref == heartbeatRef)
                    {
                        heartbeatRef = null;
                        break;
                    }

                    if (!string.Equals(message.Topic, _wireTopic, StringComparison.Ordinal))
                    {
                        break;
                    }

                    // A frame of an earlier join on this socket (one left to watch an order) is history.
                    if (message.JoinRef is not null && message.JoinRef != joinRef)
                    {
                        break;
                    }

                    switch (message.Kind)
                    {
                        case RealtimeMessageKind.Reply when message.Ref == joinRef:
                            if (!message.Ok)
                            {
                                return RealtimeChannel.IsRefusal(message.Reason)
                                    ? new Session(RealtimeState.Refused, message.Reason)
                                    : new Session(RealtimeState.Retrying, "the join failed: " + message.Reason);
                            }

                            joined = true;
                            subscribed();
                            SetState(RealtimeState.Subscribed);
                            if (!await SendAsync(socket, RealtimeFrames.Track(_wireTopic, joinRef, NextRef(), _presence)).ConfigureAwait(false))
                            {
                                return new Session(RealtimeState.Retrying, "the track could not be sent");
                            }

                            Raise(() => Subscribed?.Invoke(this, EventArgs.Empty));
                            break;

                        case RealtimeMessageKind.Reply when !message.Ok:
                            _log("Connected host: the platform refused a push: " + message.Reason);
                            break;

                        case RealtimeMessageKind.ChannelError:
                            return RealtimeChannel.IsRefusal(message.Reason)
                                ? new Session(RealtimeState.Refused, message.Reason)
                                : new Session(RealtimeState.Retrying, message.Reason);

                        case RealtimeMessageKind.Close:
                            return new Session(RealtimeState.Retrying, "the platform closed the channel");

                        case RealtimeMessageKind.RowChange:
                            RealtimeRowChange change = message.Change!;
                            Raise(() => RowChanged?.Invoke(this, change));
                            break;
                    }

                    break;
                }

                case Ended ended when ended.Generation == generation:
                    return new Session(RealtimeState.Retrying, "the socket dropped");

                case Tick tick when tick.Generation == generation:
                    if (heartbeatRef is not null)
                    {
                        return new Session(RealtimeState.Retrying, "a heartbeat went unanswered");
                    }

                    heartbeatRef = NextRef();
                    if (!await SendAsync(socket, RealtimeFrames.Heartbeat(heartbeatRef)).ConfigureAwait(false))
                    {
                        return new Session(RealtimeState.Retrying, "the heartbeat could not be sent");
                    }

                    break;

                case SetAuthCommand auth:
                    _accessToken = auth.AccessToken;
                    if (!await SendAsync(socket, RealtimeFrames.AccessToken(_wireTopic, joinRef, NextRef(), _accessToken)).ConfigureAwait(false))
                    {
                        return new Session(RealtimeState.Retrying, "the token refresh could not be sent");
                    }

                    break;

                case TrackCommand track:
                    if (string.Equals(track.PresenceJson, _presence, StringComparison.Ordinal))
                    {
                        break;
                    }

                    _presence = track.PresenceJson;
                    if (joined && !await SendAsync(socket, RealtimeFrames.Track(_wireTopic, joinRef, NextRef(), _presence)).ConfigureAwait(false))
                    {
                        return new Session(RealtimeState.Retrying, "the track could not be sent");
                    }

                    break;

                case WatchCommand watch:
                    if (string.Equals(watch.OrderId, _orderId, StringComparison.Ordinal))
                    {
                        break;
                    }

                    _orderId = watch.OrderId;
                    string left = joinRef;
                    joinRef = NextRef();
                    joined = false;
                    SetState(RealtimeState.Joining);
                    if (!await SendAsync(socket, RealtimeFrames.Leave(_wireTopic, left, NextRef())).ConfigureAwait(false)
                        || !await SendAsync(socket, RealtimeFrames.Join(_wireTopic, joinRef, _accessToken, _connectionId, Bindings())).ConfigureAwait(false))
                    {
                        return new Session(RealtimeState.Retrying, "the rejoin could not be sent");
                    }

                    break;

                case StopCommand:
                    await LeaveAsync(socket, generation, joinRef, joined).ConfigureAwait(false);
                    return new Session(RealtimeState.Stopped, string.Empty);
            }
        }
    }

    // Untrack and leave, wait a bounded time for the leave's answer, then close.
    private async Task LeaveAsync(ClientWebSocket socket, int generation, string joinRef, bool joined)
    {
        if (joined)
        {
            await SendAsync(socket, RealtimeFrames.Untrack(_wireTopic, joinRef, NextRef())).ConfigureAwait(false);
        }

        string leaveRef = NextRef();
        if (await SendAsync(socket, RealtimeFrames.Leave(_wireTopic, joinRef, leaveRef)).ConfigureAwait(false))
        {
            using CancellationTokenSource wait = new(LeaveWait);
            try
            {
                while (true)
                {
                    object item = await _inbox.Reader.ReadAsync(wait.Token).ConfigureAwait(false);
                    if (item is Ended ended && ended.Generation == generation)
                    {
                        break;
                    }

                    if (item is Inbound inbound && inbound.Generation == generation
                        && RealtimeFrames.Decode(inbound.Frame) is { Kind: RealtimeMessageKind.Reply } reply && reply.Ref == leaveRef)
                    {
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        using CancellationTokenSource close = new(LeaveWait);
        try
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "stop", close.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    // Applies commands while waiting on something that is not the socket (the connect, a backoff).
    // False when a stop arrived, which ends the wait at once.
    private async Task<bool> IdleUntilAsync(Task until)
    {
        while (!until.IsCompleted)
        {
            Task<bool> ready = _inbox.Reader.WaitToReadAsync().AsTask();
            await Task.WhenAny(until, ready).ConfigureAwait(false);
            while (_inbox.Reader.TryRead(out object? item))
            {
                switch (item)
                {
                    case StopCommand:
                        return false;
                    case SetAuthCommand auth:
                        _accessToken = auth.AccessToken;
                        break;
                    case TrackCommand track:
                        _presence = track.PresenceJson;
                        break;
                    case WatchCommand watch:
                        _orderId = watch.OrderId;
                        break;
                }
            }
        }

        return true;
    }

    private IEnumerable<RealtimeBinding> Bindings()
    {
        yield return RealtimeBinding.Exports(_connectionId);
        if (_orderId is { } order)
        {
            yield return RealtimeBinding.Order(order);
        }
    }

    private string NextRef() => RealtimeFrames.RefText(++_ref);

    private static async Task<bool> SendAsync(ClientWebSocket socket, string frame)
    {
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    // The socket's one reader: each whole text frame onto the inbox, then the end of the socket.
    private async Task PumpAsync(ClientWebSocket socket, int generation)
    {
        byte[] buffer = new byte[16 * 1024];
        using MemoryStream message = new();
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    _inbox.Writer.TryWrite(new Inbound(generation, Encoding.UTF8.GetString(message.ToArray())));
                    message.SetLength(0);
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException or IOException)
        {
        }
        finally
        {
            _inbox.Writer.TryWrite(new Ended(generation));
        }
    }

    private async Task TickAsync(int generation, CancellationToken end)
    {
        using PeriodicTimer timer = new(_heartbeat);
        try
        {
            while (await timer.WaitForNextTickAsync(end).ConfigureAwait(false))
            {
                _inbox.Writer.TryWrite(new Tick(generation));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SetState(RealtimeState state)
    {
        if (Interlocked.Exchange(ref _state, (int)state) != (int)state)
        {
            Raise(() => StateChanged?.Invoke(this, state));
        }
    }

    private void Raise(Action raise)
    {
        try
        {
            raise();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log("Connected host: an event handler threw: " + ex.Message);
        }
    }

    private static async Task Swallow(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or HttpRequestException or ObjectDisposedException)
        {
        }
    }

    private readonly record struct Session(RealtimeState Ended, string Reason);

    private sealed record Inbound(int Generation, string Frame);

    private sealed record Ended(int Generation);

    private sealed record Tick(int Generation);

    private sealed record SetAuthCommand(string AccessToken);

    private sealed record TrackCommand(string PresenceJson);

    private sealed record WatchCommand(string? OrderId);

    private sealed class StopCommand
    {
        internal static readonly StopCommand Instance = new();
    }
}
