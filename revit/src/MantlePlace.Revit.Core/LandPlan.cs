namespace MantlePlace.Revit.Core;

/// <summary>One of the two layers drawn on a land plan, each on a plan of its own.</summary>
public enum LandLayer
{
    /// <summary><c>land_use</c>: what the ground is used for — residential, recreation, park.</summary>
    LandUse,

    /// <summary><c>land_cover</c>: what physically covers the ground — forest, grass, barren.</summary>
    LandCover,
}

/// <summary>
/// The land plans: a flat plan per layer per build where the land use, or the land cover, is drawn as
/// filled regions, cropped to the order. Each plan's name, the stamps on what is drawn in it, whether
/// a layer is drawn, and which polygons become regions. Pure.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/adr/0015-revit-subdivisions-are-for-built-surfaces.md</c>: these two layers used to be cut
/// into the terrain as subdivisions, and were most of a full import's time for an edge line and a
/// selectable area under the drape. A plan of filled regions costs seconds at any count, sits on a
/// sheet as it is, and leaves the terrain to the surfaces a designer builds against.
/// </para>
/// <para>
/// Two plans, not one, because the layers lie over the same ground and their regions would cover one
/// another. Everything else is the hazard plan's (<see cref="HazardPlan"/>): the build token, the
/// lowest level, the scale, the crop to the published rectangle widened for the key, and ⛔ <b>a plan
/// is never redrawn</b> — a layer already on its plan is left alone, a later build gets plans of its
/// own, and the regions carry their stamp.
/// </para>
/// <para>
/// Polygons are drawn as published and the plan's crop decides what shows. Clipping one to the order
/// would be derivation; cropping a view is display.
/// </para>
/// </remarks>
public static class LandPlan
{
    /// <summary>The build a plan belongs to — the hazard plan's token, so one build's plans share it.</summary>
    public static string BuildToken(string? jobId) => HazardPlan.BuildToken(jobId);

    /// <summary>The plan's name, and the identity a re-import finds it by.</summary>
    public static string ViewName(LandLayer layer, string build) => $"Mantle Place {Noun(layer)} Plan {build}";

    /// <summary>The stamp every region of <paramref name="layer"/> on this build's plan carries.</summary>
    /// <remarks>
    /// <c>Mantle Place Land Cover Plan {stem}/{build}</c>, never <c>Mantle Place Land Cover {stem}/…</c>:
    /// that is the stamp a land-cover subdivision cut before ADR 0015 carries, which the drape still
    /// recognises (<see cref="SiteBoundaryIdentity.Parse"/>).
    /// </remarks>
    public static string Stamp(LandLayer layer, string cacheKeyStem, string build)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        return $"{SiteContext.StampPrefix} {Noun(layer)} Plan {cacheKeyStem}/{build}";
    }

    /// <summary>The stamp every swatch of this build's key on <paramref name="layer"/>'s plan carries.</summary>
    public static string KeyStamp(LandLayer layer, string cacheKeyStem, string build)
    {
        ArgumentNullException.ThrowIfNull(cacheKeyStem);
        return $"{SiteContext.StampPrefix} {Noun(layer)} Key {cacheKeyStem}/{build}";
    }

    /// <summary>What a land plan step does, given what the project holds — the hazard plan's rule.</summary>
    /// <param name="found">What the project has under <see cref="ViewName"/>.</param>
    /// <param name="regionComments">
    /// The Comments of the regions already in that plan. Anything that is not this layer's stamp for
    /// this build is ignored, the key's swatches included.
    /// </param>
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

    /// <summary>
    /// The regions a parsed land layer asks for: one per published polygon, its holes left out of it,
    /// as the hazard plan's and the subdivisions' are (<see cref="GroundCuts.For"/>).
    /// </summary>
    /// <remarks>
    /// A hole in a forest is a clearing the forest does not cover, so a region drawn over it would say
    /// what the bundle does not. A ring-per-region reading, which the subdivisions used, is not needed
    /// here: a region's identity is its plan's stamp, not its place in the layer.
    /// </remarks>
    public static GroundCutPlan Regions(IReadOnlyList<SiteFeature> rings) => GroundCuts.For(rings);

    /// <summary>The layer's name in a plan's name and stamps.</summary>
    internal static string Noun(LandLayer layer) => layer == LandLayer.LandUse ? "Land Use" : "Land Cover";
}

/// <summary>
/// The land plans' colours, owned by this host and keyed on the published class words. Pure.
/// </summary>
/// <remarks>
/// <para>
/// The class is the polygon's <c>subtype</c>, verbatim: the one classification both layers publish
/// (<c>land_cover</c> publishes nothing else), from a closed vocabulary, so the key stays short enough
/// to read. <c>land_use</c>'s finer <c>class</c> is quoted in the key beside its subtype
/// (<see cref="LandKey.Rows"/>) rather than given a colour of its own.
/// </para>
/// <para>
/// Colour is presentation, not derivation: the class is the bundle's, and this table only says how
/// this host draws it — land cover in the colours of the ground it names, land use in the colours a
/// planner has seen on a land-use map. A class the table does not know is drawn, never dropped, in a
/// neutral type named with its class. Matched ordinally, because a class is a published value and not
/// a phrase to search.
/// </para>
/// <para>
/// One type per layer and class, named <c>Mantle Place {layer} {class}</c>, found by name: a curator
/// who recoloured one keeps their colour on the next import, and recolouring land use's
/// <c>grass</c> leaves land cover's alone.
/// </para>
/// </remarks>
public static class LandStyles
{
    /// <summary>What a class this table does not know is drawn in.</summary>
    public static readonly RgbColour Neutral = new(200, 200, 200);

    /// <summary>What a polygon publishing no class is named for.</summary>
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

    /// <summary>Whether this table names <paramref name="classWord"/> for <paramref name="layer"/>.</summary>
    public static bool Knows(LandLayer layer, string classWord)
    {
        ArgumentNullException.ThrowIfNull(classWord);
        return Table(layer).ContainsKey(classWord);
    }

    /// <summary>How a polygon of <paramref name="layer"/> publishing <paramref name="classWord"/> is drawn.</summary>
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

/// <summary>
/// What the key inside a land plan says: a row for each class the plan shows, in the published words,
/// under a heading that says so. Pure.
/// </summary>
/// <remarks>
/// Only what was drawn is listed, as on the hazard plan's zone key: a class whose every polygon Revit
/// refused would be a row explaining nothing on the page. Rows are in ordinal order of the class word,
/// so two imports of one build list them alike.
/// </remarks>
public static class LandKey
{
    private const string Separator = " — ";

    /// <summary>The lines above the rows.</summary>
    public static IReadOnlyList<string> Heading(LandLayer layer)
        => [
            (layer == LandLayer.LandUse ? "Land use" : "Land cover")
                + ", as published. Each region is a published polygon, drawn whole; the plan's crop is the order.",
        ];

    /// <summary>The rows for the polygons drawn: each class once, with its swatch.</summary>
    /// <param name="layer">Which layer was drawn.</param>
    /// <param name="drawn">The outer rings drawn; a hole claims nothing.</param>
    /// <remarks>
    /// A land use row quotes the finer <c>class</c> values drawn under its subtype after a dash —
    /// <c>recreation — pitch, playground</c> — leaving out a class that only repeats the subtype.
    /// </remarks>
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
