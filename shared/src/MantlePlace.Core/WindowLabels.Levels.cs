using System.Globalization;

namespace MantlePlace.Core;

public static partial class WindowLabels
{
    public const string LevelHeading = "Fidelity Level";

    public const string SetAllLevels = "Set All To";

    public const string MixedLevels = "Mixed";

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

            _ => string.Empty,
        };

        return noun.Length == 0 ? count : $"{count} {noun}";
    }

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

    public const string DownloadLevelHeading = LevelHeading;

    public const string LockedExtrasWords = "More Extras come with paid sites";

    public static string LockedExtraRow(string name) => $"{name} — {LockedExtrasWords}";

    public const string RecommendedLevel = "recommended";

    public static string LevelChoice(FidelityLevel level, long sizeBytes, bool recommended)
    {
        string choice = $"{DownloadLevels.Word(level)} — {VaultRows.SizeText(sizeBytes)}";
        return recommended ? $"{choice} ({RecommendedLevel})" : choice;
    }

    public static string LevelCounts(LevelSize size)
        => string.Format(
            CultureInfo.InvariantCulture,
            "{0}: {1} rows, {2} features, {3} triangles, {4} pixels",
            DownloadLevels.Word(size.Level),
            DownloadLevels.Count(size.Rows),
            DownloadLevels.Count(size.Features),
            DownloadLevels.Count(size.Triangles),
            DownloadLevels.Count(size.Pixels));

    public static string LevelDownloading(FidelityLevel level)
        => $"Downloading the {DownloadLevels.Word(level)} level before Import…";

    public static string LevelDownloaded(FidelityLevel level)
        => $"The {DownloadLevels.Word(level)} level is downloaded and checked. Import reads it.";

    public static string LevelNotDownloaded(FidelityLevel chosen, FidelityLevel held, string why)
        => $"The {DownloadLevels.Word(chosen)} level could not be downloaded ({why.Trim().TrimEnd('.')}). Import reads the {DownloadLevels.Word(held)} level already downloaded.";
}
