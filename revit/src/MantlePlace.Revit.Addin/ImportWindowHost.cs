using System.Windows.Threading;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// Runs the <see cref="ImportWindow"/> on a thread of its own, and is the only way across between
/// that thread and Revit's.
/// </summary>
/// <remarks>
/// <para>
/// Why a thread, and why unowned, is <c>revit/CLAUDE.md</c>'s. What it takes is that nothing on this
/// thread ever waits on Revit's, so <b>everything across is posted, never invoked</b>: Revit's thread
/// posts views of the run (<see cref="ImportRunView"/>); the curator's Import, Cancel and dismissal,
/// and a line for the log, are posted back to Revit's dispatcher, where they run as the click handlers
/// they used to be — on Revit's thread, outside any API context. This thread makes no Revit API call.
/// </para>
/// <para>
/// ⛔ <b>An exception that leaves this thread ends Revit.</b> So the whole thread body is inside the
/// net, its dispatcher marks every fault handled, and a fault is said on Revit's thread in the add-in's
/// fault dialog (<see cref="MantlePlaceApplication.SayFault"/>) — after the window has stopped
/// floating, so the dialog does not open under it. A thread that cannot start, or a window that cannot
/// be built, drops its import: nothing else could ever close it.
/// </para>
/// </remarks>
internal sealed class ImportWindowHost
{
    private readonly Dispatcher _revit;
    private Dispatcher? _windowThread;

    /// <summary>The window, once built. Read and written on its own thread only.</summary>
    private ImportWindow? _window;

    private ImportWindowHost(Dispatcher revit) => _revit = revit;

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
    internal static ImportWindowHost? Open(
        string bundleName,
        string? deliveryLine,
        string? unitsDisagreement,
        ImportChecklist checklist,
        IntPtr revitWindow,
        Action<ImportLayerChoice> begin,
        Action cancelRun,
        Action dismiss,
        Action<string> note,
        out Exception? failure)
    {
        ImportWindowHost host = new(Dispatcher.CurrentDispatcher);
        Exception? startFailure = null;

        using (ManualResetEventSlim started = new())
        {
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

                    own.BeginInvoke(() => host.Build(bundleName, deliveryLine, unitsDisagreement, checklist, revitWindow, begin, cancelRun, dismiss, note));
                    host._windowThread = own;
                    started.Set();
                    Dispatcher.Run();
                }
                catch (Exception ex)
                {
                    // Before the dispatcher ran, Revit's thread is still waiting below and hears it
                    // there. After, the dispatcher's own handler has marked every fault handled, so
                    // this is the net under the net, and all it must do is keep the exception in here.
                    if (host._windowThread is null)
                    {
                        startFailure = ex;
                    }
                    else
                    {
                        host.ReportFault(ex);
                    }
                }
                finally
                {
                    started.Set();
                }
            })
            {
                IsBackground = true,
                Name = "Mantle Place import window",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            started.Wait();
        }

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

    /// <summary>Builds and shows the window: the window thread's first operation.</summary>
    private void Build(
        string bundleName,
        string? deliveryLine,
        string? unitsDisagreement,
        ImportChecklist checklist,
        IntPtr revitWindow,
        Action<ImportLayerChoice> begin,
        Action cancelRun,
        Action dismiss,
        Action<string> note)
    {
        try
        {
            ImportWindow window = new(
                bundleName,
                deliveryLine,
                unitsDisagreement,
                checklist,
                revitWindow,
                choice => ToRevit(() => begin(choice)),
                () => ToRevit(cancelRun),
                () => ToRevit(dismiss),
                line => ToRevit(() => note(line)));

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
            ToRevit(dismiss);
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
    /// A fault on the window's thread: the window stops floating here and now, then the fault is said in
    /// a dialog on Revit's thread.
    /// </summary>
    /// <remarks>
    /// It is the last net on this thread, so it catches everything it does itself: a throw from here
    /// would leave the thread, and that ends Revit.
    /// </remarks>
    private void ReportFault(Exception fault)
    {
        try
        {
            _window?.Lower();
        }
        catch (Exception)
        {
            // A window too broken to restack is no reason not to say what broke it.
        }

        try
        {
            ToRevit(() => MantlePlaceApplication.SayFault(fault));
        }
        catch (Exception)
        {
            // A Revit already shutting its dispatcher down has nobody left to tell.
        }
    }
}
