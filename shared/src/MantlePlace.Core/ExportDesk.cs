using System.Text.Json;

namespace MantlePlace.Core;

public static class ExportReasons
{
    public const string UnsupportedVersion = ExportTransitions.UnsupportedVersionReason;

    public const string BundleNotInVault = "bundle_not_in_vault";

    public const string BundleUnavailable = "bundle_unavailable";

    public const string DownloadFailed = "download_failed";

    public const string NoDocument = "no_document";

    public const string NotABundle = "not_a_bundle";

    public const string IntegrityFailed = "integrity_failed";

    public const string NothingToImport = "nothing_to_import";

    public const string WindowFailed = "window_failed";

    public const string ImportFailed = "import_failed";

    public const string ImportCancelled = "import_cancelled";

    public const string ExportLost = "export_lost";

    public static IReadOnlyList<string> All { get; } =
    [
        UnsupportedVersion, BundleNotInVault, BundleUnavailable, DownloadFailed, NoDocument, NotABundle,
        IntegrityFailed, NothingToImport, WindowFailed, ImportFailed, ImportCancelled, ExportLost,
    ];
}

public enum OrderReadiness
{
    Building,

    Ready,

    Unavailable,
}

public static class OrderRows
{
    public static IReadOnlyList<string> UnavailableStatuses { get; } =
        ["refunded", "failed", "expired", "deleted", "withheld", "cancelled"];

    public static string IdOf(string rowJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(rowJson ?? string.Empty);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Str("id") : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    public static OrderReadiness Classify(string rowJson)
    {
        if (BundleReadiness.IsReadyRow(rowJson))
        {
            return OrderReadiness.Ready;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(rowJson ?? string.Empty);
            JsonElement row = document.RootElement;
            return row.ValueKind == JsonValueKind.Object && UnavailableStatuses.Contains(row.Str("status"))
                ? OrderReadiness.Unavailable
                : OrderReadiness.Building;
        }
        catch (JsonException)
        {
            return OrderReadiness.Building;
        }
    }
}

public enum ExportFetchKind
{
    Ready,

    StillBuilding,

    Unreadable,

    Failed,
}

public readonly record struct ExportFetchResult(
    ExportFetchKind Kind,
    string ZipPath,
    string? ManifestVersion,
    string Reason,
    string Message)
{
    public static ExportFetchResult Ready(string zipPath, string? manifestVersion)
        => new(ExportFetchKind.Ready, zipPath, manifestVersion, string.Empty, string.Empty);

    public static ExportFetchResult StillBuilding(string message)
        => new(ExportFetchKind.StillBuilding, string.Empty, null, string.Empty, message);

    public static ExportFetchResult Unreadable(string? manifestVersion)
        => new(ExportFetchKind.Unreadable, string.Empty, manifestVersion, string.Empty, string.Empty);

    public static ExportFetchResult Failed(string reason, string message)
        => new(ExportFetchKind.Failed, string.Empty, null, reason, message);
}

public enum ImportEnding
{
    Imported,

    Failed,

    CancelledWhileRunning,

    DismissedBeforeStart,
}

public static class ImportEndings
{
    public static ImportEnding Of(bool failed, bool begun, bool wasCancelled)
        => failed ? ImportEnding.Failed
            : !begun ? ImportEnding.DismissedBeforeStart
            : wasCancelled ? ImportEnding.CancelledWhileRunning
            : ImportEnding.Imported;
}

public static class ImportPercent
{
    public static int Of(StagedImport run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Steps.Count == 0)
        {
            return 0;
        }

        int ended = run.Steps.Count(step => step.State is not (ImportStepState.Waiting or ImportStepState.Importing));
        double current = run.Current?.Progress?.Fraction ?? 0.0;
        int percent = (int)Math.Floor(100.0 * (ended + current) / run.Steps.Count);
        return Math.Clamp(percent, 0, 99);
    }
}

public enum ConnectedHostMove
{
    None,

    Join,

    Reauthorise,

    Leave,
}

public sealed class ConnectedHostEdge
{
    private bool _joined;

    public ConnectedHostMove Observe(AuthState state)
    {
        switch (state)
        {
            case AuthState.Authenticated:
                if (_joined)
                {
                    return ConnectedHostMove.Reauthorise;
                }

                _joined = true;
                return ConnectedHostMove.Join;

            case AuthState.Unauthenticated:
            case AuthState.Failed:
                if (!_joined)
                {
                    return ConnectedHostMove.None;
                }

                _joined = false;
                return ConnectedHostMove.Leave;

            default:
                return ConnectedHostMove.None;
        }
    }
}

public enum ExportAdvanceOutcome
{
    Ok,
    WebMovedFirst,
    RetryLater,
    RateLimited,
    Rejected,
    RefusedLocally,
    SignInNeeded,
}

public readonly record struct ExportAdvanceAnswer(
    ExportAdvanceOutcome Outcome,
    HostExport? Updated,
    string Message,
    int RetryAfterMs)
{
    public static ExportAdvanceAnswer Ok(HostExport updated) => new(ExportAdvanceOutcome.Ok, updated, string.Empty, 0);
}

public abstract record ExportAction;

public sealed record AdvanceExport(HostExport Export, ExportTransition Transition, TimeSpan Delay) : ExportAction;

public sealed record FetchExportBundle(HostExport Export) : ExportAction;

public sealed record WatchExportOrder(string OrderId) : ExportAction;

public sealed record StopWatchingExportOrder : ExportAction;

public sealed record OpenExportImport(HostExport Export, string ZipPath) : ExportAction;

public sealed record CloseExportImport(string ExportId) : ExportAction;

public sealed record NoteExport(string Line) : ExportAction;

public enum ExportStage
{
    Queued,

    Fetching,

    Building,

    ReadyToOpen,

    Opening,

    AwaitingOptions,

    Importing,

    Ending,
}

public sealed class ExportDesk
{
    public const int MaxAttempts = 4;

    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    private readonly string _connectionId;
    private readonly Func<DateTimeOffset> _now;
    private readonly List<Ticket> _tickets = [];
    private readonly HashSet<string> _finished = new(StringComparer.Ordinal);
    private string? _watching;

    public ExportDesk(string connectionId, Func<DateTimeOffset> now)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);
        _connectionId = connectionId;
        _now = now ?? throw new ArgumentNullException(nameof(now));
    }

    public IReadOnlyList<string> InHand => [.. _tickets.Select(ticket => ticket.Export.Id)];

    public ExportStage? StageOf(string exportId) => Find(exportId)?.Stage;

    public string? WatchedOrder => _watching;

    public IReadOnlyList<ExportAction> Seen(HostExport row)
    {
        if (!row.IsFor(_connectionId) || _finished.Contains(row.Id))
        {
            return [];
        }

        List<ExportAction> actions = [];
        if (Find(row.Id) is { } held)
        {
            if (row.Status is ExportStatus.Cancelled or ExportStatus.Failed && held.Stage != ExportStage.Ending)
            {
                actions.Add(new NoteExport($"Export {row.Id}: the web {ExportStatuses.ToWire(row.Status)} it, so Revit stops work on it."));
                EndQuietly(held, actions);
            }

            return actions;
        }

        if (ExportStatuses.IsTerminal(row.Status))
        {
            _finished.Add(row.Id);
            return [];
        }

        Ticket ticket = new(row, new ImportProgressPacer(_now));
        _tickets.Add(ticket);

        switch (row.Status)
        {
            case ExportStatus.Sent:
                actions.Add(new NoteExport($"Export {row.Id} received, for order {row.OrderId}."));
                Enqueue(ticket, ExportTransitions.Received, actions);
                break;

            case ExportStatus.Received:
            case ExportStatus.WaitingForBundle:
                actions.Add(new NoteExport($"Export {row.Id} picked up again at {ExportStatuses.ToWire(row.Status)}."));
                break;

            default:
                actions.Add(new NoteExport($"Export {row.Id} was at {ExportStatuses.ToWire(row.Status)} with no window open for it here."));
                Fail(
                    ticket,
                    ExportTransitions.Failed(
                        ExportReasons.ExportLost,
                        "Revit lost track of this export while its import window was open (the plugin was signed out, or its connection was rebuilt). Send it again."),
                    actions);
                return actions;
        }

        StartHead(actions);
        return actions;
    }

    public IReadOnlyList<ExportAction> Answered(string exportId, ExportAdvanceAnswer answer)
    {
        if (Find(exportId) is not { InFlight: true } ticket)
        {
            return [];
        }

        List<ExportAction> actions = [];
        ticket.InFlight = false;
        ExportTransition sent = ticket.Outbox[0];

        switch (answer.Outcome)
        {
            case ExportAdvanceOutcome.Ok:
                ticket.Outbox.RemoveAt(0);
                ticket.Attempts = 0;
                ticket.Export = answer.Updated ?? ticket.Export;
                if (ExportStatuses.IsTerminal(ticket.Export.Status))
                {
                    Remove(ticket, actions);
                    StartHead(actions);
                    return actions;
                }

                Pump(ticket, actions);
                StartHead(actions);
                return actions;

            case ExportAdvanceOutcome.WebMovedFirst:
                actions.Add(new NoteExport($"Export {exportId}: the web moved it before '{Describe(sent)}' landed, so Revit stops work on it."));
                EndQuietly(ticket, actions);
                return actions;

            case ExportAdvanceOutcome.RetryLater:
            case ExportAdvanceOutcome.RateLimited:
                ticket.Attempts++;
                if (ticket.Attempts < MaxAttempts)
                {
                    TimeSpan delay = answer.Outcome == ExportAdvanceOutcome.RateLimited
                        ? TimeSpan.FromMilliseconds(Math.Max(answer.RetryAfterMs, 0))
                        : RetryDelay * ticket.Attempts;
                    ticket.InFlight = true;
                    actions.Add(new AdvanceExport(ticket.Export, sent, delay));
                    return actions;
                }

                GiveUp(ticket, $"'{Describe(sent)}' was not taken after {MaxAttempts} tries: {answer.Message}", actions);
                return actions;

            default:
                GiveUp(ticket, $"'{Describe(sent)}' was refused: {answer.Message}", actions);
                return actions;
        }
    }

    public IReadOnlyList<ExportAction> FetchBuilding(string exportId)
    {
        if (Find(exportId) is not { Stage: ExportStage.Fetching } ticket)
        {
            return [];
        }

        List<ExportAction> actions = [];
        Wait(ticket, actions);
        return actions;
    }

    public IReadOnlyList<ExportAction> FetchEnded(string exportId, ExportFetchResult result)
    {
        if (Find(exportId) is not { Stage: ExportStage.Fetching or ExportStage.Building } ticket)
        {
            return [];
        }

        List<ExportAction> actions = [];
        ticket.FetchRunning = false;

        switch (result.Kind)
        {
            case ExportFetchKind.Ready when !ExportBundles.CanRead(result.ManifestVersion):
            case ExportFetchKind.Unreadable:
                Fail(ticket, ExportBundles.RefuseUnreadable(result.ManifestVersion)!.Value, actions);
                break;

            case ExportFetchKind.Ready:
                StopWatching(ticket, actions);
                ticket.ZipPath = result.ZipPath;
                ticket.Stage = ExportStage.Opening;
                actions.Add(new OpenExportImport(ticket.Export, result.ZipPath));
                break;

            case ExportFetchKind.StillBuilding:
                actions.Add(new NoteExport($"Export {exportId}: {result.Message} Waiting for the order to say the bundle is ready."));
                Wait(ticket, actions);
                break;

            default:
                Fail(ticket, ExportTransitions.Failed(result.Reason, result.Message), actions);
                break;
        }

        return actions;
    }

    public IReadOnlyList<ExportAction> OrderChanged(string rowJson)
    {
        string orderId = OrderRows.IdOf(rowJson);
        if (Head() is not { Stage: ExportStage.Building } ticket
            || !string.Equals(ticket.Export.OrderId, orderId, StringComparison.Ordinal))
        {
            return [];
        }

        List<ExportAction> actions = [];
        switch (OrderRows.Classify(rowJson))
        {
            case OrderReadiness.Ready when !ticket.FetchRunning:
                actions.Add(new NoteExport($"Export {ticket.Export.Id}: the order says its bundle is ready."));
                ticket.Stage = ExportStage.Fetching;
                ticket.FetchRunning = true;
                actions.Add(new FetchExportBundle(ticket.Export));
                break;

            case OrderReadiness.Unavailable:
                Fail(
                    ticket,
                    ExportTransitions.Failed(
                        ExportReasons.BundleUnavailable,
                        "This bundle is no longer available in your vault, so Revit cannot import it."),
                    actions);
                break;
        }

        return actions;
    }

    public IReadOnlyList<ExportAction> ImportOpened(string exportId)
    {
        if (Find(exportId) is not { Stage: ExportStage.Opening } ticket)
        {
            return [];
        }

        List<ExportAction> actions = [];
        ticket.Stage = ExportStage.AwaitingOptions;
        Enqueue(ticket, ExportTransitions.AwaitingOptions, actions);
        return actions;
    }

    public IReadOnlyList<ExportAction> ImportBusy(string exportId)
    {
        if (Find(exportId) is { Stage: ExportStage.Opening } ticket)
        {
            ticket.Stage = ExportStage.ReadyToOpen;
            return [new NoteExport($"Export {exportId}: another import is open in Revit, so this one waits its turn.")];
        }

        return [];
    }

    public IReadOnlyList<ExportAction> ImportRefused(string exportId, string reason, string message)
    {
        if (Find(exportId) is not { Stage: ExportStage.Opening } ticket)
        {
            return [];
        }

        List<ExportAction> actions = [];
        Fail(ticket, ExportTransitions.Failed(reason, message), actions);
        return actions;
    }

    public IReadOnlyList<ExportAction> TurnFree()
    {
        if (Head() is not { Stage: ExportStage.ReadyToOpen } ticket)
        {
            return [];
        }

        ticket.Stage = ExportStage.Opening;
        return [new OpenExportImport(ticket.Export, ticket.ZipPath!)];
    }

    public IReadOnlyList<ExportAction> ImportBegan(string exportId)
    {
        if (Find(exportId) is not { Stage: ExportStage.AwaitingOptions } ticket)
        {
            return [];
        }

        List<ExportAction> actions = [];
        ticket.Stage = ExportStage.Importing;
        ticket.Pacer.Start();
        Enqueue(ticket, ExportTransitions.StartImport, actions);
        return actions;
    }

    public IReadOnlyList<ExportAction> ImportProgress(string exportId, int percent)
    {
        if (Find(exportId) is not { Stage: ExportStage.Importing } ticket || !ticket.Pacer.TryNext(percent, out int send))
        {
            return [];
        }

        List<ExportAction> actions = [];
        ExportTransition report = ExportTransitions.ReportProgress(send);
        int last = ticket.Outbox.Count - 1;
        bool queuedBehind = last >= (ticket.InFlight ? 1 : 0);
        if (queuedBehind && ticket.Outbox[last] is { Status: ExportStatus.Importing, Progress: not null })
        {
            ticket.Outbox[last] = report;
            return actions;
        }

        Enqueue(ticket, report, actions);
        return actions;
    }

    public IReadOnlyList<ExportAction> ImportEnded(string exportId, ImportEnding ending, string message)
    {
        if (Find(exportId) is not { Stage: ExportStage.AwaitingOptions or ExportStage.Importing or ExportStage.Opening } ticket)
        {
            return [];
        }

        ExportTransition last = ending switch
        {
            ImportEnding.Imported => ExportTransitions.Imported,
            ImportEnding.DismissedBeforeStart => ExportTransitions.Cancelled,
            ImportEnding.CancelledWhileRunning => ExportTransitions.Failed(
                ExportReasons.ImportCancelled,
                string.IsNullOrWhiteSpace(message)
                    ? "The import was cancelled in Revit. What it committed before the cancel stays in the project."
                    : message),
            _ => ExportTransitions.Failed(
                ExportReasons.ImportFailed,
                string.IsNullOrWhiteSpace(message) ? "The import failed in Revit; its log is beside the bundle." : message),
        };

        List<ExportAction> actions = [];
        ticket.Stage = ExportStage.Ending;
        Enqueue(ticket, last, actions);
        StartHead(actions);
        return actions;
    }

    public void Reset()
    {
        _tickets.Clear();
        _watching = null;
    }

    private Ticket? Find(string exportId)
        => _tickets.FirstOrDefault(ticket => string.Equals(ticket.Export.Id, exportId, StringComparison.Ordinal));

    private Ticket? Head() => _tickets.FirstOrDefault(ticket => ticket.Stage != ExportStage.Ending);

    private void StartHead(List<ExportAction> actions)
    {
        if (Head() is { Stage: ExportStage.Queued } head && head.Export.Status != ExportStatus.Sent)
        {
            head.Stage = ExportStage.Fetching;
            head.FetchRunning = true;
            actions.Add(new FetchExportBundle(head.Export));
        }
    }

    private void Wait(Ticket ticket, List<ExportAction> actions)
    {
        ticket.Stage = ExportStage.Building;
        if (ticket.Export.Status == ExportStatus.Received
            && !ticket.Outbox.Any(queued => queued.Status == ExportStatus.WaitingForBundle))
        {
            Enqueue(ticket, ExportTransitions.WaitingForBundle, actions);
        }

        if (!string.Equals(_watching, ticket.Export.OrderId, StringComparison.Ordinal))
        {
            _watching = ticket.Export.OrderId;
            actions.Add(new WatchExportOrder(ticket.Export.OrderId));
        }
    }

    private void Fail(Ticket ticket, ExportTransition failure, List<ExportAction> actions)
    {
        actions.Add(new NoteExport($"Export {ticket.Export.Id} failed ({failure.Reason}): {failure.Message}"));
        StopWatching(ticket, actions);
        ticket.Stage = ExportStage.Ending;
        ticket.FetchRunning = false;
        Enqueue(ticket, failure, actions);
        StartHead(actions);
    }

    private void EndQuietly(Ticket ticket, List<ExportAction> actions)
    {
        if (ticket.Stage is ExportStage.Opening or ExportStage.AwaitingOptions)
        {
            actions.Add(new CloseExportImport(ticket.Export.Id));
        }

        Remove(ticket, actions);
        StartHead(actions);
    }

    private void GiveUp(Ticket ticket, string why, List<ExportAction> actions)
    {
        actions.Add(new NoteExport($"Export {ticket.Export.Id} given up: {why}"));
        Remove(ticket, actions);
        StartHead(actions);
    }

    private void Remove(Ticket ticket, List<ExportAction> actions)
    {
        StopWatching(ticket, actions);
        _tickets.Remove(ticket);
        _finished.Add(ticket.Export.Id);
    }

    private void StopWatching(Ticket ticket, List<ExportAction> actions)
    {
        if (_watching is not null && string.Equals(_watching, ticket.Export.OrderId, StringComparison.Ordinal))
        {
            _watching = null;
            actions.Add(new StopWatchingExportOrder());
        }
    }

    private static void Enqueue(Ticket ticket, ExportTransition transition, List<ExportAction> actions)
    {
        ticket.Outbox.Add(transition);
        Pump(ticket, actions);
    }

    private static void Pump(Ticket ticket, List<ExportAction> actions)
    {
        if (ticket.InFlight || ticket.Outbox.Count == 0)
        {
            return;
        }

        ticket.InFlight = true;
        actions.Add(new AdvanceExport(ticket.Export, ticket.Outbox[0], TimeSpan.Zero));
    }

    private static string Describe(ExportTransition transition)
        => transition.Progress is { } percent
            ? $"{ExportStatuses.ToWire(transition.Status)} {percent}%"
            : ExportStatuses.ToWire(transition.Status);

    private sealed class Ticket(HostExport export, ImportProgressPacer pacer)
    {
        public HostExport Export { get; set; } = export;

        public ImportProgressPacer Pacer { get; } = pacer;

        public ExportStage Stage { get; set; } = ExportStage.Queued;

        public List<ExportTransition> Outbox { get; } = [];

        public bool InFlight { get; set; }

        public int Attempts { get; set; }

        public bool FetchRunning { get; set; }

        public string? ZipPath { get; set; }
    }
}
