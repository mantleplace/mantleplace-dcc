namespace MantlePlace.Revit.Core;

/// <summary>How a Prepare ended.</summary>
public enum PrepareEnding
{
    /// <summary>The bundle is downloaded, verified, and waiting for Import.</summary>
    Ready,

    /// <summary>The platform refused or failed the build, or the download did not land a valid zip.</summary>
    Failed,

    /// <summary>
    /// The poll ran out of budget (<c>HPS-25</c>) with the job still building. The job goes on; only
    /// the watching stopped, and a Prepare on the same order rejoins it (<c>HPS-24</c>).
    /// </summary>
    StillPreparing,

    /// <summary>The curator pressed Cancel, or Revit is shutting down.</summary>
    Cancelled,
}

/// <summary>What the curator is told about one Prepare, and which bundle it opens the vault on.</summary>
public readonly record struct PrepareNotice(string OrderId, PrepareEnding Ending, string Text);

/// <summary>
/// Whether a Prepare's ending is worth telling the curator about, and in what words. Pure.
/// </summary>
/// <remarks>
/// <para>
/// A notice is for a job nobody is watching. <b>While the vault is open there is none</b>: the vault
/// already shows what happened, and a second saying of it is noise. <b>A cancel is never announced</b>:
/// the curator pressed it. <b>Signed out, only a download that finished is</b>: a failure after a
/// sign-out is the sign-out's doing, which the curator chose, and the job goes on untouched on the
/// platform (<c>HPS-24</c>). A bundle that finished downloading is on disk, and importing it needs no
/// sign-in at all, so that one is still news.
/// </para>
/// <para>
/// Signed-out is read when the Prepare <em>ends</em>, not from the auth event stream. A token the
/// other host rotated shows here as a brief sign-out before the retry succeeds; a watch that
/// reacted to the event would drop itself on that blip, whereas a signed-out poll ends on its own
/// consecutive-failure cap, a few seconds later, by which time the session has settled.
/// </para>
/// </remarks>
public static class PrepareNotices
{
    /// <summary>How long a notice stays up when nobody touches it.</summary>
    public const double ShowSeconds = 10.0;

    /// <summary>The name a notice gives a bundle: its area's label, or its order when it has none.</summary>
    public static string LabelOf(string aoiLabel, string orderId)
        => string.IsNullOrWhiteSpace(aoiLabel) ? orderId : aoiLabel.Trim();

    /// <summary>The notice for one ending, or <c>null</c> when there is nothing to tell.</summary>
    /// <param name="orderId">The order the notice opens the vault on.</param>
    /// <param name="label">The bundle's name, from <see cref="LabelOf"/>.</param>
    /// <param name="ending">How the Prepare ended.</param>
    /// <param name="detail">The reason, for a failure. Ignored otherwise.</param>
    /// <param name="vaultOpen">Whether the vault browser is open as the Prepare ends.</param>
    /// <param name="signedIn">Whether the session is signed in as the Prepare ends.</param>
    public static PrepareNotice? For(
        string orderId,
        string label,
        PrepareEnding ending,
        string detail,
        bool vaultOpen,
        bool signedIn)
    {
        if (vaultOpen || ending == PrepareEnding.Cancelled)
        {
            return null;
        }

        if (!signedIn && ending != PrepareEnding.Ready)
        {
            return null;
        }

        string text = ending switch
        {
            PrepareEnding.Ready => $"{label} is downloaded and ready to import.",
            PrepareEnding.StillPreparing =>
                $"{label} is still being prepared. Open the vault later and press {WindowLabels.PrepareForRevit} to pick it up.",
            _ => string.IsNullOrWhiteSpace(detail)
                ? $"Couldn't prepare {label}."
                : $"Couldn't prepare {label}. {detail.Trim()}",
        };

        return new PrepareNotice(orderId, ending, text);
    }
}

/// <summary>
/// The notices the curator has not yet seen in the vault: what the Vault button's badge counts.
/// </summary>
/// <remarks>
/// One per bundle. A Prepare that ran out of budget and was later rejoined and finished is one
/// bundle with news, not two, so a later notice for the same order replaces the earlier one.
/// </remarks>
public sealed class PendingNotices
{
    private readonly List<PrepareNotice> _notices = [];

    /// <summary>How many bundles have news.</summary>
    public int Count => _notices.Count;

    /// <summary>The notices, oldest first.</summary>
    public IReadOnlyList<PrepareNotice> All => _notices;

    /// <summary>Adds a notice, replacing any earlier one for the same order.</summary>
    public void Add(PrepareNotice notice)
    {
        _notices.RemoveAll(held => string.Equals(held.OrderId, notice.OrderId, StringComparison.Ordinal));
        _notices.Add(notice);
    }

    /// <summary>Forgets every notice. Opening the vault is seeing them.</summary>
    public void Clear() => _notices.Clear();
}
