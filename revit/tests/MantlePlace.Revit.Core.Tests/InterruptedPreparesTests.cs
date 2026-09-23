using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Which Prepares a Revit may re-join after an earlier one closed or crashed while watching them.
/// </summary>
/// <remarks>
/// The record is written as a Prepare starts and struck off as it ends, so what is left is what Revit
/// ended. Pure: the owner's liveness is a function the cases hand in.
/// </remarks>
internal static class InterruptedPreparesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);
    private static readonly PrepareOwner Self = new(100, 1_000);
    private static readonly PrepareOwner Crashed = new(200, 2_000);
    private static readonly PrepareOwner Running = new(300, 3_000);
    private static readonly string Account = AnnouncedOrders.AccountKey("curator@example.com")!;
    private static readonly string OtherAccount = AnnouncedOrders.AccountKey("someone@example.com")!;

    internal static int Run()
    {
        TestRun run = new();

        run.Case("only an interruption keeps an entry", () =>
        {
            run.True(InterruptedPrepares.KeepsEntry(PrepareEnding.Interrupted), "interrupted: kept, to be re-joined");
            run.False(InterruptedPrepares.KeepsEntry(PrepareEnding.Ready), "ready");
            run.False(InterruptedPrepares.KeepsEntry(PrepareEnding.Failed), "failed");
            run.False(InterruptedPrepares.KeepsEntry(PrepareEnding.StillPreparing), "still preparing: the curator was told to press Prepare later");
            run.False(InterruptedPrepares.KeepsEntry(PrepareEnding.Cancelled), "cancelled by the curator");
        });

        run.Case("a start writes an entry, and its end strikes off that owner's entry only", () =>
        {
            IReadOnlyList<InterruptedPrepare> record = InterruptedPrepares.Started([], Entry("order-1", Self));
            record = InterruptedPrepares.Started(record, Entry("order-1", Running));
            record = InterruptedPrepares.Started(record, Entry("order-1", Self, hoursAgo: 1));

            run.Equal(record.Count, 2, "one entry per order and owner: a second start replaces the first");

            record = InterruptedPrepares.Ended(record, "order-1", Self);
            run.Equal(record.Count, 1, "struck off");
            run.True(record[0].Owner == Running, "the other Revit's entry for the same order stays");
        });

        run.Case("a crashed owner's entry is claimed, and becomes this process's", () =>
        {
            InterruptedPrepareClaim claim = Claim([Entry("order-1", Crashed)]);

            run.Equal(claim.Claimed.Count, 1, "claimed");
            run.True(claim.Claimed[0].Owner == Self, "owned by the claimer now");
            run.True(claim.Record.Single().Owner == Self, "and so in the record, which a second Revit reads next");
        });

        run.Case("a running owner's entry is left to it", () =>
        {
            InterruptedPrepareClaim claim = Claim([Entry("order-1", Running)]);

            run.Equal(claim.Claimed.Count, 0, "not claimed: that Revit is still watching it");
            run.True(claim.Record.Single().Owner == Running, "and not taken over");
        });

        run.Case("an entry this process owns is claimed again, so a failed re-join is retried", () =>
        {
            run.Equal(Claim([Entry("order-1", Self)]).Claimed.Count, 1, "claimed");
        });

        run.Case("older than seven days: dropped, without a claim", () =>
        {
            InterruptedPrepareClaim claim = Claim([Entry("order-1", Crashed, hoursAgo: 7 * 24 + 1)]);

            run.Equal(claim.Claimed.Count, 0, "not claimed");
            run.Equal(claim.Record.Count, 0, "gone from the record");
        });

        run.Case("inside seven days: still claimed", () =>
        {
            run.Equal(Claim([Entry("order-1", Crashed, hoursAgo: 7 * 24 - 1)]).Claimed.Count, 1, "a weekend away is what this is for");
        });

        run.Case("another account's entry waits for that account", () =>
        {
            InterruptedPrepareClaim claim = Claim([Entry("order-1", Crashed, account: OtherAccount)]);

            run.Equal(claim.Claimed.Count, 0, "not claimed");
            run.Equal(claim.Record.Count, 1, "kept until its own account signs in, or it ages out");
        });

        run.Case("an entry recorded under an account waits while the grant names none", () =>
        {
            InterruptedPrepareClaim claim = InterruptedPrepares.Claim([Entry("order-1", Crashed)], Now, account: null, Self, _ => false);

            run.Equal(claim.Claimed.Count, 0, "an unknown account is not the recorded one");
            run.Equal(claim.Record.Count, 1, "kept");
        });

        run.Case("an entry recorded with no account is claimed under any", () =>
        {
            run.Equal(Claim([Entry("order-1", Crashed, account: null)]).Claimed.Count, 1, "claimed; a refusal is settled by the listing");
        });

        run.Case("two dead owners on one order: one re-join, from the earlier ask", () =>
        {
            PrepareOwner alsoCrashed = new(400, 4_000);
            InterruptedPrepareClaim claim = Claim([Entry("order-1", Crashed, hoursAgo: 2), Entry("order-1", alsoCrashed, hoursAgo: 30)]);

            run.Equal(claim.Claimed.Count, 1, "one");
            run.True(claim.Claimed[0].StartedAt == Now.AddHours(-30), "the earlier ask, so a duplicate never extends the age limit");
            run.Equal(claim.Record.Count, 1, "one entry left in the record");
        });

        run.Case("what a claimed order's row in the listing says to do", () =>
        {
            VaultListing listing = new()
            {
                Bundles =
                [
                    new VaultBundle { OrderId = "available", Status = BundleStatus.Available },
                    new VaultBundle { OrderId = "refunded", Status = BundleStatus.Refunded },
                    new VaultBundle { OrderId = "failed", Status = BundleStatus.Failed },
                    new VaultBundle { OrderId = "pending", Status = BundleStatus.RefreshPending },
                    new VaultBundle { OrderId = "unknown", Status = BundleStatus.Unknown },
                ],
            };

            run.Equal(InterruptedPrepares.Decide(listing, "available", out VaultBundle? row), RejoinDecision.Rejoin, "available: re-joined");
            run.Equal(row?.OrderId, "available", "from its own row");
            run.Equal(InterruptedPrepares.Decide(listing, "refunded", out _), RejoinDecision.Drop, "refunded: dropped");
            run.Equal(InterruptedPrepares.Decide(listing, "failed", out _), RejoinDecision.Drop, "failed: dropped");
            run.Equal(InterruptedPrepares.Decide(listing, "missing", out _), RejoinDecision.Drop, "not in this account's vault: dropped");
            run.Equal(InterruptedPrepares.Decide(listing, "pending", out _), RejoinDecision.Wait, "a refresh pending is not a refusal: kept");
            run.Equal(InterruptedPrepares.Decide(listing, "unknown", out _), RejoinDecision.Wait, "a status word this build has not met is not a refusal: kept");
        });

        return run.Report("InterruptedPrepares");
    }

    private static InterruptedPrepareClaim Claim(IReadOnlyList<InterruptedPrepare> record)
        => InterruptedPrepares.Claim(record, Now, Account, Self, owner => owner == Running);

    private static InterruptedPrepare Entry(string orderId, PrepareOwner owner, double hoursAgo = 1, string? account = "")
        => new(orderId, Now.AddHours(-hoursAgo), account == string.Empty ? Account : account, owner);
}
