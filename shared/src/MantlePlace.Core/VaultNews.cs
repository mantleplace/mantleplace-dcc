namespace MantlePlace.Core;

public sealed class AnnouncedOrders
{
    private readonly HashSet<string> _orders;
    private readonly HashSet<string> _accounts;

    public AnnouncedOrders(IEnumerable<string> orders, IEnumerable<string> accounts, bool machineListed)
    {
        _orders = new HashSet<string>(orders, StringComparer.Ordinal);
        _accounts = new HashSet<string>(accounts, StringComparer.Ordinal);
        MachineListed = machineListed;
    }

    public static AnnouncedOrders Empty { get; } = new([], [], machineListed: false);

    public IReadOnlyCollection<string> Accounts => _accounts;

    public bool MachineListed { get; }

    public IReadOnlyCollection<string> Orders => _orders;

    public bool Holds(string orderId) => _orders.Contains(orderId);

    public static string? AccountKey(string? email)
        => string.IsNullOrWhiteSpace(email) ? null : Sha256Digest.OfUtf8(email.Trim().ToLowerInvariant());

    public AnnouncedOrders Announcing(string orderId) => new(_orders.Append(orderId), _accounts, MachineListed);

    internal bool Knows(string accountKey) => _accounts.Contains(accountKey);

    internal AnnouncedOrders With(IEnumerable<string> orders, string? accountKey)
        => new(
            _orders.Concat(orders),
            accountKey is null ? _accounts : _accounts.Append(accountKey),
            machineListed: true);
}

public sealed record VaultNewsResult(IReadOnlyList<VaultBundle> Arrivals, AnnouncedOrders Record);

public static class VaultNewsCadence
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    public static readonly TimeSpan Floor = TimeSpan.FromMinutes(5);

    public static TimeSpan Bounded(TimeSpan requested) => requested < Floor ? Floor : requested;
}

public sealed class SignInEdge
{
    private AuthState _settled = AuthStateMachine.Initial;

    public bool Observe(AuthState state)
    {
        if (state == AuthState.Refreshing)
        {
            return false;
        }

        bool signedIn = state == AuthState.Authenticated && _settled != AuthState.Authenticated;
        _settled = state;
        return signedIn;
    }
}

public sealed record NewsNotice(IReadOnlyList<string> OrderIds, string? SelectOrderId, string Text);

public static class VaultNews
{
    public static VaultNewsResult Arrivals(AnnouncedOrders record, VaultListing listing, string? email)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(listing);

        List<VaultBundle> available = [.. listing.Bundles.Where(row => row.IsDownloadable)];
        string? account = AnnouncedOrders.AccountKey(email);
        AnnouncedOrders after = record.With(available.Select(row => row.OrderId), account);

        if (!record.MachineListed || account is not null && !record.Knows(account))
        {
            return new VaultNewsResult([], after);
        }

        return new VaultNewsResult([.. available.Where(row => !record.Holds(row.OrderId))], after);
    }

    public static AnnouncedOrders Seen(AnnouncedOrders record, VaultListing listing, string? email)
        => Arrivals(record, listing, email).Record;

    public static NewsNotice? NoticeFor(IReadOnlyList<VaultBundle> arrivals, bool vaultOpen)
    {
        ArgumentNullException.ThrowIfNull(arrivals);

        if (vaultOpen || arrivals.Count == 0)
        {
            return null;
        }

        List<string> orders = [.. arrivals.Select(row => row.OrderId)];
        if (arrivals.Count == 1)
        {
            VaultBundle only = arrivals[0];
            return new NewsNotice(orders, only.OrderId, $"{PrepareNotices.LabelOf(only.AoiLabel, only.OrderId)} is in your vault.");
        }

        return new NewsNotice(
            orders,
            SelectOrderId: null,
            $"{arrivals.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} new orders are in your vault.");
    }
}
