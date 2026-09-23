using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>
/// The lines a Prepare says as it goes — the vault window's status line while the window is open,
/// and the last one is what the window shows for a Prepare it reopens onto. Pure.
/// </summary>
/// <remarks>
/// These lived in the vault window while the window owned the Prepare. The Prepare now outlives the
/// window, so its words moved with it, and out of the one assembly CI never builds.
/// </remarks>
public static class PrepareMessages
{
    /// <summary>The first line, before the platform has answered.</summary>
    public static string Starting(string label) => $"Preparing {label}…";

    /// <summary>A second Prepare on an order already being watched.</summary>
    public static string AlreadyPreparing(string label)
        => $"Already preparing {label} — following that rather than starting it again.";

    /// <summary>What the platform said to the start, for the three outcomes that poll.</summary>
    public static string Began(MaterializeStart start) => start.Outcome switch
    {
        MaterializeStartOutcome.Joined =>
            "This bundle was already being prepared — following that job rather than starting a second.",
        MaterializeStartOutcome.Queued =>
            "Your order is still being built. Your Revit deliverables are queued and start on their own as soon as it finishes.",
        _ => "Preparing your Revit deliverables…",
    };

    /// <summary>What to say when the platform had nothing to build.</summary>
    /// <remarks>
    /// The response names what IS delivered and gives no reasons for the rest, so this states the
    /// shortfall without inventing a cause for it. The per-artifact reason arrives at import time
    /// from <c>hosts.revit.readiness</c>, which is the field that actually knows.
    /// </remarks>
    public static string NothingToDo(MaterializeStart start)
    {
        HashSet<string> delivered = new(start.Tokens, StringComparer.Ordinal);
        int absent = MaterializeJobs.RevitTokens.Count(token => !delivered.Contains(token));

        return absent == 0
            ? "Everything Revit needs is already built. Fetching it…"
            : $"Everything available for this area is already built. {absent} of the Revit "
                + "deliverables aren't in this bundle; the import will say why. Fetching it…";
    }

    /// <summary>The platform's own reason for each deliverable it will never produce here.</summary>
    /// <remarks>
    /// A gap, not a failure: waiting for one is waiting forever, so it is said and stepped over.
    /// </remarks>
    public static string Gaps(IReadOnlyList<MissingDeliverable> gaps)
    {
        ArgumentNullException.ThrowIfNull(gaps);

        IEnumerable<string> clauses = gaps.Select(gap =>
            ReadinessReasons.ClauseFor(gap.Reason) is { } clause
                ? $"{gap.Token} ({clause})"
                : gap.Token);

        return $"Not available for this area: {string.Join("; ", clauses)}.";
    }

    /// <summary>One poll, as the status line says it.</summary>
    public static string Progress(MaterializeStatus status, string label)
    {
        // Indeterminate is NOT zero. A progress bar sitting at 0% and a spinner say different
        // things to a curator deciding whether to wait.
        string progress = status.Fraction < 0
            ? "working"
            : (status.Fraction * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

        // The platform's own sentence when it gave one — "Building 3 deliverable(s)…" beats the bare
        // state name, and on Unknown it is the difference between a spinner and an explanation.
        return status.Message.Length > 0
            ? $"Preparing {label}: {status.Message} ({progress})."
            : $"Preparing {label}: {status.State} ({progress}).";
    }

    /// <summary>What a vault row being prepared adds to itself.</summary>
    public const string RowPreparing = "Preparing…";

    /// <summary>A Prepare the curator cancelled.</summary>
    public const string Cancelled = "Cancelled. Nothing was left half-downloaded.";
}
