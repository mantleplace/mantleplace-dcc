namespace MantlePlace.Core;

public enum PrepareEnding
{
    Ready,

    Failed,

    StillPreparing,

    Cancelled,

    Interrupted,
}

public readonly record struct PrepareNotice(string OrderId, PrepareEnding Ending, string Text);

public static class PrepareNotices
{
    public const double ShowSeconds = 10.0;

    public static string LabelOf(string aoiLabel, string orderId)
        => string.IsNullOrWhiteSpace(aoiLabel) ? orderId : aoiLabel.Trim();

    public static PrepareNotice? For(
        string orderId,
        string label,
        PrepareEnding ending,
        string detail,
        bool vaultOpen,
        bool signedIn)
    {
        if (vaultOpen || ending is PrepareEnding.Cancelled or PrepareEnding.Interrupted)
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
                $"{label} is still being prepared. Open the vault later and press “{WindowLabels.PrepareForRevit}” to pick it up.",
            _ => string.IsNullOrWhiteSpace(detail)
                ? $"Couldn't prepare {label}."
                : $"Couldn't prepare {label}. {detail.Trim()}",
        };

        return new PrepareNotice(orderId, ending, text);
    }
}

public readonly record struct PendingNotice(string OrderId, string Text);

public sealed class PendingNotices
{
    private readonly List<PendingNotice> _notices = [];

    public int Count => _notices.Count;

    public IReadOnlyList<PendingNotice> All => _notices;

    public void Add(PrepareNotice notice) => Add(notice.OrderId, notice.Text);

    public void Add(NewsNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);

        foreach (string orderId in notice.OrderIds)
        {
            Add(orderId, notice.Text);
        }
    }

    private void Add(string orderId, string text)
    {
        _notices.RemoveAll(held => string.Equals(held.OrderId, orderId, StringComparison.Ordinal));
        _notices.Add(new PendingNotice(orderId, text));
    }

    public void Clear() => _notices.Clear();
}
