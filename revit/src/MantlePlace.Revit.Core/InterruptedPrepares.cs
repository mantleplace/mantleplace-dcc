namespace MantlePlace.Revit.Core;

/// <summary>
/// The Revit process watching a Prepare: its id, and when it started, so that a process id Windows
/// has since handed to another program is not mistaken for the watcher.
/// </summary>
/// <param name="ProcessId">The process id.</param>
/// <param name="StartedUtcTicks">The process's start time, as UTC ticks.</param>
public readonly record struct PrepareOwner(int ProcessId, long StartedUtcTicks);

/// <summary>One Prepare the record holds: started, and not yet seen to end.</summary>
/// <param name="OrderId">The order being prepared.</param>
/// <param name="StartedAt">When the curator pressed Prepare. A re-join keeps it, so the age limit counts from the ask.</param>
/// <param name="Account">
/// The signed-in account as <see cref="AnnouncedOrders.AccountKey"/> gives it, or <c>null</c> when the
/// grant named no address.
/// </param>
/// <param name="Owner">The process watching it.</param>
public sealed record InterruptedPrepare(string OrderId, DateTimeOffset StartedAt, string? Account, PrepareOwner Owner);

/// <summary>What a re-join does with one claimed entry.</summary>
public enum RejoinDecision
{
    /// <summary>The order is available: hand it to the watcher.</summary>
    Rejoin,

    /// <summary>The platform would refuse it: strike the entry off, without a notice.</summary>
    Drop,

    /// <summary>The listing cannot tell yet: keep the entry for the next sign-in.</summary>
    Wait,
}

/// <summary>What a claim took, and the record once it has.</summary>
/// <param name="Record">The record with the claimed entries owned by the claiming process and the stale ones gone.</param>
/// <param name="Claimed">The entries to re-join, one per order.</param>
public sealed record InterruptedPrepareClaim(IReadOnlyList<InterruptedPrepare> Record, IReadOnlyList<InterruptedPrepare> Claimed);

/// <summary>
/// Which Prepares a Revit process may re-join after Revit closed or crashed during them. Pure.
/// </summary>
/// <remarks>
/// <para>
/// <b>An entry is written when a Prepare starts and removed when it ends</b>, never written at
/// shutdown: a crash never reaches shutdown. So whatever an ending leaves behind is an interrupted
/// Prepare, however the process went.
/// </para>
/// <para>
/// <b>Only an interruption keeps an entry</b> (<see cref="KeepsEntry"/>). A cancel was the curator's
/// choice, and a poll that ran out has already told the curator to press Prepare later.
/// </para>
/// <para>
/// ⛔ <b>A live owner keeps its entry.</b> Revit 2025 and 2027 open side by side share one record, and
/// a process that took a Prepare the other is still watching would poll the order twice. An owner is
/// live only when a process of the same id <em>and</em> start time still runs.
/// </para>
/// </remarks>
public static class InterruptedPrepares
{
    /// <summary>How long an ask stays worth re-joining. Older entries are dropped without a notice.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>Whether a Prepare that ended this way stays in the record, to be re-joined.</summary>
    public static bool KeepsEntry(PrepareEnding ending) => ending == PrepareEnding.Interrupted;

    /// <summary>
    /// The record with <paramref name="entry"/> in it, replacing any entry for the same order and owner.
    /// </summary>
    public static IReadOnlyList<InterruptedPrepare> Started(IReadOnlyList<InterruptedPrepare> record, InterruptedPrepare entry)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(entry);

        return [.. Without(record, entry.OrderId, entry.Owner), entry];
    }

    /// <summary>The record without <paramref name="owner"/>'s entry for <paramref name="orderId"/>.</summary>
    public static IReadOnlyList<InterruptedPrepare> Ended(IReadOnlyList<InterruptedPrepare> record, string orderId, PrepareOwner owner)
    {
        ArgumentNullException.ThrowIfNull(record);

        return Without(record, orderId, owner);
    }

    /// <summary>Takes the entries <paramref name="self"/> may re-join, and drops the stale ones.</summary>
    /// <param name="record">Every entry on the machine.</param>
    /// <param name="now">The time now.</param>
    /// <param name="account">The signed-in account, as <see cref="AnnouncedOrders.AccountKey"/> gives it.</param>
    /// <param name="self">The claiming process.</param>
    /// <param name="isAlive">Whether an owner's process still runs.</param>
    /// <remarks>
    /// <para>
    /// An entry older than <see cref="MaxAge"/> is dropped. One recorded under another account — or
    /// under an account while the grant now names none — is left for its own account. One whose owner
    /// is another process still running is left to it. Everything else is claimed: its owner becomes
    /// <paramref name="self"/>. An entry <paramref name="self"/> already owns is claimed again, so a
    /// re-join whose listing failed is retried at the next sign-in.
    /// </para>
    /// <para>
    /// Two claimed entries for one order — two Revits that both died watching it — become one, and it
    /// keeps the earlier ask, so the age limit is never extended by a duplicate.
    /// </para>
    /// </remarks>
    public static InterruptedPrepareClaim Claim(
        IReadOnlyList<InterruptedPrepare> record,
        DateTimeOffset now,
        string? account,
        PrepareOwner self,
        Func<PrepareOwner, bool> isAlive)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(isAlive);

        List<InterruptedPrepare> kept = [];
        Dictionary<string, InterruptedPrepare> claimed = new(StringComparer.Ordinal);

        foreach (InterruptedPrepare entry in record)
        {
            if (now - entry.StartedAt > MaxAge)
            {
                continue;
            }

            bool otherAccount = entry.Account is not null && !string.Equals(entry.Account, account, StringComparison.Ordinal);
            bool heldByAnother = entry.Owner != self && isAlive(entry.Owner);
            if (otherAccount || heldByAnother)
            {
                kept.Add(entry);
                continue;
            }

            InterruptedPrepare mine = entry with { Owner = self };
            if (!claimed.TryGetValue(entry.OrderId, out InterruptedPrepare? earlier) || entry.StartedAt < earlier.StartedAt)
            {
                claimed[entry.OrderId] = mine;
            }
        }

        List<InterruptedPrepare> taken = [.. claimed.Values.OrderBy(entry => entry.StartedAt)];
        return new InterruptedPrepareClaim([.. kept, .. taken], taken);
    }

    /// <summary>What to do with a claimed entry, given this account's vault listing.</summary>
    /// <param name="listing">The listing.</param>
    /// <param name="orderId">The claimed entry's order.</param>
    /// <param name="row">The row to re-join from, when the answer is <see cref="RejoinDecision.Rejoin"/>.</param>
    /// <remarks>
    /// <para>
    /// An order missing from this account's vault, refunded or failed is one the platform would
    /// refuse: dropped without a notice, since a refund reaches the curator through the platform's own
    /// channels. An available one is re-joined.
    /// </para>
    /// <para>
    /// Any other status — a refresh pending, or a word this build has not met (<c>HPS-22</c>) — says
    /// nothing about whether the platform would refuse, so the entry waits for the next sign-in.
    /// Dropping it would lose the curator's Prepare over a status that was never a refusal.
    /// </para>
    /// </remarks>
    public static RejoinDecision Decide(VaultListing listing, string orderId, out VaultBundle? row)
    {
        ArgumentNullException.ThrowIfNull(listing);

        row = listing.Bundles.FirstOrDefault(held => string.Equals(held.OrderId, orderId, StringComparison.Ordinal));
        return row?.Status switch
        {
            null or BundleStatus.Refunded or BundleStatus.Failed => RejoinDecision.Drop,
            BundleStatus.Available => RejoinDecision.Rejoin,
            _ => RejoinDecision.Wait,
        };
    }

    private static List<InterruptedPrepare> Without(IReadOnlyList<InterruptedPrepare> record, string orderId, PrepareOwner owner)
        => [.. record.Where(held => held.Owner != owner || !string.Equals(held.OrderId, orderId, StringComparison.Ordinal))];
}
