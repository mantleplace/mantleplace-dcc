using System.Globalization;

namespace MantlePlace.Core;

public enum FidelityLevel
{
    Raw,
    Max,
    Med,
    Min,
}

public static class FidelityLevelNames
{
    public static IReadOnlyList<FidelityLevel> All { get; } = [FidelityLevel.Raw, FidelityLevel.Max, FidelityLevel.Med, FidelityLevel.Min];

    public static string Token(FidelityLevel level) => level switch
    {
        FidelityLevel.Raw => "RAW",
        FidelityLevel.Max => "MAX",
        FidelityLevel.Med => "MED",
        FidelityLevel.Min => "MIN",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Not a fidelity level."),
    };

    public static bool TryParse(string? token, out FidelityLevel level)
    {
        foreach (FidelityLevel candidate in All)
        {
            if (string.Equals(token, Token(candidate), StringComparison.Ordinal))
            {
                level = candidate;
                return true;
            }
        }

        level = FidelityLevel.Max;
        return false;
    }
}

public enum FidelityLevelKind
{
    Max,

    Pointer,

    RowCut,

    FieldCut,

    SameAs,

    Unavailable,
}

public enum CostUnit
{
    Unknown,
    Elements,
    Cuts,
    Triangles,
    Pixels,
    Posts,
}

public enum SameAsReason
{
    MaxOnly,
    NotDerived,
    CapNotReached,
    NoReduction,
    SmallestValidSize,
}

public sealed record CostDriver(CostUnit Unit, string RawUnit, long Count);

public sealed class PublishedLevel
{
    public required FidelityLevel Level { get; init; }

    public required FidelityLevelKind Kind { get; init; }

    public CostDriver? Cost { get; init; }

    public BundleArtifact? File { get; init; }

    public GroundExtent? Extent { get; init; }

    public int? Rows { get; init; }

    public string? LowestLevelField { get; init; }

    public FidelityLevel? SameAs { get; init; }

    public SameAsReason? Reason { get; init; }
}

public sealed class FidelityLevels
{
    private readonly Dictionary<FidelityLevel, PublishedLevel> _levels;

    internal FidelityLevels(IEnumerable<PublishedLevel> levels) => _levels = levels.ToDictionary(level => level.Level);

    public IReadOnlyList<PublishedLevel> All => [.. FidelityLevelNames.All.Select(level => _levels[level])];

    public PublishedLevel this[FidelityLevel level] => _levels[level];

    public PublishedLevel Resolve(FidelityLevel level)
    {
        PublishedLevel published = _levels[level];
        return published is { Kind: FidelityLevelKind.SameAs, SameAs: { } target } ? _levels[target] : published;
    }
}

public sealed record AppliedLevel(
    FidelityLevel Chosen,
    FidelityLevel Effective,
    FidelityLevelKind Kind,
    int? Rows = null,
    string? LowestLevelField = null);

public sealed record LevelApplication(BundleArtifact? File, AppliedLevel? Applied, string? Refusal);

public static class FidelityCuts
{
    public static bool CanTake(ImportStepKind kind, FidelityLevelKind level) => level switch
    {
        FidelityLevelKind.Max => true,
        FidelityLevelKind.Pointer => kind is ImportStepKind.RoadCentrelines or ImportStepKind.LandUse
            or ImportStepKind.LandCover or ImportStepKind.Water or ImportStepKind.RoadPolygons
            or ImportStepKind.Vegetation or ImportStepKind.FloodZones or ImportStepKind.SteepGround
            or ImportStepKind.PublishedContours or ImportStepKind.ImageryDrape,
        FidelityLevelKind.RowCut => kind == ImportStepKind.Vegetation,
        FidelityLevelKind.FieldCut => kind is ImportStepKind.RoadPolygons or ImportStepKind.Water,
        _ => false,
    };

    public static IReadOnlyList<string> LevelFiles(BundleArtifact entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Levels is not { } levels)
        {
            return [];
        }

        return
        [
            .. new[] { entry.Sha256 }
                .Concat(levels.All.Where(level => level.Kind == FidelityLevelKind.Pointer).Select(level => level.File?.Sha256))
                .OfType<string>()
                .Distinct(StringComparer.Ordinal),
        ];
    }

    public static bool IsAvailable(FidelityLevels levels, FidelityLevel level, ImportStepKind kind)
    {
        ArgumentNullException.ThrowIfNull(levels);
        return CanTake(kind, levels.Resolve(level).Kind);
    }

    public static LevelApplication Apply(BundleArtifact artifact, ImportStepKind kind, FidelityLevel chosen, string label)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        if (artifact.Levels is not { } levels)
        {
            return new LevelApplication(artifact, null, null);
        }

        PublishedLevel effective = levels.Resolve(chosen);
        if (!CanTake(kind, effective.Kind))
        {
            return new LevelApplication(
                null,
                null,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "The {0} were left out: their {1} level is published in a form this plugin cannot import, "
                        + "and importing the whole file in its place would not be the level chosen. Choose "
                        + "another level for them and import again.",
                    label,
                    FidelityLevelNames.Token(chosen)));
        }

        AppliedLevel applied = new(chosen, effective.Level, effective.Kind, effective.Rows, effective.LowestLevelField);
        return new LevelApplication(effective.Kind == FidelityLevelKind.Pointer ? effective.File : artifact, applied, null);
    }

    public static bool Keeps(string? featureLevel, FidelityLevel effective)
        => FidelityLevelNames.TryParse(featureLevel, out FidelityLevel lowest)
           && Lightness(lowest) is { } feature
           && Lightness(effective) is { } chosen
           && feature <= chosen;

    private static int? Lightness(FidelityLevel level) => level switch
    {
        FidelityLevel.Min => 0,
        FidelityLevel.Med => 1,
        FidelityLevel.Max => 2,
        _ => null,
    };

    public static IReadOnlyList<bool> KeptCuts(IReadOnlyList<string?> cutLevels, AppliedLevel? applied)
    {
        ArgumentNullException.ThrowIfNull(cutLevels);
        return applied is { Kind: FidelityLevelKind.FieldCut, Effective: var effective }
            ? [.. cutLevels.Select(level => Keeps(level, effective))]
            : [.. cutLevels.Select(_ => true)];
    }

    public static LevelCutSubdivisions Subdivisions(
        IReadOnlyList<string?> cutLevels,
        IReadOnlyList<NewSiteBoundary> missing,
        AppliedLevel? applied)
    {
        ArgumentNullException.ThrowIfNull(missing);
        IReadOnlyList<bool> kept = KeptCuts(cutLevels, applied);
        List<NewSiteBoundary> toCut = [.. missing.Where(boundary => kept[boundary.Ordinal - 1])];
        int keptCount = kept.Count(keep => keep);
        int leftOut = missing.Count - toCut.Count;
        return new LevelCutSubdivisions(
            toCut,
            AlreadyPresent: keptCount - toCut.Count,
            LeftOut: leftOut,
            KeptAboveLevel: kept.Count - keptCount - leftOut);
    }

    public static string LevelClause(AppliedLevel? applied, int leftOut, int keptAboveLevel, string noun)
    {
        if (applied is null || (leftOut == 0 && keptAboveLevel == 0))
        {
            return string.Empty;
        }

        string level = FidelityLevelNames.Token(applied.Chosen);
        string clause = leftOut > 0
            ? string.Format(CultureInfo.InvariantCulture, "; {0:N0} {1} above the {2} level were left out", leftOut, noun, level)
            : string.Empty;
        return keptAboveLevel > 0 ? clause + "; " + KeptAbove(keptAboveLevel, noun, level) : clause;
    }

    private static string KeptAbove(int kept, string noun, string level) => string.Format(
        CultureInfo.InvariantCulture,
        "{0:N0} {1} above the {2} level, from an earlier import of this bundle, were kept, because a lower level never deletes",
        kept,
        noun,
        level);

    public static string? LevelLogLine(string stepName, AppliedLevel? applied)
    {
        if (applied is null || applied.Chosen == FidelityLevel.Max)
        {
            return null;
        }

        string chosen = FidelityLevelNames.Token(applied.Chosen);
        return applied.Effective == applied.Chosen
            ? string.Format(CultureInfo.InvariantCulture, "{0}: imported at the {1} level.", stepName, chosen)
            : string.Format(
                CultureInfo.InvariantCulture,
                "{0}: imported at the {1} level, which the bundle publishes as the same as {2}.",
                stepName,
                chosen,
                FidelityLevelNames.Token(applied.Effective));
    }

    public static string RowCutSentence(AppliedLevel? applied, int keptAboveLevel)
    {
        if (applied is null)
        {
            return string.Empty;
        }

        string level = FidelityLevelNames.Token(applied.Chosen);
        string read = applied is { Kind: FidelityLevelKind.RowCut, Rows: { } rows }
            ? string.Format(
                CultureInfo.InvariantCulture,
                "The tree points were read at the {0} level: the first {1:N0} rows of the ranked file.",
                level,
                rows)
            : string.Empty;
        string kept = keptAboveLevel > 0 ? KeptAbove(keptAboveLevel, "trees", level) + "." : string.Empty;
        return string.Join(" ", new[] { read, kept }.Where(part => part.Length > 0));
    }
}

public sealed record LevelCutSubdivisions(IReadOnlyList<NewSiteBoundary> ToCut, int AlreadyPresent, int LeftOut, int KeptAboveLevel);
