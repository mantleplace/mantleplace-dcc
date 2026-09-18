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
/// run a step, commit one chunk — refreshes the import window, and raises the event again. Revit
/// repaints and processes input between the two.
/// </para>
/// <para>
/// ⛔ <b>The next raise is posted at <see cref="DispatcherPriority.Background"/>, not made inline.</b>
/// Background sits below input and render, so the window draws the slice that just finished and a
/// click on Cancel is handled before the next slice can start. Raising inline would leave both to
/// whichever Revit happened to service first.
/// </para>
/// <para>
/// Two ways in, one driver. The vault window is on its own and queues a zip path (<see cref="QueueImport"/>),
/// opened here on Revit's thread. The ribbon command is already on Revit's thread, opens its own
/// import and hands it over (<see cref="Start"/>). The unattended path never comes here: a modeless
/// window in a journal playback never closes, so it runs its import to the end in place.
/// </para>
/// </remarks>
internal sealed class BundleImportEventHandler : IExternalEventHandler
{
    private readonly object _gate = new();
    private string? _zipPath;
    private ExternalEvent? _event;
    private ActiveImport? _import;
    private ImportWindow? _window;

    /// <summary>Whether the running import came from the vault window, which is who hears about it.</summary>
    private bool _fromVault;

    /// <summary>
    /// Raised on Revit's thread with one line for the vault window's status: why an import it asked
    /// for did not start, or that it finished and where its report is. An import started from the
    /// ribbon is not the vault window's to report.
    /// </summary>
    internal event EventHandler<string>? Completed;

    /// <summary>Whether an import is running. One at a time: two would interleave in one document.</summary>
    internal bool IsImporting => _import is not null;

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

    /// <summary>Takes over an import the ribbon command opened, shows its window and starts it.</summary>
    internal void Start(ActiveImport import, IntPtr revitWindow)
    {
        ArgumentNullException.ThrowIfNull(import);
        if (_import is not null)
        {
            throw new InvalidOperationException("An import is already running.");
        }

        Begin(import, revitWindow, fromVault: false);
        RaiseNextSlice();
    }

    /// <summary>Brings the running import's window forward, for a second click on Import.</summary>
    internal void ShowRunning() => _window?.Activate();

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

        if (_import is not { } import)
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

        _window?.Refresh();

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
            _window?.Activate();
            Completed?.Invoke(this, "An import is already running — wait for it, or cancel it in its window.");
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

        Begin(import, application.MainWindowHandle, fromVault: true);
    }

    private void Begin(ActiveImport import, IntPtr revitWindow, bool fromVault)
    {
        _import = import;
        _fromVault = fromVault;
        _window = new ImportWindow(Path.GetFileName(import.ZipPath), import.Staged, revitWindow);
        _window.Show();
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
        _window?.ShowFinished(import.Summary ?? string.Empty);
        import.Dispose();

        bool fromVault = _fromVault;
        _import = null;
        _window = null;

        if (fromVault)
        {
            Completed?.Invoke(this, $"{import.Heading} The report is in the {WindowLabels.ImportHeading} window, and in the log beside the bundle.");
        }
    }
}
