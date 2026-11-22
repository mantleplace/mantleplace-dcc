using System.Globalization;

namespace MantlePlace.Core;

public readonly record struct FootprintExtent(
    double MinEastM,
    double MinNorthM,
    double MaxEastM,
    double MaxNorthM)
{
    public double WidthM => MaxEastM - MinEastM;

    public double DepthM => MaxNorthM - MinNorthM;

    public bool IsMeasurable
        => double.IsFinite(MinEastM)
            && double.IsFinite(MinNorthM)
            && double.IsFinite(MaxEastM)
            && double.IsFinite(MaxNorthM)
            && WidthM > 0.0
            && DepthM > 0.0;

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

    public string Describe()
        => string.Format(CultureInfo.InvariantCulture, "{0:N0} x {1:N0} m", WidthM, DepthM);
}
