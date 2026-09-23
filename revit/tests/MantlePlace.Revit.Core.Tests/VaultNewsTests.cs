using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Which orders in a vault listing are news to the curator on this machine: the unannounced orders.
/// </summary>
/// <remarks>
/// An order is news once, the first time it is Available and nobody has told the curator about it
/// here. A first listing is not news at all — every order in it predates the record, and announcing
/// them would bury the curator in notices about orders they bought months ago.
/// </remarks>
internal static class VaultNewsTests
{
    private const string Email = "curator@example.com";

    internal static int Run()
    {
        TestRun run = new();

        run.Case("the machine's first listing announces nothing, and remembers every available order", () =>
        {
            VaultNewsResult result = VaultNews.Arrivals(AnnouncedOrders.Empty, Listing(Available("order-1"), Available("order-2")), Email);

            run.Equal(result.Arrivals.Count, 0, "nothing in a first listing is news");
            run.True(result.Record.Holds("order-1"), "order-1 is remembered");
            run.True(result.Record.Holds("order-2"), "order-2 is remembered");
        });

        run.Case("an order that turns up in a later listing is news, once", () =>
        {
            AnnouncedOrders known = VaultNews.Arrivals(AnnouncedOrders.Empty, Listing(Available("order-1")), Email).Record;

            VaultNewsResult later = VaultNews.Arrivals(known, Listing(Available("order-1"), Available("order-2")), Email);
            run.Equal(later.Arrivals.Count, 1, "one new order");
            run.Equal(later.Arrivals[0].OrderId, "order-2", "the new one, not the one already known");

            VaultNewsResult again = VaultNews.Arrivals(later.Record, Listing(Available("order-1"), Available("order-2")), Email);
            run.Equal(again.Arrivals.Count, 0, "announced once is announced");
        });

        run.Case("only an available order is news: failed, refunded, refreshing and unknown are not, yet", () =>
        {
            AnnouncedOrders known = VaultNews.Arrivals(AnnouncedOrders.Empty, Listing(), Email).Record;

            VaultNewsResult result = VaultNews.Arrivals(
                known,
                Listing(
                    Row("failed", BundleStatus.Failed),
                    Row("refunded", BundleStatus.Refunded),
                    Row("pending", BundleStatus.RefreshPending),
                    Row("unknown", BundleStatus.Unknown)),
                Email);

            run.Equal(result.Arrivals.Count, 0, "none of them is downloadable");
            run.False(result.Record.Holds("pending"), "not announced, so still news once it is available");

            VaultNewsResult delivered = VaultNews.Arrivals(result.Record, Listing(Available("pending")), Email);
            run.Equal(delivered.Arrivals.Count, 1, "an order first delivered after a wait is announced then");
        });

        run.Case("an announced order rebuilt and delivered again is still the same order", () =>
        {
            AnnouncedOrders known = VaultNews.Arrivals(AnnouncedOrders.Empty, Listing(Available("order-1")), Email).Record;

            AnnouncedOrders rebuilding = VaultNews.Arrivals(known, Listing(Row("order-1", BundleStatus.RefreshPending)), Email).Record;
            VaultNewsResult redelivered = VaultNews.Arrivals(rebuilding, Listing(Available("order-1")), Email);

            run.Equal(redelivered.Arrivals.Count, 0, "the key is the order, not the build");
        });

        run.Case("a second account's first listing on this machine is not news either", () =>
        {
            AnnouncedOrders known = VaultNews.Arrivals(AnnouncedOrders.Empty, Listing(Available("order-1")), Email).Record;

            VaultNewsResult other = VaultNews.Arrivals(known, Listing(Available("order-9")), "colleague@example.com");
            run.Equal(other.Arrivals.Count, 0, "every order in it predates this machine knowing the account");

            VaultNewsResult later = VaultNews.Arrivals(other.Record, Listing(Available("order-9"), Available("order-10")), "colleague@example.com");
            run.Equal(later.Arrivals.Count, 1, "after that, the account's new orders are news");
        });

        run.Case("signing out and back in is not a first listing", () =>
        {
            AnnouncedOrders known = VaultNews.Arrivals(AnnouncedOrders.Empty, Listing(Available("order-1")), Email).Record;

            // Built while signed out; the account is one this machine already knows.
            VaultNewsResult back = VaultNews.Arrivals(known, Listing(Available("order-1"), Available("order-2")), " Curator@Example.com ");
            run.Equal(back.Arrivals.Count, 1, "the order built in between is announced");
        });

        run.Case("with no address, only the machine's own first listing is taken as seen", () =>
        {
            AnnouncedOrders known = VaultNews.Arrivals(AnnouncedOrders.Empty, Listing(Available("order-1")), string.Empty).Record;

            VaultNewsResult later = VaultNews.Arrivals(known, Listing(Available("order-1"), Available("order-2")), null);
            run.Equal(later.Arrivals.Count, 1, "no account to be new, so the machine's record decides");
        });

        run.Case("the record keeps no address, only a digest of one", () =>
        {
            AnnouncedOrders known = VaultNews.Arrivals(AnnouncedOrders.Empty, Listing(), Email).Record;

            run.Equal(known.Accounts.Count, 1, "one account known");
            run.False(known.Accounts.Any(account => account.Contains('@', StringComparison.Ordinal)), "no address on disk");
        });

        run.Case("an order the curator saw listed in the vault browser is announced", () =>
        {
            AnnouncedOrders known = VaultNews.Arrivals(AnnouncedOrders.Empty, Listing(Available("order-1")), Email).Record;

            AnnouncedOrders seen = VaultNews.Seen(known, Listing(Available("order-1"), Available("order-2")), Email);
            VaultNewsResult next = VaultNews.Arrivals(seen, Listing(Available("order-1"), Available("order-2")), Email);

            run.Equal(next.Arrivals.Count, 0, "a notice about something already on screen is noise");
        });

        run.Case("an order a Prepare notice told the curator about is announced", () =>
        {
            AnnouncedOrders known = VaultNews.Arrivals(AnnouncedOrders.Empty, Listing(), Email).Record;

            VaultNewsResult next = VaultNews.Arrivals(known.Announcing("order-3"), Listing(Available("order-3")), Email);

            run.Equal(next.Arrivals.Count, 0, "one order, one notice");
        });

        run.Case("one new order is named, and opens the vault on itself", () =>
        {
            NewsNotice? notice = VaultNews.NoticeFor([Available("Harbour Point")], vaultOpen: false);

            run.True(notice is not null, "a notice is given");
            run.Equal(notice!.Text, "Harbour Point is in your vault.", "in the vault, not ready to import: it still needs a Prepare");
            run.Equal(notice.SelectOrderId, "Harbour Point", "a click selects it");
            run.Equal(notice.OrderIds.Count, 1, "one order for the badge");
        });

        run.Case("an order with no area label is named by its order", () =>
        {
            NewsNotice? notice = VaultNews.NoticeFor([new VaultBundle { OrderId = "order-7", Status = BundleStatus.Available }], vaultOpen: false);

            run.Equal(notice!.Text, "order-7 is in your vault.", "never an empty name");
        });

        run.Case("several new orders in one listing are one notice", () =>
        {
            NewsNotice? notice = VaultNews.NoticeFor([Available("a"), Available("b"), Available("c")], vaultOpen: false);

            run.Equal(notice!.Text, "3 new orders are in your vault.", "one notice, not three");
            run.True(notice.SelectOrderId is null, "a click opens the vault on nothing in particular");
            run.Equal(notice.OrderIds.Count, 3, "each still counts on the badge");
        });

        run.Case("nothing to say: no notice with the vault open, or with no arrivals", () =>
        {
            run.True(VaultNews.NoticeFor([Available("a")], vaultOpen: true) is null, "the vault already shows it");
            run.True(VaultNews.NoticeFor([], vaultOpen: false) is null, "no arrivals, no notice");
        });

        run.Case("the badge counts each announced order once, beside the Prepare notices", () =>
        {
            PendingNotices pending = new();
            pending.Add(PrepareNotices.For("order-1", "Harbour Point", PrepareEnding.Ready, string.Empty, false, true)!.Value);

            pending.Add(VaultNews.NoticeFor([Available("order-1"), Available("order-2"), Available("order-3")], vaultOpen: false)!);

            run.Equal(pending.Count, 3, "three orders have news; order-1 is one of them, not two");
        });

        return run.Report("Vault news (unannounced orders)");
    }

    private static VaultBundle Available(string orderId) => Row(orderId, BundleStatus.Available);

    private static VaultBundle Row(string orderId, BundleStatus status)
        => new() { OrderId = orderId, AoiLabel = orderId, Status = status };

    private static VaultListing Listing(params VaultBundle[] rows) => new() { Bundles = rows };
}
