using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The next Revit picking back up a Prepare an earlier one was watching when it closed or crashed.
/// </summary>
/// <remarks>
/// Driven through a real record in a temp folder, a scripted vault and scripted Prepare steps, so no
/// request leaves the machine (<c>HPS-42</c>). The crashed Revit is an owner the liveness check says
/// is gone.
/// </remarks>
internal static class PrepareRejoinerTests
{
    private const string Email = "curator@example.com";
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly PrepareOwner Crashed = new(2025, 20_250);
    private static readonly PrepareOwner Self = new(2027, 20_270);

    internal static int Run()
    {
        TestRun run = new();
        string sandbox = Path.Combine(Path.GetTempPath(), "mp-rejoin-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandbox);

        try
        {
            Cases(run, sandbox);
        }
        finally
        {
            try
            {
                Directory.Delete(sandbox, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp folder is not a test failure.
            }
        }

        return run.Report("PrepareRejoiner");
    }

    private static void Cases(TestRun run, string sandbox)
    {
        run.Case("a Prepare a crashed Revit was watching is re-joined, and ends ready with the usual notice", () =>
        {
            Rig rig = new(sandbox, "ready");
            rig.Interrupt("order-1", hoursAgo: 3);

            rig.Rejoin();
            PrepareRun run1 = rig.WaitForEnd();

            run.Equal(run1.OrderId, "order-1", "the order");
            run.True(run1.Rejoined, "a re-join");
            run.Equal(run1.Ending == PrepareEnding.Ready, true, "ready");
            run.True(run1.StartedAt == Now.AddHours(-3), "the original ask's time is kept");
            run.Equal(rig.Steps.Starts, 1, "one start request, never a second build");
            run.True(
                PrepareNotices.For(run1.OrderId, run1.Label, run1.Ending!.Value, run1.Detail, vaultOpen: false, signedIn: true) is not null,
                "announced as any Prepare is");
            run.Equal(rig.Store.Claim(Now).Count, 0, "struck off once it ended");
        });

        run.Case("signed out: nothing is claimed or listed, and the entry waits for a sign-in", () =>
        {
            Rig rig = new(sandbox, "signed-out");
            rig.Interrupt("order-1");
            rig.Source.SignedIn = false;

            rig.Rejoin();

            run.Equal(rig.Source.Lists, 0, "no listing");
            run.Equal(rig.Steps.Starts, 0, "no start");

            rig.Source.SignedIn = true;
            rig.Rejoin();
            run.Equal(rig.WaitForEnd().Ending == PrepareEnding.Ready, true, "re-joined at the sign-in");
        });

        run.Case("nothing interrupted: no listing at all", () =>
        {
            Rig rig = new(sandbox, "empty");

            rig.Rejoin();

            run.Equal(rig.Source.Lists, 0, "the record was empty, so the vault was never asked");
        });

        run.Case("an order no longer in the vault, or no longer available, is dropped without a start", () =>
        {
            Rig rig = new(sandbox, "refused");
            rig.Interrupt("order-gone");
            rig.Interrupt("order-refunded");
            rig.Source.Rows = [new VaultBundle { OrderId = "order-refunded", Status = BundleStatus.Refunded }];

            rig.Rejoin();

            run.Equal(rig.Steps.Starts, 0, "no start request");
            run.Equal(rig.Ended.Count, 0, "no ending, so no notice");
            run.Equal(rig.Store.Claim(Now).Count, 0, "both dropped from the record");
        });

        run.Case("a listing that fails keeps the entry, and the next sign-in re-joins it", () =>
        {
            Rig rig = new(sandbox, "list-fails");
            rig.Interrupt("order-1");
            rig.Source.Error = "The vault could not be reached.";

            rig.Rejoin();
            run.Equal(rig.Steps.Starts, 0, "nothing started");

            rig.Source.Error = null;
            rig.Rejoin();
            run.Equal(rig.WaitForEnd().Ending == PrepareEnding.Ready, true, "re-joined at the next try");
        });

        run.Case("a re-join whose start fails ends interrupted, silently, and is kept", () =>
        {
            Rig rig = new(sandbox, "start-fails", new ScriptedSteps { Start = VaultResult<MaterializeStart>.Failed("The platform is unavailable.") });
            rig.Interrupt("order-1");

            rig.Rejoin();
            PrepareRun ended = rig.WaitForEnd();

            run.Equal(ended.Ending == PrepareEnding.Interrupted, true, "interrupted, not failed");
            run.True(PrepareNotices.For(ended.OrderId, ended.Label, PrepareEnding.Interrupted, ended.Detail, false, true) is null, "no notice");
            run.Equal(rig.Store.Claim(Now).Count, 1, "still in the record for the next sign-in");
        });

        run.Case("a re-join that fails after joining the job is a failure, announced", () =>
        {
            ScriptedSteps steps = new()
            {
                Poll = new PollOutcome(VaultResult<MaterializeStatus>.Failed("The platform could not build this bundle."), OutOfPolls: false),
            };
            Rig rig = new(sandbox, "poll-fails", steps);
            rig.Interrupt("order-1");

            rig.Rejoin();
            PrepareRun ended = rig.WaitForEnd();

            run.Equal(ended.Ending == PrepareEnding.Failed, true, "failed");
            run.True(PrepareNotices.For(ended.OrderId, ended.Label, ended.Ending!.Value, ended.Detail, false, true) is not null, "announced");
            run.Equal(rig.Store.Claim(Now).Count, 0, "struck off");
        });

        run.Case("an order this Revit is already watching is not re-joined over its own Prepare", () =>
        {
            ScriptedSteps steps = new() { Hold = new TaskCompletionSource() };
            Rig rig = new(sandbox, "watching", steps);
            PrepareRun own = rig.Watcher.Prepare(new VaultBundle { OrderId = "order-1", Status = BundleStatus.Available }, out _);
            WaitUntil(() => rig.Store.Claim(Now).Count == 1);

            rig.Rejoin();

            run.Equal(rig.Source.Lists, 0, "nothing interrupted to list for");
            run.Equal(rig.Watcher.Runs.Count, 1, "one watch");
            steps.Hold.SetResult();
            own.Completion.Wait(TimeSpan.FromSeconds(10));
        });

        run.Case("a Prepare Revit was shutting down on is kept for the next Revit", () =>
        {
            ScriptedSteps steps = new() { Hold = new TaskCompletionSource() };
            Rig rig = new(sandbox, "shutdown", steps);
            PrepareRun own = rig.Watcher.Prepare(new VaultBundle { OrderId = "order-1", Status = BundleStatus.Available }, out _);

            rig.Watcher.InterruptAll();
            own.Completion.Wait(TimeSpan.FromSeconds(10));

            run.Equal(own.Ending == PrepareEnding.Interrupted, true, "interrupted, not cancelled");

            // The next Revit: this one is gone.
            InterruptedPrepareStore next = new(rig.Path, new PrepareOwner(9, 9), _ => false, () => Email);
            run.Equal(next.Claim(Now).Count, 1, "left for it");
        });
    }

    private static void WaitUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("the condition never held");
            }

            Thread.Sleep(10);
        }
    }

    /// <summary>A record, a vault, a watcher and a re-joiner, as one Revit that has just started.</summary>
    private sealed class Rig
    {
        internal Rig(string sandbox, string name, ScriptedSteps? steps = null)
        {
            Path = System.IO.Path.Combine(sandbox, name + ".json");
            Steps = steps ?? new ScriptedSteps();
            Store = new InterruptedPrepareStore(Path, Self, owner => owner == Self, () => Email);
            Watcher = new PrepareWatcher(Steps, Store);
            Watcher.Ended += (_, finished) => { lock (Ended) { Ended.Add(finished); } };
            Rejoiner = new PrepareRejoiner(Source, Store, Watcher, () => Now);
        }

        internal string Path { get; }

        internal ScriptedSteps Steps { get; }

        internal ScriptedVault Source { get; } = new();

        internal InterruptedPrepareStore Store { get; }

        internal PrepareWatcher Watcher { get; }

        internal PrepareRejoiner Rejoiner { get; }

        internal List<PrepareRun> Ended { get; } = [];

        /// <summary>A Prepare the crashed Revit was watching, and the order listed as available.</summary>
        internal void Interrupt(string orderId, double hoursAgo = 1)
        {
            new InterruptedPrepareStore(Path, Crashed, _ => false, () => Email).Started(orderId, Now.AddHours(-hoursAgo));
            Source.Rows = [.. Source.Rows, new VaultBundle { OrderId = orderId, AoiLabel = "Harbour Point", Status = BundleStatus.Available }];
        }

        internal void Rejoin() => Rejoiner.RejoinAsync(CancellationToken.None).GetAwaiter().GetResult();

        internal PrepareRun WaitForEnd()
        {
            WaitUntil(() => { lock (Ended) { return Ended.Count > 0; } });
            lock (Ended)
            {
                return Ended[^1];
            }
        }
    }

    private sealed class ScriptedVault : IVaultNewsSource
    {
        public bool SignedIn { get; set; } = true;

        public string? Email { get; set; } = PrepareRejoinerTests.Email;

        public IReadOnlyList<VaultBundle> Rows { get; set; } = [];

        public string? Error { get; set; }

        public int Lists { get; private set; }

        public Task<(VaultListing? Listing, string? Error)> ListAsync(CancellationToken cancellationToken)
        {
            Lists++;
            return Task.FromResult<(VaultListing?, string?)>(
                Error is not null ? (null, Error) : (new VaultListing { Bundles = Rows }, null));
        }
    }

    /// <summary>Prepare steps that succeed unless told otherwise.</summary>
    private sealed class ScriptedSteps : IPrepareSteps
    {
        private int _starts;

        internal VaultResult<MaterializeStart> Start { get; init; }
            = VaultResult<MaterializeStart>.Ok(MaterializeStart.Started("job-1", MaterializeJobs.RevitTokens));

        internal PollOutcome Poll { get; init; } = new(
            VaultResult<MaterializeStatus>.Ok(new MaterializeStatus(MaterializeState.Complete, 1.0, "job-1", string.Empty)),
            OutOfPolls: false);

        internal TaskCompletionSource? Hold { get; init; }

        internal int Starts => _starts;

        public Task<VaultResult<MaterializeStart>> StartAsync(string orderId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _starts);
            return Task.FromResult(Start);
        }

        public async Task<PollOutcome> PollAsync(
            string orderId,
            IReadOnlyCollection<string> requested,
            IProgress<MaterializeStatus> progress,
            CancellationToken cancellationToken)
        {
            if (Hold is { } hold)
            {
                await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return Poll;
        }

        public Task<PreparedDownload> DownloadAsync(VaultBundle bundle, IProgress<string> say, CancellationToken cancellationToken)
            => Task.FromResult(new PreparedDownload(bundle, Ready: true, "Downloaded and verified."));
    }
}
