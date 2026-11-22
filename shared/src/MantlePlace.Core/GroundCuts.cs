namespace MantlePlace.Core;

public sealed class GroundCut
{
    public required SiteFeature Outer { get; init; }

    public IReadOnlyList<SiteFeature> Holes { get; init; } = [];
}

public readonly record struct GroundCutPlan(IReadOnlyList<GroundCut> Cuts, int StrandedHoles);

public static class GroundCuts
{
    public static GroundLayer? LayerOf(ImportStepKind kind) => kind switch
    {
        ImportStepKind.Water => GroundLayer.Water,
        ImportStepKind.RoadPolygons => GroundLayer.RoadSurface,
        _ => null,
    };

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

    public static IReadOnlyList<string?> Names(IReadOnlyList<GroundCut> cuts)
    {
        ArgumentNullException.ThrowIfNull(cuts);
        return [.. cuts.Select(cut => (string?)cut.Outer.Name)];
    }
}
