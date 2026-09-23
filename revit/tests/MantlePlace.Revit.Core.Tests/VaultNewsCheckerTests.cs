using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The background listing that finds orders nobody asked Revit to prepare.
/// </summary>
/// <remarks>
/// It costs a request on every signed-in session, including ones where nobody is waiting on
/// anything, so it is bounded: never signed out, never while the vault browser owns the listing,
/// never more often than the floor, and never a word to the curator when it fails. Driven through an
/// <see cref="IVaultNewsSource"/> fake, so no request leaves the machine (<c>HPS-42</c>).
/// </remarks>
internal static class VaultNewsCheckerTests
{
    private const string Email = "curator@example.com";

    internal static int Run()
    {
        TestRun run = new();
        string sandbox = Path.Combine(Path.GetTempPath(), "mp-news-tests-" + Guid.NewGuid().ToString("N")[..8]);
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

        return run.Report("vault news checker");
    }

    private static void Cases(TestRun run, string sandbox)
    {
        int stores = 0;
        AnnouncedOrderStore NewStore() => new(Path.Combine(sandbox, $"record-{++stores}.json"));

        run.Case("a new order is raised once, after the first listing", () =>
        {
            FakeSource source = new();
            VaultNewsChecker checker = new(source, NewStore(), () => false);
            List<IReadOnlyList<VaultBundle>> raised = [];
            checker.Arrived += (_, arrivals) => raised.Add(arrivals);

            source.Orders = ["order-1"];
            Check(checker);
            run.Equal(raised.Count, 0, "the first listing is not news");

            source.Orders = ["order-1", "order-2"];
            Check(checker);
            run.Equal(raised.Count, 1, "the new order is");
            run.Equal(raised[0][0].OrderId, "order-2", "and only it");

            Check(checker);
            run.Equal(raised.Count, 1, "once");
        });

        run.Case("signed out, nothing is asked of the platform", () =>
        {
            FakeSource source = new() { SignedIn = false };
            Check(new VaultNewsChecker(source, NewStore(), () => false));

            run.Equal(source.Lists, 0, "no request");
        });

        run.Case("with the vault browser open, the window owns the listing", () =>
        {
            FakeSource source = new();
            Check(new VaultNewsChecker(source, NewStore(), () => true));

            run.Equal(source.Lists, 0, "no request");
        });

        run.Case("a listing that fails says nothing, and costs no news later", () =>
        {
            FakeSource source = new() { Orders = ["order-1"] };
            VaultNewsChecker checker = new(source, NewStore(), () => false);
            int raised = 0;
            checker.Arrived += (_, _) => raised++;
            Check(checker);

            source.Orders = ["order-1", "order-2"];
            source.Error = "The platform did not answer.";
            Check(checker);
            run.Equal(raised, 0, "no notice, no prompt");

            source.Error = null;
            Check(checker);
            run.Equal(raised, 1, "the order is still news when the platform answers");
        });

        run.Case("a listing that throws is as silent as one that fails", () =>
        {
            FakeSource source = new() { Throws = true };
            bool threw = false;
            try
            {
                Check(new VaultNewsChecker(source, NewStore(), () => false));
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            run.False(threw, "a background check never faults its caller");
        });

        run.Case("a sign-in lists at once — a startup restore included — and a token renewal does not", () =>
        {
            SignInEdge restore = new();
            run.False(restore.Observe(AuthState.Refreshing), "the restore is under way");
            run.True(restore.Observe(AuthState.Authenticated), "a restored session is a sign-in: this is Revit's usual start");
            run.False(restore.Observe(AuthState.Refreshing), "a renewal begins");
            run.False(restore.Observe(AuthState.Authenticated), "and is not a sign-in: an hourly token is not an hourly listing");

            SignInEdge browser = new();
            run.False(browser.Observe(AuthState.Authenticating), "the browser round trip");
            run.True(browser.Observe(AuthState.Authenticated), "an interactive sign-in");
            run.False(browser.Observe(AuthState.Unauthenticated), "signed out");
            run.True(browser.Observe(AuthState.Authenticated), "signing back in lists again");

            SignInEdge failed = new();
            failed.Observe(AuthState.Authenticated);
            failed.Observe(AuthState.Refreshing);
            failed.Observe(AuthState.Failed);
            run.True(failed.Observe(AuthState.Authenticated), "back from a failed renewal is a sign-in");
        });

        run.Case("a listing that lands after the checker stopped claims nothing", () =>
        {
            string path = Path.Combine(sandbox, "stopped.json");
            FakeSource source = new() { Orders = ["order-1"] };
            VaultNewsChecker first = new(source, new AnnouncedOrderStore(path), () => false);
            Check(first);

            using CancellationTokenSource stopped = new();
            source.Orders = ["order-1", "order-2"];
            source.OnList = stopped.Cancel;
            first.CheckAsync(stopped.Token).GetAwaiter().GetResult();

            source.OnList = null;
            VaultNewsChecker next = new(source, new AnnouncedOrderStore(path), () => false);
            int raised = 0;
            next.Arrived += (_, _) => raised++;
            Check(next);
            run.Equal(raised, 1, "Revit closing mid-listing leaves the order news for the next Revit");
        });

        run.Case("the interval is fifteen minutes, and never below the five-minute floor", () =>
        {
            FakeSource source = new();
            run.Equal(new VaultNewsChecker(source, NewStore(), () => false).Interval == TimeSpan.FromMinutes(15), true, "the reference interval");
            run.Equal(
                new VaultNewsChecker(source, NewStore(), () => false, TimeSpan.FromSeconds(3)).Interval == TimeSpan.FromMinutes(5),
                true,
                "a shorter interval is raised to the floor");
        });
    }

    private static void Check(VaultNewsChecker checker) => checker.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();

    private sealed class FakeSource : IVaultNewsSource
    {
        public bool SignedIn { get; set; } = true;

        public string? Email { get; set; } = VaultNewsCheckerTests.Email;

        public IReadOnlyList<string> Orders { get; set; } = [];

        public string? Error { get; set; }

        public bool Throws { get; set; }

        public int Lists { get; private set; }

        /// <summary>Runs as the listing answers — the moment a shutdown can land in.</summary>
        public Action? OnList { get; set; }

        public Task<(VaultListing? Listing, string? Error)> ListAsync(CancellationToken cancellationToken)
        {
            Lists++;
            OnList?.Invoke();
            if (Throws)
            {
                throw new InvalidOperationException("boom");
            }

            if (Error is not null)
            {
                return Task.FromResult<(VaultListing?, string?)>((null, Error));
            }

            VaultListing listing = new()
            {
                Bundles = [.. Orders.Select(id => new VaultBundle { OrderId = id, AoiLabel = id, Status = BundleStatus.Available })],
            };
            return Task.FromResult<(VaultListing?, string?)>((listing, null));
        }
    }
}
