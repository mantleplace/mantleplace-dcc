using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The connected host, without a platform or a Revit: which export it acts on, what it reports
/// next and when it gives up (<see cref="ExportDesk"/>), how it fetches the bundle
/// (<see cref="ExportBundleFetcher"/>), and the wiring between the channel, the routes and the import
/// window (<see cref="ConnectedHostService"/>).
/// </summary>
internal static class ConnectedHostTests
{
    private const string Connection = "revit-0123456789abcdef";
    private const string Order = "ord-1";

    internal static int Run()
    {
        TestRun run = new();
        ReceivingCases(run);
        LifecycleCases(run);
        BuildingCases(run);
        TurnCases(run);
        WebEndsCases(run);
        GivingUpCases(run);
        EndingCases(run);
        SmallRuleCases(run);
        FetcherCases(run);
        ServiceCases(run);
        return run.Report("connected host");
    }

    private static HostExport Row(string id, ExportStatus status, string connection = Connection, string host = "revit", string order = Order)
        => new(id, order, host, connection, "Site", "DESK", "1.0.0", status, null, null, null, "t0", "t0", null);

    private static ExportDesk Desk(Func<DateTimeOffset>? now = null) => new(Connection, now ?? (() => DateTimeOffset.UnixEpoch));

    private static List<T> Of<T>(IReadOnlyList<ExportAction> actions) where T : ExportAction => [.. actions.OfType<T>()];

    private static ExportAdvanceAnswer OkAt(HostExport export, ExportStatus status) => ExportAdvanceAnswer.Ok(export with { Status = status });

    // Seen, then its received answered: the desk now fetches.
    private static ExportDesk Received(string id = "e1", Func<DateTimeOffset>? now = null)
    {
        ExportDesk desk = Desk(now);
        HostExport row = Row(id, ExportStatus.Sent);
        desk.Seen(row);
        desk.Answered(id, OkAt(row, ExportStatus.Received));
        return desk;
    }

    // Through to the window open on its checklist, awaiting_options answered.
    private static ExportDesk Awaiting(string id = "e1", Func<DateTimeOffset>? now = null)
    {
        ExportDesk desk = Received(id, now);
        desk.FetchEnded(id, ExportFetchResult.Ready("C:/cache/bundle.zip", "2.0.0"));
        desk.ImportOpened(id);
        desk.Answered(id, OkAt(Row(id, ExportStatus.Received), ExportStatus.AwaitingOptions));
        return desk;
    }

    private static void ReceivingCases(TestRun run)
    {
        run.Case("a sent export for this connection is reported received at once", () =>
        {
            ExportDesk desk = Desk();
            IReadOnlyList<ExportAction> actions = desk.Seen(Row("e1", ExportStatus.Sent));

            List<AdvanceExport> advances = Of<AdvanceExport>(actions);
            run.Equal(advances.Count, 1, "one report");
            run.Equal(advances[0].Transition.Status, ExportStatus.Received, "received");
            run.Equal(Of<FetchExportBundle>(actions).Count, 0, "and nothing fetched before the platform took it");
        });

        run.Case("an export for another connection, or another host, is left alone", () =>
        {
            ExportDesk desk = Desk();
            run.Equal(desk.Seen(Row("e1", ExportStatus.Sent, connection: "revit-other")).Count, 0, "another connection");
            run.Equal(desk.Seen(Row("e2", ExportStatus.Sent, host: "unreal")).Count, 0, "another host");
            run.Equal(desk.InHand.Count, 0, "nothing held");
        });

        run.Case("an export seen twice is acted on once: from the list and from a change, before and after its answer", () =>
        {
            ExportDesk desk = Desk();
            HostExport row = Row("e1", ExportStatus.Sent);
            desk.Seen(row);
            run.Equal(desk.Seen(row).Count, 0, "the change after the list");
            desk.Answered("e1", OkAt(row, ExportStatus.Received));
            run.Equal(desk.Seen(row with { Status = ExportStatus.Received }).Count, 0, "its own echo");
            run.Equal(desk.Seen(row).Count, 0, "a stale copy");
        });

        run.Case("an export already over is never picked up", () =>
        {
            ExportDesk desk = Desk();
            run.Equal(desk.Seen(Row("e1", ExportStatus.Imported)).Count, 0, "imported");
            run.Equal(desk.Seen(Row("e1", ExportStatus.Sent)).Count, 0, "nor later as sent");
        });

        run.Case("received answered: the bundle is fetched", () =>
        {
            ExportDesk desk = Desk();
            HostExport row = Row("e1", ExportStatus.Sent);
            desk.Seen(row);
            IReadOnlyList<ExportAction> actions = desk.Answered("e1", OkAt(row, ExportStatus.Received));
            run.Equal(Of<FetchExportBundle>(actions).Count, 1, "one fetch");
            run.True(desk.StageOf("e1") == ExportStage.Fetching, "fetching");
        });
    }

    private static void LifecycleCases(TestRun run)
    {
        run.Case("a fetched bundle opens the window, then reports awaiting options", () =>
        {
            ExportDesk desk = Received();
            IReadOnlyList<ExportAction> opened = desk.FetchEnded("e1", ExportFetchResult.Ready("C:/cache/bundle.zip", "2.0.0"));
            List<OpenExportImport> opens = Of<OpenExportImport>(opened);
            run.Equal(opens.Count, 1, "one window");
            run.Equal(opens[0].ZipPath, "C:/cache/bundle.zip", "on the cached zip");
            run.Equal(Of<AdvanceExport>(opened).Count, 0, "not reported before the window is open");

            IReadOnlyList<ExportAction> shown = desk.ImportOpened("e1");
            run.Equal(Of<AdvanceExport>(shown).Single().Transition.Status, ExportStatus.AwaitingOptions, "awaiting options");
        });

        run.Case("Import pressed: importing, then paced progress, then imported", () =>
        {
            DateTimeOffset clock = DateTimeOffset.UnixEpoch;
            ExportDesk desk = Awaiting(now: () => clock);
            HostExport awaiting = Row("e1", ExportStatus.AwaitingOptions);

            AdvanceExport start = Of<AdvanceExport>(desk.ImportBegan("e1")).Single();
            run.Equal(start.Transition.Status, ExportStatus.Importing, "importing");
            run.True(start.Transition.Progress is null, "the first carries no progress");
            desk.Answered("e1", OkAt(awaiting, ExportStatus.Importing));

            clock = clock.AddSeconds(1);
            run.Equal(desk.ImportProgress("e1", 20).Count, 0, "inside 2 seconds, nothing");
            clock = clock.AddSeconds(1.5);
            AdvanceExport progress = Of<AdvanceExport>(desk.ImportProgress("e1", 30)).Single();
            run.True(progress.Transition.Progress == 30, "past 2 seconds, the percent");
            desk.Answered("e1", OkAt(awaiting, ExportStatus.Importing));

            AdvanceExport done = Of<AdvanceExport>(desk.ImportEnded("e1", ImportEnding.Imported, string.Empty)).Single();
            run.Equal(done.Transition.Status, ExportStatus.Imported, "imported");
            desk.Answered("e1", OkAt(awaiting, ExportStatus.Imported));
            run.Equal(desk.InHand.Count, 0, "and it is let go");
        });

        run.Case("progress queued behind a report in flight coalesces to the newest", () =>
        {
            DateTimeOffset clock = DateTimeOffset.UnixEpoch;
            ExportDesk desk = Awaiting(now: () => clock);
            desk.ImportBegan("e1");

            // The importing report is still in flight.
            clock = clock.AddSeconds(3);
            run.Equal(Of<AdvanceExport>(desk.ImportProgress("e1", 10)).Count, 0, "queued, not sent");
            clock = clock.AddSeconds(3);
            desk.ImportProgress("e1", 40);
            AdvanceExport next = Of<AdvanceExport>(desk.Answered("e1", OkAt(Row("e1", ExportStatus.AwaitingOptions), ExportStatus.Importing))).Single();
            run.True(next.Transition.Progress == 40, "the newest goes, the older is dropped");
        });

        run.Case("the window closed before Import reports cancelled", () =>
        {
            ExportDesk desk = Awaiting();
            AdvanceExport cancelled = Of<AdvanceExport>(desk.ImportEnded("e1", ImportEnding.DismissedBeforeStart, string.Empty)).Single();
            run.Equal(cancelled.Transition.Status, ExportStatus.Cancelled, "cancelled");
        });

        run.Case("a dismissal before awaiting options landed queues behind it, in order", () =>
        {
            ExportDesk desk = Received();
            desk.FetchEnded("e1", ExportFetchResult.Ready("z", null));
            desk.ImportOpened("e1");
            run.Equal(Of<AdvanceExport>(desk.ImportEnded("e1", ImportEnding.DismissedBeforeStart, string.Empty)).Count, 0, "held");
            AdvanceExport next = Of<AdvanceExport>(desk.Answered("e1", OkAt(Row("e1", ExportStatus.Received), ExportStatus.AwaitingOptions))).Single();
            run.Equal(next.Transition.Status, ExportStatus.Cancelled, "then sent");
        });
    }

    private static void BuildingCases(TestRun run)
    {
        run.Case("a bundle still building reports waiting for the bundle and watches the order", () =>
        {
            ExportDesk desk = Received();
            IReadOnlyList<ExportAction> actions = desk.FetchBuilding("e1");
            run.Equal(Of<AdvanceExport>(actions).Single().Transition.Status, ExportStatus.WaitingForBundle, "waiting_for_bundle");
            run.Equal(Of<WatchExportOrder>(actions).Single().OrderId, Order, "its order watched");
            run.True(desk.StageOf("e1") == ExportStage.Building, "building");
        });

        run.Case("a build that outlasts the fetch waits for the order row, and goes on by itself when it says ready", () =>
        {
            ExportDesk desk = Received();
            desk.FetchBuilding("e1");
            desk.Answered("e1", OkAt(Row("e1", ExportStatus.Received), ExportStatus.WaitingForBundle));
            desk.FetchEnded("e1", ExportFetchResult.StillBuilding("Still building."));

            run.Equal(desk.OrderChanged("{\"id\":\"ord-1\",\"status\":\"paid\",\"processing_phase\":\"building\"}").Count, 0, "not yet");
            run.Equal(desk.OrderChanged("{\"id\":\"ord-other\",\"status\":\"fulfilled\"}").Count, 0, "another order is not this one");

            IReadOnlyList<ExportAction> ready = desk.OrderChanged("{\"id\":\"ord-1\",\"status\":\"paid\",\"processing_phase\":\"ready\"}");
            run.Equal(Of<FetchExportBundle>(ready).Count, 1, "fetched again");

            IReadOnlyList<ExportAction> fetched = desk.FetchEnded("e1", ExportFetchResult.Ready("z", "2.0.0"));
            run.Equal(Of<StopWatchingExportOrder>(fetched).Count, 1, "the watch is dropped");
            run.Equal(Of<OpenExportImport>(fetched).Count, 1, "and the window opens");
            run.True(desk.WatchedOrder is null, "nothing watched");
        });

        run.Case("a ready row while the fetch still follows the build starts no second fetch", () =>
        {
            ExportDesk desk = Received();
            desk.FetchBuilding("e1");
            run.Equal(desk.OrderChanged("{\"id\":\"ord-1\",\"status\":\"fulfilled\"}").Count, 0, "one fetch at a time");
        });

        run.Case("an order that becomes unavailable fails the export with a reason", () =>
        {
            ExportDesk desk = Received();
            desk.FetchBuilding("e1");
            desk.Answered("e1", OkAt(Row("e1", ExportStatus.Received), ExportStatus.WaitingForBundle));
            AdvanceExport failed = Of<AdvanceExport>(desk.OrderChanged("{\"id\":\"ord-1\",\"status\":\"refunded\"}")).Single();
            run.Equal(failed.Transition.Status, ExportStatus.Failed, "failed");
            run.Equal(failed.Transition.Reason, ExportReasons.BundleUnavailable, "bundle_unavailable");
        });

        run.Case("a bundle outside the reader's range fails as unsupported_version, at the listing or after the download", () =>
        {
            ExportDesk listed = Received();
            AdvanceExport early = Of<AdvanceExport>(listed.FetchEnded("e1", ExportFetchResult.Unreadable("9.0.0"))).Single();
            run.Equal(early.Transition.Reason, ExportReasons.UnsupportedVersion, "from the listing");
            run.Contains(early.Transition.Message, "9.0.0", "naming the version");

            ExportDesk late = Received();
            IReadOnlyList<ExportAction> actions = late.FetchEnded("e1", ExportFetchResult.Ready("z", "9.0.0"));
            run.Equal(Of<OpenExportImport>(actions).Count, 0, "no window");
            run.Equal(Of<AdvanceExport>(actions).Single().Transition.Reason, ExportReasons.UnsupportedVersion, "from the cached bundle");
        });

        run.Case("a fetch that fails reports failed with the fetch's reason and words", () =>
        {
            ExportDesk desk = Received();
            AdvanceExport failed = Of<AdvanceExport>(desk.FetchEnded("e1", ExportFetchResult.Failed(ExportReasons.DownloadFailed, "No link."))).Single();
            run.Equal(failed.Transition.Reason, ExportReasons.DownloadFailed, "the reason");
            run.Equal(failed.Transition.Message, "No link.", "the message");
        });
    }

    private static void TurnCases(TestRun run)
    {
        run.Case("an export arriving while another is open is received, and waits its turn", () =>
        {
            ExportDesk desk = Awaiting("e1");
            HostExport second = Row("e2", ExportStatus.Sent);
            IReadOnlyList<ExportAction> seen = desk.Seen(second);
            run.Equal(Of<AdvanceExport>(seen).Single().Transition.Status, ExportStatus.Received, "acknowledged at once, so the web does not fail it");
            IReadOnlyList<ExportAction> answered = desk.Answered("e2", OkAt(second, ExportStatus.Received));
            run.Equal(Of<FetchExportBundle>(answered).Count, 0, "not fetched while the first holds the turn");
            run.True(desk.StageOf("e2") == ExportStage.Queued, "queued");

            IReadOnlyList<ExportAction> ended = desk.ImportEnded("e1", ImportEnding.DismissedBeforeStart, string.Empty);
            run.Equal(Of<FetchExportBundle>(ended).Single().Export.Id, "e2", "the first ending starts the second");
        });

        run.Case("a window held by an import opened some other way: the export waits, then opens when the turn is free", () =>
        {
            ExportDesk desk = Received();
            desk.FetchEnded("e1", ExportFetchResult.Ready("z", null));
            desk.ImportBusy("e1");
            run.True(desk.StageOf("e1") == ExportStage.ReadyToOpen, "ready, waiting");
            run.Equal(Of<OpenExportImport>(desk.TurnFree()).Single().ZipPath, "z", "opened when free");
        });
    }

    private static void WebEndsCases(TestRun run)
    {
        run.Case("a cancel from the web closes the window, and nothing is sent", () =>
        {
            ExportDesk desk = Awaiting();
            IReadOnlyList<ExportAction> actions = desk.Seen(Row("e1", ExportStatus.Cancelled));
            run.Equal(Of<CloseExportImport>(actions).Single().ExportId, "e1", "closed");
            run.Equal(Of<AdvanceExport>(actions).Count, 0, "nothing reported");
            run.Equal(desk.ImportEnded("e1", ImportEnding.DismissedBeforeStart, string.Empty).Count, 0, "the window's own close after it is ignored");
        });

        run.Case("a cancel from the web while building drops the watch", () =>
        {
            ExportDesk desk = Received();
            desk.FetchBuilding("e1");
            IReadOnlyList<ExportAction> actions = desk.Seen(Row("e1", ExportStatus.Cancelled));
            run.Equal(Of<StopWatchingExportOrder>(actions).Count, 1, "watch dropped");
            run.Equal(desk.FetchEnded("e1", ExportFetchResult.Ready("z", null)).Count, 0, "a late fetch opens nothing");
        });

        run.Case("a refusal because the web moved the export first ends the work on it quietly", () =>
        {
            ExportDesk desk = Received();
            desk.FetchEnded("e1", ExportFetchResult.Ready("z", null));
            desk.ImportOpened("e1");
            IReadOnlyList<ExportAction> actions = desk.Answered(
                "e1", new ExportAdvanceAnswer(ExportAdvanceOutcome.WebMovedFirst, null, "moved", 0));
            run.Equal(Of<CloseExportImport>(actions).Count, 1, "its window closes");
            run.Equal(Of<AdvanceExport>(actions).Count, 0, "and nothing more is sent");
            run.Equal(desk.InHand.Count, 0, "let go");
        });

        run.Case("the web failing a running import stops the reports and leaves the import running", () =>
        {
            ExportDesk desk = Awaiting();
            desk.ImportBegan("e1");
            IReadOnlyList<ExportAction> actions = desk.Seen(Row("e1", ExportStatus.Failed));
            run.Equal(Of<CloseExportImport>(actions).Count, 0, "a running import is not closed");
            run.Equal(desk.InHand.Count, 0, "let go");
        });

        run.Case("our own failed report echoing back does not close anything", () =>
        {
            ExportDesk desk = Received();
            desk.FetchEnded("e1", ExportFetchResult.Failed(ExportReasons.DownloadFailed, "x"));
            run.Equal(desk.Seen(Row("e1", ExportStatus.Failed)).Count, 0, "the echo is ours");
        });
    }

    private static void GivingUpCases(TestRun run)
    {
        run.Case("a 5xx is tried again with a growing wait, then the export is given up", () =>
        {
            ExportDesk desk = Desk();
            desk.Seen(Row("e1", ExportStatus.Sent));
            ExportAdvanceAnswer later = new(ExportAdvanceOutcome.RetryLater, null, "HTTP 500", 0);

            for (int attempt = 1; attempt < ExportDesk.MaxAttempts; attempt++)
            {
                AdvanceExport again = Of<AdvanceExport>(desk.Answered("e1", later)).Single();
                run.Equal(again.Transition.Status, ExportStatus.Received, $"attempt {attempt + 1} resends received");
                run.True(again.Delay == ExportDesk.RetryDelay * attempt, $"after {attempt} x the delay");
            }

            IReadOnlyList<ExportAction> last = desk.Answered("e1", later);
            run.Equal(Of<AdvanceExport>(last).Count, 0, "no more tries");
            run.Equal(desk.InHand.Count, 0, "given up");
        });

        run.Case("a 429 waits as the platform asks", () =>
        {
            ExportDesk desk = Desk();
            desk.Seen(Row("e1", ExportStatus.Sent));
            AdvanceExport again = Of<AdvanceExport>(desk.Answered("e1", new ExportAdvanceAnswer(ExportAdvanceOutcome.RateLimited, null, "slow", 5000))).Single();
            run.True(again.Delay == TimeSpan.FromSeconds(5), "retryAfterMs");
        });

        run.Case("a rejected report gives the export up and leaves its open window alone", () =>
        {
            ExportDesk desk = Received();
            desk.FetchEnded("e1", ExportFetchResult.Ready("z", null));
            desk.ImportOpened("e1");
            IReadOnlyList<ExportAction> actions = desk.Answered("e1", new ExportAdvanceAnswer(ExportAdvanceOutcome.Rejected, null, "400", 0));
            run.Equal(Of<CloseExportImport>(actions).Count, 0, "the import needs no platform");
            run.Equal(desk.InHand.Count, 0, "given up");
        });

        run.Case("an export already past awaiting options with no window here is failed as lost", () =>
        {
            ExportDesk desk = Desk();
            AdvanceExport failed = Of<AdvanceExport>(desk.Seen(Row("e1", ExportStatus.AwaitingOptions))).Single();
            run.Equal(failed.Transition.Reason, ExportReasons.ExportLost, "export_lost");
        });

        run.Case("after the session leaves, an export picked up again at received or waiting goes on from there", () =>
        {
            ExportDesk desk = Received();
            desk.Reset();
            run.Equal(desk.InHand.Count, 0, "dropped");
            IReadOnlyList<ExportAction> again = desk.Seen(Row("e1", ExportStatus.Received));
            run.Equal(Of<AdvanceExport>(again).Count, 0, "not acknowledged twice");
            run.Equal(Of<FetchExportBundle>(again).Count, 1, "fetched");

            ExportDesk waiting = Desk();
            waiting.Seen(Row("e2", ExportStatus.WaitingForBundle));
            run.Equal(Of<AdvanceExport>(waiting.FetchBuilding("e2")).Count, 0, "already waiting: no second waiting report");
        });
    }

    private static void EndingCases(TestRun run)
    {
        run.Case("a run that fails reports failed with the plugin's message", () =>
        {
            ExportDesk desk = Awaiting();
            desk.ImportBegan("e1");
            desk.Answered("e1", OkAt(Row("e1", ExportStatus.AwaitingOptions), ExportStatus.Importing));
            AdvanceExport failed = Of<AdvanceExport>(desk.ImportEnded("e1", ImportEnding.Failed, "The terrain step threw.")).Single();
            run.Equal(failed.Transition.Reason, ExportReasons.ImportFailed, "import_failed");
            run.Equal(failed.Transition.Message, "The terrain step threw.", "its words");
        });

        run.Case("a run cancelled after Import fails as import_cancelled, since cancelled follows only awaiting options", () =>
        {
            ExportDesk desk = Awaiting();
            desk.ImportBegan("e1");
            desk.Answered("e1", OkAt(Row("e1", ExportStatus.AwaitingOptions), ExportStatus.Importing));
            AdvanceExport failed = Of<AdvanceExport>(desk.ImportEnded("e1", ImportEnding.CancelledWhileRunning, string.Empty)).Single();
            run.Equal(failed.Transition.Reason, ExportReasons.ImportCancelled, "import_cancelled");
            run.True(ExportLifecycle.Check(ExportStatus.Importing, failed.Transition) is null, "a move the contract allows");
        });

        run.Case("a window that would not open on the bundle fails the export with its reason", () =>
        {
            ExportDesk desk = Received();
            desk.FetchEnded("e1", ExportFetchResult.Ready("z", null));
            AdvanceExport failed = Of<AdvanceExport>(desk.ImportRefused("e1", ExportReasons.NoDocument, "Open a project.")).Single();
            run.Equal(failed.Transition.Reason, ExportReasons.NoDocument, "no_document");
        });
    }

    private static void SmallRuleCases(TestRun run)
    {
        run.Case("every reason code this plugin sends is a code the contract takes, and none is the web's", () =>
        {
            foreach (string reason in ExportReasons.All)
            {
                run.True(
                    ExportLifecycle.Check(ExportStatus.Received, ExportTransitions.Failed(reason, "m")) is null,
                    $"'{reason}' is a valid failure");
            }
        });

        run.Case("the run's ending is read from its three facts", () =>
        {
            run.Equal(ImportEndings.Of(failed: false, begun: false, wasCancelled: false), ImportEnding.DismissedBeforeStart, "closed unpressed");
            run.Equal(ImportEndings.Of(failed: true, begun: false, wasCancelled: false), ImportEnding.Failed, "failed before it began");
            run.Equal(ImportEndings.Of(failed: false, begun: true, wasCancelled: true), ImportEnding.CancelledWhileRunning, "cancelled");
            run.Equal(ImportEndings.Of(failed: false, begun: true, wasCancelled: false), ImportEnding.Imported, "imported");
        });

        run.Case("the auth session's transitions are one join, re-authorisations, and one leave", () =>
        {
            ConnectedHostEdge edge = new();
            run.Equal(edge.Observe(AuthState.Authenticating), ConnectedHostMove.None, "signing in");
            run.Equal(edge.Observe(AuthState.Authenticated), ConnectedHostMove.Join, "signed in");
            run.Equal(edge.Observe(AuthState.Refreshing), ConnectedHostMove.None, "renewing");
            run.Equal(edge.Observe(AuthState.Authenticated), ConnectedHostMove.Reauthorise, "renewed");
            run.Equal(edge.Observe(AuthState.Unauthenticated), ConnectedHostMove.Leave, "signed out");
            run.Equal(edge.Observe(AuthState.Unauthenticated), ConnectedHostMove.None, "once");
            run.Equal(edge.Observe(AuthState.Failed), ConnectedHostMove.None, "a failed sign-in while out");
        });

        run.Case("an order row is ready, unavailable, or still building", () =>
        {
            run.Equal(OrderRows.Classify("{\"status\":\"fulfilled\"}"), OrderReadiness.Ready, "fulfilled");
            run.Equal(OrderRows.Classify("{\"status\":\"paid\",\"processing_phase\":\"ready\"}"), OrderReadiness.Ready, "paid and ready");
            run.Equal(OrderRows.Classify("{\"status\":\"paid\",\"processing_phase\":\"tiling\"}"), OrderReadiness.Building, "paid and building");
            run.Equal(OrderRows.Classify("{\"status\":\"refunded\"}"), OrderReadiness.Unavailable, "refunded");
            run.Equal(OrderRows.Classify("{\"status\":\"a-word-from-later\"}"), OrderReadiness.Building, "an unknown word keeps waiting");
            run.Equal(OrderRows.Classify("not json"), OrderReadiness.Building, "unreadable keeps waiting");
            run.Equal(OrderRows.IdOf("{\"id\":\"ord-9\"}"), "ord-9", "its id");
        });

        run.Case("a staged run's percent counts finished steps and the one in flight, and never says 100", () =>
        {
            StepRunner runner = new();
            StagedImport staged = new(
                [new ImportStep { Kind = ImportStepKind.ToposurfaceFromPointsFile }, new ImportStep { Kind = ImportStepKind.PublishedContours }],
                runner);
            run.Equal(ImportPercent.Of(staged), 0, "nothing yet");

            int last = 0;
            bool rose = true;
            while (staged.Advance())
            {
                int now = ImportPercent.Of(staged);
                rose &= now >= last;
                last = now;
            }

            run.True(rose, "it never goes down");
            run.Equal(ImportPercent.Of(staged), 99, "all steps ended: 99, imported says the rest");
        });
    }

    private sealed class StepRunner : IImportStepRunner
    {
        public IEnumerable<StepProgress> Run(ImportStep step)
        {
            yield return new StepProgress(1, 2);
            yield return new StepProgress(2, 2);
        }

        public bool Committed(ImportStep step) => true;

        public bool IsStepFailure(Exception exception) => false;

        public void StepStarting(ImportStep step)
        {
        }

        public void StepEnded(ImportStep step, ImportStepState state, Exception? failure)
        {
        }
    }

    private static void FetcherCases(TestRun run)
    {
        run.Case("the fetch refuses an order the vault does not list", () =>
        {
            FakeVault vault = new() { Bundles = [] };
            ExportFetchResult result = Fetch(vault, new FakePrepare(), out _);
            run.Equal(result.Kind, ExportFetchKind.Failed, "failed");
            run.Equal(result.Reason, ExportReasons.BundleNotInVault, "bundle_not_in_vault");
        });

        run.Case("the fetch checks the manifest version before a byte is downloaded", () =>
        {
            FakeVault vault = new() { Bundles = [Bundle("9.0.0")] };
            ExportFetchResult result = Fetch(vault, new FakePrepare(), out _);
            run.Equal(result.Kind, ExportFetchKind.Unreadable, "unreadable");
            run.Equal(vault.Routed, 0, "no route asked");
        });

        run.Case("a valid cached copy is used as it is", () =>
        {
            FakeVault vault = new() { Bundles = [Bundle("2.0.0")], Cached = "C:/cache/ord-1/bundle.zip" };
            ExportFetchResult result = Fetch(vault, new FakePrepare(), out bool building);
            run.Equal(result.Kind, ExportFetchKind.Ready, "ready");
            run.Equal(result.ZipPath, "C:/cache/ord-1/bundle.zip", "the cached zip");
            run.Equal(vault.Routed, 0, "no route asked");
            run.False(building, "no wait");
        });

        run.Case("a complete order's download lands in the cache and is ready", () =>
        {
            FakeVault vault = new() { Bundles = [Bundle("2.0.0")], Route = new ImportRouteDecision(ImportRoute.DownloadView, string.Empty, []) };
            vault.OnDownload = () => vault.Cached = "C:/cache/ord-1/bundle.zip";
            ExportFetchResult result = Fetch(vault, new FakePrepare(), out bool building);
            run.Equal(result.Kind, ExportFetchKind.Ready, "ready");
            run.False(building, "no wait");
        });

        run.Case("an order still building says so at once, follows the build, and is ready when it lands", () =>
        {
            FakeVault vault = new() { Bundles = [Bundle(null)], Route = new ImportRouteDecision(ImportRoute.Prepare, string.Empty, null) };
            FakePrepare prepare = new() { OnDownload = () => vault.Cached = "C:/cache/ord-1/bundle.zip" };
            ExportFetchResult result = Fetch(vault, prepare, out bool building);
            run.True(building, "building said");
            run.Equal(prepare.Polled, 1, "the build followed");
            run.Equal(result.Kind, ExportFetchKind.Ready, "ready");
            run.Equal(result.ManifestVersion, "2.0.0", "with the built bundle's version");
        });

        run.Case("a build that outlasts the poll's budget is still building", () =>
        {
            FakeVault vault = new() { Bundles = [Bundle(null)], Route = new ImportRouteDecision(ImportRoute.Prepare, string.Empty, null) };
            FakePrepare prepare = new() { OutOfPolls = true };
            ExportFetchResult result = Fetch(vault, prepare, out _);
            run.Equal(result.Kind, ExportFetchKind.StillBuilding, "still building");
        });

        run.Case("a refused route is a failed download with the platform's words", () =>
        {
            FakeVault vault = new() { Bundles = [Bundle("2.0.0")], Route = ImportRouteDecision.Refuse("Not yours.") };
            ExportFetchResult result = Fetch(vault, new FakePrepare(), out _);
            run.Equal(result.Reason, ExportReasons.DownloadFailed, "download_failed");
            run.Equal(result.Message, "Not yours.", "its words");
        });
    }

    private static VaultBundle Bundle(string? manifestVersion)
        => new() { OrderId = Order, Status = BundleStatus.Available, ManifestVersion = manifestVersion };

    private static ExportFetchResult Fetch(FakeVault vault, FakePrepare prepare, out bool building)
    {
        bool said = false;
        ExportFetchResult result = new ExportBundleFetcher(vault, prepare)
            .FetchAsync(Order, () => said = true, CancellationToken.None).GetAwaiter().GetResult();
        building = said;
        return result;
    }

    private sealed class FakeVault : IExportVault
    {
        public IReadOnlyList<VaultBundle> Bundles { get; set; } = [];

        public string? Cached { get; set; }

        public ImportRouteDecision Route { get; set; } = ImportRouteDecision.Refuse("no route set");

        public Action? OnDownload { get; set; }

        public int Routed { get; private set; }

        public Task<(VaultListing? Listing, string? Error)> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<(VaultListing?, string?)>((new VaultListing { Bundles = Bundles }, null));

        public string? CachedZip(VaultBundle bundle) => Cached;

        public Task<ImportRouteDecision> RouteImportAsync(string orderId, CancellationToken cancellationToken)
        {
            Routed++;
            return Task.FromResult(Route);
        }

        public Task<CompleteDownload> DownloadCompleteAsync(VaultBundle bundle, IReadOnlyCollection<string>? delivered, CancellationToken cancellationToken)
        {
            OnDownload?.Invoke();
            return Task.FromResult(new CompleteDownload(Cached is not null, false, string.Empty, bundle, ViewFetch.View));
        }
    }

    private sealed class FakePrepare : IPrepareSteps
    {
        public bool OutOfPolls { get; set; }

        public Action? OnDownload { get; set; }

        public int Polled { get; private set; }

        public Task<VaultResult<MaterializeStart>> StartAsync(string orderId, CancellationToken cancellationToken)
            => Task.FromResult(VaultResult<MaterializeStart>.Ok(new MaterializeStart(MaterializeStartOutcome.Started, "job-1", ["elevation.surface_tin"])));

        public Task<PollOutcome> PollAsync(string orderId, IReadOnlyCollection<string> requested, IProgress<MaterializeStatus> progress, CancellationToken cancellationToken)
        {
            Polled++;
            MaterializeStatus done = new(MaterializeState.Complete, 1.0, "job-1", string.Empty, [.. requested], []);
            return Task.FromResult(OutOfPolls
                ? new PollOutcome(VaultResult<MaterializeStatus>.Failed("Still going."), true)
                : new PollOutcome(VaultResult<MaterializeStatus>.Ok(done), false));
        }

        public Task<PreparedDownload> DownloadAsync(VaultBundle bundle, IProgress<string> say, CancellationToken cancellationToken)
        {
            OnDownload?.Invoke();
            VaultBundle built = new() { OrderId = bundle.OrderId, Status = BundleStatus.Available, ManifestVersion = "2.0.0" };
            return Task.FromResult(new PreparedDownload(built, true, string.Empty));
        }
    }

    private static void ServiceCases(TestRun run)
    {
        run.Case("joining starts a channel; its SUBSCRIBED lists the exports and a sent one is received", () =>
        {
            Rig rig = new();
            run.True(rig.Service.Join("token", Presence()), "joined");
            run.Equal(rig.Channels.Count, 1, "one channel");
            rig.Routes.Listed = [Row("e1", ExportStatus.Sent)];
            rig.Channels[0].RaiseSubscribed();

            run.True(WaitFor(() => rig.Routes.Advanced.Count >= 1), "a report went");
            run.Equal(rig.Routes.Advanced[0].Status, ExportStatus.Received, "received");
        });

        run.Case("a row change and the catch-up of the same export act on it once", () =>
        {
            Rig rig = new();
            rig.Service.Join("token", Presence());
            rig.Routes.Listed = [Row("e1", ExportStatus.Sent)];
            rig.Channels[0].RaiseRow(new RealtimeRowChange(
                "bundle_exports",
                "INSERT",
                "{\"id\":\"e1\",\"order_id\":\"ord-1\",\"host\":\"revit\",\"target_connection_id\":\"" + Connection + "\",\"status\":\"sent\"}",
                "{}"));
            rig.Channels[0].RaiseSubscribed();

            run.True(WaitFor(() => rig.Routes.ListCalls == 1 && rig.Routes.Advanced.Count >= 1), "both arrived");
            Thread.Sleep(100);
            run.Equal(rig.Routes.Advanced.Count(t => t.Status == ExportStatus.Received), 1, "received once");
        });

        run.Case("a fetched bundle asks the host for the window, and the window's reports reach the routes", () =>
        {
            Rig rig = new();
            rig.Service.Join("token", Presence());
            rig.Routes.Listed = [Row("e1", ExportStatus.Sent)];
            rig.Channels[0].RaiseSubscribed();

            run.True(WaitFor(() => rig.Host.Opened.Count == 1), "the window was asked for");
            run.Equal(rig.Host.Opened[0], "C:/cache/bundle.zip", "on the fetched zip");

            rig.Service.ImportOpened("e1");
            run.True(WaitFor(() => rig.Routes.Advanced.Any(t => t.Status == ExportStatus.AwaitingOptions)), "awaiting options reported");
            rig.Service.ImportBegan("e1");
            run.True(WaitFor(() => rig.Routes.Advanced.Any(t => t.Status == ExportStatus.Importing)), "importing reported");
            rig.Service.ImportEnded("e1", ImportEnding.Imported, string.Empty);
            run.True(WaitFor(() => rig.Routes.Advanced.Any(t => t.Status == ExportStatus.Imported)), "imported reported");
        });

        run.Case("a host whose window is held makes the export wait, and the turn coming free opens it", () =>
        {
            Rig rig = new();
            rig.Host.Busy = true;
            rig.Service.Join("token", Presence());
            rig.Routes.Listed = [Row("e1", ExportStatus.Sent)];
            rig.Channels[0].RaiseSubscribed();

            run.True(WaitFor(() => rig.Host.Asked >= 1), "asked once");
            Thread.Sleep(50);
            run.Equal(rig.Host.Opened.Count, 0, "not opened while held");
            rig.Host.Busy = false;
            rig.Service.TurnFree();
            run.Equal(rig.Host.Opened.Count, 1, "opened when free");
        });

        run.Case("leaving stops the channel, and an answer that lands after it is ignored", () =>
        {
            Rig rig = new();
            rig.Service.Join("token", Presence());
            rig.Routes.Hold = new TaskCompletionSource();
            rig.Routes.Listed = [Row("e1", ExportStatus.Sent)];
            rig.Channels[0].RaiseSubscribed();
            run.True(WaitFor(() => rig.Routes.Advanced.Count == 1), "a report in flight");

            rig.Service.LeaveAsync().GetAwaiter().GetResult();
            run.True(rig.Channels[0].Stopped, "the channel stopped");
            run.False(rig.Service.IsJoined, "not joined");
            rig.Routes.Hold.SetResult();
            Thread.Sleep(100);
            run.Equal(rig.Host.Asked, 0, "the late answer fetched and opened nothing");

            run.True(rig.Service.Join("token", Presence()), "a second join");
            run.Equal(rig.Channels.Count, 2, "makes a fresh channel");
        });

        run.Case("a document change re-tracks presence on the running channel", () =>
        {
            Rig rig = new();
            rig.Service.Join("token", Presence());
            rig.Service.Announce(ConnectedHosts.Announce("2026", "1.0.0", null, "DESK"));
            run.Equal(rig.Channels[0].Tracked.Count, 1, "tracked again");
            run.True(rig.Channels[0].Tracked[0].Document is null, "with no document");
        });
    }

    private static ConnectedHost Presence() => ConnectedHosts.Announce("2026", "1.0.0", "Site", "DESK");

    private static bool WaitFor(Func<bool> condition)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return condition();
    }

    private sealed class Rig
    {
        public Rig()
        {
            Service = new ConnectedHostService(
                Connection,
                () =>
                {
                    FakeChannel channel = new();
                    lock (Channels)
                    {
                        Channels.Add(channel);
                    }

                    return channel;
                },
                Routes,
                (_, _, _) => Task.FromResult(ExportFetchResult.Ready("C:/cache/bundle.zip", "2.0.0")),
                _ => { });
            Service.Attach(Host);
        }

        public List<FakeChannel> Channels { get; } = [];

        public FakeRoutes Routes { get; } = new();

        public FakeHost Host { get; } = new();

        public ConnectedHostService Service { get; }
    }

    private sealed class FakeChannel : IConnectedChannel
    {
        public event EventHandler? Subscribed;

        public event EventHandler<RealtimeRowChange>? RowChanged;

        public List<ConnectedHost> Tracked { get; } = [];

        public bool Stopped { get; private set; }

        public bool Start(string accessToken, ConnectedHost presence) => true;

        public void SetAuth(string accessToken)
        {
        }

        public void Track(ConnectedHost presence) => Tracked.Add(presence);

        public void WatchOrder(string orderId)
        {
        }

        public void StopWatchingOrder()
        {
        }

        public Task StopAsync()
        {
            Stopped = true;
            return Task.CompletedTask;
        }

        public void RaiseSubscribed() => Subscribed?.Invoke(this, EventArgs.Empty);

        public void RaiseRow(RealtimeRowChange change) => RowChanged?.Invoke(this, change);
    }

    private sealed class FakeRoutes : IExportRoutes
    {
        private readonly object _gate = new();
        private readonly List<ExportTransition> _advanced = [];

        public IReadOnlyList<HostExport> Listed { get; set; } = [];

        public TaskCompletionSource? Hold { get; set; }

        public int ListCalls { get; private set; }

        public IReadOnlyList<ExportTransition> Advanced
        {
            get
            {
                lock (_gate)
                {
                    return [.. _advanced];
                }
            }
        }

        public Task<ExportCall<IReadOnlyList<HostExport>>> ListAsync(string connectionId, CancellationToken cancellationToken)
        {
            ListCalls++;
            return Task.FromResult(new ExportCall<IReadOnlyList<HostExport>>(ExportCallOutcome.Ok, Listed, string.Empty, 200, string.Empty, 0));
        }

        public async Task<ExportCall<HostExport>> AdvanceAsync(HostExport current, string connectionId, ExportTransition transition, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _advanced.Add(transition);
            }

            if (Hold is { } hold)
            {
                await hold.Task.ConfigureAwait(false);
            }

            return new ExportCall<HostExport>(ExportCallOutcome.Ok, current with { Status = transition.Status }, string.Empty, 200, string.Empty, 0);
        }
    }

    private sealed class FakeHost : IExportImportHost
    {
        public bool Busy { get; set; }

        public int Asked { get; private set; }

        public List<string> Opened { get; } = [];

        public bool RequestOpen(HostExport export, string zipPath)
        {
            Asked++;
            if (Busy)
            {
                return false;
            }

            Opened.Add(zipPath);
            return true;
        }

        public void Close(string exportId)
        {
        }
    }
}
