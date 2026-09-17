using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>
/// The axis-aligned box a footprint occupies in the bundle's local frame — east/north metres, no
/// elevation.
/// </summary>
/// <remarks>
/// Plan only. A site-boundary ring is 2-D by construction (<see cref="SiteVectorReader"/>) and a
/// toposolid's height says nothing about how much of the site it marks, so the third ordinate is
/// dropped rather than carried unused.
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
    /// vertices that happened to parse is a footprint nobody published, and reporting a subdivision
    /// against it would be worse than saying nothing.
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

/// <summary>
/// Whether a site-boundary subdivision's footprint is the ground toposolid's own footprint. Pure.
/// </summary>
/// <remarks>
/// <para>
/// A subdivision is a region marked on the terrain, and a region the size and shape of the whole
/// terrain marks nothing: it carries no information a reader can use, while costing a full
/// re-triangulation, its own material, and a set of contour lines Revit generates per element and
/// which therefore do not line up with the ground's. On the site that prompted this — 1,086 x
/// 1,080 m with 23 subdivisions — that is the difference between "these districts sit on a
/// continuous terrain" and "this terrain has regions marked on it".
/// </para>
/// <para>
/// ⛔ <b>This reports. It does not refuse, skip, arbitrate or score.</b> The published land-use
/// polygons are drawn whatever this says: a plugin that silently dropped published data because it
/// looked redundant would be worse than one that draws it and says so. Deciding which of two
/// overlapping polygons owns a square metre is a derivation, and so is a coverage fraction — both
/// are refused by the root <c>CLAUDE.md</c>'s "boundary that keeps this client thin". Coextension is
/// the narrowest test that catches the case, and it is a comparison of two boxes rather than a
/// judgement about which polygon wins.
/// </para>
/// <para>
/// The decision lives here rather than in the shim because it is policy, and policy in the shim is
/// covered by nothing but review (<c>HPS-02</c>). The shim reads the two footprints and says
/// whatever comes back.
/// </para>
/// </remarks>
public static class SiteBoundaryCoextension
{
    /// <summary>
    /// How far each of a subdivision's four edges may sit from the ground's corresponding edge and
    /// still be called the same edge, as a fraction of the ground's extent on that axis.
    /// </summary>
    /// <remarks>
    /// One per cent — about eleven metres on the 1,086 m site this was measured against. It is a
    /// fraction rather than an absolute distance because "the same outline" is a claim about shape
    /// at the site's own scale: eleven metres is nothing on a kilometre-wide site and is the whole
    /// of a courtyard. It is deliberately loose enough to absorb a ring simplified or rounded
    /// upstream, and far tighter than any subdivision that genuinely marks a district.
    /// </remarks>
    public const double CoextensionFraction = 0.01;

    /// <summary>
    /// Whether <paramref name="subdivision"/> occupies the same ground as <paramref name="ground"/>.
    /// </summary>
    /// <remarks>
    /// Every one of the four edges is compared, so a footprint of the ground's size placed somewhere
    /// else is not coextensive — width and depth alone would call a site-sized district in the
    /// corner of a larger site the whole of it. An unmeasurable box on either side is <c>false</c>:
    /// there is no tolerance to measure against, and inventing a verdict is what this type exists to
    /// avoid.
    /// </remarks>
    public static bool IsCoextensive(FootprintExtent subdivision, FootprintExtent ground)
    {
        if (!subdivision.IsMeasurable || !ground.IsMeasurable)
        {
            return false;
        }

        double eastSlack = CoextensionFraction * ground.WidthM;
        double northSlack = CoextensionFraction * ground.DepthM;

        return Math.Abs(subdivision.MinEastM - ground.MinEastM) <= eastSlack
            && Math.Abs(subdivision.MaxEastM - ground.MaxEastM) <= eastSlack
            && Math.Abs(subdivision.MinNorthM - ground.MinNorthM) <= northSlack
            && Math.Abs(subdivision.MaxNorthM - ground.MaxNorthM) <= northSlack;
    }

    /// <summary>
    /// The one line to say about a coextensive subdivision, or <c>null</c> when there is nothing to
    /// say.
    /// </summary>
    /// <remarks>
    /// It states what was compared and what the tolerance was, and stops. A reader who wants the
    /// polygon changed changes it upstream, where the land-use data is published; a reader who
    /// wants it kept keeps it, and it is already drawn either way.
    /// </remarks>
    /// <param name="subdivisionName">
    /// The published feature's name, or a stand-in for a feature that has none.
    /// </param>
    /// <param name="oneBasedPosition">
    /// The feature's position in the layer, stated alongside the name because names are not unique:
    /// two features called "Residential" become two subdivisions, and the position is what tells a
    /// reader which of them this line is about — it is also the token an unnamed feature's stamp
    /// carries (<see cref="SiteBoundaryIdentity"/>).
    /// </param>
    public static string? Describe(
        string subdivisionName,
        int oneBasedPosition,
        FootprintExtent subdivision,
        FootprintExtent ground)
    {
        ArgumentNullException.ThrowIfNull(subdivisionName);

        if (!IsCoextensive(subdivision, ground))
        {
            return null;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "Site boundary \"{0}\" (position {1} in the layer) is coextensive with the ground it is "
            + "cut from: its footprint is {2} against the ground's {3}, with every edge inside the "
            + "{4:0.#}% tolerance this test applies. It marks the whole site as one region, and "
            + "draws its own contour lines, which Revit generates per element and which therefore "
            + "will not line up with the ground's. It was imported as published.",
            subdivisionName,
            oneBasedPosition,
            subdivision.Describe(),
            ground.Describe(),
            CoextensionFraction * 100.0);
    }
}
