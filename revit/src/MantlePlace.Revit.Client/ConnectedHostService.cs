using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Client;

/// <summary>The curator's private channel, as the connected host drives it. <see cref="RealtimeClient"/> is the real one.</summary>
public interface IConnectedChannel
{
    /// <summary>Raised on every <c>SUBSCRIBED</c>: a catch-up is due. On the channel's thread.</summary>
    event EventHandler? Subscribed;

    /// <summary>A row change on a subscribed table. On the channel's thread.</summary>
    event EventHandler<RealtimeRowChange>? RowChanged;

    /// <summary>Joins on this session, tracking <paramref name="presence"/>; <c>false</c> when it will not connect.</summary>
    bool Start(string accessToken, ConnectedHost presence);

    /// <summary>Re-authorises with a renewed access token.</summary>
    void SetAuth(string accessToken);

    /// <summary>Re-tracks presence with a changed payload.</summary>
    void Track(ConnectedHost presence);

    /// <summary>Watches one order's row.</summary>
    void WatchOrder(string orderId);

    /// <summary>Drops the order subscription.</summary>
    void StopWatchingOrder();

    /// <summary>Untracks, leaves and closes, waiting a bounded time.</summary>
    Task StopAsync();
}

/// <summary>The two v1 export routes. <see cref="ExportsClient"/> is the real one.</summary>
public interface IExportRoutes
{
    Task<ExportCall<IReadOnlyList<HostExport>>> ListAsync(string connectionId, CancellationToken cancellationToken);

    Task<ExportCall<HostExport>> AdvanceAsync(
        HostExport current,
        string connectionId,
        ExportTransition transition,
        CancellationToken cancellationToken);
}

/// <summary>What the connected host needs from Revit: an import window it can open and close for an export.</summary>
/// <remarks>
/// Both calls come from any thread and must return without waiting on Revit: the implementation
/// queues the work and marshals it (an <c>ExternalEvent</c> for the document, the window's dispatcher
/// for the window). What happens next comes back through the service's <c>Import…</c> methods.
/// </remarks>
public interface IExportImportHost
{
    /// <summary>
    /// Asks Revit to open the import window on <paramref name="zipPath"/> for this export.
    /// <c>false</c> when another import already holds the window: the export waits its turn.
    /// </summary>
    bool RequestOpen(HostExport export, string zipPath);

    /// <summary>Closes this export's window if it is still on its checklist; a running import is left alone.</summary>
    void Close(string exportId);
}

/// <summary>
/// The connected host: one Revit for the whole session, on the curator's private channel, receiving
/// exports from the web vault and reporting each one's life (the platform export contract).
/// </summary>
/// <remarks>
/// <para>
/// Every decision is <see cref="ExportDesk"/>'s, in <c>MantlePlace.Revit.Core</c>. What is here is the
/// wiring: the channel's events and the routes' answers go into the desk, and the desk's actions go
/// out to the channel, the routes, the fetch and the host's import window.
/// </para>
/// <para>
/// ⛔ <b>Nothing here waits on the network on the caller's thread.</b> Every route call and every
/// fetch runs on the thread pool; the desk is touched under one lock, briefly, from whichever thread
/// brings news — the channel's, the pool's, or Revit's own when the import window reports. The host's
/// two calls are its to marshal (<see cref="IExportImportHost"/>).
/// </para>
/// <para>
/// <b>Joins and leaves.</b> <see cref="Join"/> on a sign-in or a restored session; <see cref="Reauthorise"/>
/// after each renewal; <see cref="Announce"/> when the active document changes; <see cref="LeaveAsync"/> on
/// sign-out and when Revit closes. A channel is made fresh for every join, because a stopped one never
/// starts again. Whatever was in flight when it left is dropped where it stands; news from it that
/// arrives late is ignored.
/// </para>
/// </remarks>
public sealed class ConnectedHostService
{
    private readonly object _gate = new();
    private readonly string _connectionId;
    private readonly Func<IConnectedChannel> _channels;
    private readonly IExportRoutes _routes;
    private readonly Func<string, Action, CancellationToken, Task<ExportFetchResult>> _fetch;
    private readonly Action<string> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly ExportDesk _desk;

    private IExportImportHost? _host;
    private IConnectedChannel? _channel;
    private CancellationTokenSource? _work;
    private int _generation;

    /// <param name="connectionId">This launch's connection id.</param>
    /// <param name="channels">Makes a fresh channel for each join.</param>
    /// <param name="routes">The export routes, on the bearer session.</param>
    /// <param name="fetch">Fetches an order's bundle (<see cref="ExportBundleFetcher.FetchAsync"/>).</param>
    /// <param name="log">Where the one-line notes go.</param>
    /// <param name="now">The clock progress is paced by; the system clock when <c>null</c>.</param>
    /// <param name="delay">How a retry waits, for tests; a real delay when <c>null</c>.</param>
    public ConnectedHostService(
        string connectionId,
        Func<IConnectedChannel> channels,
        IExportRoutes routes,
        Func<string, Action, CancellationToken, Task<ExportFetchResult>> fetch,
        Action<string> log,
        Func<DateTimeOffset>? now = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);
        _connectionId = connectionId;
        _channels = channels ?? throw new ArgumentNullException(nameof(channels));
        _routes = routes ?? throw new ArgumentNullException(nameof(routes));
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _delay = delay ?? Task.Delay;
        _desk = new ExportDesk(connectionId, now ?? (() => DateTimeOffset.UtcNow));
    }

    /// <summary>The connection id the channel and the routes are scoped to.</summary>
    public string ConnectionId => _connectionId;

    /// <summary>Whether a channel is running.</summary>
    public bool IsJoined
    {
        get
        {
            lock (_gate)
            {
                return _channel is not null;
            }
        }
    }

    /// <summary>The import window this service opens. Set once, before the first join.</summary>
    public void Attach(IExportImportHost host)
    {
        lock (_gate)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }
    }

    /// <summary>Joins the curator's channel on this session. <c>false</c> when it will not connect (not configured, say).</summary>
    public bool Join(string accessToken, ConnectedHost presence)
    {
        IConnectedChannel channel;
        lock (_gate)
        {
            if (_channel is not null)
            {
                _channel.SetAuth(accessToken);
                _channel.Track(presence);
                return true;
            }

            _generation++;
            _work = new CancellationTokenSource();
            channel = _channels();
            int generation = _generation;
            channel.Subscribed += (_, _) => OnSubscribed(generation);
            channel.RowChanged += (_, change) => OnRowChanged(generation, change);
            _channel = channel;
        }

        if (channel.Start(accessToken, presence))
        {
            _log("Connected host: joining as " + _connectionId + ".");
            return true;
        }

        lock (_gate)
        {
            if (ReferenceEquals(_channel, channel))
            {
                _channel = null;
                _generation++;
                _work?.Dispose();
                _work = null;
            }
        }

        return false;
    }

    /// <summary>Authorises the channel again after a renewal.</summary>
    public void Reauthorise(string accessToken)
    {
        lock (_gate)
        {
            _channel?.SetAuth(accessToken);
        }
    }

    /// <summary>Re-tracks presence: the active document changed, or there is none now.</summary>
    public void Announce(ConnectedHost presence)
    {
        lock (_gate)
        {
            _channel?.Track(presence);
        }
    }

    /// <summary>Untracks and leaves. Exports in hand are dropped where they stand.</summary>
    public async Task LeaveAsync()
    {
        IConnectedChannel? channel;
        lock (_gate)
        {
            channel = _channel;
            _channel = null;
            _generation++;
            _work?.Cancel();
            _work?.Dispose();
            _work = null;
            _desk.Reset();
        }

        if (channel is not null)
        {
            _log("Connected host: leaving.");
            await channel.StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The import window for this export is open on its checklist. Revit's thread.</summary>
    public void ImportOpened(string exportId) => Feed(desk => desk.ImportOpened(exportId));

    /// <summary>The window could not open on the bundle, for this reason. Revit's thread.</summary>
    public void ImportRefused(string exportId, string reason, string message)
        => Feed(desk => desk.ImportRefused(exportId, reason, message));

    /// <summary>Another import held the window when this export's came to open. Revit's thread.</summary>
    public void ImportBusy(string exportId) => Feed(desk => desk.ImportBusy(exportId));

    /// <summary>The curator pressed Import. Revit's thread.</summary>
    public void ImportBegan(string exportId) => Feed(desk => desk.ImportBegan(exportId));

    /// <summary>How far the running import has got. Revit's thread; paced by the desk.</summary>
    public void ImportProgress(string exportId, int percent) => Feed(desk => desk.ImportProgress(exportId, percent));

    /// <summary>The window's run ended, or the window went before Import. Revit's thread.</summary>
    public void ImportEnded(string exportId, ImportEnding ending, string message)
        => Feed(desk => desk.ImportEnded(exportId, ending, message));

    /// <summary>No import holds the window any more. Revit's thread.</summary>
    public void TurnFree() => Feed(desk => desk.TurnFree());

    private void OnSubscribed(int generation)
    {
        CancellationToken token;
        lock (_gate)
        {
            if (generation != _generation || _work is null)
            {
                return;
            }

            token = _work.Token;
        }

        _ = Task.Run(async () =>
        {
            ExportCall<IReadOnlyList<HostExport>> listed;
            try
            {
                listed = await _routes.ListAsync(_connectionId, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // The next SUBSCRIBED lists again; a fault here is a line, not a dialog.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log("Connected host: the catch-up list failed: " + ex.Message);
                return;
            }

            if (!listed.Succeeded)
            {
                _log("Connected host: the catch-up list failed: " + listed.Message);
                return;
            }

            foreach (HostExport row in listed.Value!)
            {
                Feed(desk => desk.Seen(row), generation);
            }
        });
    }

    private void OnRowChanged(int generation, RealtimeRowChange change)
    {
        if (change.IsExport)
        {
            if (HostExports.TryParse(change.RecordJson, out HostExport row) is { } why)
            {
                _log("Connected host: an export row change was not read: " + why);
                return;
            }

            Feed(desk => desk.Seen(row), generation);
        }
        else if (change.IsOrder)
        {
            Feed(desk => desk.OrderChanged(change.RecordJson), generation);
        }
    }

    private void Feed(Func<ExportDesk, IReadOnlyList<ExportAction>> input, int? generation = null)
    {
        lock (_gate)
        {
            if (generation is { } stated && stated != _generation)
            {
                return;
            }

            Execute(input(_desk));
        }
    }

    // Under the lock. Nothing here waits: channel commands are queued, host calls are queued by the
    // host, and route calls and fetches are started on the pool.
    private void Execute(IReadOnlyList<ExportAction> actions)
    {
        int generation = _generation;
        CancellationToken token = _work?.Token ?? new CancellationToken(canceled: true);

        foreach (ExportAction action in actions)
        {
            switch (action)
            {
                case NoteExport note:
                    _log("Connected host: " + note.Line);
                    break;

                case WatchExportOrder watch:
                    _channel?.WatchOrder(watch.OrderId);
                    break;

                case StopWatchingExportOrder:
                    _channel?.StopWatchingOrder();
                    break;

                case AdvanceExport advance:
                    _ = Task.Run(() => AdvanceAsync(advance, generation, token));
                    break;

                case FetchExportBundle fetch:
                    _ = Task.Run(() => FetchAsync(fetch.Export, generation, token));
                    break;

                case OpenExportImport open:
                    if (_host is null || !_host.RequestOpen(open.Export, open.ZipPath))
                    {
                        Execute(_desk.ImportBusy(open.Export.Id));
                    }

                    break;

                case CloseExportImport close:
                    _host?.Close(close.ExportId);
                    break;
            }
        }
    }

    private async Task AdvanceAsync(AdvanceExport advance, int generation, CancellationToken token)
    {
        ExportAdvanceAnswer answer;
        try
        {
            if (advance.Delay > TimeSpan.Zero)
            {
                await _delay(advance.Delay, token).ConfigureAwait(false);
            }

            ExportCall<HostExport> call = await _routes.AdvanceAsync(advance.Export, _connectionId, advance.Transition, token)
                .ConfigureAwait(false);
            answer = call.Succeeded
                ? ExportAdvanceAnswer.Ok(call.Value)
                : new ExportAdvanceAnswer(OutcomeOf(call.Outcome), null, call.Message, call.RetryAfterMs);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
#pragma warning disable CA1031 // A report that faults is a retry, not a fault dialog.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            answer = new ExportAdvanceAnswer(ExportAdvanceOutcome.RetryLater, null, ex.Message, 0);
        }

        Feed(desk => desk.Answered(advance.Export.Id, answer), generation);
    }

    private async Task FetchAsync(HostExport export, int generation, CancellationToken token)
    {
        ExportFetchResult result = await _fetch(
            export.OrderId,
            () => Feed(desk => desk.FetchBuilding(export.Id), generation),
            token).ConfigureAwait(false);
        Feed(desk => desk.FetchEnded(export.Id, result), generation);
    }

    private static ExportAdvanceOutcome OutcomeOf(ExportCallOutcome outcome) => outcome switch
    {
        ExportCallOutcome.Ok => ExportAdvanceOutcome.Ok,
        ExportCallOutcome.WebMovedFirst => ExportAdvanceOutcome.WebMovedFirst,
        ExportCallOutcome.RateLimited => ExportAdvanceOutcome.RateLimited,
        ExportCallOutcome.RetryLater => ExportAdvanceOutcome.RetryLater,
        ExportCallOutcome.RefusedLocally => ExportAdvanceOutcome.RefusedLocally,
        ExportCallOutcome.SignInNeeded => ExportAdvanceOutcome.SignInNeeded,
        _ => ExportAdvanceOutcome.Rejected,
    };
}

/// <summary>The connected host's log: one file per machine, appended, kept small.</summary>
public sealed class ConnectedHostLog
{
    /// <summary>A log past this is started again at the first line of a launch.</summary>
    public const long MaxBytes = 1_000_000;

    private readonly object _gate = new();
    private readonly string _path;
    private bool _checked;

    public ConnectedHostLog(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
    }

    /// <summary>Under <c>%LOCALAPPDATA%</c>, beside the machine's other records.</summary>
    public static string DefaultPath => MachineRecordFile.InLocalAppData("connected-host.log");

    /// <summary>Where it is written.</summary>
    public string Path => _path;

    /// <summary>Appends one line. A file that cannot be written costs the line, never the caller.</summary>
    public void Append(string line)
    {
        lock (_gate)
        {
            try
            {
                if (!_checked)
                {
                    _checked = true;
                    FileInfo file = new(_path);
                    if (file.Exists && file.Length > MaxBytes)
                    {
                        file.Delete();
                    }
                }

                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                File.AppendAllText(
                    _path,
                    DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture)
                        + "  " + line + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
