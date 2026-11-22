using System.Globalization;

namespace MantlePlace.Core;

public enum HazardLayer
{
    FloodZones,
    SteepGround,
}

public enum PlanViewFound
{
    None,

    PlanView,

    SomethingElse,
}

public sealed class PlanDecision
{
    public required bool CreateView { get; init; }

    public required bool Draw { get; init; }

    public int AlreadyDrawn { get; init; }

    public string Explanation { get; init; } = string.Empty;
}

public readonly record struct KeyAnchor(double EastM, double NorthM);

public readonly record struct KeyRowPlacement(FootprintExtent Swatch, double TextEastM, double NorthM);

public static class HazardPlan
{
    public const int DefaultScale = 1000;

    public const double RowPitchMm = 9.0;

    private const string ViewNamePrefix = "Mantle Place Hazard Plan ";

    private const int MaxBuildTokenLength = 32;

    private const double SiteSpanMm = 600.0;

    private const double SwatchWidthMm = 10.0;

    private const double SwatchHeightMm = 6.0;

    private const double TextGapMm = 4.0;

    private const double KeyMarginMm = 15.0;

    private static readonly int[] Scales = [100, 200, 500, 1000, 2000, 2500, 5000, 10000, 20000, 25000, 50000];

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

    public static string ViewName(string build) => ViewNamePrefix + build;

    public static string Stamp(HazardLayer layer, string cacheKeyStem, string build)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        string noun = layer == HazardLayer.FloodZones ? "Flood Zones" : "Steep Ground";
        return $"{SiteContext.StampPrefix} {noun} {cacheKeyStem}/{build}";
    }

    public static string KeyStamp(string cacheKeyStem, string build)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        return $"{SiteContext.StampPrefix} Zone Key {cacheKeyStem}/{build}";
    }

    public static PlanDecision Decide(
        PlanViewFound found,
        IEnumerable<string?> regionComments,
        HazardLayer layer,
        string cacheKeyStem,
        string build)
    {
        return Decide(
            found,
            regionComments,
            Stamp(layer, cacheKeyStem, build),
            ViewName(build),
            layer == HazardLayer.FloodZones ? "flood zones are" : "steep ground is");
    }

    internal static PlanDecision Decide(
        PlanViewFound found,
        IEnumerable<string?> regionComments,
        string stamp,
        string viewName,
        string layerIsAlready)
    {
        ArgumentNullException.ThrowIfNull(regionComments);

        if (found == PlanViewFound.SomethingElse)
        {
            return new PlanDecision
            {
                CreateView = false,
                Draw = false,
                Explanation = $"A view named \"{viewName}\" is already in this project and is not a plan this plugin "
                    + "can draw into, so nothing was drawn. Rename that view and import again.",
            };
        }

        int present = found == PlanViewFound.PlanView
            ? regionComments.Count(comments => string.Equals(comments, stamp, StringComparison.Ordinal))
            : 0;

        if (present > 0)
        {
            return new PlanDecision
            {
                CreateView = false,
                Draw = false,
                AlreadyDrawn = present,
                Explanation = string.Format(
                    CultureInfo.InvariantCulture,
                    "This build's {0} already on \"{1}\" ({2:N0} region(s)), so it was left alone and nothing "
                        + "was drawn over it.",
                    layerIsAlready,
                    viewName,
                    present),
            };
        }

        return new PlanDecision { CreateView = found == PlanViewFound.None, Draw = true };
    }

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

    public static GroundCutPlan Regions(IReadOnlyList<SiteFeature> rings) => GroundCuts.For(rings);

    public static FootprintExtent CropWithKey(FootprintExtent crop, FootprintExtent? keySwatches)
        => keySwatches is not { } key
            ? crop
            : new FootprintExtent(
                Math.Min(crop.MinEastM, key.MinEastM),
                Math.Min(crop.MinNorthM, key.MinNorthM),
                Math.Max(crop.MaxEastM, key.MaxEastM),
                Math.Max(crop.MaxNorthM, key.MaxNorthM));

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

    private static double PaperToModel(double millimetres, int scale) => millimetres / 1000.0 * scale;
}
