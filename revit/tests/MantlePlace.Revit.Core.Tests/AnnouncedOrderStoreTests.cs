using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The one record per machine of which orders have been announced, shared by every Revit on it.
/// </summary>
/// <remarks>
/// Two Revit processes open at once both see a new order. The first to claim it announces it; the
/// other finds it claimed and stays silent. A duplicate notice is a smaller failure than a lost one,
/// so a record that cannot be written still announces.
/// </remarks>
internal static class AnnouncedOrderStoreTests
{
    private const string Email = "curator@example.com";

    internal static int Run()
    {
        TestRun run = new();
        string sandbox = Path.Combine(Path.GetTempPath(), "mp-announced-tests-" + Guid.NewGuid().ToString("N")[..8]);
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

        return run.Report("announced-order store");
    }

    private static void Cases(TestRun run, string sandbox)
    {
        run.Case("the record outlives the process that wrote it", () =>
        {
            string path = Path.Combine(sandbox, "restart.json");
            new AnnouncedOrderStore(path).Claim(Listing("order-1"), Email);

            VaultNewsResult after = new AnnouncedOrderStore(path).Claim(Listing("order-1", "order-2"), Email);

            run.Equal(after.Arrivals.Count, 1, "a restarted Revit knows what was announced before");
            run.Equal(after.Arrivals[0].OrderId, "order-2", "and announces only what is new");
        });

        run.Case("two Revits on one machine: the first claim wins, the second stays silent", () =>
        {
            string path = Path.Combine(sandbox, "two.json");
            AnnouncedOrderStore revit2025 = new(path);
            AnnouncedOrderStore revit2027 = new(path);
            revit2025.Claim(Listing("order-1"), Email);

            VaultNewsResult first = revit2025.Claim(Listing("order-1", "order-2"), Email);
            VaultNewsResult second = revit2027.Claim(Listing("order-1", "order-2"), Email);

            run.Equal(first.Arrivals.Count, 1, "the first to list it announces it");
            run.Equal(second.Arrivals.Count, 0, "the other finds it already announced");
        });

        run.Case("a record that cannot be written still announces, once per process", () =>
        {
            string path = Path.Combine(sandbox, "locked.json");
            AnnouncedOrderStore store = new(path);
            store.Claim(Listing("order-1"), Email);

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                VaultNewsResult locked = store.Claim(Listing("order-1", "order-2"), Email);
                run.Equal(locked.Arrivals.Count, 1, "a duplicate notice beats a lost one");

                VaultNewsResult again = store.Claim(Listing("order-1", "order-2"), Email);
                run.Equal(again.Arrivals.Count, 0, "but this process does not repeat itself");
            }
        });

        run.Case("a record this process has never read, and cannot open, loses nothing", () =>
        {
            string path = Path.Combine(sandbox, "locked-fresh.json");
            new AnnouncedOrderStore(path).Claim(Listing("order-1"), Email);

            AnnouncedOrderStore fresh = new(path);
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                VaultNewsResult locked = fresh.Claim(Listing("order-1", "order-2"), Email);
                run.Equal(locked.Arrivals.Count, 0, "it cannot tell news from a first listing, so it waits");
            }

            VaultNewsResult later = fresh.Claim(Listing("order-1", "order-2"), Email);
            run.Equal(later.Arrivals.Count, 1, "and the order is still news once the record opens");
        });

        run.Case("a record that is not readable is a machine that has never listed", () =>
        {
            string path = Path.Combine(sandbox, "garbled.json");
            File.WriteAllText(path, "{ not json");

            VaultNewsResult result = new AnnouncedOrderStore(path).Claim(Listing("order-1"), Email);

            run.Equal(result.Arrivals.Count, 0, "no flood from a damaged file");

            VaultNewsResult next = new AnnouncedOrderStore(path).Claim(Listing("order-1", "order-2"), Email);
            run.Equal(next.Arrivals.Count, 1, "and it is rewritten whole");
        });

        run.Case("orders seen in the vault browser, or announced by a Prepare, are recorded too", () =>
        {
            string path = Path.Combine(sandbox, "seen.json");
            AnnouncedOrderStore store = new(path);
            store.Claim(Listing("order-1"), Email);

            store.Seen(Listing("order-1", "order-2"), Email);
            store.Announce("order-3");

            VaultNewsResult next = new AnnouncedOrderStore(path).Claim(Listing("order-1", "order-2", "order-3"), Email);
            run.Equal(next.Arrivals.Count, 0, "neither is news any more");
        });
    }

    private static VaultListing Listing(params string[] orderIds) => new()
    {
        Bundles = [.. orderIds.Select(id => new VaultBundle { OrderId = id, AoiLabel = id, Status = BundleStatus.Available })],
    };
}
