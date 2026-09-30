using System.Windows.Threading;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// Runs the <see cref="ImportWindow"/> on a thread of its own, and is the only way across between
/// that thread and Revit's.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Why a thread.</b> A bundle import spends most of its time inside single commits — road
/// surfaces for fifteen to twenty minutes, the drape for up to forty-five in Revit 2027 — and a commit
/// cannot yield. A window on Revit's thread repaints only between them, so it sat for the whole of each
/// naming a step that had finished, and a UI Automation read of it went unanswered for up to
/// 2,759 s. On its own STA thread with its own <see cref="Dispatcher"/>, the window paints, ticks its
/// clock and answers reads whatever Revit's thread is doing. WPF supports a window per thread; what it
/// takes is that nothing on this thread ever waits on Revit's.
/// </para>
/// <para>
/// So <b>everything across is posted, never invoked</b>. Revit's thread posts views of the run
/// (<see cref="ImportRunView"/>), and the curator's Import, Cancel and dismissal are posted back to
/// Revit's dispatcher, where they run exactly as the click handlers they used to be ran: on Revit's
/// thread, outside any API context. A cancel pressed during a commit therefore lands when the commit
/// returns, ahead of the next slice, which is posted below it at background priority
/// (<see cref="BundleImportEventHandler"/>). This thread makes no Revit API call at all, which is
/// what makes it allowed.
/// </para>
/// <para>
/// ⛔ <b>An exception here would end Revit.</b> An exception that leaves a thread's start method
/// terminates the process, and this thread's dispatcher has no host behind it to catch one. Its
/// dispatcher is this add-in's own, with nothing of Revit's running on it, so it marks every fault
/// handled and posts it to Revit's thread to be said in the same dialog as a fault there
/// (<see cref="MantlePlaceApplication.SayFault"/>). A window that could not be built also drops its
/// import, which would otherwise wait for an Import press forever.
/// </para>
/// </remarks>
internal sealed class ImportWindowHost
{
    private readonly Dispatcher _windowThread;

    /// <summary>The window, once built. Read and written on its own thread only.</summary>
    private ImportWindow? _window;

    private ImportWindowHost(Dispatcher windowThread) => _windowThread = windowThread;

    /// <summary>
    /// Starts the window's thread and shows the window on its checklist. Called on Revit's thread,
    /// whose dispatcher the callbacks are posted to.
    /// </summary>
    /// <remarks>
    /// Revit's thread waits only for the new thread's dispatcher to exist, never for the window: the
    /// window is built by an operation on that dispatcher once Revit's thread has moved on, so showing
    /// it — which activates it — never meets a Revit thread that is waiting on it.
    /// </remarks>
    internal static ImportWindowHost Open(
        string bundleName,
        string? deliveryLine,
        string? unitsDisagreement,
        ImportChecklist checklist,
        IntPtr revitWindow,
        Action<ImportLayerChoice> begin,
        Action cancelRun,
        Action dismiss)
    {
        Dispatcher revit = Dispatcher.CurrentDispatcher;
        Dispatcher? windowThread = null;

        using (ManualResetEventSlim started = new())
        {
            Thread thread = new(() =>
            {
                windowThread = Dispatcher.CurrentDispatcher;
                windowThread.UnhandledException += (_, e) =>
                {
                    e.Handled = true;
                    ReportFault(revit, e.Exception);
                };

                started.Set();
                try
                {
                    Dispatcher.Run();
                }
                catch (Exception ex)
                {
                    // Not reached while the handler above marks faults handled; here so that nothing
                    // can leave this method, which is the one place an exception ends Revit.
                    ReportFault(revit, ex);
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

        ImportWindowHost host = new(windowThread!);
        host.Post(() =>
        {
            try
            {
                ImportWindow window = new(
                    bundleName,
                    deliveryLine,
                    unitsDisagreement,
                    checklist,
                    revitWindow,
                    choice => ToRevit(revit, () => begin(choice)),
                    () => ToRevit(revit, cancelRun),
                    () => ToRevit(revit, dismiss));

                // The thread lives exactly as long as its window.
                window.Closed += (_, _) => Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                host._window = window;
                window.Show();
            }
            catch (Exception ex)
            {
                // No window, so no Import to press and no close box: the import is dropped, the
                // curator is told, and the thread ends.
                ToRevit(revit, dismiss);
                ReportFault(revit, ex);
                Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            }
        });

        return host;
    }

    /// <summary>Swaps the checklist for the chosen steps. Called on Revit's thread.</summary>
    internal void ShowRun(ImportRunView view) => Post(() => _window?.ShowRun(view));

    /// <summary>Hands the window the run as it stands now. Called on Revit's thread, as often as it likes.</summary>
    internal void Refresh(ImportRunView view) => Post(() => _window?.Refresh(view));

    /// <summary>Shows the closing report. Called on Revit's thread.</summary>
    internal void ShowFinished(ImportRunView? view, string? outcome, string report)
        => Post(() => _window?.ShowFinished(view, outcome, report));

    /// <summary>Brings the window forward. Called on Revit's thread.</summary>
    internal void BringForward() => Post(() => _window?.BringForward());

    /// <summary>
    /// Queues <paramref name="action"/> on the window's thread. Never throws and never waits: a
    /// window that has closed, taking its thread with it, has nothing left to show.
    /// </summary>
    private void Post(Action action)
    {
        if (_windowThread.HasShutdownStarted)
        {
            return;
        }

        try
        {
            _windowThread.BeginInvoke(DispatcherPriority.Normal, action);
        }
        catch (InvalidOperationException)
        {
            // Shut down between the check and the post.
        }
    }

    /// <summary>Queues <paramref name="action"/> on Revit's thread, where the import lives.</summary>
    private static void ToRevit(Dispatcher revit, Action action)
        => revit.BeginInvoke(DispatcherPriority.Normal, action);

    /// <summary>Has a fault on the window's thread said on Revit's, where a dialog may be shown.</summary>
    private static void ReportFault(Dispatcher revit, Exception fault)
        => ToRevit(revit, () => MantlePlaceApplication.SayFault(fault));
}
