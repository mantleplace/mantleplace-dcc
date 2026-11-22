namespace MantlePlace.Core;

public readonly record struct PrepareOwner(int ProcessId, long StartedUtcTicks);

public sealed record InterruptedPrepare(string OrderId, DateTimeOffset StartedAt, string? Account, PrepareOwner Owner);

public enum RejoinDecision
{
    Rejoin,

    Drop,

    Wait,
}

public sealed record InterruptedPrepareClaim(IReadOnlyList<InterruptedPrepare> Record, IReadOnlyList<InterruptedPrepare> Claimed);

public static class InterruptedPrepares
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public static bool KeepsEntry(PrepareEnding ending) => ending == PrepareEnding.Interrupted;

    public static IReadOnlyList<InterruptedPrepare> Started(IReadOnlyList<InterruptedPrepare> record, InterruptedPrepare entry)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(entry);

        return [.. Without(record, entry.OrderId, entry.Owner), entry];
    }

    public static IReadOnlyList<InterruptedPrepare> Ended(IReadOnlyList<InterruptedPrepare> record, string orderId, PrepareOwner owner)
    {
        ArgumentNullException.ThrowIfNull(record);

        return Without(record, orderId, owner);
    }

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
