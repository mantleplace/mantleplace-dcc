using System.Windows.Threading;
using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.Wpf;

public sealed record ImportWindowRequest(
    string BundleName,
    string? DeliveryLine,
    string? UnitsDisagreement,
    ImportChecklist? Checklist,
    IntPtr HostWindow,
    IImportWindowHost Host)
{
    public IReadOnlyDictionary<ImportStepKind, TimeSpan>? MeasuredCosts { get; init; }

    public LevelControl? Levels { get; init; }

    public string? LevelNote { get; init; }

    public string? BeforeImport { get; init; }
}

public sealed class ImportWindowThread
{
    private readonly Dispatcher _host;

    private readonly ImportWindowRequest _toHost;

    private Dispatcher? _windowThread;

    private ImportWindow? _window;

    private bool _faultSaid;

    private ImportWindowThread(Dispatcher host, ImportWindowRequest request)
    {
        _host = host;
        _toHost = request with { Host = new Posted(this, request.Host) };
    }

    public event Action? Ended;

    public static ImportWindowThread? Open(ImportWindowRequest request, out Exception? failure)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Host);

        ImportWindowThread host = new(Dispatcher.CurrentDispatcher, request);
        Exception? startFailure = null;

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

    public void ShowRun(ImportRunView view) => Post(() => _window?.ShowRun(view));

    public void Refresh(ImportRunView view) => Post(() => _window?.Refresh(view));

    public void ShowFinished(ImportRunView? view, string? outcome, string report, ImportFeedbackLinks? ask = null)
        => Post(() => _window?.ShowFinished(view, outcome, report, ask));

    public void BringForward() => Post(() => _window?.BringForward());

    public void BringForwardAtTheEnd() => Post(() => _window?.BringForwardAtTheEnd());

    public void Lower() => Post(() => _window?.Lower());

    public void CloseNow() => Post(() => _window?.CloseNow());

    public void LevelKept(FidelityLevel held, string note) => Post(() => _window?.LevelKept(held, note));

    private static void Quietly(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ClientLog.Write($"The import window dropped a fault where nothing may be thrown: {ex}");
        }
    }

    private void Build()
    {
        try
        {
            ImportWindow window = new(_toHost);

            window.Closed += (_, _) => Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
            _window = window;
            window.Show();
        }
        catch (Exception ex)
        {
            _window = null;
            Quietly(_toHost.Host.Dismiss);
            ReportFault(ex);
            Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
        }
    }

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
        }
    }

    private void ToHost(Action action) => _host.BeginInvoke(DispatcherPriority.Normal, action);

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
