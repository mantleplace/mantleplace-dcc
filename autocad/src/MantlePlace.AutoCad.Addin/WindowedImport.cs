using System.IO;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using MantlePlace.AutoCad;
using MantlePlace.Core;
using MantlePlace.Wpf;

namespace MantlePlace.AutoCad.Addin;

/// <summary>How an import in the window ended: what the web hears, and the closing report.</summary>
internal sealed record WindowedImportEnd(ImportEnding Ending, string Summary);

/// <summary>
/// Every AutoCAD and Civil 3D import, from a zip on disk or a send from the web vault, run behind the
/// shared import window (<c>HPS-60</c>): its steps with their state, the step in flight, the clock, and
/// Cancel between steps.
/// </summary>
/// <remarks>
/// <para>
/// AutoCAD offers no per-layer choice, so the window opens with no checklist and the import begins at
/// once. What the window shows is <see cref="AutoCadImportRun"/>'s, decided in Core; this runs the step
/// it is handed (<see cref="OrderImporter"/>) and posts the view.
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

    private static volatile WindowedImport? _running;

    private readonly Document _document;
    private readonly Action<WindowedImportEnd>? _ended;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private OrderImporter? _importer;
    private AutoCadImportRun? _run;
    private ImportWindowThread? _window;
    private string? _stoppedBecause;
    private Exception? _fault;
    private bool _over;

    private WindowedImport(Document document, Action<WindowedImportEnd>? ended)
    {
        _document = document;
        _ended = ended;
    }

    /// <summary>Whether an import is running in this AutoCAD. Read from any thread.</summary>
    internal static bool IsRunning => _running is not null;

    /// <summary>
    /// Opens the import window on <paramref name="zipPath"/> and starts importing it into
    /// <paramref name="document"/>. On AutoCAD's thread.
    /// </summary>
    /// <param name="document">The drawing the order goes into.</param>
    /// <param name="zipPath">The bundle zip.</param>
    /// <param name="ended">Told once the run is over, on AutoCAD's thread; <c>null</c> when nobody listens.</param>
    /// <param name="refusal">Why no import started: one is already running, or the window would not open.</param>
    /// <returns>Whether the import started; when it did, <paramref name="ended"/> will be called.</returns>
    internal static bool Start(Document document, string zipPath, Action<WindowedImportEnd>? ended, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (_running is not null)
        {
            refusal = AlreadyRunning;
            return false;
        }

        WindowedImport import = new(document, ended);
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

        ImportWindowRequest request = new(
            Path.GetFileName(zipPath),
            import._importer?.DeliveryLine,
            UnitsDisagreement: null,
            Checklist: null,
            MantlePlaceApplication.MainWindowHandle(),
            import);

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

        import._run = new AutoCadImportRun(import._importer.Plan, import._importer.Civil3d);
        window.ShowRun(import._run.View());
        import.Post(import.Step);
        return true;
    }

    /// <inheritdoc/>
    /// <remarks>Never called: the window opens with no checklist, and the run begins here.</remarks>
    public void Begin(ImportLayerChoice choice)
    {
    }

    /// <inheritdoc/>
    /// <remarks>Never called: the window is given no level control (<see cref="AutoCadExportImportHost"/>).</remarks>
    public void ChooseLevel(FidelityLevel level)
    {
    }

    /// <inheritdoc/>
    public void CancelRun() => _run?.RequestCancel();

    /// <inheritdoc/>
    /// <remarks>The window is gone with the run still going: nothing is left to show it, so it stops at its next boundary.</remarks>
    public void Dismiss()
    {
        _window = null;
        _run?.RequestCancel();
    }

    /// <inheritdoc/>
    public void Note(string line) => MantlePlaceApplication.Say(line);

    /// <inheritdoc/>
    public void Fault(Exception fault) => MantlePlaceApplication.SayFault(fault);

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
        _window?.BringForward();
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
