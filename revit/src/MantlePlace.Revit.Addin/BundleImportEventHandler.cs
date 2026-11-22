using System.IO;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MantlePlace.Client;
using MantlePlace.Core;
using MantlePlace.Wpf;

namespace MantlePlace.Revit.Addin;

internal sealed class BundleImportEventHandler : IExternalEventHandler
{
    private readonly object _gate = new();
    private string? _zipPath;
    private ExternalEvent? _event;
    private ActiveImport? _import;

    private ImportWindowThread? _window;

    private readonly List<ImportWindowThread> _hosts = [];

    private bool _inSlice;

    private readonly List<Action> _heldUntilSliceEnds = [];

    private bool _fromVault;

    private sealed record QueuedExport(string ExportId, string OrderId, string ZipPath, FidelityLevel? Level = null, string? LevelNote = null);

    private QueuedExport? _queuedExport;

    private string? _exportId;

    private string? _exportOrderId;

    private FidelityLevel? _exportLevel;

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

    private VaultExportSource? _levels;

    internal event EventHandler<string>? Completed;

    internal bool IsImporting => _import is not null || _refetch is not null;

    internal string BusyReason => _refetch is not null
        ? "An import window is downloading the level chosen in it — wait for it to reopen, or close it."
        : _import?.Staged is null
        ? "An import is waiting in its window — press Import there, or close it."
        : "An import is already running — wait for it, or cancel it in its window.";

    internal void Attach(ExternalEvent externalEvent) => _event = externalEvent;

    internal void AttachLevels(VaultExportSource levels) => _levels = levels;

    internal void QueueImport(string zipPath)
    {
        lock (_gate)
        {
            _zipPath = zipPath;
        }
    }

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

    internal void TakeOver(ActiveImport import, IntPtr revitWindow)
    {
        ArgumentNullException.ThrowIfNull(import);
        if (_import is not null)
        {
            throw new InvalidOperationException("An import is already running.");
        }

        Show(import, revitWindow, fromVault: false);
    }

    internal void ShowRunning() => _window?.BringForward();

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

    private void OpenQueuedExport(UIApplication application, QueuedExport queued)
    {
        if (MantlePlaceApplication.Connected is not { } connected)
        {
            return;
        }

        string exportId = queued.ExportId;
        string zipPath = queued.ZipPath;

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

        connected.ImportOpened(exportId);
    }

    private void ReportProgress(ActiveImport import)
    {
        if (_exportId is { } exportId && import.Staged is { } staged && ReferenceEquals(import, _import))
        {
            MantlePlaceApplication.Connected?.ImportProgress(exportId, ImportPercent.Of(staged));
        }
    }

    private bool Show(ActiveImport import, IntPtr revitWindow, bool fromVault, LevelControl? levels = null, string? levelNote = null)
    {
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

    private void OutsideSlice(Action callback)
    {
        if (_inSlice)
        {
            _heldUntilSliceEnds.Add(callback);
            return;
        }

        callback();
    }

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
            }
        }
    }

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

    private void Refresh(ActiveImport import)
    {
        if (import.Staged is { } staged)
        {
            _window?.Refresh(ImportRunView.Of(staged));
        }
    }

    private void BeginChosen(ActiveImport import, ImportLayerChoice choice)
    {
        if (!ReferenceEquals(import, _import) || import.Staged is not null)
        {
            return;
        }

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

    private void CancelRun(ActiveImport import)
    {
        if (!ReferenceEquals(import, _import) || import.Staged is not { } staged)
        {
            return;
        }

        staged.RequestCancel();
        Refresh(import);
    }

    private void DismissBeforeStart(ActiveImport import)
    {
        if (_refetch is { } refetch && ReferenceEquals(refetch.Import, import))
        {
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

    private void Refetched(LevelRefetch refetch, LevelFetch fetched)
    {
        if (!ReferenceEquals(_refetch, refetch))
        {
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

    private void DropRefetch(LevelRefetch refetch)
    {
        lock (_gate)
        {
            _refetch = null;
        }

        refetch.Stop.Cancel();
    }

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

            connected.TurnFree();
        }

        if (!fromVault)
        {
            return;
        }

        Completed?.Invoke(
            this,
            import.Staged is null && !import.Failed
                ? import.Heading
                : $"{import.Heading} The report is in the {WindowLabels.ImportHeading} window, and in the log beside the bundle.");
    }

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
