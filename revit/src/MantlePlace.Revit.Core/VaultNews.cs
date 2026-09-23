namespace MantlePlace.Revit.Core;

/// <summary>
/// What this machine has already told its curators about: the orders announced, the accounts that
/// have listed the vault here, and whether the machine has had a listing at all. Immutable; every
/// change returns a new record.
/// </summary>
/// <remarks>
/// An account is held as a digest of its address (<see cref="AccountKey"/>), never the address: the
/// record only has to recognise an account again, not name one.
/// </remarks>
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

    /// <summary>A machine that has never listed the vault.</summary>
    public static AnnouncedOrders Empty { get; } = new([], [], machineListed: false);

    /// <summary>The accounts that have listed the vault here, as <see cref="AccountKey"/> digests.</summary>
    public IReadOnlyCollection<string> Accounts => _accounts;

    /// <summary>Whether any listing has ever been recorded on this machine.</summary>
    public bool MachineListed { get; }

    /// <summary>The announced orders, in no particular order.</summary>
    public IReadOnlyCollection<string> Orders => _orders;

    /// <summary>Whether <paramref name="orderId"/> has been announced here.</summary>
    public bool Holds(string orderId) => _orders.Contains(orderId);

    /// <summary>
    /// The key an address is known by: a digest of it trimmed and lower-cased, or <c>null</c> when
    /// there is no address.
    /// </summary>
    public static string? AccountKey(string? email)
        => string.IsNullOrWhiteSpace(email) ? null : Sha256Digest.OfUtf8(email.Trim().ToLowerInvariant());

    /// <summary>The record with <paramref name="orderId"/> announced by some other notice.</summary>
    public AnnouncedOrders Announcing(string orderId) => new(_orders.Append(orderId), _accounts, MachineListed);

    internal bool Knows(string accountKey) => _accounts.Contains(accountKey);

    internal AnnouncedOrders With(IEnumerable<string> orders, string? accountKey)
        => new(
            _orders.Concat(orders),
            accountKey is null ? _accounts : _accounts.Append(accountKey),
            machineListed: true);
}

/// <summary>What one listing is news of, and the record once it has been.</summary>
/// <param name="Arrivals">The unannounced orders, in listing order.</param>
/// <param name="Record">The record with them — and everything else the listing showed — announced.</param>
public sealed record VaultNewsResult(IReadOnlyList<VaultBundle> Arrivals, AnnouncedOrders Record);

/// <summary>
/// How often the background listing runs (<c>HPS-55</c>): a declared interval, and a floor no
/// configuration goes below.
/// </summary>
/// <remarks>
/// A web order takes minutes to hours to build, so the curator loses nothing to a quarter of an hour,
/// and the platform pays for every signed-in session whether anyone is waiting or not.
/// </remarks>
public static class VaultNewsCadence
{
    /// <summary>The reference interval.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>The hard floor.</summary>
    public static readonly TimeSpan Floor = TimeSpan.FromMinutes(5);

    /// <summary><paramref name="requested"/>, raised to the floor.</summary>
    public static TimeSpan Bounded(TimeSpan requested) => requested < Floor ? Floor : requested;
}

/// <summary>
/// Which auth transitions are a sign-in, the moment the background listing runs at once rather than
/// at its next tick (<c>HPS-55</c>). Not thread-safe; the caller serialises.
/// </summary>
/// <remarks>
/// ⚠ <b>Refreshing is two different things.</b> A startup restore goes Unauthenticated → Refreshing →
/// Authenticated, and is the sign-in Revit most often starts with; a token renewal goes Authenticated
/// → Refreshing → Authenticated, and is not a sign-in at all — an hourly token would otherwise be an
/// hourly listing on top of the interval. Only the last state that was not Refreshing tells them
/// apart, so that is what is kept.
/// </remarks>
public sealed class SignInEdge
{
    private AuthState _settled = AuthStateMachine.Initial;

    /// <summary>Takes the session's new state; <c>true</c> when it is a sign-in.</summary>
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

/// <summary>What the curator is told about the unannounced orders one listing found.</summary>
/// <param name="OrderIds">The orders it announces — each one counts on the Vault badge.</param>
/// <param name="SelectOrderId">The order a click selects in the vault; <c>null</c> for several.</param>
/// <param name="Text">What the notice says.</param>
public sealed record NewsNotice(IReadOnlyList<string> OrderIds, string? SelectOrderId, string Text);

/// <summary>
/// Which orders in a vault listing are unannounced: available, and never yet told to the curator on
/// this machine. Pure.
/// </summary>
public static class VaultNews
{
    /// <summary>The unannounced orders in <paramref name="listing"/>.</summary>
    /// <param name="record">What this machine has announced so far.</param>
    /// <param name="listing">The listing just fetched.</param>
    /// <param name="email">The signed-in address, or empty when the grant named none.</param>
    public static VaultNewsResult Arrivals(AnnouncedOrders record, VaultListing listing, string? email)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(listing);

        List<VaultBundle> available = [.. listing.Bundles.Where(row => row.IsDownloadable)];
        string? account = AnnouncedOrders.AccountKey(email);
        AnnouncedOrders after = record.With(available.Select(row => row.OrderId), account);

        // A first listing — the machine's, or an account's on this machine — predates the record, so
        // nothing in it is news. With no address, only the machine's first listing can be told apart.
        if (!record.MachineListed || account is not null && !record.Knows(account))
        {
            return new VaultNewsResult([], after);
        }

        return new VaultNewsResult([.. available.Where(row => !record.Holds(row.OrderId))], after);
    }

    /// <summary>
    /// The record once the curator has seen <paramref name="listing"/> in the vault browser: every
    /// order it shows is announced, the way a notice would have announced it.
    /// </summary>
    public static AnnouncedOrders Seen(AnnouncedOrders record, VaultListing listing, string? email)
        => Arrivals(record, listing, email).Record;

    /// <summary>
    /// The notice for <paramref name="arrivals"/>, or <c>null</c> when there is nothing to tell.
    /// </summary>
    /// <remarks>
    /// "In your vault", never "ready": an order bought on the web is delivered, not built for Revit,
    /// and the curator's next step is a Prepare. Several orders found by one listing are one notice —
    /// a stack of them after a weekend away would push every other notice off the screen.
    /// </remarks>
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
