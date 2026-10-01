using System.Windows.Threading;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>Everything an import window is opened with: what it shows, and what it calls back.</summary>
/// <param name="BundleName">The bundle's file name, its first line.</param>
/// <param name="DeliveryLine">The order's unit system, linear unit and delivery CRS; <c>null</c> shows no line.</param>
/// <param name="UnitsDisagreement">Shown only when set: the project displays the other unit system.</param>
/// <param name="Checklist">What the bundle carries, for the curator to choose from.</param>
/// <param name="RevitWindow">Revit's main window: where the window opens, and what it floats over. Not its owner.</param>
/// <param name="Begin">Called once, with the curator's choice, when Import is pressed.</param>
/// <param name="CancelRun">Called once, when the curator asks a running import to stop.</param>
/// <param name="Dismiss">Called once when the window goes before Import was pressed, or cannot go on.</param>
/// <param name="Note">Writes a line to the import's log, for what went wrong without stopping anything.</param>
/// <param name="Fault">Says a fault of the window's in the add-in's fault dialog.</param>
internal sealed record ImportWindowRequest(
    string BundleName,
    string? DeliveryLine,
    string? UnitsDisagreement,
    ImportChecklist Checklist,
    IntPtr RevitWindow,
    Action<ImportLayerChoice> Begin,
    Action CancelRun,
    Action Dismiss,
    Action<string> Note,
    Action<Exception> Fault);

/// <summary>
/// Runs the <see cref="ImportWindow"/> on a thread of its own, and is the only way across between
/// that thread and Revit's.
/// </summary>
/// <remarks>
/// <para>
/// Why a thread, and why unowned, is <c>revit/CLAUDE.md</c>'s. What it takes is that nothing on this
/// thread ever waits on Revit's, so <b>everything across is posted, never invoked</b>: Revit's thread
/// posts views of the run (<see cref="ImportRunView"/>); every callback of the request is posted back
/// to Revit's dispatcher, where it runs as the click handler it used to be — on Revit's thread, outside
/// any API context. This thread makes no Revit API call.
/// </para>
/// <para>
/// ⛔ <b>An exception that leaves this thread ends Revit.</b> So the whole thread body is inside the
/// net, and its dispatcher marks every fault handled. The window stops floating first, so a dialog
/// does not open under it; the first fault is then said in the add-in's fault dialog and every later
/// one goes to the log, since a fault that repeats on every post would otherwise queue a dialog for
/// each. A thread that cannot start, a window that cannot be built, and a dispatcher that dies all
/// drop an import still on its checklist: nothing else could ever close it.
/// </para>
/// </remarks>
internal sealed class ImportWindowHost
{
    private readonly Dispatcher _revit;

    /// <summary>The request, its callbacks already marshalled onto Revit's thread.</summary>
    private readonly ImportWindowRequest _toRevit;

    private Dispatcher? _windowThread;

    /// <summary>The window, once built. Read and written on its own thread only.</summary>
    private ImportWindow? _window;

    /// <summary>Whether a fault has been said in a dialog. On the window's thread only.</summary>
    private bool _faultSaid;

    private ImportWindowHost(Dispatcher revit, ImportWindowRequest request)
    {
        _revit = revit;
        _toRevit = request with
        {
            Begin = choice => ToRevit(() => request.Begin(choice)),
            CancelRun = () => ToRevit(request.CancelRun),
            Dismiss = () => ToRevit(request.Dismiss),
            Note = line => ToRevit(() => request.Note(line)),
            Fault = fault => ToRevit(() => request.Fault(fault)),
        };
    }

    /// <summary>
    /// Raised on Revit's thread once the window's thread has ended — its window closed, or never
    /// built: the host has nothing left to show, and can be let go.
    /// </summary>
    internal event Action? Ended;

    /// <summary>
    /// Starts the window's thread and shows the window on its checklist, or says why it could not.
    /// Called on Revit's thread, whose dispatcher the callbacks are posted to.
    /// </summary>
    /// <remarks>
    /// Revit's thread waits only for the new thread's dispatcher to exist, or to fail to, never for the
    /// window: the window is built by an operation on that dispatcher once Revit's thread has moved on,
    /// so showing it — which may activate it — never meets a Revit thread that is waiting on it.
    /// </remarks>
    /// <returns>The host, or <c>null</c> with <paramref name="failure"/> set when the thread did not start.</returns>
    internal static ImportWindowHost? Open(ImportWindowRequest request, out Exception? failure)
    {
        ArgumentNullException.ThrowIfNull(request);

        ImportWindowHost host = new(Dispatcher.CurrentDispatcher, request);
        Exception? startFailure = null;

        // Set exactly once, on the start path, before the dispatcher runs; disposed below only once
        // Wait has returned, after which the thread never touches it again.
        ManualResetEventSlim started = new();
        Thread thread = new(() =>
        {
            try
            {
                Dispatcher own = Dispatcher.CurrentDispatcher;
                own.UnhandledException += (_, e) =>
                {
                    e.Handled = true;
                    host.ReportFault(e.Exception);
                };

                own.BeginInvoke(host.Build);
                host._windowThread = own;
            }
            catch (Exception ex)
            {
                startFailure = ex;
            }
            finally
            {
                started.Set();
            }

            if (host._windowThread is null)
            {
                return;
            }

            try
            {
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                // Not reached while the handler above marks faults handled; here because an exception
                // that leaves this method ends Revit. The window is gone with its dispatcher, so an
                // import still on its checklist is dropped.
                host.ReportFault(ex);
                Quietly(host._toRevit.Dismiss);
            }

            Quietly(() => host.ToRevit(() => host.Ended?.Invoke()));
        })
        {
            IsBackground = true,
            Name = "Mantle Place import window",
        };
        thread.SetApartmentState(ApartmentState.STA);

        try
        {
            thread.Start();
        }
        catch (Exception ex) when (ex is ThreadStateException or OutOfMemoryException)
        {
            started.Dispose();
            failure = ex;
            return null;
        }

        started.Wait();
        started.Dispose();

        failure = startFailure;
        return host._windowThread is null ? null : host;
    }

    /// <summary>Swaps the checklist for the chosen steps. Called on Revit's thread.</summary>
    internal void ShowRun(ImportRunView view) => Post(() => _window?.ShowRun(view));

    /// <summary>Hands the window the run as it stands now. Called on Revit's thread, as often as it likes.</summary>
    internal void Refresh(ImportRunView view) => Post(() => _window?.Refresh(view));

    /// <summary>Shows the closing report. Called on Revit's thread.</summary>
    internal void ShowFinished(ImportRunView? view, string? outcome, string report)
        => Post(() => _window?.ShowFinished(view, outcome, report));

    /// <summary>Brings the window forward, unless a Revit modal is up. Called on Revit's thread.</summary>
    internal void BringForward() => Post(() => _window?.BringForward());

    /// <summary>Stops the window floating, before Revit shows a dialog. Called on Revit's thread.</summary>
    internal void Lower() => Post(() => _window?.Lower());

    /// <summary>Closes the window whatever it shows, asking nothing. Called on Revit's thread.</summary>
    internal void CloseNow() => Post(() => _window?.CloseNow());

    /// <summary>Runs <paramref name="action"/> where nothing may be thrown: this thread's last nets.</summary>
    private static void Quietly(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // A Revit already shutting its dispatcher down, or a window too broken to restack: neither is
            // worth ending Revit over.
        }
    }

    /// <summary>Builds and shows the window: the window thread's first operation.</summary>
    private void Build()
    {
        try
        {
            ImportWindow window = new(_toRevit);

            // The thread lives exactly as long as its window.
            window.Closed += (_, _) => Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            _window = window;
            window.Show();
        }
        catch (Exception ex)
        {
            // No window, so no Import to press and no close box: the import is dropped, the curator is
            // told, and the thread ends.
            _window = null;
            Quietly(_toRevit.Dismiss);
            ReportFault(ex);
            Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// Queues <paramref name="action"/> on the window's thread. Never throws and never waits: a window
    /// that has closed, taking its thread with it, has nothing left to show.
    /// </summary>
    private void Post(Action action)
    {
        if (_windowThread is not { HasShutdownStarted: false } windowThread)
        {
            return;
        }

        try
        {
            windowThread.BeginInvoke(DispatcherPriority.Normal, action);
        }
        catch (InvalidOperationException)
        {
            // Shut down between the check and the post.
        }
    }

    /// <summary>Queues <paramref name="action"/> on Revit's thread, where the import lives.</summary>
    private void ToRevit(Action action) => _revit.BeginInvoke(DispatcherPriority.Normal, action);

    /// <summary>
    /// A fault on the window's thread: the window stops floating here and now, then the first fault is
    /// said in a dialog and every later one is written to the log.
    /// </summary>
    /// <remarks>It is the last net on this thread, so nothing it does may throw.</remarks>
    private void ReportFault(Exception fault)
    {
        Quietly(() => _window?.Lower());

        if (_faultSaid)
        {
            Quietly(() => _toRevit.Note(
                $"The import window hit another fault, after the one already shown: {fault.GetType().Name}: {fault.Message}"));
            return;
        }

        _faultSaid = true;
        Quietly(() => _toRevit.Fault(fault));
    }
}
