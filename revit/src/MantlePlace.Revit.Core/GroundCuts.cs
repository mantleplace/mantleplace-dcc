namespace MantlePlace.Revit.Core;

/// <summary>
/// One region a published polygon becomes — a subdivision cut into the ground, or a filled region on
/// a drawn plan: the ring around it and the holes to leave in it.
/// </summary>
public sealed class GroundCut
{
    /// <summary>The ring the outer loop is drawn from.</summary>
    public required SiteFeature Outer { get; init; }

    /// <summary>The rings to leave out of it — a water body's islands, a merged road network's city blocks.</summary>
    public IReadOnlyList<SiteFeature> Holes { get; init; } = [];
}

/// <summary>Every region one parsed layer asks for, and what was left out of them.</summary>
/// <param name="Cuts">The regions, in layer order.</param>
/// <param name="StrandedHoles">
/// Inner rings whose polygon has no outer ring left — the reader dropped it as too small to close.
/// A hole with nothing to be cut out of is not a region of its own, so it is dropped and said.
/// </param>
public readonly record struct GroundCutPlan(IReadOnlyList<GroundCut> Cuts, int StrandedHoles);

/// <summary>
/// Turns a parsed polygon layer into the subdivisions to cut from it. Pure.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Only the surfaces a designer builds against are cut: the water bodies and the road
/// surfaces</b> (<c>docs/adr/0015-revit-subdivisions-are-for-built-surfaces.md</c>). Land use and
/// land cover are drawn on land plans instead (<see cref="LandPlan"/>), so no step kind maps to their
/// <see cref="GroundLayer"/> here; those two members survive only to recognise the subdivisions an
/// earlier build cut.
/// </para>
/// <para>
/// Each polygon is cut whole, its holes left out of it. A merged road network's inner rings are city
/// blocks and a water body's are islands: cutting them as subdivisions of their own would put asphalt
/// on the block and water on the island. That an outer loop plus its inner loops is a thing
/// <c>Toposolid.CreateSubDivision</c> takes at all was measured before this was written, in Revit
/// 2025, 2026 and 2027: all three accepted it, left the holes out of the subdivision's surface, and
/// did not care which way round a ring was wound (<c>revit/README.md</c> ▸ "Holes in a subdivision").
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
    /// it. A third polygon layer is a row here, and a row in <see cref="SiteVectorLayers"/> naming the
    /// manifest layer it is cut from and what the step is called; a test holds the two to each other.
    /// </remarks>
    public static GroundLayer? LayerOf(ImportStepKind kind) => kind switch
    {
        ImportStepKind.Water => GroundLayer.Water,
        ImportStepKind.RoadPolygons => GroundLayer.RoadSurface,
        _ => null,
    };

    /// <summary>The subdivisions <paramref name="rings"/> asks for, in layer order: one per polygon, holes left out.</summary>
    /// <param name="rings">The layer's rings, as <see cref="SiteVectorReader"/> returns them.</param>
    /// <remarks>
    /// Grouped by the polygon each ring came out of rather than by adjacency: a polygon whose outer
    /// ring the reader dropped must not hand its holes to the polygon before it.
    /// </remarks>
    public static GroundCutPlan For(IReadOnlyList<SiteFeature> rings)
    {
        ArgumentNullException.ThrowIfNull(rings);

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

            cuts.Add(new GroundCut { Outer = outer, Holes = [.. polygon.Where(ring => ring.IsHole)] });
        }

        return new GroundCutPlan(cuts, stranded);
    }

    /// <summary>The name each cut is stamped by, in cut order — the outer ring's, or none.</summary>
    public static IReadOnlyList<string?> Names(IReadOnlyList<GroundCut> cuts)
    {
        ArgumentNullException.ThrowIfNull(cuts);
        return [.. cuts.Select(cut => (string?)cut.Outer.Name)];
    }
}
