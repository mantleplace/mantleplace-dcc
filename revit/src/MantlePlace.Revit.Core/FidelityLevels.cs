using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>
/// A fidelity level, from most to least: the source-native data, the deliverable in full, and two
/// lighter cuts of it (MPB 1.9.0). The manifest's own words are <see cref="FidelityLevelNames.Token"/>.
/// </summary>
public enum FidelityLevel
{
    Raw,
    Max,
    Med,
    Min,
}

/// <summary>The manifest's words for a <see cref="FidelityLevel"/>, which are also the window's.</summary>
public static class FidelityLevelNames
{
    /// <summary>Every level, most to least, which is the order the window lists them in.</summary>
    public static IReadOnlyList<FidelityLevel> All { get; } = [FidelityLevel.Raw, FidelityLevel.Max, FidelityLevel.Med, FidelityLevel.Min];

    /// <summary>The level as the manifest and the window spell it: <c>RAW</c>, <c>MAX</c>, <c>MED</c>, <c>MIN</c>.</summary>
    public static string Token(FidelityLevel level) => level switch
    {
        FidelityLevel.Raw => "RAW",
        FidelityLevel.Max => "MAX",
        FidelityLevel.Med => "MED",
        FidelityLevel.Min => "MIN",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Not a fidelity level."),
    };

    /// <summary>Reads a manifest token, exactly as spelled; anything else is not a level.</summary>
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

/// <summary>What a published level is, as this host reads it.</summary>
public enum FidelityLevelKind
{
    /// <summary>The entry itself, in full.</summary>
    Max,

    /// <summary>A file of its own, named by a pointer that states its own frame.</summary>
    Pointer,

    /// <summary>The first N data rows of the MAX file.</summary>
    RowCut,

    /// <summary>The MAX file's features whose level field names this level or a lighter one.</summary>
    FieldCut,

    /// <summary>Identical to another level, which it names.</summary>
    SameAs,

    /// <summary>
    /// Published in a form this host cannot take: an unknown cut form, a malformed shape, or a
    /// <c>same_as</c> that chains. Offered as unavailable, never as the whole file.
    /// </summary>
    Unavailable,
}

/// <summary>The unit a level's cost driver counts in. <see cref="Unknown"/> is a word this host does not know.</summary>
public enum CostUnit
{
    Unknown,
    Elements,
    Cuts,
    Triangles,
    Pixels,
    Posts,
}

/// <summary>Why a level is the same as another, in the manifest's words; an unknown word reads as no reason.</summary>
public enum SameAsReason
{
    MaxOnly,
    NotDerived,
    CapNotReached,
    NoReduction,
    SmallestValidSize,
}

/// <summary>
/// The published count a level's import cost scales with. A host multiplies it by its own measured
/// unit cost; the manifest never states host time.
/// </summary>
/// <param name="Unit">The unit, or <see cref="CostUnit.Unknown"/> for a word this host does not know.</param>
/// <param name="RawUnit">The unit as published.</param>
/// <param name="Count">The count.</param>
/// <param name="AreaM2">The cuts' total area, published only with <see cref="CostUnit.Cuts"/>.</param>
public sealed record CostDriver(CostUnit Unit, string RawUnit, long Count, double? AreaM2);

/// <summary>One of an entry's four levels, as published.</summary>
public sealed class PublishedLevel
{
    public required FidelityLevel Level { get; init; }

    public required FidelityLevelKind Kind { get; init; }

    /// <summary>The level's cost driver, or <c>null</c> where none is published.</summary>
    public CostDriver? Cost { get; init; }

    /// <summary>A <see cref="FidelityLevelKind.Pointer"/> level's file, with the frame keys its pointer states.</summary>
    public BundleArtifact? File { get; init; }

    /// <summary>A pointer level's own ground extent, where its entry publishes one (a drape).</summary>
    public GroundExtent? Extent { get; init; }

    /// <summary>A <see cref="FidelityLevelKind.RowCut"/> level's row count.</summary>
    public int? Rows { get; init; }

    /// <summary>A <see cref="FidelityLevelKind.FieldCut"/> level's field name.</summary>
    public string? LowestLevelField { get; init; }

    /// <summary>A <see cref="FidelityLevelKind.SameAs"/> level's target.</summary>
    public FidelityLevel? SameAs { get; init; }

    /// <summary>A <see cref="FidelityLevelKind.SameAs"/> level's reason, or <c>null</c> for one this host does not know.</summary>
    public SameAsReason? Reason { get; init; }
}

/// <summary>An entry's four published levels, RAW to MIN.</summary>
/// <remarks>
/// Built only by <see cref="FidelityLevelsReader"/>, which guarantees all four are present and that
/// a <see cref="FidelityLevelKind.SameAs"/> level names a level that is not itself one.
/// </remarks>
public sealed class FidelityLevels
{
    private readonly Dictionary<FidelityLevel, PublishedLevel> _levels;

    internal FidelityLevels(IEnumerable<PublishedLevel> levels) => _levels = levels.ToDictionary(level => level.Level);

    /// <summary>The four levels, most to least.</summary>
    public IReadOnlyList<PublishedLevel> All => [.. FidelityLevelNames.All.Select(level => _levels[level])];

    /// <summary>The level as published.</summary>
    public PublishedLevel this[FidelityLevel level] => _levels[level];

    /// <summary>
    /// The level whose content <paramref name="level"/> imports: itself, or the one its
    /// <c>same_as</c> names. Never more than one hop, because <c>same_as</c> never chains.
    /// </summary>
    public PublishedLevel Resolve(FidelityLevel level)
    {
        PublishedLevel published = _levels[level];
        return published is { Kind: FidelityLevelKind.SameAs, SameAs: { } target } ? _levels[target] : published;
    }
}

/// <summary>A level as the planner applied it to one step.</summary>
/// <param name="Chosen">The level the curator chose.</param>
/// <param name="Effective">The level whose content is imported: <paramref name="Chosen"/>, or the one its <c>same_as</c> names.</param>
/// <param name="Kind">What <paramref name="Effective"/> is.</param>
/// <param name="Rows">A row cut's row count.</param>
/// <param name="LowestLevelField">A field cut's field.</param>
public sealed record AppliedLevel(
    FidelityLevel Chosen,
    FidelityLevel Effective,
    FidelityLevelKind Kind,
    int? Rows = null,
    string? LowestLevelField = null);

/// <summary>What applying a level to an entry gave the planner: the file to plan from, or why there is none.</summary>
/// <param name="File">The file the step reads: the entry's own, or a pointer level's. <c>null</c> when refused.</param>
/// <param name="Applied">The level as applied; <c>null</c> for an entry that publishes no levels.</param>
/// <param name="Refusal">Why the chosen level cannot be imported, or <c>null</c>.</param>
public sealed record LevelApplication(BundleArtifact? File, AppliedLevel? Applied, string? Refusal);

/// <summary>
/// How a chosen level is applied, verbatim: the first N rows, the features a field keeps, or the
/// file a pointer names. Nothing here computes a subset, re-ranks, or filters spatially.
/// </summary>
public static class FidelityCuts
{
    /// <summary>
    /// Whether a step of <paramref name="kind"/> can take a level of the form <paramref name="level"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A row cut is the tree points' — a ranked row file whose row number is the rank. A field cut is
    /// taken by the two subdivision steps, which cut every feature they keep and stamp each by its
    /// place in the whole layer, so a cut is applied after the stamps are named
    /// (<see cref="KeptCuts"/>). Every other step reads its file whole — the land and hazard plans
    /// among them, which draw one plan per build and never redraw it — so a cut there is offered as
    /// unavailable rather than imported as the whole file.
    /// </para>
    /// <para>
    /// <c>MAX</c> is taken by every step, and so is a <c>same_as</c>, which is the level it names. A
    /// pointer is taken by every step that places one file by its own pointer — the vector layers,
    /// the trees, the land and hazard plans, the published contours and the drape — where its file is planned
    /// as the entry's would be, refusals and all. The terrain's tiers and the site model's two steps
    /// read their entry's file by its published name and compare tiers by it, so a pointer there is
    /// unavailable until they are taught to plan one.
    /// </para>
    /// </remarks>
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

    /// <summary>Whether <paramref name="level"/> of an entry can be imported by a step of <paramref name="kind"/>.</summary>
    public static bool IsAvailable(FidelityLevels levels, FidelityLevel level, ImportStepKind kind)
    {
        ArgumentNullException.ThrowIfNull(levels);
        return CanTake(kind, levels.Resolve(level).Kind);
    }

    /// <summary>
    /// The file a step of <paramref name="kind"/> plans from at <paramref name="chosen"/>, or why it
    /// cannot.
    /// </summary>
    /// <param name="label">The layer's noun, for the refusal.</param>
    public static LevelApplication Apply(BundleArtifact artifact, ImportStepKind kind, FidelityLevel chosen, string label)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        // An entry that publishes no levels is MAX only, whatever was chosen: the window offers no
        // level for it, and an unattended import asks for MAX.
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

    /// <summary>
    /// Whether a feature whose level field reads <paramref name="featureLevel"/> is kept at
    /// <paramref name="effective"/>: the field names the lowest level a feature belongs to, so a
    /// feature is kept when that level is <paramref name="effective"/> or a lighter one, in the order
    /// MIN, MED, MAX.
    /// </summary>
    /// <remarks>
    /// A value that names no level this host knows, or none at all, is not kept: the cut says which
    /// features belong, and keeping one it does not name would import more than the level.
    /// </remarks>
    public static bool Keeps(string? featureLevel, FidelityLevel effective)
        => FidelityLevelNames.TryParse(featureLevel, out FidelityLevel lowest)
           && Lightness(lowest) is { } feature
           && Lightness(effective) is { } chosen
           && feature <= chosen;

    /// <summary>MIN 0, MED 1, MAX 2; RAW is never a cut's value.</summary>
    private static int? Lightness(FidelityLevel level) => level switch
    {
        FidelityLevel.Min => 0,
        FidelityLevel.Med => 1,
        FidelityLevel.Max => 2,
        _ => null,
    };

    /// <summary>
    /// Which of a subdivision layer's cuts a field cut keeps, by index into the whole layer.
    /// </summary>
    /// <param name="cutLevels">Each cut's level field, in the order the layer's cuts are named and stamped.</param>
    /// <param name="applied">The level applied, or <c>null</c> for an entry with no levels — every cut kept.</param>
    public static IReadOnlyList<bool> KeptCuts(IReadOnlyList<string?> cutLevels, AppliedLevel? applied)
    {
        ArgumentNullException.ThrowIfNull(cutLevels);
        return applied is { Kind: FidelityLevelKind.FieldCut, Effective: var effective }
            ? [.. cutLevels.Select(level => Keeps(level, effective))]
            : [.. cutLevels.Select(_ => true)];
    }

    /// <summary>
    /// Which of a subdivision layer's missing cuts this import makes, at the level applied, and what
    /// it says about the rest.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The stamps are named over the WHOLE layer first (<see cref="SiteBoundaryIdentity.NewFeatures"/>)
    /// and the cut applied after, so a feature's stamp is the same at every level: a later import at a
    /// higher level recognises what this one cut, and cuts only the rest.
    /// </para>
    /// <para>
    /// A cut above the level that an earlier import at a higher level made is kept, and counted: a
    /// subdivision's level is fixed when it is cut, and nothing here deletes
    /// (<c>docs/adr/0004-revit-terrain-identity.md</c>).
    /// </para>
    /// </remarks>
    /// <param name="cutLevels">Each cut's level field, over the whole layer, in cut order.</param>
    /// <param name="missing">The cuts not yet on the terrain, over the whole layer.</param>
    /// <param name="applied">The level applied, or <c>null</c> for a layer with no levels.</param>
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

    /// <summary>
    /// The clause a step's summary adds for what its level left out and kept, or empty for an import
    /// that left nothing out.
    /// </summary>
    /// <param name="noun">The elements, plural, in the summary's own words.</param>
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
        return keptAboveLevel > 0
            ? clause + string.Format(
                CultureInfo.InvariantCulture,
                "; {0:N0} {1} above the {2} level, from an earlier import of this bundle, were kept, because a lower level never deletes",
                keptAboveLevel,
                noun,
                level)
            : clause;
    }

    /// <summary>
    /// The log's line for a step imported at a level other than MAX, or <c>null</c> at MAX and for an
    /// entry with no levels — the unattended import, and every bundle before levels, log nothing new.
    /// </summary>
    /// <param name="stepName">The step's name in the window (<see cref="WindowLabels.StepName"/>).</param>
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

    /// <summary>
    /// What the planting step says about its level, or empty at MAX with nothing kept above it: the
    /// rows a row cut read, and the trees an earlier import at a higher level placed and this one kept.
    /// </summary>
    /// <param name="applied">The level applied, or <c>null</c> for tree points with no levels.</param>
    /// <param name="keptAboveLevel">This build's trees already in the project beyond the rows read (<see cref="TreeDecision.KeptAboveLevel"/>).</param>
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
        string kept = keptAboveLevel > 0
            ? string.Format(
                CultureInfo.InvariantCulture,
                "{0:N0} trees above the {1} level, from an earlier import of this bundle, were kept, because a lower level never deletes.",
                keptAboveLevel,
                level)
            : string.Empty;
        return string.Join(" ", new[] { read, kept }.Where(part => part.Length > 0));
    }
}

/// <summary>What a subdivision step cuts at the level applied, and what it says about the rest.</summary>
/// <param name="ToCut">The missing cuts the level keeps, in layer order.</param>
/// <param name="AlreadyPresent">Cuts the level keeps that an earlier import already made.</param>
/// <param name="LeftOut">Missing cuts above the level, not cut.</param>
/// <param name="KeptAboveLevel">Cuts above the level that an earlier import made, kept.</param>
public sealed record LevelCutSubdivisions(IReadOnlyList<NewSiteBoundary> ToCut, int AlreadyPresent, int LeftOut, int KeptAboveLevel);
