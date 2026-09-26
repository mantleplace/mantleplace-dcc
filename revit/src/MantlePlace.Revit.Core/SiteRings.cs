namespace MantlePlace.Revit.Core;

/// <summary>
/// A published polygon ring reduced to the vertices Revit can close a loop through. Pure.
/// </summary>
/// <remarks>
/// <para>
/// Revit refuses a line no longer than its short-curve tolerance, and refuses a curve loop whose
/// lines do not meet end to end. Dropping a short <em>edge</em> satisfies the first and breaks the
/// second: the edges either side of it are left a fraction of a millimetre apart, and the whole loop
/// is refused as not contiguous. A platform-built road layer carried edges of 0.1 to 0.6 mm, so the
/// rule here drops a <em>vertex</em> instead — every vertex within tolerance of the last one kept —
/// and the loop closes by construction. That drops published vertices and derives none: what comes
/// back is a subsequence of what was read, each vertex as published.
/// </para>
/// <para>
/// It is the rule <see cref="PublishedContours"/> applies to a contour line, adapted for a ring. The
/// two are not one helper because their ends differ: a contour keeps its last vertex, so a clipped
/// end stays on the crop window's edge, while a ring has no last vertex — its closing edge runs back
/// to the first, which is the one that is never displaced. The point types differ too.
/// </para>
/// </remarks>
public static class SiteRings
{
    /// <summary>
    /// The ring with every vertex within <paramref name="shortCurveToleranceM"/> of the last one kept
    /// skipped, in plan, or <c>null</c> when fewer than three vertices would remain to close a loop.
    /// </summary>
    /// <param name="ring">The ring's vertices, without the repeated closing position.</param>
    /// <param name="shortCurveToleranceM">Revit's short-curve tolerance, in metres.</param>
    /// <remarks>
    /// Every edge of the result, the closing one included, is longer than the tolerance. The first
    /// vertex is always kept; a vertex near the end of the ring that falls within tolerance of it is
    /// skipped instead.
    /// </remarks>
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

        // The closing edge runs from the last kept vertex back to the first.
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
