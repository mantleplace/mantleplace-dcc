using System.Globalization;

namespace MantlePlace.Core;

public static class PrepareMessages
{
    public static string Starting(string label) => $"Preparing {label}…";

    public static string AlreadyPreparing(string label)
        => $"Already preparing {label} — following that rather than starting it again.";

    public static string Began(MaterializeStart start) => start.Outcome switch
    {
        MaterializeStartOutcome.Joined =>
            "This bundle was already being prepared — following that job rather than starting a second.",
        _ => "Preparing your Revit deliverables…",
    };

    public static string NothingToDo(MaterializeStart start)
    {
        HashSet<string> delivered = new(start.Tokens, StringComparer.Ordinal);
        int absent = MaterializeJobs.RevitTokens.Count(token => !delivered.Contains(token));

        return absent == 0
            ? "Everything Revit needs is already built. Fetching it…"
            : $"Everything available for this area is already built. {absent} of the Revit "
                + "deliverables aren't in this bundle; the import will say why. Fetching it…";
    }

    public static string Gaps(IReadOnlyList<MissingDeliverable> gaps)
    {
        ArgumentNullException.ThrowIfNull(gaps);

        IEnumerable<string> clauses = gaps.Select(gap =>
            ReadinessReasons.ClauseFor(gap.Reason) is { } clause
                ? $"{gap.Token} ({clause})"
                : gap.Token);

        return $"Not available for this area: {string.Join("; ", clauses)}.";
    }

    public static string Progress(MaterializeStatus status, string label)
    {
        string progress = status.Fraction < 0
            ? "working"
            : (status.Fraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

        return status.Message.Length > 0
            ? $"Preparing {label}: {status.Message} ({progress})."
            : $"Preparing {label}: {status.State} ({progress}).";
    }

    public const string RowPreparing = "Preparing…";

    public const string Cancelled = "Cancelled. Nothing was left half-downloaded.";
}
