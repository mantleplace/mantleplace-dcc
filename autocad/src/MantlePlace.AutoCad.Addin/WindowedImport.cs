using System.IO;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using MantlePlace.AutoCad;
using MantlePlace.Client;
using MantlePlace.Core;
using MantlePlace.Wpf;

namespace MantlePlace.AutoCad.Addin;

internal sealed record WindowedImportEnd(ImportEnding Ending, string Summary);

internal sealed record WindowedImportStage(
    LevelControl? Levels,
    string? LevelNote,
    Func<FidelityLevel, CancellationToken, Task<LevelFetch>>? Fetch,
    Action<string, FidelityLevel, string>? Reopen,
    Action? Began);

internal sealed class WindowedImport : IImportWindowHost
{
    internal const string AlreadyRunning = "A Mantle Place import is already running in this AutoCAD. Wait for it to finish, or cancel it in its window.";

    private const string DrawingClosed = "the drawing was closed before it ran.";

    internal const string ClosedBeforeImport = "The import window was closed before Import was pressed. Nothing was imported.";

    private static volatile WindowedImport? _running;

    private readonly Document _document;
    private readonly string _zipPath;
    private readonly Action<WindowedImportEnd>? _ended;
    private readonly WindowedImportStage? _stage;
    private readonly FidelityLevel? _held;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private OrderImporter? _importer;
    private AutoCadImportRun? _run;
    private ImportWindowThread? _window;
    private CancellationTokenSource? _refetch;
    private string? _stoppedBecause;
    private Exception? _fault;
    private bool _over;
    private bool _staged;

    private WindowedImport(Document document, string zipPath, Action<WindowedImportEnd>? ended, WindowedImportStage? stage)
    {
        _document = document;
        _zipPath = zipPath;
        _ended = ended;
        _stage = stage;

        _held = stage?.Levels?.Selected;
    }

    internal static bool IsRunning => _running is not null;

    internal static bool AwaitsImport => _running is { _staged: true, _run: null, _over: false };

    internal static bool Start(Document document, string zipPath, Action<WindowedImportEnd>? ended, out string? refusal, WindowedImportStage? stage = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (_running is not null)
        {
            refusal = AlreadyRunning;
            return false;
        }

        WindowedImport import = new(document, zipPath, ended, stage);
        _running = import;

        OrderImportResult? refused = null;
        try
        {
            PresenceVerdict presence = Presence.Decide(HostObservations.Observe());
            import._importer = OrderImporter.Prepare(document, zipPath, presence, out refused);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            import._fault = ex;
        }

        bool staged = stage is { Levels: not null } && import._importer is not null;
        ImportWindowRequest request = new(
            Path.GetFileName(zipPath),
            import._importer?.DeliveryLine,
            UnitsDisagreement: null,
            Checklist: null,
            MantlePlaceApplication.MainWindowHandle(),
            import)
        {
            BeforeImport = staged ? import._importer!.Plan.Summary() : null,
            Levels = staged ? stage!.Levels : null,
            LevelNote = staged ? stage!.LevelNote : null,
        };

        ImportWindowThread? window;
        Exception? failure;
        try
        {
            window = ImportWindowThread.Open(request, out failure);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OutOfMemoryException)
        {
            window = null;
            failure = ex;
        }

        if (window is null)
        {
            import._importer?.Dispose();
            _running = null;
            refusal = $"The import window could not be opened, so nothing was imported ({failure?.Message}).";
            return false;
        }

        import._window = window;
        refusal = null;

        if (import._importer is null)
        {
            OrderImportResult result = refused ?? import.FaultResult();
            import.Post(() => import.Finish(result));
            return true;
        }

        if (staged)
        {
            import._staged = true;
            return true;
        }

        import.Run();
        return true;
    }

    public void Begin(ImportLayerChoice choice)
    {
        if (_over || _run is not null || _refetch is not null || _importer is null)
        {
            return;
        }

        Run();
        Tell(_stage?.Began);
    }

    public void ChooseLevel(FidelityLevel level)
    {
        if (_over || _run is not null || _refetch is not null || _importer is null)
        {
            return;
        }

        if (_stage is not { Fetch: { } fetch, Reopen: not null } || _held is not { } held)
        {
            _window?.LevelKept(_held ?? level, "The level cannot be changed in this window.");
            return;
        }

        MantlePlaceApplication.Say($"The {DownloadLevels.Word(level)} level was chosen in the import window, so it is downloaded before Import.");
        _importer.Dispose();
        _importer = null;

        CancellationTokenSource stop = new();
        _refetch = stop;
        _ = Task.Run(async () =>
        {
            LevelFetch fetched;
            try
            {
                fetched = await fetch(level, stop.Token).ConfigureAwait(false);
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                fetched = new LevelFetch(false, string.Empty, ex.Message);
            }

            _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => Refetched(stop, level, held, fetched));
        });
    }

    public void CancelRun() => _run?.RequestCancel();

    public void Dismiss()
    {
        _window = null;
        if (_run is not null)
        {
            _run.RequestCancel();
            return;
        }

        if (_staged)
        {
            FinishBeforeImport();
        }
    }

    public void Note(string line) => MantlePlaceApplication.Say(line);

    public void Fault(Exception fault) => MantlePlaceApplication.SayFault(fault);

    private void Run()
    {
        _run = new AutoCadImportRun(_importer!.Plan, _importer.Civil3d);
        _window?.ShowRun(_run.View());
        Post(Step);
    }

    private static void Tell(Action? listener)
    {
        try
        {
            listener?.Invoke();
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            MantlePlaceApplication.SayFault(ex);
        }
    }

    private void Refetched(CancellationTokenSource stop, FidelityLevel chosen, FidelityLevel held, LevelFetch fetched)
    {
        if (_over || !ReferenceEquals(_refetch, stop))
        {
            return;
        }

        _refetch = null;
        stop.Dispose();
        _over = true;
        _window?.CloseNow();
        _window = null;
        _running = null;

        FidelityLevel now = fetched.Ready ? chosen : held;
        string note = fetched.Ready
            ? WindowLabels.LevelDownloaded(chosen)
            : WindowLabels.LevelNotDownloaded(chosen, held, fetched.Message);
        MantlePlaceApplication.Say(note);

        string zip = fetched.Ready ? fetched.ZipPath : _zipPath;
        Tell(() => _stage!.Reopen!(zip, now, note));
    }

    private void FinishBeforeImport()
    {
        if (_over)
        {
            return;
        }

        _over = true;
        if (_refetch is { } stop)
        {
            _refetch = null;
            stop.Cancel();
        }

        _importer?.Dispose();
        _importer = null;
        _running = null;

        MantlePlaceApplication.Say(ClosedBeforeImport);
        Tell(() => _ended?.Invoke(new WindowedImportEnd(ImportEnding.DismissedBeforeStart, ClosedBeforeImport)));
        MantlePlaceApplication.Connected?.TurnFree();
    }

    private void Post(Action action) => _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, action);

    private void Step()
    {
        if (_over || _run is null)
        {
            return;
        }

        if (_document.IsDisposed)
        {
            _stoppedBecause = DrawingClosed;
            _run.Stop();
        }

        if (_run.Next() is not { } index)
        {
            Finish(Result());
            return;
        }

        _window?.Refresh(_run.View());
        Post(() => RunStep(index));
    }

    private void RunStep(int index)
    {
        if (_run is null || _importer is null)
        {
            return;
        }

        bool succeeded;
        try
        {
            succeeded = _run.PartAt(index) is { } part ? _importer.Place(part) : _importer.Verify();
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            succeeded = false;
            _fault = ex;
        }

        _run.End(index, succeeded);
        if (_fault is not null)
        {
            _run.Stop();
        }

        _window?.Refresh(_run.View());
        Post(Step);
    }

    private OrderImportResult Result()
    {
        if (_fault is not null)
        {
            return FaultResult();
        }

        string why = _stoppedBecause ?? AutoCadImportRun.NotReachedReason;
        return _importer!.Result(_run!.NotReached(why));
    }

    private OrderImportResult FaultResult()
    {
        AutoCadImportPlan plan = _importer?.Plan ?? new AutoCadImportPlan();
        string summary = $"The import stopped on an error: {_fault?.Message}. What it placed before the error stays in the drawing.";
        return new OrderImportResult(plan, summary, false);
    }

    private void Finish(OrderImportResult result)
    {
        if (_over)
        {
            return;
        }

        _over = true;
        _window?.ShowFinished(
            _run?.View(),
            _run is { WasCancelled: true } ? AutoCadImportRun.CancelledOutcome : null,
            result.Summary);

        _window?.BringForwardAtTheEnd();
        _importer?.Dispose();

        MantlePlaceApplication.Say(result.Summary);
        if (_fault is not null)
        {
            MantlePlaceApplication.SayFault(_fault);
        }

        ImportEnding ending = _fault is not null
            ? ImportEnding.Failed
            : _run?.Ending(result.PlacedAnything) ?? ImportEnding.Failed;

        _running = null;
        try
        {
            _ended?.Invoke(new WindowedImportEnd(ending, result.Summary));
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            MantlePlaceApplication.SayFault(ex);
        }

        MantlePlaceApplication.Connected?.TurnFree();
    }
}
