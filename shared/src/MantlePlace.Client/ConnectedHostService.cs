using MantlePlace.Core;

namespace MantlePlace.Client;

public interface IConnectedChannel
{
    event EventHandler? Subscribed;

    event EventHandler<RealtimeRowChange>? RowChanged;

    bool Start(string accessToken, ConnectedHost presence);

    void SetAuth(string accessToken);

    void Track(ConnectedHost presence);

    void WatchOrder(string orderId);

    void StopWatchingOrder();

    Task StopAsync();
}

public interface IExportRoutes
{
    Task<ExportCall<IReadOnlyList<HostExport>>> ListAsync(string connectionId, CancellationToken cancellationToken);

    Task<ExportCall<HostExport>> AdvanceAsync(
        HostExport current,
        string connectionId,
        ExportTransition transition,
        CancellationToken cancellationToken);
}

public interface IExportImportHost
{
    bool RequestOpen(HostExport export, string zipPath);

    void Close(string exportId);
}

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

    public string ConnectionId => _connectionId;

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

    public void Attach(IExportImportHost host)
    {
        lock (_gate)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
        }
    }

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

    public void Reauthorise(string accessToken)
    {
        lock (_gate)
        {
            _channel?.SetAuth(accessToken);
        }
    }

    public void Announce(ConnectedHost presence)
    {
        lock (_gate)
        {
            _channel?.Track(presence);
        }
    }

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

    public void ImportOpened(string exportId) => Feed(desk => desk.ImportOpened(exportId));

    public void ImportRefused(string exportId, string reason, string message)
        => Feed(desk => desk.ImportRefused(exportId, reason, message));

    public void ImportBusy(string exportId) => Feed(desk => desk.ImportBusy(exportId));

    public void ImportBegan(string exportId) => Feed(desk => desk.ImportBegan(exportId));

    public void ImportProgress(string exportId, int percent) => Feed(desk => desk.ImportProgress(exportId, percent));

    public void ImportEnded(string exportId, ImportEnding ending, string message)
        => Feed(desk => desk.ImportEnded(exportId, ending, message));

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
#pragma warning disable CA1031
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
#pragma warning disable CA1031
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

public sealed class ConnectedHostLog
{
    public const long MaxBytes = 1_000_000;

    private readonly object _gate = new();
    private readonly string _path;
    private bool _checked;

    public ConnectedHostLog(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
    }

    public static string DefaultPath => MachineRecordFile.InLocalAppData("connected-host.log");

    public string Path => _path;

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
