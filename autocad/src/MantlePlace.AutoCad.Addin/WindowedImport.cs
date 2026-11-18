using System.IO;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using MantlePlace.AutoCad;
using MantlePlace.Client;
using MantlePlace.Core;
using MantlePlace.Wpf;

namespace MantlePlace.AutoCad.Addin;

/// <summary>How an import in the window ended: what the web hears, and the closing report.</summary>
internal sealed record WindowedImportEnd(ImportEnding Ending, string Summary);

/// <summary>
/// A stage before Import: the window opens on what the import will do, with Import to press, and the
/// run waits for it (<see cref="ImportWindowRequest.BeforeImport"/>). With a level control, a received
/// send's 3.0 order can be fetched at another level first, as in Revit.
/// </summary>
/// <param name="Levels">The level control, on the level the zip is at; <c>null</c> for none.</param>
/// <param name="LevelNote">The line under the control: what the last level choice did; <c>null</c> for none.</param>
/// <param name="Fetch">Downloads the order at a level, off AutoCAD's thread; <c>null</c> when no level can be changed.</param>
/// <param name="Reopen">
/// Opens the import again on the zip the slot now holds, at that level, with the note: the chosen level's,
/// or the held one's when its download failed. On AutoCAD's thread, once this window has closed.
/// </param>
/// <param name="Began">Told on AutoCAD's thread when Import is pressed and the run begins; <c>null</c> when nobody listens.</param>
internal sealed record WindowedImportStage(
    LevelControl? Levels,
    string? LevelNote,
    Func<FidelityLevel, CancellationToken, Task<LevelFetch>>? Fetch,
    Action<string, FidelityLevel, string>? Reopen,
    Action? Began);

/// <summary>
/// Every AutoCAD and Civil 3D import, from a zip on disk or a send from the web vault, run behind the
/// shared import window (<c>HPS-60</c>): its steps with their state, the step in flight, the clock, and
/// Cancel between steps.
/// </summary>
/// <remarks>
/// <para>
/// AutoCAD offers no per-layer choice, so the window opens with no checklist. Import Bundle's import
/// begins at once. A received send and an import from the vault open on a stage before Import instead
/// (<see cref="WindowedImportStage"/>): what the plan will do, and Import to press; a send's 3.0 order
/// shows the level control there, and another level is downloaded and the window reopened on it before
/// anything is drawn. What the window shows is <see cref="AutoCadImportRun"/>'s, decided in Core; this
/// runs the step it is handed (<see cref="OrderImporter"/>) and posts the view.
/// </para>
/// <para>
/// ⛔ <b>One step per dispatcher operation, on AutoCAD's thread.</b> Each step is posted at
/// <see cref="DispatcherPriority.Background"/>, so between two steps AutoCAD pumps its messages and the
/// window's Cancel, which <see cref="ImportWindowThread"/> posts here at a higher priority, is heard before
/// the next step begins. The step in flight is one transaction and is never interrupted. The window is on a
/// thread of its own, so it keeps painting and its clock keeps running while a step holds this thread.
/// </para>
/// <para>
/// One import at a time: a second start while one runs is refused with a sentence, and a send from the web
/// waits its turn (<see cref="AutoCadExportImportHost"/>). Whoever started the run hears its end
/// (<see cref="WindowedImportEnd"/>); the command line hears the report either way.
/// </para>
/// </remarks>
internal sealed class WindowedImport : IImportWindowHost
{
    /// <summary>What a second import is told while one runs.</summary>
    internal const string AlreadyRunning = "A Mantle Place import is already running in this AutoCAD. Wait for it to finish, or cancel it in its window.";

    /// <summary>What the report says of the parts a closed drawing left unplaced.</summary>
    private const string DrawingClosed = "the drawing was closed before it ran.";

    /// <summary>What an import closed before Import is told.</summary>
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

        // Read before the window has the control: the window's thread moves its selection as the curator chooses.
        _held = stage?.Levels?.Selected;
    }

    /// <summary>Whether an import is running in this AutoCAD. Read from any thread.</summary>
    internal static bool IsRunning => _running is not null;

    /// <summary>Whether the import window is on its stage before Import, waiting for it to be pressed. AutoCAD's thread.</summary>
    internal static bool AwaitsImport => _running is { _staged: true, _run: null, _over: false };

    /// <summary>
    /// Opens the import window on <paramref name="zipPath"/> and starts importing it into
    /// <paramref name="document"/>. On AutoCAD's thread.
    /// </summary>
    /// <param name="document">The drawing the order goes into.</param>
    /// <param name="zipPath">The bundle zip.</param>
    /// <param name="ended">Told once the run is over, on AutoCAD's thread; <c>null</c> when nobody listens.</param>
    /// <param name="refusal">Why no import started: one is already running, or the window would not open.</param>
    /// <param name="stage">
    /// A stage before Import, or <c>null</c> for an import that begins as the window opens. Shown only for
    /// a plan that places something; a refusal is reported as before.
    /// </param>
    /// <returns>
    /// Whether the import started; when it did, <paramref name="ended"/> will be called, unless a level
    /// choice reopens it (<see cref="WindowedImportStage.Reopen"/>), whose import is told instead.
    /// </returns>
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
#pragma warning disable CA1031 // A drawing or a zip that throws while being read is reported in the window, never left to take AutoCAD down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            import._fault = ex;
        }

        // The stage before Import only over a plan to press it on: a refusal opens on its report, as before.
        bool staged = stage is not null && import._importer is not null;
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
            // Refused before any step (not a bundle, another coordinate system), or the reading threw:
            // the window shows why, and nothing was drawn. Posted, so whoever started the import hears it
            // end only after Start has returned.
            OrderImportResult result = refused ?? import.FaultResult();
            import.Post(() => import.Finish(result));
            return true;
        }

        if (staged)
        {
            // The run waits for Import (Begin), or ends at a close (Dismiss) or a level choice (ChooseLevel).
            import._staged = true;
            return true;
        }

        import.Run();
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Import, pressed on the stage before Import. Every part the plan places runs: AutoCAD has no
    /// per-layer choice, so <paramref name="choice"/> is every layer.
    /// </remarks>
    public void Begin(ImportLayerChoice choice)
    {
        if (_over || _run is not null || _refetch is not null || _importer is null)
        {
            return;
        }

        Run();
        Tell(_stage?.Began);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Another level of a sent 3.0 order, before Import. The importer is let go, since its archive holds the
    /// zip the download replaces; the window stays, saying the level is downloading; and the import reopens
    /// on the new level once it is in the slot, or on the level it held when the download failed.
    /// </remarks>
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
#pragma warning disable CA1031 // The window goes on at the level it holds, whatever the fault was.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                fetched = new LevelFetch(false, string.Empty, ex.Message);
            }

            _ = _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => Refetched(stop, level, held, fetched));
        });
    }

    /// <inheritdoc/>
    public void CancelRun() => _run?.RequestCancel();

    /// <inheritdoc/>
    /// <remarks>
    /// The window is gone. With the run going, nothing is left to show it, so it stops at its next boundary;
    /// before Import, nothing has run and nothing will, and whoever started the import hears it dropped.
    /// </remarks>
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

    /// <inheritdoc/>
    public void Note(string line) => MantlePlaceApplication.Say(line);

    /// <inheritdoc/>
    public void Fault(Exception fault) => MantlePlaceApplication.SayFault(fault);

    /// <summary>The run begins: the window shows its steps, and the first is queued.</summary>
    private void Run()
    {
        _run = new AutoCadImportRun(_importer!.Plan, _importer.Civil3d);
        _window?.ShowRun(_run.View());
        Post(Step);
    }

    /// <summary>Tells a listener, which must not take AutoCAD down by throwing.</summary>
    private static void Tell(Action? listener)
    {
        try
        {
            listener?.Invoke();
        }
#pragma warning disable CA1031 // Whoever listens is told; a listener that throws must not take AutoCAD down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            MantlePlaceApplication.SayFault(ex);
        }
    }

    /// <summary>
    /// AutoCAD's thread: the chosen level's download ended. This window closes, and the import reopens on
    /// whichever level the slot now holds, saying which. Nothing, when the window was closed meanwhile.
    /// </summary>
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

    /// <summary>The window went before Import: any level download stops, nothing ran, and whoever started the import hears it dropped.</summary>
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

    /// <summary>Queues <paramref name="action"/> behind everything AutoCAD and the window have queued.</summary>
    private void Post(Action action) => _ = _dispatcher.BeginInvoke(DispatcherPriority.Background, action);

    /// <summary>Begins the next step, or ends the run. One dispatcher operation.</summary>
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

        // The row reads Importing before the step holds this thread, so the window says which step it is.
        _window?.Refresh(_run.View());
        Post(() => RunStep(index));
    }

    /// <summary>Runs step <paramref name="index"/>, then queues the next. One dispatcher operation.</summary>
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
#pragma warning disable CA1031 // A step that throws ends the run and is reported; it never takes AutoCAD down.
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

    /// <summary>What the run did, each part it never reached said as not imported.</summary>
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

    /// <summary>The run is over: the window shows the report, the command line says it, and whoever started it hears.</summary>
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

        // Unasked, so only over AutoCAD: a curator in another application is not interrupted.
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
#pragma warning disable CA1031 // Whoever listens is told; a listener that throws must not take AutoCAD down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            MantlePlaceApplication.SayFault(ex);
        }

        // Whichever import it was, an export waiting for the window may have it now.
        MantlePlaceApplication.Connected?.TurnFree();
    }
}
