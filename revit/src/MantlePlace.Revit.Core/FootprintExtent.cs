using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>
/// The axis-aligned box a footprint occupies in the bundle's local frame — east/north metres, no
/// elevation.
/// </summary>
/// <remarks>
/// Plan only. A published polygon's ring is 2-D by construction (<see cref="SiteVectorReader"/>),
/// and a drawn plan's crop is a rectangle on the level, so the third ordinate is dropped rather than
/// carried unused.
/// </remarks>
/// <param name="MinEastM">The west edge.</param>
/// <param name="MinNorthM">The south edge.</param>
/// <param name="MaxEastM">The east edge.</param>
/// <param name="MaxNorthM">The north edge.</param>
public readonly record struct FootprintExtent(
    double MinEastM,
    double MinNorthM,
    double MaxEastM,
    double MaxNorthM)
{
    public double WidthM => MaxEastM - MinEastM;

    public double DepthM => MaxNorthM - MinNorthM;

    /// <summary>Whether this box has a real extent on both axes and finite edges.</summary>
    public bool IsMeasurable
        => double.IsFinite(MinEastM)
            && double.IsFinite(MinNorthM)
            && double.IsFinite(MaxEastM)
            && double.IsFinite(MaxNorthM)
            && WidthM > 0.0
            && DepthM > 0.0;

    /// <summary>The box around a ring's vertices, or <c>null</c> when there is no box to draw.</summary>
    /// <remarks>
    /// A non-finite ordinate returns <c>null</c> rather than being skipped: a box drawn around the
    /// vertices that happened to parse is a footprint nobody published, and acting on it would be
    /// worse than saying nothing.
    /// </remarks>
    public static FootprintExtent? Around(IReadOnlyList<SiteVertex> vertices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        if (vertices.Count == 0)
        {
            return null;
        }

        double minEast = double.PositiveInfinity;
        double minNorth = double.PositiveInfinity;
        double maxEast = double.NegativeInfinity;
        double maxNorth = double.NegativeInfinity;

        foreach (SiteVertex vertex in vertices)
        {
            if (!double.IsFinite(vertex.EastM) || !double.IsFinite(vertex.NorthM))
            {
                return null;
            }

            minEast = Math.Min(minEast, vertex.EastM);
            minNorth = Math.Min(minNorth, vertex.NorthM);
            maxEast = Math.Max(maxEast, vertex.EastM);
            maxNorth = Math.Max(maxNorth, vertex.NorthM);
        }

        return new FootprintExtent(minEast, minNorth, maxEast, maxNorth);
    }

    /// <summary>The "1,086 x 1,080 m" a reader compares against another.</summary>
    public string Describe()
        => string.Format(CultureInfo.InvariantCulture, "{0:N0} x {1:N0} m", WidthM, DepthM);
}
