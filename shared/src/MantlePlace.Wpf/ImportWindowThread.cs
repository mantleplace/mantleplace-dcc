using System.Windows.Threading;
using MantlePlace.Core;

namespace MantlePlace.Wpf;

/// <summary>Everything an import window is opened with: what it shows, and who it tells.</summary>
/// <param name="BundleName">The bundle's file name, its first line.</param>
/// <param name="DeliveryLine">The order's unit system, linear unit and delivery CRS; <c>null</c> shows no line.</param>
/// <param name="UnitsDisagreement">Shown only when set: the project displays the other unit system.</param>
/// <param name="Checklist">
/// What the bundle carries, for the curator to choose from; <c>null</c> for a host with no per-layer
/// choice, whose window opens on its steps with no Import to press, and which begins its run itself.
/// </param>
/// <param name="HostWindow">The host's main window: where the window opens, and what it floats over. Not its owner.</param>
/// <param name="Host">The host's side of the import, told of Import, Cancel, a dismissal, a log line and a fault.</param>
public sealed record ImportWindowRequest(
    string BundleName,
    string? DeliveryLine,
    string? UnitsDisagreement,
    ImportChecklist? Checklist,
    IntPtr HostWindow,
    IImportWindowHost Host)
{
    /// <summary>
    /// What the host measured each step at, said beside that step's clock; <c>null</c>, or a step
    /// absent from it, says the clock alone. Data rather than a call, so the window's thread never
    /// calls into the host.
    /// </summary>
    public IReadOnlyDictionary<ImportStepKind, TimeSpan>? MeasuredCosts { get; init; }

    /// <summary>
    /// A received send's 3.0 order: the level control, on the level the bundle on disk is at
    /// (<see cref="LevelChoiceRow"/>). <c>null</c> for a 2.x order and for a window the curator opened
    /// from the vault, where the level was chosen before the fetch.
    /// </summary>
    public LevelControl? Levels { get; init; }

    /// <summary>A line under the level control: what the last level choice did. <c>null</c> for none.</summary>
    public string? LevelNote { get; init; }
}

/// <summary>
/// Runs the <see cref="ImportWindow"/> on a thread of its own, and is the only way across between
/// that thread and the host's.
/// </summary>
/// <remarks>
/// <para>
/// Why a thread, and why unowned, is Revit's record (<c>revit/docs/api-record.md</c>): a window that
/// shares the host's input queue freezes with the host through every long commit. What it takes is
/// that nothing on this thread ever waits on the host's, so <b>everything across is posted, never
/// invoked</b>: the host's thread posts views of the run (<see cref="ImportRunView"/>); every call to
/// <see cref="IImportWindowHost"/> is posted back to the dispatcher of the thread that opened the
/// window, where it runs as the click handler it used to be — on the host's thread, outside any API
/// context. This thread makes no host API call.
/// </para>
/// <para>
/// ⛔ <b>An exception that leaves this thread ends the host.</b> So the whole thread body is inside
/// the net, and its dispatcher marks every fault handled. The window stops floating first, so a dialog
/// does not open under it; the first fault is then said through <see cref="IImportWindowHost.Fault"/>
/// and every later one goes to the log, since a fault that repeats on every post would otherwise queue
/// a dialog for each. A thread that cannot start, a window that cannot be built, and a dispatcher that
/// dies all drop an import still on its checklist: nothing else could ever close it.
/// </para>
/// </remarks>
public sealed class ImportWindowThread
{
    private readonly Dispatcher _host;

    /// <summary>The request, its host's calls already marshalled onto the host's thread.</summary>
    private readonly ImportWindowRequest _toHost;

    private Dispatcher? _windowThread;

    /// <summary>The window, once built. Read and written on its own thread only.</summary>
    private ImportWindow? _window;

    /// <summary>Whether a fault has been said in a dialog. On the window's thread only.</summary>
    private bool _faultSaid;

    private ImportWindowThread(Dispatcher host, ImportWindowRequest request)
    {
        _host = host;
        _toHost = request with { Host = new Posted(this, request.Host) };
    }

    /// <summary>
    /// Raised on the host's thread once the window's thread has ended — its window closed, or never
    /// built: there is nothing left to show, and this can be let go.
    /// </summary>
    public event Action? Ended;

    /// <summary>
    /// Starts the window's thread and shows the window — on its checklist, or on its steps when the
    /// request has none — or says why it could not. Called on the host's thread, whose dispatcher the
    /// host's calls are posted to.
    /// </summary>
    /// <remarks>
    /// The host's thread waits only for the new thread's dispatcher to exist, or to fail to, never for
    /// the window: the window is built by an operation on that dispatcher once the host's thread has
    /// moved on, so showing it — which may activate it — never meets a host thread that is waiting on it.
    /// </remarks>
    /// <returns>The thread, or <c>null</c> with <paramref name="failure"/> set when it did not start.</returns>
    public static ImportWindowThread? Open(ImportWindowRequest request, out Exception? failure)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Host);

        ImportWindowThread host = new(Dispatcher.CurrentDispatcher, request);
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
                // that leaves this method ends the host. The window is gone with its dispatcher, so an
                // import still on its checklist is dropped.
                host.ReportFault(ex);
                Quietly(host._toHost.Host.Dismiss);
            }

            Quietly(() => host.ToHost(() => host.Ended?.Invoke()));
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

    /// <summary>Swaps the checklist for the chosen steps. Called on the host's thread.</summary>
    public void ShowRun(ImportRunView view) => Post(() => _window?.ShowRun(view));

    /// <summary>Hands the window the run as it stands now. Called on the host's thread, as often as it likes.</summary>
    public void Refresh(ImportRunView view) => Post(() => _window?.Refresh(view));

    /// <summary>Shows the closing report. Called on the host's thread.</summary>
    /// <param name="view">The run as it ended, or <c>null</c> for one that never began.</param>
    /// <param name="outcome">How a cancelled run ended, in one line; <c>null</c> otherwise.</param>
    /// <param name="report">The closing report.</param>
    /// <param name="ask">What Yes and No open, when this import asks "Did this import come in right?"; otherwise <c>null</c>.</param>
    public void ShowFinished(ImportRunView? view, string? outcome, string report, ImportFeedbackLinks? ask = null)
        => Post(() => _window?.ShowFinished(view, outcome, report, ask));

    /// <summary>Brings the window forward, unless a host modal is up. Called on the host's thread.</summary>
    public void BringForward() => Post(() => _window?.BringForward());

    /// <summary>Stops the window floating, before the host shows a dialog. Called on the host's thread.</summary>
    public void Lower() => Post(() => _window?.Lower());

    /// <summary>Closes the window whatever it shows, asking nothing. Called on the host's thread.</summary>
    public void CloseNow() => Post(() => _window?.CloseNow());

    /// <summary>The chosen level could not be downloaded: the control goes back to <paramref name="held"/>, with why.</summary>
    public void LevelKept(FidelityLevel held, string note) => Post(() => _window?.LevelKept(held, note));

    /// <summary>Runs <paramref name="action"/> where nothing may be thrown: this thread's last nets.</summary>
    private static void Quietly(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // A host already shutting its dispatcher down, or a window too broken to restack: neither is
            // worth ending the host over.
        }
    }

    /// <summary>Builds and shows the window: the window thread's first operation.</summary>
    private void Build()
    {
        try
        {
            ImportWindow window = new(_toHost);

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
            Quietly(_toHost.Host.Dismiss);
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

    /// <summary>Queues <paramref name="action"/> on the host's thread, where the import lives.</summary>
    private void ToHost(Action action) => _host.BeginInvoke(DispatcherPriority.Normal, action);

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
            Quietly(() => _toHost.Host.Note(
                $"The import window hit another fault, after the one already shown: {fault.GetType().Name}: {fault.Message}"));
            return;
        }

        _faultSaid = true;
        Quietly(() => _toHost.Host.Fault(fault));
    }

    /// <summary>The host's side, every call posted to the host's thread rather than made on this one.</summary>
    private sealed class Posted(ImportWindowThread thread, IImportWindowHost host) : IImportWindowHost
    {
        public void Begin(ImportLayerChoice choice) => thread.ToHost(() => host.Begin(choice));

        public void CancelRun() => thread.ToHost(host.CancelRun);

        public void Dismiss() => thread.ToHost(host.Dismiss);

        public void Note(string line) => thread.ToHost(() => host.Note(line));

        public void Fault(Exception fault) => thread.ToHost(() => host.Fault(fault));

        public void ChooseLevel(FidelityLevel level) => thread.ToHost(() => host.ChooseLevel(level));
    }
}
