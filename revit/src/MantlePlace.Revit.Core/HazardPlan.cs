using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>One of the layers a hazard plan draws.</summary>
public enum HazardLayer
{
    FloodZones,
    SteepGround,
}

/// <summary>What the project already has under a hazard plan's name.</summary>
public enum HazardViewFound
{
    /// <summary>No view by that name.</summary>
    None,

    /// <summary>A plan view by that name — the one an earlier import of this build made.</summary>
    PlanView,

    /// <summary>A view by that name this plugin cannot draw into: a template, or another kind of view.</summary>
    SomethingElse,
}

/// <summary>What a hazard step does to its build's plan, and the sentence when it draws nothing.</summary>
public sealed class HazardPlanDecision
{
    /// <summary>Whether the plan has to be made before anything is drawn.</summary>
    public required bool CreateView { get; init; }

    /// <summary>Whether this layer is drawn.</summary>
    public required bool Draw { get; init; }

    /// <summary>How many of this layer's regions the plan already holds.</summary>
    public int AlreadyDrawn { get; init; }

    /// <summary>One line for the log when nothing is drawn; empty otherwise.</summary>
    public string Explanation { get; init; } = string.Empty;
}

/// <summary>Where the next zone key row starts: its swatch's top-left corner, in frame-local metres.</summary>
public readonly record struct KeyAnchor(double EastM, double NorthM);

/// <summary>One zone key row placed in the plan, in frame-local metres.</summary>
/// <param name="Swatch">The swatch's rectangle; unused by a heading row.</param>
/// <param name="TextEastM">Where the row's text starts.</param>
/// <param name="NorthM">The row's top edge, which the text hangs from.</param>
public readonly record struct KeyRowPlacement(FootprintExtent Swatch, double TextEastM, double NorthM);

/// <summary>
/// The hazard plan: a flat plan per build where the flood zones and the steep ground are drawn as
/// context, apart from the terrain and the model a visualiser renders. Its name, the stamps on what
/// is drawn in it, whether a layer is drawn, and where its zone key goes. Pure.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>A plan is never redrawn.</b> A curator may have annotated it or put it on a sheet, so a layer
/// already on it is left alone, and a later build gets a plan of its own named with that build. What
/// a re-import of the same build may do is add a layer the plan does not hold yet — both rows start
/// unticked, so adding them one import at a time is the likely path. That differs on purpose from
/// the site-context view, which is reused by name: that view holds a filter, and this one holds a
/// build's drawing.
/// </para>
/// <para>
/// Each layer's regions carry their own stamp, in Comments, and the plan is found by its name. The
/// zone key's swatches carry a third stamp, which is how a re-import finds where the key ends.
/// </para>
/// </remarks>
public static class HazardPlan
{
    /// <summary>The scale a plan with nothing to fit is drawn at.</summary>
    public const int DefaultScale = 1000;

    /// <summary>The zone key's row pitch on paper, in millimetres.</summary>
    public const double RowPitchMm = 9.0;

    private const string ViewNamePrefix = "Mantle Place Hazard Plan ";

    private const int MaxBuildTokenLength = 32;

    /// <summary>How much paper the site may span, in millimetres, leaving room beside it for the key.</summary>
    private const double SiteSpanMm = 600.0;

    private const double SwatchWidthMm = 10.0;

    private const double SwatchHeightMm = 6.0;

    private const double TextGapMm = 4.0;

    private const double KeyMarginMm = 15.0;

    /// <summary>The engineering and metric scales a sheet is commonly drawn at, smallest first.</summary>
    private static readonly int[] Scales = [100, 200, 500, 1000, 2000, 2500, 5000, 10000, 20000, 25000, 50000];

    /// <summary>
    /// The build a plan belongs to, as a short token: a job id's first group when it is a UUID, the
    /// whole id otherwise, with any character a Revit name refuses replaced.
    /// </summary>
    /// <remarks>
    /// The job, because a job identifies a build (<c>CONTEXT.md</c>) and both hazard layers of one
    /// build share one plan. A manifest with no job id gets a token that says so rather than one
    /// borrowed from a file hash, which the two layers would not share.
    /// </remarks>
    public static string BuildToken(string? jobId)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return "unidentified";
        }

        string trimmed = jobId.Trim();
        if (Guid.TryParse(trimmed, out _))
        {
            return trimmed[..8].ToLowerInvariant();
        }

        string legal = new([.. trimmed.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' ? character : '-')]);
        return legal.Length > MaxBuildTokenLength ? legal[..MaxBuildTokenLength] : legal;
    }

    /// <summary>The plan's name, and the identity a re-import finds it by.</summary>
    public static string ViewName(string build) => ViewNamePrefix + build;

    /// <summary>The stamp every region of <paramref name="layer"/> on this build's plan carries.</summary>
    public static string Stamp(HazardLayer layer, string cacheKeyStem, string build)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        string noun = layer == HazardLayer.FloodZones ? "Flood Zones" : "Steep Ground";
        return $"{SiteContext.StampPrefix} {noun} {cacheKeyStem}/{build}";
    }

    /// <summary>The stamp every swatch of this build's zone key carries.</summary>
    public static string KeyStamp(string cacheKeyStem, string build)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        return $"{SiteContext.StampPrefix} Zone Key {cacheKeyStem}/{build}";
    }

    /// <summary>What a hazard step does, given what the project holds.</summary>
    /// <param name="found">What the project has under <see cref="ViewName"/>.</param>
    /// <param name="regionComments">
    /// The Comments of the regions already in that plan. Anything that is not this layer's stamp for
    /// this build is ignored, so a caller may pass more than it needs to.
    /// </param>
    public static HazardPlanDecision Decide(
        HazardViewFound found,
        IEnumerable<string?> regionComments,
        HazardLayer layer,
        string cacheKeyStem,
        string build)
    {
        ArgumentNullException.ThrowIfNull(regionComments);

        string view = ViewName(build);
        if (found == HazardViewFound.SomethingElse)
        {
            return new HazardPlanDecision
            {
                CreateView = false,
                Draw = false,
                Explanation = $"A view named \"{view}\" is already in this project and is not a plan this plugin "
                    + "can draw into, so nothing was drawn. Rename that view and import again.",
            };
        }

        string stamp = Stamp(layer, cacheKeyStem, build);
        int present = found == HazardViewFound.PlanView
            ? regionComments.Count(comments => string.Equals(comments, stamp, StringComparison.Ordinal))
            : 0;

        if (present > 0)
        {
            return new HazardPlanDecision
            {
                CreateView = false,
                Draw = false,
                AlreadyDrawn = present,
                Explanation = string.Format(
                    CultureInfo.InvariantCulture,
                    "This build's {0} already on \"{1}\" ({2:N0} region(s)), so it was left alone and nothing "
                        + "was drawn over it.",
                    layer == HazardLayer.FloodZones ? "flood zones are" : "steep ground is",
                    view,
                    present),
            };
        }

        return new HazardPlanDecision { CreateView = found == HazardViewFound.None, Draw = true };
    }

    /// <summary>
    /// The first common scale at which <paramref name="extent"/> spans no more than
    /// <see cref="SiteSpanMm"/> of paper, or <see cref="DefaultScale"/> with nothing to fit.
    /// </summary>
    public static int ScaleFor(FootprintExtent? extent)
    {
        if (extent is not { IsMeasurable: true } measured)
        {
            return DefaultScale;
        }

        double spanMm = Math.Max(measured.WidthM, measured.DepthM) * 1000.0;
        foreach (int scale in Scales)
        {
            if (spanMm / scale <= SiteSpanMm)
            {
                return scale;
            }
        }

        return Scales[^1];
    }

    /// <summary>Where the next key row starts.</summary>
    /// <param name="crop">The plan's crop, when it has one.</param>
    /// <param name="drawn">What this step drew, for a plan with no crop.</param>
    /// <param name="existingKey">The box around the key's swatches already on the plan, or <c>null</c>.</param>
    /// <param name="scale">The plan's scale.</param>
    /// <remarks>
    /// A key already on the plan is continued below its last row, in its own column. A first key
    /// starts beside the crop, level with its top; with no crop, beside what was drawn.
    /// </remarks>
    public static KeyAnchor KeyAnchorFor(FootprintExtent? crop, FootprintExtent? drawn, FootprintExtent? existingKey, int scale)
    {
        if (existingKey is { } key)
        {
            return new KeyAnchor(key.MinEastM, key.MinNorthM - PaperToModel(RowPitchMm - SwatchHeightMm, scale));
        }

        if ((crop ?? drawn) is { } beside)
        {
            return new KeyAnchor(beside.MaxEastM + PaperToModel(KeyMarginMm, scale), beside.MaxNorthM);
        }

        return new KeyAnchor(0.0, 0.0);
    }

    /// <summary><paramref name="rowCount"/> rows from <paramref name="anchor"/> down, one pitch apart.</summary>
    public static IReadOnlyList<KeyRowPlacement> LayoutKey(KeyAnchor anchor, int rowCount, int scale)
    {
        double pitch = PaperToModel(RowPitchMm, scale);
        double width = PaperToModel(SwatchWidthMm, scale);
        double height = PaperToModel(SwatchHeightMm, scale);
        double gap = PaperToModel(TextGapMm, scale);

        List<KeyRowPlacement> rows = [];
        for (int row = 0; row < rowCount; row++)
        {
            double top = anchor.NorthM - (row * pitch);
            rows.Add(new KeyRowPlacement(
                new FootprintExtent(anchor.EastM, top - height, anchor.EastM + width, top),
                anchor.EastM + width + gap,
                top));
        }

        return rows;
    }

    /// <summary>
    /// The regions a parsed hazard layer asks for: one per published polygon, its holes left out of it.
    /// </summary>
    /// <remarks>
    /// Whole polygons, as the water bodies are cut: the schema says the hazard layers keep their holes
    /// and multipolygon parts as published, and a hole in a flood zone is ground the zone does not
    /// cover. Grouped by the polygon each ring came out of, so a polygon whose outer ring the reader
    /// dropped leaves its holes stranded rather than handing them to its neighbour.
    /// </remarks>
    public static GroundCutPlan Regions(IReadOnlyList<SiteFeature> rings)
    {
        ArgumentNullException.ThrowIfNull(rings);

        List<GroundCut> regions = [];
        int stranded = 0;
        foreach (IGrouping<int, SiteFeature> polygon in rings.GroupBy(ring => ring.PolygonOrdinal))
        {
            if (polygon.FirstOrDefault(ring => !ring.IsHole) is not { } outer)
            {
                stranded += polygon.Count();
                continue;
            }

            regions.Add(new GroundCut { Outer = outer, Holes = [.. polygon.Where(ring => ring.IsHole)] });
        }

        return new GroundCutPlan(regions, stranded);
    }

    /// <summary>
    /// The plan's crop: the published rectangle, widened east just far enough to hold the zone key's
    /// swatches.
    /// </summary>
    /// <remarks>
    /// A crop region hides the detail elements outside it, and the key's swatches are detail elements,
    /// so a crop of the drape alone would hide the key that explains the plan. Only the swatches are
    /// taken in: the key's text is annotation, which a plan's model crop does not hide while its
    /// annotation crop is off.
    /// </remarks>
    public static FootprintExtent CropWithKey(FootprintExtent crop, FootprintExtent? keySwatches)
        => keySwatches is not { } key
            ? crop
            : new FootprintExtent(
                Math.Min(crop.MinEastM, key.MinEastM),
                Math.Min(crop.MinNorthM, key.MinNorthM),
                Math.Max(crop.MaxEastM, key.MaxEastM),
                Math.Max(crop.MaxNorthM, key.MaxNorthM));

    /// <summary>The box around every extent given, or <c>null</c> when none is measurable.</summary>
    public static FootprintExtent? Around(IEnumerable<FootprintExtent?> extents)
    {
        ArgumentNullException.ThrowIfNull(extents);

        FootprintExtent? around = null;
        foreach (FootprintExtent? extent in extents)
        {
            if (extent is not { IsMeasurable: true } box)
            {
                continue;
            }

            around = around is { } soFar ? CropWithKey(soFar, box) : box;
        }

        return around;
    }

    /// <summary>A paper length in millimetres, as model metres at <paramref name="scale"/>.</summary>
    private static double PaperToModel(double millimetres, int scale) => millimetres / 1000.0 * scale;
}
