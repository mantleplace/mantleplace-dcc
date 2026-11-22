namespace MantlePlace.Core;

public enum LandLayer
{
    LandUse,

    LandCover,
}

public static class LandPlan
{
    public static string BuildToken(string? jobId) => HazardPlan.BuildToken(jobId);

    public static string ViewName(LandLayer layer, string build) => $"Mantle Place {Noun(layer)} Plan {build}";

    public static string Stamp(LandLayer layer, string cacheKeyStem, string build)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        return $"{SiteContext.StampPrefix} {Noun(layer)} Plan {cacheKeyStem}/{build}";
    }

    public static string KeyStamp(LandLayer layer, string cacheKeyStem, string build)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        return $"{SiteContext.StampPrefix} {Noun(layer)} Key {cacheKeyStem}/{build}";
    }

    public static PlanDecision Decide(
        PlanViewFound found,
        IEnumerable<string?> regionComments,
        LandLayer layer,
        string cacheKeyStem,
        string build)
        => HazardPlan.Decide(
            found,
            regionComments,
            Stamp(layer, cacheKeyStem, build),
            ViewName(layer, build),
            Noun(layer).ToLowerInvariant() + " is");

    public static GroundCutPlan Regions(IReadOnlyList<SiteFeature> rings) => GroundCuts.For(rings);

    internal static string Noun(LandLayer layer) => layer == LandLayer.LandUse ? "Land Use" : "Land Cover";
}

public static class LandStyles
{
    public static readonly RgbColour Neutral = new(200, 200, 200);

    private const string NoClass = "(no class published)";

    private static readonly Dictionary<string, RgbColour> LandCover = new(StringComparer.Ordinal)
    {
        ["forest"] = new(118, 164, 98),
        ["mangrove"] = new(86, 140, 112),
        ["shrub"] = new(170, 190, 120),
        ["grass"] = new(196, 222, 150),
        ["moss"] = new(178, 196, 152),
        ["crop"] = new(236, 222, 156),
        ["wetland"] = new(150, 200, 192),
        ["barren"] = new(218, 202, 176),
        ["snow"] = new(236, 242, 248),
        ["urban"] = new(196, 184, 184),
    };

    private static readonly Dictionary<string, RgbColour> LandUse = new(StringComparer.Ordinal)
    {
        ["residential"] = new(250, 228, 140),
        ["developed"] = new(236, 160, 146),
        ["entertainment"] = new(232, 174, 204),
        ["education"] = new(156, 184, 228),
        ["medical"] = new(140, 170, 232),
        ["religious"] = new(184, 164, 214),
        ["military"] = new(190, 160, 160),
        ["transportation"] = new(204, 204, 204),
        ["pedestrian"] = new(222, 216, 204),
        ["construction"] = new(212, 192, 160),
        ["landfill"] = new(182, 172, 152),
        ["resource_extraction"] = new(190, 170, 140),
        ["agriculture"] = new(226, 212, 160),
        ["aquaculture"] = new(160, 200, 222),
        ["horticulture"] = new(202, 226, 152),
        ["park"] = new(142, 200, 122),
        ["recreation"] = new(176, 216, 150),
        ["golf"] = new(160, 212, 130),
        ["grass"] = new(196, 222, 150),
        ["managed"] = new(206, 216, 176),
        ["protected"] = new(122, 172, 112),
        ["campground"] = new(170, 200, 132),
        ["cemetery"] = new(172, 192, 172),
        ["winter_sports"] = new(212, 230, 242),
    };

    public static bool Knows(LandLayer layer, string classWord)
    {
        ArgumentNullException.ThrowIfNull(classWord);
        return Table(layer).ContainsKey(classWord);
    }

    public static RegionStyle For(LandLayer layer, string classWord)
    {
        ArgumentNullException.ThrowIfNull(classWord);

        RgbColour fill = Table(layer).TryGetValue(classWord, out RgbColour known) ? known : Neutral;
        string named = classWord.Length > 0 ? classWord : NoClass;
        return new RegionStyle(
            HazardStyles.LegalName($"Mantle Place {LandPlan.Noun(layer)} {named}"),
            fill,
            Hatch: null,
            HatchAngleDeg: 0.0,
            HatchPatternName: string.Empty);
    }

    private static Dictionary<string, RgbColour> Table(LandLayer layer) => layer == LandLayer.LandUse ? LandUse : LandCover;
}

public static class LandKey
{
    private const string Separator = " — ";

    public static IReadOnlyList<string> Heading(LandLayer layer)
        => [
            (layer == LandLayer.LandUse ? "Land use" : "Land cover")
                + ", as published. Each region is a published polygon, drawn whole; the plan's crop is the order.",
        ];

    public static IReadOnlyList<KeyRow> Rows(LandLayer layer, IEnumerable<SiteFeature> drawn)
    {
        ArgumentNullException.ThrowIfNull(drawn);

        SortedDictionary<string, SortedSet<string>> classes = new(StringComparer.Ordinal);
        foreach (SiteFeature feature in drawn)
        {
            if (feature.IsHole)
            {
                continue;
            }

            if (!classes.TryGetValue(feature.Subtype, out SortedSet<string>? finer))
            {
                finer = new SortedSet<string>(StringComparer.Ordinal);
                classes[feature.Subtype] = finer;
            }

            if (layer == LandLayer.LandUse
                && feature.Classification.Length > 0
                && !string.Equals(feature.Classification, feature.Subtype, StringComparison.Ordinal))
            {
                finer.Add(feature.Classification);
            }
        }

        return
        [
            .. classes.Select(entry => new KeyRow(
                RowText(entry.Key, entry.Value),
                LandStyles.For(layer, entry.Key))),
        ];
    }

    private static string RowText(string classWord, IReadOnlyCollection<string> finer)
    {
        string named = classWord.Length > 0 ? classWord : "(no class published)";
        return finer.Count > 0 ? named + Separator + string.Join(", ", finer) : named;
    }
}
