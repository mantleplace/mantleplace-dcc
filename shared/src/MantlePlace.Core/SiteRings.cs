namespace MantlePlace.Core;

public static class SiteRings
{
    public static IReadOnlyList<SiteVertex>? Thin(IReadOnlyList<SiteVertex> ring, double shortCurveToleranceM)
    {
        ArgumentNullException.ThrowIfNull(ring);
        if (ring.Count < SiteVectorReader.MinimumRingVertices)
        {
            return null;
        }

        List<SiteVertex> kept = [ring[0]];
        for (int index = 1; index < ring.Count; index++)
        {
            if (PlanDistance(kept[^1], ring[index]) > shortCurveToleranceM)
            {
                kept.Add(ring[index]);
            }
        }

        while (kept.Count > 1 && PlanDistance(kept[^1], kept[0]) <= shortCurveToleranceM)
        {
            kept.RemoveAt(kept.Count - 1);
        }

        return kept.Count < SiteVectorReader.MinimumRingVertices ? null : kept;
    }

    private static double PlanDistance(SiteVertex a, SiteVertex b)
    {
        double east = a.EastM - b.EastM;
        double north = a.NorthM - b.NorthM;
        return Math.Sqrt((east * east) + (north * north));
    }
}
