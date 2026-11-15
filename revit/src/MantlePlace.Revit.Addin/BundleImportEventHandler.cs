using System.IO;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MantlePlace.Client;
using MantlePlace.Core;
using MantlePlace.Wpf;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// Drives an import on Revit's thread, one slice per <see cref="ExternalEvent"/> raise.
/// </summary>
/// <remarks>
/// <para>
/// Revit's document API is main-thread-only and there is no supported way to marshal onto it except
/// <see cref="ExternalEvent"/>. This used to run the whole import inside one <see cref="Execute"/>,
/// so Revit reported "not responding" for as long as the import took and nothing could be cancelled.
/// Now each <see cref="Execute"/> does one slice of an <see cref="ActiveImport"/> — start a step,
/// run a step, commit one chunk — posts the import window what it did, and raises the event again.
/// Revit repaints and processes input between the two.
/// </para>
/// <para>
/// The window is on a thread of its own (<see cref="ImportWindowThread"/>), so it does not wait for a
/// slice to end to be told anything: it is posted a view of the run after every slice, and from
/// inside one whenever a commit starts or ends or a step says what its wait has measured at
/// (<see cref="ActiveImport.Changed"/>). That is what keeps it naming the step Revit is committing.
/// </para>
/// <para>
/// ⛔ <b>The next raise is posted at <see cref="DispatcherPriority.Background"/>, not made inline.</b>
/// Background sits below input, so Revit's own window catches up with the slice that just finished
/// and a Cancel the window posted — at normal priority, from its own thread, possibly minutes ago
/// while Revit sat in a commit — is handled before the next slice can start. Raising inline would
/// leave both to whichever Revit happened to service first.
/// </para>
/// <para>
/// Three ways in, one driver. The vault window is on its own and queues a zip path (<see cref="QueueImport"/>),
/// opened here on Revit's thread; the connected host queues an export's zip the same way
/// (<see cref="QueueExport"/>), and hears how its window went through <see cref="MantlePlaceApplication.Connected"/>. The ribbon command is already on Revit's thread, opens its own
/// import and hands it over (<see cref="TakeOver"/>). The unattended path never comes here: a modeless
/// window in a journal playback never closes, so it runs its import to the end in place.
/// </para>
/// <para>
/// Either way the window opens on its checklist and nothing is raised until the curator presses
/// Import. Choosing is not a slice: it plans, and touches no document.
/// </para>
/// </remarks>
internal sealed class BundleImportEventHandler : IExternalEventHandler
{
    private readonly object _gate = new();
    private string? _zipPath;
    private ExternalEvent? _event;
    private ActiveImport? _import;

    /// <summary>The open import's window. Cleared when the import ends; its host stays in <see cref="_hosts"/>.</summary>
    private ImportWindowThread? _window;

    /// <summary>
    /// Every import window still on screen, the finished ones showing their report included, until its
    /// thread ends — so a fault dialog can lower them all and shutdown can close them all.
    /// </summary>
    private readonly List<ImportWindowThread> _hosts = [];

    /// <summary>Whether a slice is running, inside which a window's callback must not.</summary>
    private bool _inSlice;

    /// <summary>The window callbacks that arrived while a slice ran, to run once it has ended.</summary>
    private readonly List<Action> _heldUntilSliceEnds = [];

    /// <summary>Whether the running import came from the vault window, which is who hears about it.</summary>
    private bool _fromVault;

    /// <summary>
    /// An export's zip the connected host queued, opened at the next raise; or the same export queued
    /// again after the curator chose another level in its window (<c>Level</c> set). Guarded by
    /// <see cref="_gate"/>.
    /// </summary>
    private sealed record QueuedExport(string ExportId, string OrderId, string ZipPath, FidelityLevel? Level = null, string? LevelNote = null);

    private QueuedExport? _queuedExport;

    /// <summary>The export the open import was opened for, or <c>null</c> for one the curator opened.</summary>
    private string? _exportId;

    /// <summary>The order the open export's import is for, which its level control is built from.</summary>
    private string? _exportOrderId;

    /// <summary>The level the open export's 3.0 bundle is at in the slot; <c>null</c> for a 2.x order.</summary>
    private FidelityLevel? _exportLevel;

    /// <summary>
    /// An export's chosen level downloading into the slot: its import is dropped (its archive holds the
    /// zip the download replaces) and its window stays on screen saying so, until the export reopens
    /// on the new level. Set on Revit's thread under <see cref="_gate"/>.
    /// </summary>
    private sealed record LevelRefetch(
        string ExportId,
        string OrderId,
        string ZipPath,
        FidelityLevel Chosen,
        FidelityLevel Held,
        ActiveImport Import,
        ImportWindowThread? Window,
        CancellationTokenSource Stop);

    private LevelRefetch? _refetch;

    /// <summary>The source a received send's level control and its fresh download come from; set once, at startup.</summary>
    private VaultExportSource? _levels;

    /// <summary>
    /// Raised on Revit's thread with one line for the vault window's status: why an import it asked
    /// for did not start, or that it finished and where its report is. An import started from the
    /// ribbon is not the vault window's to report.
    /// </summary>
    internal event EventHandler<string>? Completed;

    /// <summary>
    /// Whether an import is open — running, or waiting on its checklist. One at a time: two would
    /// interleave in one document.
    /// </summary>
    internal bool IsImporting => _import is not null || _refetch is not null;

    /// <summary>Why a second import cannot open now, in the words that fit which state the first is in.</summary>
    internal string BusyReason => _refetch is not null
        ? "An import window is downloading the level chosen in it — wait for it to reopen, or close it."
        : _import?.Staged is null
        ? "An import is waiting in its window — press Import there, or close it."
        : "An import is already running — wait for it, or cancel it in its window.";

    /// <summary>The event this handler re-raises between slices. Set once, at startup.</summary>
    internal void Attach(ExternalEvent externalEvent) => _event = externalEvent;

    /// <summary>The source of a received send's levels and its fresh downloads. Set once, at startup.</summary>
    internal void AttachLevels(VaultExportSource levels) => _levels = levels;

    /// <summary>Queues a zip from the vault window. The last one queued before the event fires is the one that runs.</summary>
    internal void QueueImport(string zipPath)
    {
        lock (_gate)
        {
            _zipPath = zipPath;
        }
    }

    /// <summary>
    /// Queues an export's zip from the connected host, on any thread. <c>false</c> when an import already
    /// holds the window, or another export's is queued: the export waits its turn.
    /// </summary>
    /// <remarks>
    /// Read off Revit's thread, the busy check can be a moment stale; the raise re-checks on Revit's
    /// thread and says busy then (<see cref="OpenQueuedExport"/>), so a stale answer costs a turn, not
    /// a second window.
    /// </remarks>
    internal bool QueueExport(string exportId, string orderId, string zipPath)
    {
        lock (_gate)
        {
            if (_import is not null || _queuedExport is not null || _refetch is not null)
            {
                return false;
            }

            _queuedExport = new QueuedExport(exportId, orderId, zipPath);
        }

        return true;
    }

    /// <summary>
    /// The web cancelled an export: its window goes if it is still on its checklist, and a queued one
    /// is dropped. Revit's thread. A running import is left alone: the web cannot cancel one.
    /// </summary>
    internal void CloseExport(string exportId)
    {
        lock (_gate)
        {
            if (_queuedExport is { } queued && string.Equals(queued.ExportId, exportId, StringComparison.Ordinal))
            {
                _queuedExport = null;
            }
        }

        if (_refetch is { } refetch && string.Equals(refetch.ExportId, exportId, StringComparison.Ordinal))
        {
            // The web cancelled it while its chosen level downloaded: the download stops, the window
            // goes, and nothing reopens. The desk has already moved the export on.
            DropRefetch(refetch);
            refetch.Window?.CloseNow();
            MantlePlaceApplication.Connected?.TurnFree();
            return;
        }

        if (!string.Equals(_exportId, exportId, StringComparison.Ordinal) || _import is not { Staged: null } import)
        {
            return;
        }

        import.Note("The export was cancelled from the web, so the import window closed before anything ran.");
        _window?.CloseNow();
        import.CancelBeforeStart();
        End(import);
    }

    /// <summary>
    /// Takes over an import the ribbon command opened and shows its window, on its checklist. The
    /// first slice is raised when the curator presses Import.
    /// </summary>
    internal void TakeOver(ActiveImport import, IntPtr revitWindow)
    {
        ArgumentNullException.ThrowIfNull(import);
        if (_import is not null)
        {
            throw new InvalidOperationException("An import is already running.");
        }

        Show(import, revitWindow, fromVault: false);
    }

    /// <summary>Brings the running import's window forward, for a second click on Import.</summary>
    internal void ShowRunning() => _window?.BringForward();

    /// <summary>Lowers every import window before Revit shows a dialog, so the dialog does not open under one.</summary>
    internal void LowerWindow()
    {
        foreach (ImportWindowThread host in _hosts)
        {
            host.Lower();
        }
    }

    public void Execute(UIApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        string? zipPath;
        QueuedExport? export;
        lock (_gate)
        {
            zipPath = _zipPath;
            _zipPath = null;
            export = _queuedExport;
            _queuedExport = null;
        }

        if (zipPath is not null)
        {
            OpenQueued(application, zipPath);
        }

        if (export is { } queuedExport)
        {
            OpenQueuedExport(application, queuedExport);
        }

        // An import still on its checklist has nothing to advance: it was either opened by this very
        // raise, or it is waiting on the curator's Import.
        if (_import is not { Staged: not null } import)
        {
            return;
        }

        bool more;
        _inSlice = true;
        try
        {
            more = import.Advance();
        }
        catch (Exception ex)
        {
            // The one catch-all in the import, and the contract rather than a shortcut: an exception
            // out of here would leave the run unable to advance or close (ActiveImport.Abandon).
            import.Abandon(ex);
            more = false;
        }
        finally
        {
            _inSlice = false;
        }

        RunHeld(import);
        Refresh(import);
        ReportProgress(import);

        if (more)
        {
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(RaiseNextSlice));
            return;
        }

        End(import);
    }

    public string GetName() => "Mantle Place bundle import";

    private void OpenQueued(UIApplication application, string zipPath)
    {
        if (_import is not null || _refetch is not null)
        {
            _window?.BringForward();
            Completed?.Invoke(this, BusyReason);
            return;
        }

        if (application.ActiveUIDocument?.Document is not Document document)
        {
            Completed?.Invoke(this, "Open a project first — there is no active document to import into.");
            return;
        }

        if (ActiveImport.Open(application.Application, document, zipPath, out ImportRefusal? refusal) is not { } import)
        {
            Completed?.Invoke(this, refusal!.Instruction + Environment.NewLine + refusal.Message);
            return;
        }

        Show(import, application.MainWindowHandle, fromVault: true);
    }

    /// <summary>Opens the import window for an export, on Revit's thread, and tells the connected host how it went.</summary>
    private void OpenQueuedExport(UIApplication application, QueuedExport queued)
    {
        if (MantlePlaceApplication.Connected is not { } connected)
        {
            return;
        }

        string exportId = queued.ExportId;
        string zipPath = queued.ZipPath;

        // Reopened after a level choice, the web already heard the window open: a refusal now is the
        // end of an import that never ran, and the window it held is free again.
        void Refuse(string reason, string message)
        {
            if (queued.Level is null)
            {
                connected.ImportRefused(exportId, reason, message);
                return;
            }

            connected.ImportEnded(exportId, ImportEnding.Failed, message);
            connected.TurnFree();
        }

        if (_import is not null)
        {
            connected.ImportBusy(exportId);
            return;
        }

        if (application.ActiveUIDocument?.Document is not Document document)
        {
            Refuse(
                ExportReasons.NoDocument,
                "Revit has no project open to import into. Open one, then send the bundle again.");
            return;
        }

        if (ActiveImport.Open(application.Application, document, zipPath, out ImportRefusal? refusal) is not { } import)
        {
            Refuse(refusal!.Code, refusal.Instruction + " " + refusal.Message);
            return;
        }

        // A send names no level: a 3.0 order was fetched at the recommendation, and its window opens
        // with the control on it; reopened after a level choice, on the level now in the slot.
        VaultBundle? listed = _levels?.Listed(queued.OrderId);
        LevelControl? levels = queued.Level is { } held ? LevelControl.At(listed, held) : LevelControl.For(listed);

        import.Note(queued.Level is { } level
            ? $"Reopened for export {exportId} at the {DownloadLevels.Word(level)} level."
            : $"Opened for export {exportId}, sent from the web vault.");
        if (!Show(import, application.MainWindowHandle, fromVault: false, levels, queued.LevelNote))
        {
            Refuse(ExportReasons.WindowFailed, "The import window could not be opened in Revit.");
            return;
        }

        _exportId = exportId;
        _exportOrderId = queued.OrderId;
        _exportLevel = levels?.Selected;

        // After a reopen this is nothing to the desk: the export is already awaiting options.
        connected.ImportOpened(exportId);
    }

    /// <summary>Tells the connected host how far an export's running import has got; it paces the reports.</summary>
    private void ReportProgress(ActiveImport import)
    {
        if (_exportId is { } exportId && import.Staged is { } staged && ReferenceEquals(import, _import))
        {
            MantlePlaceApplication.Connected?.ImportProgress(exportId, ImportPercent.Of(staged));
        }
    }

    /// <remarks>
    /// The import becomes this handler's only once its window is open. A window that could not open
    /// leaves no Import to press and no close box, so nothing could ever end that import: it is
    /// dropped, and the curator is told why. Every callback is bound to this import, so one arriving
    /// late from an earlier import's window acts on nothing it does not own.
    /// </remarks>
    /// <returns>Whether the window opened, and the import is this handler's.</returns>
    private bool Show(ActiveImport import, IntPtr revitWindow, bool fromVault, LevelControl? levels = null, string? levelNote = null)
    {
        // No measured costs: Revit's are said by the step itself, beside its clock (SlowStepNotice),
        // as they always were.
        ImportWindowRequest request = new(
            Path.GetFileName(import.ZipPath),
            import.DeliveryLine,
            import.UnitsDisagreement,
            import.Checklist,
            revitWindow,
            new WindowCalls(this, import))
        {
            Levels = levels,
            LevelNote = levelNote,
        };

        ImportWindowThread? host;
        Exception? failure;
        try
        {
            host = ImportWindowThread.Open(request, out failure);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OutOfMemoryException)
        {
            host = null;
            failure = ex;
        }

        if (host is null)
        {
            import.Note($"The import window could not be opened, so the import was dropped: {failure?.Message}");
            import.CancelBeforeStart();
            import.Dispose();
            if (fromVault)
            {
                Completed?.Invoke(this, "The import window could not be opened, so the import was dropped.");
            }

            MantlePlaceApplication.SayFault(failure ?? new InvalidOperationException("The import window could not be opened."));
            return false;
        }

        _import = import;
        _fromVault = fromVault;
        _window = host;
        _hosts.Add(host);
        host.Ended += () => _hosts.Remove(host);
        import.Changed += () => Refresh(import);
        return true;
    }

    /// <summary>
    /// Runs a window's callback now, or — when it arrives while a slice is running — once the slice has
    /// ended.
    /// </summary>
    /// <remarks>
    /// A callback is posted to Revit's dispatcher and runs wherever Revit next pumps messages. Whether
    /// Revit pumps inside a commit is not known; if it does, a Cancel, a log line or a fault dialog
    /// could otherwise run in the middle of a slice, with its transaction open. Held, each runs after
    /// the slice, before the next one is raised, and the log says how many were held, which is how a
    /// Revit run shows whether it ever happens.
    /// </remarks>
    private void OutsideSlice(Action callback)
    {
        if (_inSlice)
        {
            _heldUntilSliceEnds.Add(callback);
            return;
        }

        callback();
    }

    /// <summary>Runs what arrived during the slice that has just ended, and says that it was held.</summary>
    private void RunHeld(ActiveImport import)
    {
        if (_heldUntilSliceEnds.Count == 0)
        {
            return;
        }

        Action[] held = [.. _heldUntilSliceEnds];
        _heldUntilSliceEnds.Clear();
        Guarded(import, () => import.Note($"{held.Length:N0} call(s) from the import window arrived while a slice was running, and were held until it ended."));
        foreach (Action callback in held)
        {
            Guarded(import, callback);
        }
    }

    /// <summary>
    /// Runs one held callback inside the same net as the slice it waited for, so a throw costs that
    /// callback alone: the rest still run, and the window's refresh, the import's end and the next
    /// raise still follow.
    /// </summary>
    /// <remarks>
    /// A catch-all for the slice's catch-all's reason (<see cref="Execute"/>): this runs where an
    /// exception would leave the import unable to advance or close.
    /// </remarks>
    private static void Guarded(ActiveImport import, Action callback)
    {
        try
        {
            callback();
        }
        catch (Exception ex)
        {
            try
            {
                import.Note($"A call from the import window failed after its slice, and the import went on: {ex.GetType().Name}: {ex.Message}");
            }
            catch (Exception)
            {
                // The log itself refusing is no reason to strand the import.
            }
        }
    }

    /// <summary>
    /// A project has closed. If it was the one an import is still choosing for, the checklist goes
    /// with it: nothing ran, and Import would have nothing to write into.
    /// </summary>
    /// <remarks>
    /// After the close rather than before it, because a curator can still cancel a closing project at
    /// its save prompt. A running import needs nothing here: its next slice finds the project gone,
    /// stops, and says so in the window it leaves open.
    /// </remarks>
    internal void OnDocumentClosed()
    {
        if (_import is not { Staged: null } import || import.DocumentIsOpen)
        {
            return;
        }

        _window?.CloseNow();
        import.CancelBeforeStart();
        End(import);
    }

    /// <summary>
    /// Revit is shutting down: the window goes with it, and an import still open is dropped where it
    /// stands, with a line in its log.
    /// </summary>
    /// <remarks>
    /// An unowned window is not closed by Revit's main window going, and its thread would keep it on
    /// screen until the process ended. At shutdown rather than at <c>ApplicationClosing</c>, which a
    /// save prompt's Cancel can still undo, leaving Revit open with its import window closed.
    /// </remarks>
    internal void Shutdown()
    {
        foreach (ImportWindowThread host in _hosts)
        {
            host.CloseNow();
        }

        _hosts.Clear();
        _window = null;

        if (_import is { } import)
        {
            import.Note("Revit closed while this import was open, so it stopped where it stood.");
            import.Dispose();
            _import = null;
        }

        _exportId = null;
        _exportOrderId = null;
        _exportLevel = null;
        if (_refetch is { } refetch)
        {
            DropRefetch(refetch);
        }

        lock (_gate)
        {
            _queuedExport = null;
        }
    }

    /// <summary>Posts the window the run as it stands. Never waits on the window's thread.</summary>
    private void Refresh(ActiveImport import)
    {
        if (import.Staged is { } staged)
        {
            _window?.Refresh(ImportRunView.Of(staged));
        }
    }

    /// <summary>The curator pressed Import: plan what they ticked, show its steps, run the first slice.</summary>
    /// <remarks>
    /// Posted here from the window's click, so it runs on Revit's thread but outside any API context —
    /// so this plans and touches no document. The document work starts at the raise.
    /// </remarks>
    private void BeginChosen(ActiveImport import, ImportLayerChoice choice)
    {
        if (!ReferenceEquals(import, _import) || import.Staged is not null)
        {
            return;
        }

        // Remembered the moment Import is pressed, and only then: a window closed unpressed leaves the
        // last choice as it was. A file that cannot be written costs the memory, never the import.
        if (!ActiveImport.ChoiceStore.Write(RememberedChoice.Of(import.Checklist, choice)))
        {
            import.Note("The choice made here could not be saved, so the next import window opens on the last one that was.");
        }

        try
        {
            import.Begin(choice);
        }
        catch (Exception ex)
        {
            // Out of a WPF click, an exception is Revit's internal-error dialog; ActiveImport.Abandon
            // is the contract that keeps a run from being left unable to close.
            import.Abandon(ex);
            End(import);
            return;
        }

        _window?.ShowRun(ImportRunView.Of(import.Staged!));
        if (_exportId is { } exportId)
        {
            MantlePlaceApplication.Connected?.ImportBegan(exportId);
        }

        RaiseNextSlice();
    }

    /// <summary>The curator pressed Cancel, or closed the window, while the import ran.</summary>
    /// <remarks>
    /// Posted here from the window's thread, so it lands when Revit's thread is next free — after the
    /// commit it may be sitting in, or held until that slice ends should Revit pump inside it
    /// (<see cref="OutsideSlice"/>) — and ahead of the next slice, which it stops.
    /// </remarks>
    private void CancelRun(ActiveImport import)
    {
        if (!ReferenceEquals(import, _import) || import.Staged is not { } staged)
        {
            return;
        }

        staged.RequestCancel();
        Refresh(import);
    }

    /// <summary>The window went before Import was pressed: nothing ran, and the import is dropped.</summary>
    private void DismissBeforeStart(ActiveImport import)
    {
        // Only this window's import, and only one that never began: the window treats a close after
        // Import as a cancel, so a dismissal that reaches a begun import is a stale one.
        if (_refetch is { } refetch && ReferenceEquals(refetch.Import, import))
        {
            // Closed while its chosen level downloaded: the download stops, and the export ends as
            // dismissed before an import started.
            DropRefetch(refetch);
            if (MantlePlaceApplication.Connected is { } connected)
            {
                connected.ImportEnded(refetch.ExportId, ImportEnding.DismissedBeforeStart, string.Empty);
                connected.TurnFree();
            }

            return;
        }

        if (!ReferenceEquals(import, _import) || import.Staged is not null)
        {
            return;
        }

        import.CancelBeforeStart();
        End(import);
    }

    /// <summary>
    /// Revit's thread. The curator chose another level of a sent 3.0 order before Import. The import
    /// is dropped, since its archive holds the zip the download replaces; the window stays, saying the
    /// level is downloading; and the export reopens on the new level once it is in the slot, or on the
    /// level it held when the download fails (<see cref="Refetched"/>).
    /// </summary>
    private void ChooseLevel(ActiveImport import, FidelityLevel level)
    {
        if (!ReferenceEquals(import, _import) || import.Staged is not null || _refetch is not null)
        {
            return;
        }

        if (_exportId is not { } exportId || _exportOrderId is not { } orderId || _exportLevel is not { } held
            || _levels is not { } source)
        {
            _window?.LevelKept(_exportLevel ?? level, "The level cannot be changed in this window.");
            return;
        }

        import.Note($"The {DownloadLevels.Word(level)} level was chosen in the window, so it is downloaded before Import.");
        import.CancelBeforeStart();
        import.Dispose();

        LevelRefetch refetch = new(exportId, orderId, import.ZipPath, level, held, import, _window, new CancellationTokenSource());
        lock (_gate)
        {
            _refetch = refetch;
        }

        _import = null;
        _window = null;
        _exportId = null;
        _exportOrderId = null;
        _exportLevel = null;

        _ = Task.Run(async () =>
        {
            LevelFetch fetched = await source.FetchLevelAsync(orderId, level, new Progress<string>(_ => { }), refetch.Stop.Token).ConfigureAwait(false);
            MantlePlaceApplication.OnUiThread(() => Refetched(refetch, fetched));
        });
    }

    /// <summary>Revit's thread: the chosen level's download ended. The export reopens on whichever level the slot now holds.</summary>
    private void Refetched(LevelRefetch refetch, LevelFetch fetched)
    {
        if (!ReferenceEquals(_refetch, refetch))
        {
            // Closed, cancelled from the web, or Revit shut down while it downloaded.
            return;
        }

        FidelityLevel now = fetched.Ready ? refetch.Chosen : refetch.Held;
        string note = fetched.Ready
            ? WindowLabels.LevelDownloaded(refetch.Chosen)
            : WindowLabels.LevelNotDownloaded(refetch.Chosen, refetch.Held, fetched.Message);
        refetch.Window?.CloseNow();

        lock (_gate)
        {
            _refetch = null;
            _queuedExport = new QueuedExport(
                refetch.ExportId,
                refetch.OrderId,
                fetched.Ready ? fetched.ZipPath : refetch.ZipPath,
                now,
                note);
        }

        ExternalEventRequest request = _event?.Raise() ?? ExternalEventRequest.Denied;
        if (request is ExternalEventRequest.Accepted or ExternalEventRequest.Pending)
        {
            return;
        }

        lock (_gate)
        {
            _queuedExport = null;
        }

        if (MantlePlaceApplication.Connected is { } connected)
        {
            connected.ImportEnded(refetch.ExportId, ImportEnding.Failed, $"Revit did not accept the request to reopen the import window ({request}).");
            connected.TurnFree();
        }
    }

    /// <summary>Stops a level download and forgets it; nothing reopens.</summary>
    private void DropRefetch(LevelRefetch refetch)
    {
        lock (_gate)
        {
            _refetch = null;
        }

        refetch.Stop.Cancel();
    }

    /// <summary>
    /// Raises the event for the next slice, and ends the import if Revit will not take the request.
    /// </summary>
    /// <remarks>
    /// ⛔ A refused raise is otherwise silent: no slice ever runs again, the window sits on its last
    /// state with Cancel lit, and the handler believes an import is running until Revit restarts.
    /// </remarks>
    private void RaiseNextSlice()
    {
        if (_import is not { } import || _event is null)
        {
            return;
        }

        ExternalEventRequest request = _event.Raise();
        if (request == ExternalEventRequest.Accepted)
        {
            return;
        }

        import.Abandon(new InvalidOperationException(
            $"Revit did not accept the request to run the next step ({request}), so the import stopped where it stood."));
        End(import);
    }

    /// <summary>
    /// What the "Did this import come in right?" line opens for Yes and No, or <c>null</c> when this
    /// import asks nothing (<c>HPS-56</c>).
    /// </summary>
    /// <remarks>
    /// Only with a window to show it in, and the bundle is remembered as asked about the moment it is
    /// decided here, so a line the curator ignores is not asked again. The decision is
    /// <see cref="ImportFeedbackPrompt"/>'s; the store only keeps it.
    /// </remarks>
    private ImportFeedbackLinks? FeedbackAsk(ActiveImport import)
    {
        if (_window is null)
        {
            return null;
        }

        ImportEnding ending = ImportEndings.Of(import.Failed, import.Staged is not null, import.Staged?.WasCancelled ?? false);
        return ActiveImport.AskedStore.Claim(ending, import.OrderId) is { } order
            ? FeedbackLauncher.ImportLinks(order)
            : null;
    }

    private void End(ActiveImport import)
    {
        _window?.ShowFinished(
            import.Staged is { } staged ? ImportRunView.Of(staged) : null,
            import.Staged?.Outcome,
            import.Summary ?? string.Empty,
            FeedbackAsk(import));
        import.Dispose();

        bool fromVault = _fromVault;
        string? exportId = _exportId;
        _import = null;
        _window = null;
        _exportId = null;
        _exportOrderId = null;
        _exportLevel = null;

        if (MantlePlaceApplication.Connected is { } connected)
        {
            if (exportId is not null)
            {
                ImportEnding ending = ImportEndings.Of(import.Failed, import.Staged is not null, import.Staged?.WasCancelled ?? false);
                connected.ImportEnded(exportId, ending, $"{import.Heading} {import.Summary}".Trim());
            }

            // Whichever import it was, an export waiting for the window may have it now.
            connected.TurnFree();
        }

        if (!fromVault)
        {
            return;
        }

        // A window dismissed on its checklist is gone, and nothing ran for a report to describe.
        Completed?.Invoke(
            this,
            import.Staged is null && !import.Failed
                ? import.Heading
                : $"{import.Heading} The report is in the {WindowLabels.ImportHeading} window, and in the log beside the bundle.");
    }

    /// <summary>
    /// The import window's calls for one import, each run outside a slice. Bound to its import, so a
    /// call arriving late from an earlier import's window acts on nothing it does not own.
    /// </summary>
    private sealed class WindowCalls(BundleImportEventHandler handler, ActiveImport import) : IImportWindowHost
    {
        public void Begin(ImportLayerChoice choice) => handler.OutsideSlice(() => handler.BeginChosen(import, choice));

        public void CancelRun() => handler.OutsideSlice(() => handler.CancelRun(import));

        public void Dismiss() => handler.OutsideSlice(() => handler.DismissBeforeStart(import));

        public void Note(string line) => handler.OutsideSlice(() => import.Note(line));

        public void Fault(Exception fault) => handler.OutsideSlice(() => MantlePlaceApplication.SayFault(fault));

        public void ChooseLevel(FidelityLevel level) => handler.OutsideSlice(() => handler.ChooseLevel(import, level));
    }
}
