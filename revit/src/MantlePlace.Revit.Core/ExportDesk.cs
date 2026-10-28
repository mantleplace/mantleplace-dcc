using System.Text.Json;

namespace MantlePlace.Revit.Core;

/// <summary>
/// The reason codes this plugin fails an export with. Each is a code, never free text (the platform
/// export contract, section 4): the plugin's own words go in the message beside it.
/// </summary>
public static class ExportReasons
{
    /// <summary>The bundle's manifest version is outside what the reader takes.</summary>
    public const string UnsupportedVersion = ExportTransitions.UnsupportedVersionReason;

    /// <summary>The export names an order the curator's vault listing does not hold.</summary>
    public const string BundleNotInVault = "bundle_not_in_vault";

    /// <summary>The bundle became unavailable after the export was made (refunded, failed, withdrawn).</summary>
    public const string BundleUnavailable = "bundle_unavailable";

    /// <summary>The vault routes refused, or the download did not land a valid zip in the cache.</summary>
    public const string DownloadFailed = "download_failed";

    /// <summary>Revit has no project open to import into.</summary>
    public const string NoDocument = "no_document";

    /// <summary>The zip has no bundle manifest.</summary>
    public const string NotABundle = "not_a_bundle";

    /// <summary>The bundle's bytes do not match the hashes its own manifest publishes.</summary>
    public const string IntegrityFailed = "integrity_failed";

    /// <summary>The bundle holds nothing this host imports.</summary>
    public const string NothingToImport = "nothing_to_import";

    /// <summary>The import window could not be opened, or Revit would not run the import.</summary>
    public const string WindowFailed = "window_failed";

    /// <summary>The import stopped on an exception.</summary>
    public const string ImportFailed = "import_failed";

    /// <summary>The curator cancelled the import in Revit after it began; what was committed stays.</summary>
    public const string ImportCancelled = "import_cancelled";

    /// <summary>
    /// The export was past the point a window opens, and this plugin no longer holds that window: it
    /// was signed out, or the channel was rebuilt, while the export was open.
    /// </summary>
    public const string ExportLost = "export_lost";

    /// <summary>Every code above, for the test that holds each to the contract's pattern.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        UnsupportedVersion, BundleNotInVault, BundleUnavailable, DownloadFailed, NoDocument, NotABundle,
        IntegrityFailed, NothingToImport, WindowFailed, ImportFailed, ImportCancelled, ExportLost,
    ];
}

/// <summary>Where the order behind an export stands, read from its row.</summary>
public enum OrderReadiness
{
    /// <summary>Still being built, or a row that says nothing this plugin reads: keep waiting.</summary>
    Building,

    /// <summary>The row says the bundle is ready (<see cref="BundleReadiness.IsReady"/>).</summary>
    Ready,

    /// <summary>The order moved to a status no bundle comes back from.</summary>
    Unavailable,
}

/// <summary>An order row, as a row change carries it.</summary>
public static class OrderRows
{
    /// <summary>
    /// The order statuses a bundle does not come back from. The contract names refunded, failed,
    /// expired, deleted and withheld bundles as the ones an export is refused for; a row that moves to
    /// one of them after the export was made fails the export. A status word not listed here is NOT
    /// read as unavailable: an unknown word keeps the export waiting, where the web can still cancel it.
    /// </summary>
    public static IReadOnlyList<string> UnavailableStatuses { get; } =
        ["refunded", "failed", "expired", "deleted", "withheld", "cancelled"];

    /// <summary>The row's <c>id</c>, or empty.</summary>
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

    /// <summary>Ready, unavailable, or still building.</summary>
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

/// <summary>How fetching an export's bundle ended.</summary>
public enum ExportFetchKind
{
    /// <summary>The cache holds a zip Import can open.</summary>
    Ready,

    /// <summary>The bundle is still being built, and the fetch stopped watching it: wait for the order row.</summary>
    StillBuilding,

    /// <summary>The listing's manifest version is outside what the reader takes.</summary>
    Unreadable,

    /// <summary>Anything else; the reason and message say what.</summary>
    Failed,
}

/// <summary>The result of one fetch of an export's bundle.</summary>
/// <param name="Kind">How it ended.</param>
/// <param name="ZipPath">The cached zip, for <see cref="ExportFetchKind.Ready"/>.</param>
/// <param name="ManifestVersion">The bundle's manifest version as the listing states it, or <c>null</c> when unknown.</param>
/// <param name="Reason">The failure code, for <see cref="ExportFetchKind.Failed"/>.</param>
/// <param name="Message">The plugin's own words, for the log and the curator.</param>
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

/// <summary>How an export's import window ended.</summary>
public enum ImportEnding
{
    /// <summary>The import ran to its end.</summary>
    Imported,

    /// <summary>The import stopped on an exception, before or after it began.</summary>
    Failed,

    /// <summary>The curator cancelled after Import was pressed. What was committed stays.</summary>
    CancelledWhileRunning,

    /// <summary>The window went before Import was pressed. Nothing ran.</summary>
    DismissedBeforeStart,
}

/// <summary>How an import run ended, from the three facts the window's host holds.</summary>
public static class ImportEndings
{
    /// <param name="failed">Whether the run was stopped by an exception.</param>
    /// <param name="begun">Whether Import was pressed and the run planned.</param>
    /// <param name="wasCancelled">Whether a cancel stopped something.</param>
    public static ImportEnding Of(bool failed, bool begun, bool wasCancelled)
        => failed ? ImportEnding.Failed
            : !begun ? ImportEnding.DismissedBeforeStart
            : wasCancelled ? ImportEnding.CancelledWhileRunning
            : ImportEnding.Imported;
}

/// <summary>How far a staged import has got, as the whole number 0 to 99 the progress report carries.</summary>
/// <remarks>
/// Every step counts the same; the step in flight adds the fraction it has reported. Never 100: the
/// run reports <c>imported</c> when it is over, and a 100 that is followed by minutes of finishing
/// work would say the opposite.
/// </remarks>
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

/// <summary>What the connected host does about the session it is signed in on.</summary>
public enum ConnectedHostMove
{
    /// <summary>Nothing changes.</summary>
    None,

    /// <summary>Signed in, or a stored session restored: join the channel and track presence.</summary>
    Join,

    /// <summary>The access token was renewed while joined: authorise the channel again.</summary>
    Reauthorise,

    /// <summary>Signed out, or the session failed: untrack and leave.</summary>
    Leave,
}

/// <summary>
/// Turns the auth session's transitions into the connected host's joins and leaves. Not thread-safe;
/// the caller serialises.
/// </summary>
/// <remarks>
/// A renewal passes through <c>Refreshing</c> and back to <c>Authenticated</c>: that is a
/// re-authorisation, never a second join, so the curator sees one Revit for the whole session.
/// </remarks>
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

/// <summary>How an advance call ended, as the desk needs it.</summary>
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

/// <summary>The answer to one advance.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="Updated">The export as the platform now holds it, for <see cref="ExportAdvanceOutcome.Ok"/>.</param>
/// <param name="Message">Why not, otherwise.</param>
/// <param name="RetryAfterMs">With <see cref="ExportAdvanceOutcome.RateLimited"/>, the wait asked for.</param>
public readonly record struct ExportAdvanceAnswer(
    ExportAdvanceOutcome Outcome,
    HostExport? Updated,
    string Message,
    int RetryAfterMs)
{
    public static ExportAdvanceAnswer Ok(HostExport updated) => new(ExportAdvanceOutcome.Ok, updated, string.Empty, 0);
}

/// <summary>Something the desk asks its host to do.</summary>
public abstract record ExportAction;

/// <summary>Send <paramref name="Transition"/> for <paramref name="Export"/>, after <paramref name="Delay"/>.</summary>
public sealed record AdvanceExport(HostExport Export, ExportTransition Transition, TimeSpan Delay) : ExportAction;

/// <summary>Fetch the export's bundle into the cache, through the vault routes.</summary>
public sealed record FetchExportBundle(HostExport Export) : ExportAction;

/// <summary>Watch this order's row on the channel.</summary>
public sealed record WatchExportOrder(string OrderId) : ExportAction;

/// <summary>Stop watching the order row.</summary>
public sealed record StopWatchingExportOrder : ExportAction;

/// <summary>Open the import window on <paramref name="ZipPath"/>, for this export.</summary>
public sealed record OpenExportImport(HostExport Export, string ZipPath) : ExportAction;

/// <summary>Close this export's import window, if it is still on its checklist.</summary>
public sealed record CloseExportImport(string ExportId) : ExportAction;

/// <summary>A line for the log.</summary>
public sealed record NoteExport(string Line) : ExportAction;

/// <summary>Where one export stands on the desk.</summary>
public enum ExportStage
{
    /// <summary>Seen and acknowledged (or being acknowledged), waiting its turn.</summary>
    Queued,

    /// <summary>Its bundle is being fetched.</summary>
    Fetching,

    /// <summary>Its bundle is still being built; the order row is watched.</summary>
    Building,

    /// <summary>Its bundle is in the cache, and another import holds the window: it waits its turn.</summary>
    ReadyToOpen,

    /// <summary>The window has been asked for.</summary>
    Opening,

    /// <summary>The window is open on its checklist, waiting for the curator.</summary>
    AwaitingOptions,

    /// <summary>The curator pressed Import.</summary>
    Importing,

    /// <summary>Its last report is queued; nothing more is done for it.</summary>
    Ending,
}

/// <summary>
/// Which export the connected host acts on, what it reports next, and when it gives up: every
/// decision of receiving an export, with no thread, no clock but the one it is given, and no I/O
/// (the platform export contract, sections 3 to 5).
/// </summary>
/// <remarks>
/// <para>
/// Each input returns the actions it calls for; the host carries them out and feeds the outcome back
/// in. The desk is not thread-safe: its host serialises every call.
/// </para>
/// <para>
/// <b>Seen twice, acted on once.</b> An export arrives from the catch-up list and from a row change
/// alike, and both ways after every reconnect. A row whose id the desk holds, or has finished with, is
/// read only for one thing: the web having ended it (cancelled or failed), which stops the plugin's
/// work quietly and closes a window still on its checklist.
/// </para>
/// <para>
/// <b>One at a time, and nothing dropped.</b> Every export is acknowledged as <c>received</c> the
/// moment it is seen, so the web never fails it as <c>not_received</c>; then they are worked in the
/// order they arrived, one at a time, from fetch to the end of its import. An export that arrives while
/// another import is open waits its turn at <c>received</c>, where the web can still cancel it. A bundle
/// fetched while an import the curator opened some other way holds the window waits at
/// <see cref="ExportStage.ReadyToOpen"/> until the host says the turn is free.
/// </para>
/// <para>
/// <b>One report in flight per export.</b> Each transition is checked against the status the platform
/// last answered with, so they go one after another; progress reports queued behind one in flight
/// coalesce to the newest. A refusal because the web moved the export first ends the plugin's work on
/// it quietly. A 5xx or a 429 is tried again, up to <see cref="MaxAttempts"/> times; anything else, or
/// running out of tries, gives the export up: no more reports, and a window already open stays open,
/// since the import itself needs no platform.
/// </para>
/// <para>
/// <b>Ready</b> is the order row's word when the plugin has had to wait (<see cref="OrderRows"/>), or
/// the vault routes' own proof that the bundle is whole when it has not: the cache holding it, a
/// materialize with nothing left to build, a view that is not partial, or a Prepare that finished. A
/// partial view is never ready. Protocol 1 gives a plugin no way to read an order row's state on
/// connect, only its changes, so the routes' proof is the one there is before any change arrives.
/// </para>
/// </remarks>
public sealed class ExportDesk
{
    /// <summary>How many times one report is sent before the export is given up.</summary>
    public const int MaxAttempts = 4;

    /// <summary>The wait before a report is sent again after a 5xx, times the attempt.</summary>
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

    /// <summary>The exports in hand, in arrival order.</summary>
    public IReadOnlyList<string> InHand => [.. _tickets.Select(ticket => ticket.Export.Id)];

    /// <summary>Where an export stands, or <c>null</c> when the desk does not hold it.</summary>
    public ExportStage? StageOf(string exportId) => Find(exportId)?.Stage;

    /// <summary>The order being watched, or <c>null</c>.</summary>
    public string? WatchedOrder => _watching;

    /// <summary>An export row, from the catch-up list or a row change.</summary>
    public IReadOnlyList<ExportAction> Seen(HostExport row)
    {
        if (!row.IsFor(_connectionId) || _finished.Contains(row.Id))
        {
            return [];
        }

        List<ExportAction> actions = [];
        if (Find(row.Id) is { } held)
        {
            // Seen twice: only the web ending it is news.
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
                // Acknowledged before this desk held it (a sign-out and back in, a rebuilt channel):
                // picked up where it stands.
                actions.Add(new NoteExport($"Export {row.Id} picked up again at {ExportStatuses.ToWire(row.Status)}."));
                break;

            default:
                // Past the point a window opens, and no window here is bound to it.
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

    /// <summary>The platform answered an advance this desk asked for.</summary>
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

    /// <summary>The fetch found the bundle still being built, and is following the build.</summary>
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

    /// <summary>The fetch ended.</summary>
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

    /// <summary>A change to an order row.</summary>
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

    /// <summary>The import window is open on its checklist.</summary>
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

    /// <summary>Another import holds the window: this one waits for <see cref="TurnFree"/>.</summary>
    public IReadOnlyList<ExportAction> ImportBusy(string exportId)
    {
        if (Find(exportId) is { Stage: ExportStage.Opening } ticket)
        {
            ticket.Stage = ExportStage.ReadyToOpen;
            return [new NoteExport($"Export {exportId}: another import is open in Revit, so this one waits its turn.")];
        }

        return [];
    }

    /// <summary>The import window could not open on the bundle, for a reason the web can show.</summary>
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

    /// <summary>No import holds the window any more.</summary>
    public IReadOnlyList<ExportAction> TurnFree()
    {
        if (Head() is not { Stage: ExportStage.ReadyToOpen } ticket)
        {
            return [];
        }

        ticket.Stage = ExportStage.Opening;
        return [new OpenExportImport(ticket.Export, ticket.ZipPath!)];
    }

    /// <summary>The curator pressed Import.</summary>
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

    /// <summary>The running import has got this far; reported about every 2 seconds.</summary>
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

    /// <summary>The import window's run ended, or the window went before Import.</summary>
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

    /// <summary>
    /// The session left the channel: every export in hand is dropped where it stands, and what was
    /// finished stays finished. A catch-up after the next join picks the dropped ones up again.
    /// </summary>
    public void Reset()
    {
        _tickets.Clear();
        _watching = null;
    }

    private Ticket? Find(string exportId)
        => _tickets.FirstOrDefault(ticket => string.Equals(ticket.Export.Id, exportId, StringComparison.Ordinal));

    // The export being worked: the oldest one not merely sending its last report.
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

    // The web ended it: nothing is sent, and a window still on its checklist goes.
    private void EndQuietly(Ticket ticket, List<ExportAction> actions)
    {
        if (ticket.Stage is ExportStage.Opening or ExportStage.AwaitingOptions)
        {
            actions.Add(new CloseExportImport(ticket.Export.Id));
        }

        Remove(ticket, actions);
        StartHead(actions);
    }

    // No more reports for it; a window already open stays, since the import needs no platform.
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
