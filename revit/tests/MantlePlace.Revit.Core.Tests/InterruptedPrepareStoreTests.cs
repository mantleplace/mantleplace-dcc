using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The one per-machine record of Prepares being watched, which a Revit that closed or crashed leaves
/// behind for the next one to re-join.
/// </summary>
/// <remarks>
/// Two stores on one file stand in for two Revit processes; each is told its own identity and which
/// owners are alive, so a crash is a process that says it is gone.
/// </remarks>
internal static class InterruptedPrepareStoreTests
{
    private const string Email = "curator@example.com";
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly PrepareOwner Revit2025 = new(2025, 20_250);
    private static readonly PrepareOwner Revit2027 = new(2027, 20_270);

    internal static int Run()
    {
        TestRun run = new();
        string sandbox = Path.Combine(Path.GetTempPath(), "mp-interrupted-tests-" + Guid.NewGuid().ToString("N")[..8]);
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

        return run.Report("interrupted-Prepare store");
    }

    private static void Cases(TestRun run, string sandbox)
    {
        run.Case("an entry written at the start outlives a crash, and the next Revit claims it", () =>
        {
            string path = Path.Combine(sandbox, "crash.json");
            Store(path, Revit2025, alive: []).Started("order-1", Now.AddHours(-2));

            // Revit 2025 crashed: nothing ended the Prepare, and its process is gone.
            IReadOnlyList<InterruptedPrepare> claimed = Store(path, Revit2027, alive: [Revit2027]).Claim(Now);

            run.Equal(claimed.Count, 1, "claimed");
            run.Equal(claimed[0].OrderId, "order-1", "the order");
            run.True(claimed[0].StartedAt == Now.AddHours(-2), "the ask's own time survives the round trip");
        });

        run.Case("every ending but an interruption strikes the entry off", () =>
        {
            foreach (PrepareEnding ending in new[] { PrepareEnding.Ready, PrepareEnding.Failed, PrepareEnding.StillPreparing, PrepareEnding.Cancelled })
            {
                string path = Path.Combine(sandbox, $"ended-{ending}.json");
                InterruptedPrepareStore revit = Store(path, Revit2025, alive: []);
                revit.Started("order-1", Now);
                revit.Ended("order-1", ending);

                run.Equal(Store(path, Revit2027, alive: [Revit2027]).Claim(Now).Count, 0, $"{ending}: nothing to re-join");
            }
        });

        run.Case("an interrupted ending leaves the entry for the next Revit", () =>
        {
            string path = Path.Combine(sandbox, "interrupted.json");
            InterruptedPrepareStore revit = Store(path, Revit2025, alive: []);
            revit.Started("order-1", Now);
            revit.Ended("order-1", PrepareEnding.Interrupted);

            run.Equal(Store(path, Revit2027, alive: [Revit2027]).Claim(Now).Count, 1, "re-joined");
        });

        run.Case("a Revit still running keeps its Prepare from the one that just opened", () =>
        {
            string path = Path.Combine(sandbox, "live.json");
            Store(path, Revit2025, alive: [Revit2025, Revit2027]).Started("order-1", Now);

            run.Equal(Store(path, Revit2027, alive: [Revit2025, Revit2027]).Claim(Now).Count, 0, "left to Revit 2025");
        });

        run.Case("two Revits claiming one crashed entry: exactly one takes it", () =>
        {
            string path = Path.Combine(sandbox, "race.json");
            Store(path, new PrepareOwner(1, 1), alive: []).Started("order-1", Now);

            // Each sees the other alive, as two Revits open side by side do.
            InterruptedPrepareStore first = Store(path, Revit2025, alive: [Revit2025, Revit2027]);
            InterruptedPrepareStore second = Store(path, Revit2027, alive: [Revit2025, Revit2027]);

            int[] counts = new int[2];
            Parallel.Invoke(
                () => counts[0] = first.Claim(Now).Count,
                () => counts[1] = second.Claim(Now).Count);

            run.Equal(counts[0] + counts[1], 1, "one claim between them");
        });

        run.Case("a dropped entry is gone", () =>
        {
            string path = Path.Combine(sandbox, "drop.json");
            InterruptedPrepareStore revit = Store(path, Revit2027, alive: [Revit2027]);
            revit.Started("order-1", Now);
            revit.Drop("order-1");

            run.Equal(revit.Claim(Now).Count, 0, "nothing left");
        });

        run.Case("a file that is not a record holds no entries, and is written over", () =>
        {
            string path = Path.Combine(sandbox, "damaged.json");
            File.WriteAllText(path, "{ not json");
            InterruptedPrepareStore revit = Store(path, Revit2027, alive: [Revit2027]);

            run.Equal(revit.Claim(Now).Count, 0, "nothing claimed from garbage");

            revit.Started("order-1", Now);
            run.Equal(revit.Claim(Now).Count, 1, "a working record again");
        });

        run.Case("a record that cannot be opened does nothing, and throws nothing", () =>
        {
            string path = Path.Combine(sandbox, "locked.json");
            InterruptedPrepareStore revit = Store(path, Revit2027, alive: [Revit2027]);
            revit.Started("order-1", Now);

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                revit.Started("order-2", Now);
                revit.Ended("order-1", PrepareEnding.Ready);
                run.Equal(revit.Claim(Now).Count, 0, "no claim against a record it could not read");
            }

            run.Equal(revit.Claim(Now).Count, 1, "and the record is as it was: order-1 still there, order-2 never written");
        });

        run.Case("the record holds a digest of the address, never the address", () =>
        {
            string path = Path.Combine(sandbox, "private.json");
            Store(path, Revit2025, alive: []).Started("order-1", Now);

            run.False(File.ReadAllText(path).Contains(Email, StringComparison.OrdinalIgnoreCase), "no address on disk");
        });
    }

    private static InterruptedPrepareStore Store(string path, PrepareOwner self, PrepareOwner[] alive)
        => new(path, self, owner => alive.Contains(owner), () => Email);
}
