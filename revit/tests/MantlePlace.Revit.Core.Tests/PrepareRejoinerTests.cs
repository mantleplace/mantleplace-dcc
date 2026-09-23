using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The next Revit picking back up a Prepare an earlier one was watching when it closed or crashed.
/// </summary>
/// <remarks>
/// Driven through a real record in a temp folder, the real background checker over a scripted vault,
/// and scripted Prepare steps, so no request leaves the machine (<c>HPS-42</c>). The crashed Revit is
/// an owner the liveness check says is gone.
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
        run.Case("a Prepare a crashed Revit was watching is re-joined at sign-in, and ends ready with the usual notice", () =>
        {
            Rig rig = new(sandbox, "ready");
            rig.Interrupt("order-1", hoursAgo: 3);

            rig.SignIn();
            PrepareRun run1 = rig.WaitForEnd();

            run.Equal(run1.OrderId, "order-1", "the order");
            run.True(run1.Rejoined, "a re-join");
            run.Equal(run1.Ending == PrepareEnding.Ready, true, "ready");
            run.True(run1.StartedAt == Now.AddHours(-3), "the original ask's time is kept");
            run.Equal(rig.Steps.Starts, 1, "one start request, never a second build");
            run.Equal(rig.Vault.Lists, 1, "one listing: the background checker's own, and no second one for the re-join");
            PrepareNotice? rejoined = PrepareNotices.For(run1.OrderId, run1.Label, run1.Ending!.Value, run1.Detail, vaultOpen: false, signedIn: true);
            PrepareNotice? pressed = PrepareNotices.For("order-1", "Harbour Point", PrepareEnding.Ready, string.Empty, vaultOpen: false, signedIn: true);
            run.True(rejoined is not null && rejoined == pressed, "the same notice as a Prepare pressed this session");
            run.Equal(rig.Store.Claim(Now).Count, 0, "struck off once it ended");
        });

        run.Case("a listing with no sign-in before it re-joins nothing", () =>
        {
            Rig rig = new(sandbox, "unarmed");
            rig.Interrupt("order-1");

            rig.List();

            run.Equal(rig.Steps.Starts, 0, "an ordinary interval listing is not a sign-in");
            run.Equal(rig.Store.Claim(Now).Count, 1, "the entry waits");
        });

        run.Case("signed out: the checker lists nothing, so nothing is re-joined until a sign-in's listing", () =>
        {
            Rig rig = new(sandbox, "signed-out");
            rig.Interrupt("order-1");
            rig.Vault.SignedIn = false;

            rig.SignIn();
            run.Equal(rig.Vault.Lists, 0, "no listing");
            run.Equal(rig.Steps.Starts, 0, "no start");

            rig.Vault.SignedIn = true;
            rig.SignIn();
            run.Equal(rig.WaitForEnd().Ending == PrepareEnding.Ready, true, "re-joined at the sign-in");
        });

        run.Case("with the vault browser open, nothing is listed or re-joined; the next listing after it closes does", () =>
        {
            Rig rig = new(sandbox, "vault-open");
            rig.Interrupt("order-1");
            rig.VaultOpen = true;

            rig.SignIn();
            run.Equal(rig.Vault.Lists, 0, "the window owns the listing (HPS-55)");
            run.Equal(rig.Steps.Starts, 0, "no start");

            rig.VaultOpen = false;
            rig.List();
            run.Equal(rig.WaitForEnd().Ending == PrepareEnding.Ready, true, "still armed from the sign-in, re-joined at the next listing");
        });

        run.Case("a listing that fails leaves it armed, and the next listing re-joins", () =>
        {
            Rig rig = new(sandbox, "list-fails");
            rig.Interrupt("order-1");
            rig.Vault.Error = "The vault could not be reached.";

            rig.SignIn();
            run.Equal(rig.Steps.Starts, 0, "nothing started");

            rig.Vault.Error = null;
            rig.List();
            run.Equal(rig.WaitForEnd().Ending == PrepareEnding.Ready, true, "re-joined at the next listing that worked");
        });

        run.Case("nothing interrupted: the sign-in's listing starts nothing", () =>
        {
            Rig rig = new(sandbox, "empty");

            rig.SignIn();

            run.Equal(rig.Steps.Starts, 0, "the record was empty");
        });

        run.Case("an order no longer in the vault, refunded, or failed is dropped without a start", () =>
        {
            Rig rig = new(sandbox, "refused");
            rig.Interrupt("order-gone", listed: null);
            rig.Interrupt("order-refunded", listed: BundleStatus.Refunded);
            rig.Interrupt("order-failed", listed: BundleStatus.Failed);

            rig.SignIn();

            run.Equal(rig.Steps.Starts, 0, "no start request");
            run.Equal(rig.Ended.Count, 0, "no ending, so no notice");
            run.Equal(rig.Store.Claim(Now).Count, 0, "all three dropped from the record");
        });

        run.Case("an order whose status says nothing yet is kept, without a start", () =>
        {
            Rig rig = new(sandbox, "pending");
            rig.Interrupt("order-1", listed: BundleStatus.RefreshPending);

            rig.SignIn();

            run.Equal(rig.Steps.Starts, 0, "no start");
            run.Equal(rig.Store.Claim(Now).Count, 1, "kept for the next sign-in");
        });

        run.Case("a re-join whose start fails ends interrupted, silently, and waits for the next sign-in", () =>
        {
            Rig rig = new(sandbox, "start-fails", new ScriptedSteps { Start = VaultResult<MaterializeStart>.Failed("The platform is unavailable.") });
            rig.Interrupt("order-1");

            rig.SignIn();
            PrepareRun ended = rig.WaitForEnd();

            run.Equal(ended.Ending == PrepareEnding.Interrupted, true, "interrupted, not failed");
            run.True(PrepareNotices.For(ended.OrderId, ended.Label, PrepareEnding.Interrupted, ended.Detail, false, true) is null, "no notice");

            rig.List();
            run.Equal(rig.Steps.Starts, 1, "not retried at every interval listing: a start is a request too");
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

            rig.SignIn();
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
            rig.Vault.Rows = [new VaultBundle { OrderId = "order-1", Status = BundleStatus.Available }];

            rig.SignIn();

            run.Equal(rig.Steps.Starts, 1, "only its own start");
            run.Equal(rig.Watcher.Runs.Count, 1, "one watch");
            steps.Hold.SetResult();
            own.Completion.Wait(TimeSpan.FromSeconds(10));
        });

        run.Case("stopped for shutdown: an armed listing re-joins nothing", () =>
        {
            Rig rig = new(sandbox, "stopped");
            rig.Interrupt("order-1");

            rig.Rejoiner.Stop();
            rig.SignIn();

            run.Equal(rig.Steps.Starts, 0, "nothing started behind the shutdown");
            run.Equal(rig.Store.Claim(Now).Count, 1, "left for the next Revit");
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

    /// <summary>
    /// A record, a vault, the background checker, a watcher and a re-joiner, wired as the add-in wires
    /// them, in one Revit that has just started.
    /// </summary>
    private sealed class Rig
    {
        internal Rig(string sandbox, string name, ScriptedSteps? steps = null)
        {
            Path = System.IO.Path.Combine(sandbox, name + ".json");
            Steps = steps ?? new ScriptedSteps();
            Store = new InterruptedPrepareStore(Path, Self, owner => owner == Self, () => Email);
            Watcher = new PrepareWatcher(Steps, Store);
            Watcher.Ended += (_, finished) => { lock (Ended) { Ended.Add(finished); } };
            Rejoiner = new PrepareRejoiner(Store, Watcher, () => Now);
            Checker = new VaultNewsChecker(
                Vault, new AnnouncedOrderStore(System.IO.Path.Combine(sandbox, name + "-announced.json")), () => VaultOpen);
            Checker.Listed += (_, listing) => Rejoiner.OnListed(listing);
        }

        internal string Path { get; }

        internal ScriptedSteps Steps { get; }

        internal ScriptedVault Vault { get; } = new();

        internal InterruptedPrepareStore Store { get; }

        internal PrepareWatcher Watcher { get; }

        internal PrepareRejoiner Rejoiner { get; }

        internal VaultNewsChecker Checker { get; }

        internal bool VaultOpen { get; set; }

        internal List<PrepareRun> Ended { get; } = [];

        /// <summary>A Prepare the crashed Revit was watching, and the order's row in the vault.</summary>
        internal void Interrupt(string orderId, double hoursAgo = 1, BundleStatus? listed = BundleStatus.Available)
        {
            new InterruptedPrepareStore(Path, Crashed, _ => false, () => Email).Started(orderId, Now.AddHours(-hoursAgo));
            if (listed is { } status)
            {
                Vault.Rows = [.. Vault.Rows, new VaultBundle { OrderId = orderId, AoiLabel = "Harbour Point", Status = status }];
            }
        }

        /// <summary>What the add-in does on a sign-in: arm, then wake the listing.</summary>
        internal void SignIn()
        {
            Rejoiner.Arm();
            List();
        }

        /// <summary>One background listing, as the checker's loop makes it.</summary>
        internal void List() => Checker.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();

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
