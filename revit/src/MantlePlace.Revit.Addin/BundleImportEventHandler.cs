using System.IO;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MantlePlace.Revit.Core;

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
/// The window is on a thread of its own (<see cref="ImportWindowHost"/>), so it does not wait for a
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
/// Two ways in, one driver. The vault window is on its own and queues a zip path (<see cref="QueueImport"/>),
/// opened here on Revit's thread. The ribbon command is already on Revit's thread, opens its own
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
    private ImportWindowHost? _window;

    /// <summary>Whether the running import came from the vault window, which is who hears about it.</summary>
    private bool _fromVault;

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
    internal bool IsImporting => _import is not null;

    /// <summary>Why a second import cannot open now, in the words that fit which state the first is in.</summary>
    internal string BusyReason => _import?.Staged is null
        ? "An import is waiting in its window — press Import there, or close it."
        : "An import is already running — wait for it, or cancel it in its window.";

    /// <summary>The event this handler re-raises between slices. Set once, at startup.</summary>
    internal void Attach(ExternalEvent externalEvent) => _event = externalEvent;

    /// <summary>Queues a zip from the vault window. The last one queued before the event fires is the one that runs.</summary>
    internal void QueueImport(string zipPath)
    {
        lock (_gate)
        {
            _zipPath = zipPath;
        }
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

    public void Execute(UIApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        string? zipPath;
        lock (_gate)
        {
            zipPath = _zipPath;
            _zipPath = null;
        }

        if (zipPath is not null)
        {
            OpenQueued(application, zipPath);
        }

        // An import still on its checklist has nothing to advance: it was either opened by this very
        // raise, or it is waiting on the curator's Import.
        if (_import is not { Staged: not null } import)
        {
            return;
        }

        bool more;
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

        Refresh(import);

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
        if (_import is not null)
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

    private void Show(ActiveImport import, IntPtr revitWindow, bool fromVault)
    {
        _import = import;
        _fromVault = fromVault;
        import.Changed += () => Refresh(import);
        _window = ImportWindowHost.Open(
            Path.GetFileName(import.ZipPath),
            import.DeliveryLine,
            import.UnitsDisagreement,
            import.Checklist,
            revitWindow,
            BeginChosen,
            CancelRun,
            DismissBeforeStart);
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
    private void BeginChosen(ImportLayerChoice choice)
    {
        if (_import is not { } import)
        {
            return;
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
        RaiseNextSlice();
    }

    /// <summary>The curator pressed Cancel, or closed the window, while the import ran.</summary>
    /// <remarks>
    /// Posted here from the window's thread, so it lands when Revit's thread is next free — after the
    /// commit it may be sitting in — and ahead of the next slice, which it stops.
    /// </remarks>
    private void CancelRun()
    {
        if (_import is not { Staged: { } staged } import)
        {
            return;
        }

        staged.RequestCancel();
        Refresh(import);
    }

    /// <summary>The window went before Import was pressed: nothing ran, and the import is dropped.</summary>
    private void DismissBeforeStart()
    {
        // Only an import that never began: the window treats a close after Import as a cancel, so a
        // dismissal that reaches a begun import is a stale one and has nothing to drop.
        if (_import is not { Staged: null } import)
        {
            return;
        }

        import.CancelBeforeStart();
        End(import);
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

    private void End(ActiveImport import)
    {
        _window?.ShowFinished(
            import.Staged is { } staged ? ImportRunView.Of(staged) : null,
            import.Staged?.Outcome,
            import.Summary ?? string.Empty);
        import.Dispose();

        bool fromVault = _fromVault;
        _import = null;
        _window = null;

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
}
