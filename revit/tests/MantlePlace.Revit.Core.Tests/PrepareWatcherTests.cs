using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The session-wide Prepare: start → poll → re-list → download, owned by nothing that closes.
/// </summary>
/// <remarks>
/// <para>
/// It used to live in the vault window. Closing the window did not stop it — it ran on with no owner,
/// reporting to a status line nobody could see — and reopening the window knew nothing of it, so a
/// second Prepare started a second poll of the same order. The watcher is what outlives the window,
/// and the one thing that can say an order is already being watched.
/// </para>
/// <para>
/// Driven through <see cref="IPrepareSteps"/> fakes, so no request leaves the machine (<c>HPS-42</c>).
/// </para>
/// </remarks>
internal static class PrepareWatcherTests
{
    private static readonly VaultBundle Bundle = new() { OrderId = "order-1", AoiLabel = "Harbour Point" };

    internal static int Run()
    {
        TestRun run = new();

        run.Case("a started Prepare polls, downloads, and ends ready", () =>
        {
            FakeSteps steps = new();
            PrepareWatcher watcher = new(steps);
            List<PrepareRun> ended = [];
            watcher.Ended += (_, finished) => ended.Add(finished);

            PrepareRun prepared = watcher.Prepare(Bundle, out bool joined);
            Wait(prepared);

            run.False(joined, "nothing was being watched");
            run.Equal(prepared.Ending == PrepareEnding.Ready, true, "ready");
            run.Equal(steps.Starts, 1, "one start");
            run.Equal(steps.Polls, 1, "one poll loop");
            run.Equal(steps.Downloads, 1, "one download");
            run.Equal(ended.Count, 1, "ended once");
            run.True(watcher.Watching(Bundle.OrderId) is null, "no longer watched once it has ended");
            run.Equal(prepared.LastMessage, "Downloaded and verified.", "the download's own verdict is the last line");
        });

        run.Case("nothing to build: no poll, straight to the download", () =>
        {
            FakeSteps steps = new() { Start = VaultResult<MaterializeStart>.Ok(MaterializeStart.NothingToDo(MaterializeJobs.RevitTokens)) };
            PrepareRun prepared = new PrepareWatcher(steps).Prepare(Bundle, out _);
            Wait(prepared);

            run.Equal(steps.Polls, 0, "no job, so nothing to poll");
            run.Equal(prepared.Ending == PrepareEnding.Ready, true, "ready");
        });

        run.Case("a poll that runs out of budget is still preparing, and nothing is downloaded", () =>
        {
            FakeSteps steps = new() { Poll = new PollOutcome(VaultResult<MaterializeStatus>.Failed("taking longer"), OutOfPolls: true) };
            PrepareRun prepared = new PrepareWatcher(steps).Prepare(Bundle, out _);
            Wait(prepared);

            run.Equal(prepared.Ending == PrepareEnding.StillPreparing, true, "still preparing, not failed");
            run.Equal(steps.Downloads, 0, "nothing to download");
        });

        run.Case("a failed poll is a failed Prepare, with its reason", () =>
        {
            FakeSteps steps = new() { Poll = new PollOutcome(VaultResult<MaterializeStatus>.Failed("The platform could not build this bundle."), OutOfPolls: false) };
            PrepareRun prepared = new PrepareWatcher(steps).Prepare(Bundle, out _);
            Wait(prepared);

            run.Equal(prepared.Ending == PrepareEnding.Failed, true, "failed");
            run.Equal(prepared.Detail, "The platform could not build this bundle.", "the reason is kept for the notice");
        });

        run.Case("a refused start is a failed Prepare", () =>
        {
            FakeSteps steps = new() { Start = VaultResult<MaterializeStart>.Failed("This order was refunded.") };
            PrepareRun prepared = new PrepareWatcher(steps).Prepare(Bundle, out _);
            Wait(prepared);

            run.Equal(prepared.Ending == PrepareEnding.Failed, true, "failed");
            run.Equal(steps.Polls, 0, "no poll after a refusal");
        });

        run.Case("a download that did not land a valid zip is a failed Prepare", () =>
        {
            FakeSteps steps = new() { Download = b => new PreparedDownload(b, Ready: false, "The download did not match its digest.") };
            PrepareRun prepared = new PrepareWatcher(steps).Prepare(Bundle, out _);
            Wait(prepared);

            run.Equal(prepared.Ending == PrepareEnding.Failed, true, "failed");
            run.Equal(prepared.Detail, "The download did not match its digest.", "the cache's verdict is the reason");
        });

        run.Case("a Prepare on an order already being watched joins that watch", () =>
        {
            FakeSteps steps = new() { Hold = new TaskCompletionSource() };
            PrepareWatcher watcher = new(steps);

            PrepareRun first = watcher.Prepare(Bundle, out bool firstJoined);
            PrepareRun second = watcher.Prepare(Bundle, out bool secondJoined);

            run.False(firstJoined, "the first starts");
            run.True(secondJoined, "the second joins");
            run.True(ReferenceEquals(first, second), "one run");
            run.True(ReferenceEquals(watcher.Watching(Bundle.OrderId), first), "and it is what the watcher says it is watching");

            steps.Hold.SetResult();
            Wait(first);
            run.Equal(steps.Starts, 1, "one start request, so one poll loop");
        });

        run.Case("after a Prepare ends, the next one starts afresh", () =>
        {
            FakeSteps steps = new();
            PrepareWatcher watcher = new(steps);
            Wait(watcher.Prepare(Bundle, out _));

            PrepareRun again = watcher.Prepare(Bundle, out bool joined);
            Wait(again);

            run.False(joined, "nothing to join");
            run.Equal(steps.Starts, 2, "a second start");
        });

        run.Case("a cancelled Prepare ends cancelled and stops being watched", () =>
        {
            FakeSteps steps = new() { Hold = new TaskCompletionSource() };
            PrepareWatcher watcher = new(steps);
            PrepareRun prepared = watcher.Prepare(Bundle, out _);

            prepared.Cancel();
            Wait(prepared);

            run.Equal(prepared.Ending == PrepareEnding.Cancelled, true, "cancelled");
            run.Equal(steps.Downloads, 0, "nothing half-downloaded");
            run.True(watcher.Watching(Bundle.OrderId) is null, "not watched");
        });

        run.Case("interrupt-all stops every watch, as shutdown needs, and ends each interrupted", () =>
        {
            FakeSteps steps = new() { Hold = new TaskCompletionSource() };
            PrepareWatcher watcher = new(steps);
            PrepareRun one = watcher.Prepare(Bundle, out _);
            PrepareRun two = watcher.Prepare(new VaultBundle { OrderId = "order-2" }, out _);

            run.Equal(watcher.Runs.Count, 2, "two watched");
            watcher.InterruptAll();
            Wait(one);
            Wait(two);

            run.Equal(one.Ending == PrepareEnding.Interrupted && two.Ending == PrepareEnding.Interrupted, true, "both interrupted, not cancelled");
            run.Equal(watcher.Runs.Count, 0, "none watched");
        });

        run.Case("an interrupted step that returns a failure still ends interrupted, and stays in the ledger", () =>
        {
            FakeSteps steps = new() { Hold = new TaskCompletionSource(), FailOnCancel = true };
            FakeLedger ledger = new();
            PrepareWatcher watcher = new(steps, ledger);
            PrepareRun prepared = watcher.Prepare(Bundle, out _);

            watcher.InterruptAll();
            Wait(prepared);

            run.Equal(prepared.Ending == PrepareEnding.Interrupted, true, "interrupted, whatever the step made of the cancel");
            run.Equal(ledger.Lines, "started order-1|ended order-1 Interrupted", "written at the start, told of the interruption");
        });

        run.Case("the ledger hears every Prepare start and end, in order", () =>
        {
            FakeLedger ledger = new();
            PrepareRun prepared = new PrepareWatcher(new FakeSteps(), ledger).Prepare(Bundle, out _);
            Wait(prepared);

            run.Equal(ledger.Lines, "started order-1|ended order-1 Ready", "start, then end");
        });

        run.Case("a ledger that throws costs nothing but the re-join", () =>
        {
            PrepareRun prepared = new PrepareWatcher(new FakeSteps(), new FakeLedger { Throws = true }).Prepare(Bundle, out _);
            Wait(prepared);

            run.Equal(prepared.Ending == PrepareEnding.Ready, true, "still ready");
        });

        run.Case("a re-join joins a watch already on the order, and keeps the original ask's time", () =>
        {
            FakeSteps steps = new() { Hold = new TaskCompletionSource() };
            PrepareWatcher watcher = new(steps);
            DateTimeOffset asked = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

            PrepareRun rejoined = watcher.Rejoin(Bundle, asked);
            PrepareRun pressed = watcher.Prepare(Bundle, out bool joined);

            run.True(rejoined.Rejoined && rejoined.StartedAt == asked, "a re-join, from the old ask");
            run.True(joined && ReferenceEquals(rejoined, pressed), "pressing Prepare follows it");
            steps.Hold.SetResult();
            Wait(rejoined);
        });

        run.Case("a re-join whose start fails ends interrupted; a pressed Prepare's ends failed", () =>
        {
            FakeSteps steps = new() { Start = VaultResult<MaterializeStart>.Failed("The platform is unavailable.") };
            PrepareRun rejoined = new PrepareWatcher(steps).Rejoin(Bundle, DateTimeOffset.UtcNow);
            PrepareRun pressed = new PrepareWatcher(steps).Prepare(Bundle, out _);
            Wait(rejoined);
            Wait(pressed);

            run.Equal(rejoined.Ending == PrepareEnding.Interrupted, true, "re-join: interrupted, to be tried again");
            run.Equal(pressed.Ending == PrepareEnding.Failed, true, "pressed: failed, as before");
        });

        run.Case("a step that throws ends the Prepare failed rather than leaving it watched forever", () =>
        {
            FakeSteps steps = new() { Throw = new InvalidOperationException("disk full") };
            PrepareWatcher watcher = new(steps);
            PrepareRun prepared = watcher.Prepare(Bundle, out _);
            Wait(prepared);

            run.Equal(prepared.Ending == PrepareEnding.Failed, true, "failed");
            run.Contains(prepared.Detail, "disk full", "the reason survives");
            run.True(watcher.Watching(Bundle.OrderId) is null, "not watched");
        });

        run.Case("the run carries the re-listed bundle, whose integrity facts the row needs", () =>
        {
            VaultBundle relisted = new() { OrderId = Bundle.OrderId, AoiLabel = Bundle.AoiLabel, Sha256 = new string('a', 64) };
            FakeSteps steps = new() { Download = _ => new PreparedDownload(relisted, Ready: true, "Downloaded and verified.") };
            PrepareRun prepared = new PrepareWatcher(steps).Prepare(Bundle, out _);
            Wait(prepared);

            run.True(ReferenceEquals(prepared.Bundle, relisted), "the re-listed row, not the one Prepare was pressed on");
        });

        run.Case("every line said is raised, in order, and the label names the bundle", () =>
        {
            FakeSteps steps = new();
            PrepareWatcher watcher = new(steps);
            List<string> said = [];
            watcher.Said += (_, speaking) => { lock (said) { said.Add(speaking.LastMessage); } };

            PrepareRun prepared = watcher.Prepare(Bundle, out _);
            Wait(prepared);

            run.Equal(prepared.Label, "Harbour Point", "the label");
            run.Equal(said.Count > 0 ? said[0] : null, "Preparing Harbour Point…", "the first line");
            run.Equal(said[^1], "Downloaded and verified.", "the last line");
        });

        return run.Report("PrepareWatcher");
    }

    private static void Wait(PrepareRun prepared)
    {
        if (!prepared.Completion.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("the Prepare never ended");
        }
    }

    /// <summary>Scripted steps. Every step succeeds unless told otherwise.</summary>
    private sealed class FakeSteps : IPrepareSteps
    {
        private int _starts;
        private int _polls;
        private int _downloads;

        internal VaultResult<MaterializeStart> Start { get; init; }
            = VaultResult<MaterializeStart>.Ok(MaterializeStart.Started("job-1", MaterializeJobs.RevitTokens));

        internal PollOutcome Poll { get; init; } = new(
            VaultResult<MaterializeStatus>.Ok(new MaterializeStatus(MaterializeState.Complete, 1.0, "job-1", string.Empty)),
            OutOfPolls: false);

        internal Func<VaultBundle, PreparedDownload> Download { get; init; }
            = bundle => new PreparedDownload(bundle, Ready: true, "Downloaded and verified.");

        /// <summary>When set, the poll waits on it — a job still building.</summary>
        internal TaskCompletionSource? Hold { get; init; }

        internal Exception? Throw { get; init; }

        /// <summary>When set, a cancelled poll returns a failure rather than throwing.</summary>
        internal bool FailOnCancel { get; init; }

        internal int Starts => _starts;

        internal int Polls => _polls;

        internal int Downloads => _downloads;

        public Task<VaultResult<MaterializeStart>> StartAsync(string orderId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _starts);
            return Throw is { } thrown ? Task.FromException<VaultResult<MaterializeStart>>(thrown) : Task.FromResult(Start);
        }

        public async Task<PollOutcome> PollAsync(
            string orderId,
            IReadOnlyCollection<string> requested,
            IProgress<MaterializeStatus> progress,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _polls);
            if (Hold is { } hold)
            {
                try
                {
                    await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (FailOnCancel)
                {
                    return new PollOutcome(VaultResult<MaterializeStatus>.Failed("The request was cancelled."), OutOfPolls: false);
                }
            }

            return Poll;
        }

        public Task<PreparedDownload> DownloadAsync(VaultBundle bundle, IProgress<string> say, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _downloads);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Download(bundle));
        }
    }

    /// <summary>Writes down what the watcher tells the ledger.</summary>
    private sealed class FakeLedger : IPrepareLedger
    {
        private readonly List<string> _lines = [];

        internal bool Throws { get; init; }

        internal string Lines
        {
            get
            {
                lock (_lines)
                {
                    return string.Join('|', _lines);
                }
            }
        }

        public void Started(string orderId, DateTimeOffset startedAt) => Write($"started {orderId}");

        public void Ended(string orderId, PrepareEnding ending) => Write($"ended {orderId} {ending}");

        private void Write(string line)
        {
            if (Throws)
            {
                throw new IOException("the record is locked");
            }

            lock (_lines)
            {
                _lines.Add(line);
            }
        }
    }
}
