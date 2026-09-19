namespace MantlePlace.Revit.Core;

/// <summary>
/// One subdivision to cut into the ground: the ring around it, the holes to leave in it, and the
/// renderer phrase its material will carry.
/// </summary>
public sealed class GroundCut
{
    /// <summary>The ring the subdivision's outer loop is drawn from.</summary>
    public required SiteFeature Outer { get; init; }

    /// <summary>
    /// The rings to leave out of it — a water body's islands, a merged road network's city blocks.
    /// Empty for the layers that cut one subdivision per ring.
    /// </summary>
    public IReadOnlyList<SiteFeature> Holes { get; init; } = [];

    /// <summary>The renderer phrase this cut's material carries, or <c>null</c> for none.</summary>
    public string? Keyword { get; init; }
}

/// <summary>Every subdivision one parsed layer asks for, and what was left out of them.</summary>
/// <param name="Cuts">The subdivisions, in layer order.</param>
/// <param name="StrandedHoles">
/// Inner rings whose polygon has no outer ring left — the reader dropped it as too small to close.
/// A hole with nothing to be cut out of is not a subdivision of its own, so it is dropped and said.
/// </param>
public readonly record struct GroundCutPlan(IReadOnlyList<GroundCut> Cuts, int StrandedHoles);

/// <summary>
/// Turns a parsed polygon layer into the subdivisions to cut from it. Pure.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Two layers keep their holes and two do not, and the difference is what a hole means.</b> A
/// merged road network's inner rings are city blocks and a water body's are islands: cutting them as
/// subdivisions of their own would put asphalt on the block and water on the island. <c>land_use</c>
/// and <c>land_cover</c> keep one subdivision per ring, as they always have — their stamps are
/// positions in the layer, so grouping their rings now would move every stamp after the first
/// polygon with a hole and a re-import would cut the whole layer again.
/// </para>
/// <para>
/// That an outer loop plus its inner loops is a thing <c>Toposolid.CreateSubDivision</c> takes at all
/// was measured before this was written, in Revit 2025, 2026 and 2027: all three accepted it, left
/// the holes out of the subdivision's surface, and did not care which way round a ring was wound
/// (<c>revit/README.md</c> ▸ "Holes in a subdivision").
/// </para>
/// <para>
/// The keyword is decided here for the same reason the rest is: it depends on the layer for water
/// and roads and on the ring's own subtype for the two land layers, and one place that knows which
/// is one place to test (<c>HPS-02</c>).
/// </para>
/// </remarks>
public static class GroundCuts
{
    /// <summary>
    /// The polygon layer a step of this kind cuts, or <c>null</c> for a step that cuts none.
    /// </summary>
    /// <remarks>
    /// The one place a step kind becomes a layer: the shim dispatches through it rather than
    /// carrying a case per layer, and <see cref="SlowStepNotice"/> takes the layer's own words from
    /// it. A fifth polygon layer is a row here and nowhere else in either.
    /// </remarks>
    public static GroundLayer? LayerOf(ImportStepKind kind) => kind switch
    {
        ImportStepKind.SiteBoundaries => GroundLayer.LandUse,
        ImportStepKind.LandCover => GroundLayer.LandCover,
        ImportStepKind.Water => GroundLayer.Water,
        ImportStepKind.RoadPolygons => GroundLayer.RoadSurface,
        _ => null,
    };

    /// <summary>
    /// Whether this layer's polygons are cut whole — one subdivision per polygon, holes left out —
    /// rather than one subdivision per ring.
    /// </summary>
    public static bool CutsWholePolygons(GroundLayer layer)
        => layer is GroundLayer.Water or GroundLayer.RoadSurface;

    /// <summary>The subdivisions <paramref name="rings"/> asks for, in layer order.</summary>
    /// <param name="layer">Which layer was parsed.</param>
    /// <param name="rings">The layer's rings, as <see cref="SiteVectorReader"/> returns them.</param>
    public static GroundCutPlan For(GroundLayer layer, IReadOnlyList<SiteFeature> rings)
    {
        ArgumentNullException.ThrowIfNull(rings);

        if (!CutsWholePolygons(layer))
        {
            return new GroundCutPlan(
                [.. rings.Select(ring => new GroundCut { Outer = ring, Keyword = KeywordFor(layer, ring) })],
                StrandedHoles: 0);
        }

        // Grouped by the polygon each ring came out of rather than by adjacency: a polygon whose
        // outer ring the reader dropped must not hand its holes to the polygon before it.
        List<GroundCut> cuts = [];
        int stranded = 0;
        foreach (IGrouping<int, SiteFeature> polygon in rings.GroupBy(ring => ring.PolygonOrdinal))
        {
            SiteFeature? outer = polygon.FirstOrDefault(ring => !ring.IsHole);
            if (outer is null)
            {
                stranded += polygon.Count();
                continue;
            }

            cuts.Add(new GroundCut
            {
                Outer = outer,
                Holes = [.. polygon.Where(ring => ring.IsHole)],
                Keyword = KeywordFor(layer, outer),
            });
        }

        return new GroundCutPlan(cuts, stranded);
    }

    /// <summary>The name each cut is stamped by, in cut order — the outer ring's, or none.</summary>
    public static IReadOnlyList<string?> Names(IReadOnlyList<GroundCut> cuts)
    {
        ArgumentNullException.ThrowIfNull(cuts);
        return [.. cuts.Select(cut => (string?)cut.Outer.Name)];
    }

    /// <summary>
    /// Each keyworded cut's stamp paired with its phrase, for the cuts that have one.
    /// </summary>
    /// <param name="cuts">The layer's cuts.</param>
    /// <param name="stamps">The stamp of every cut, in the same order (<see cref="SiteBoundaryIdentity.Stamps"/>).</param>
    /// <remarks>
    /// What lets a subdivision an earlier import cut be given its keyword: the shim reads the stamp
    /// off the element and looks it up here. Keyed on the full stamp, so another layer's
    /// subdivisions, another order's and a curator's own never match.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> KeywordsByStamp(
        IReadOnlyList<GroundCut> cuts,
        IReadOnlyList<string> stamps)
    {
        ArgumentNullException.ThrowIfNull(cuts);
        ArgumentNullException.ThrowIfNull(stamps);
        if (cuts.Count != stamps.Count)
        {
            throw new ArgumentException("One stamp per cut, in layer order.", nameof(stamps));
        }

        Dictionary<string, string> byStamp = new(StringComparer.Ordinal);
        for (int index = 0; index < cuts.Count; index++)
        {
            if (cuts[index].Keyword is { } keyword)
            {
                byStamp[stamps[index]] = keyword;
            }
        }

        return byStamp;
    }

    /// <summary>
    /// The phrase a cut of <paramref name="layer"/> around <paramref name="outer"/> carries.
    /// </summary>
    /// <remarks>
    /// Water and roads take the layer's word — every feature in those layers is the same material
    /// whatever its published subtype says, so a swimming pool and a reservoir are both water. The
    /// land layers read the ring's own subtype, where a hole claims nothing
    /// (<see cref="RendererKeywords.ForRing"/>).
    /// </remarks>
    private static string? KeywordFor(GroundLayer layer, SiteFeature outer)
        => RendererKeywords.ForLayer(layer) ?? RendererKeywords.ForRing(outer);
}
