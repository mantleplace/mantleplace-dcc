using System.Globalization;

namespace MantlePlace.Core;

/// <summary>The import window's words for a fidelity level: its column, its set-all control and each choice.</summary>
public static partial class WindowLabels
{
    /// <summary>
    /// The heading over the level column: the glossary's term, with the words it rules out
    /// (<c>CONTEXT.md</c>).
    /// </summary>
    public const string LevelHeading = "Fidelity Level";

    /// <summary>The label beside the one control that sets every row's level at once.</summary>
    public const string SetAllLevels = "Set All To";

    /// <summary>What the set-all control shows while the rows are not all at one level.</summary>
    public const string MixedLevels = "Mixed";

    /// <summary>
    /// One choice in a row's level list: the level's token, what it is when it is another level's
    /// content, and what importing it is estimated to cost in this Revit.
    /// </summary>
    /// <param name="level">The level the choice is.</param>
    /// <param name="published">The level as the bundle publishes it.</param>
    /// <param name="isAvailable">Whether this plugin can import it.</param>
    /// <param name="cost">The count the import scales with: the level's own, or the level it is the same as.</param>
    /// <param name="estimate">That count times this Revit's measured cost per unit, or <c>null</c>.</param>
    /// <remarks>
    /// <para>
    /// <c>same_as</c> reads <c>Same as MAX</c> — the level's own name for it, which the dropdown
    /// shows rather than hides, so a MED that is MAX is not mistaken for a smaller import. Its reason
    /// follows in brackets in the curator's words; a reason this plugin does not know is left out and
    /// the level still reads as the same, which is what the manifest says to do.
    /// </para>
    /// <para>
    /// The count is the publisher's and the estimate is this host's (<see cref="SlowStepNotice.EstimateLevel"/>).
    /// A count in a unit this plugin does not know is shown bare, with no unit and no estimate.
    /// </para>
    /// </remarks>
    public static string LevelOption(
        FidelityLevel level,
        PublishedLevel published,
        bool isAvailable,
        CostDriver? cost,
        SlowStepNotice.LevelEstimate? estimate)
    {
        ArgumentNullException.ThrowIfNull(published);

        string text = FidelityLevelNames.Token(level);
        if (published is { Kind: FidelityLevelKind.SameAs, SameAs: { } target })
        {
            text += $": Same as {FidelityLevelNames.Token(target)}";
            if (published.Reason is { } reason)
            {
                text += $" ({SameAsReasonPhrase(reason)})";
            }
        }

        if (!isAvailable)
        {
            return text + " — not available in this version of Mantle Place";
        }

        if (cost is null)
        {
            return text;
        }

        text += " — " + CostText(cost);
        if (estimate is not null)
        {
            text += ", about " + Duration(estimate.Seconds);
            if (!estimate.Timed)
            {
                text += string.Format(CultureInfo.InvariantCulture, " (as timed in Revit {0})", estimate.Version);
            }
        }

        return text;
    }

    /// <summary>
    /// What a row of a 3.0 bundle says beside its box: what its file holds, what importing it is
    /// estimated to take in this Revit, and where that breaks the budget, the one plain line that says
    /// why the box starts unticked and that ticking it imports all of it.
    /// </summary>
    /// <param name="cost">The file's catalog count.</param>
    /// <param name="estimate">That count times this Revit's measured cost per unit.</param>
    /// <param name="overBudget">Whether the estimate breaks the budget (<see cref="SlowStepNotice.BreaksBudget"/>).</param>
    public static string EstimateNote(CostDriver cost, SlowStepNotice.LevelEstimate estimate, bool overBudget)
    {
        ArgumentNullException.ThrowIfNull(cost);
        ArgumentNullException.ThrowIfNull(estimate);

        string text = CostText(cost) + ", about " + Duration(estimate.Seconds);
        if (!estimate.Timed)
        {
            text += string.Format(CultureInfo.InvariantCulture, " (as timed in Revit {0})", estimate.Version);
        }

        return overBudget
            ? text + ". That is over five minutes, so it starts unticked. Tick it to import all of it."
            : text;
    }

    /// <summary>A <c>same_as</c> reason in the curator's words.</summary>
    public static string SameAsReasonPhrase(SameAsReason reason) => reason switch
    {
        SameAsReason.MaxOnly => "published at MAX only",
        SameAsReason.NotDerived => "not derived for this order",
        SameAsReason.CapNotReached => "nothing was capped",
        SameAsReason.NoReduction => "a smaller level would keep everything",
        SameAsReason.SmallestValidSize => "already the smallest useful size",
        _ => reason.ToString(),
    };

    private static string CostText(CostDriver cost)
    {
        string count = cost.Count.ToString("N0", CultureInfo.InvariantCulture);
        string noun = cost.Unit switch
        {
            CostUnit.Elements => cost.Count == 1 ? "element" : "elements",
            CostUnit.Cuts => cost.Count == 1 ? "subdivision" : "subdivisions",
            CostUnit.Triangles => cost.Count == 1 ? "triangle" : "triangles",
            CostUnit.Pixels => cost.Count == 1 ? "pixel" : "pixels",
            CostUnit.Posts => cost.Count == 1 ? "post" : "posts",

            // The manifest says to show a count in a unit nobody taught this plugin without a unit
            // and never a cost: a word this plugin cannot vouch for is not put in front of a curator.
            _ => string.Empty,
        };

        return noun.Length == 0 ? count : $"{count} {noun}";
    }

    /// <summary>A seconds range as the window says it: seconds under two minutes, minutes above.</summary>
    internal static string Duration(SlowStepNotice.SecondsRange range)
    {
        bool minutes = range.High >= 120;
        double scale = minutes ? 60 : 1;
        long low = Math.Max(1, (long)Math.Round(range.Low / scale, MidpointRounding.AwayFromZero));
        long high = Math.Max(low, (long)Math.Round(range.High / scale, MidpointRounding.AwayFromZero));
        string unit = minutes ? "minutes" : "seconds";
        return low == high
            ? string.Format(CultureInfo.InvariantCulture, "{0} {1}", low, high == 1 ? unit.TrimEnd('s') : unit)
            : string.Format(CultureInfo.InvariantCulture, "{0} to {1} {2}", low, high, unit);
    }

    /// <summary>
    /// The label beside a 3.0 order's level control, in every host's vault window and in the window a received send opens: the glossary's
    /// term, the same as the checklist column's.
    /// </summary>
    public const string DownloadLevelHeading = LevelHeading;

    /// <summary>The words that mark the level the platform recommends for this host and site.</summary>
    public const string RecommendedLevel = "recommended";

    /// <summary>
    /// One choice in a 3.0 order's level control: the level as the bundle spells it, the size of its
    /// download, and the recommendation when it is the one. <c>Med — 1.4 GB (recommended)</c>.
    /// </summary>
    public static string LevelChoice(FidelityLevel level, long sizeBytes, bool recommended)
    {
        string choice = $"{DownloadLevels.Word(level)} — {VaultRows.SizeText(sizeBytes)}";
        return recommended ? $"{choice} ({RecommendedLevel})" : choice;
    }

    /// <summary>The counts behind one level's size, for its tooltip.</summary>
    public static string LevelCounts(LevelSize size)
        => string.Format(
            CultureInfo.InvariantCulture,
            "{0}: {1} rows, {2} features, {3} triangles, {4} pixels",
            DownloadLevels.Word(size.Level),
            DownloadLevels.Count(size.Rows),
            DownloadLevels.Count(size.Features),
            DownloadLevels.Count(size.Triangles),
            DownloadLevels.Count(size.Pixels));

    /// <summary>What the window a received send opens says while the chosen level downloads into the slot.</summary>
    public static string LevelDownloading(FidelityLevel level)
        => $"Downloading the {DownloadLevels.Word(level)} level before Import…";

    /// <summary>What it says once the chosen level is in the slot.</summary>
    public static string LevelDownloaded(FidelityLevel level)
        => $"The {DownloadLevels.Word(level)} level is downloaded and checked. Import reads it.";

    /// <summary>What it says when the chosen level could not be downloaded: the slot still holds the level it had.</summary>
    public static string LevelNotDownloaded(FidelityLevel chosen, FidelityLevel held, string why)
        => $"The {DownloadLevels.Word(chosen)} level could not be downloaded ({why.Trim().TrimEnd('.')}). Import reads the {DownloadLevels.Word(held)} level already downloaded.";
}
