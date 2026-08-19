using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>
/// The one geographic forward projection this host is allowed to perform: WGS84 lon/lat to UTM.
/// </summary>
/// <remarks>
/// <para>
/// <c>HPS-33</c> is the default and stays the default — placement values arrive pre-derived and are
/// applied verbatim. <c>HPS-45</c> carves out exactly one exception, and this is it: a bundle's
/// <c>vector</c> GeoJSON layers are RFC 7946, so their coordinates are lon/lat while the manifest
/// describes the layer rather than any vertex in it. There is nothing pre-derived to apply, and the
/// alternative to projecting is not importing them.
/// </para>
/// <para>
/// It is narrow on purpose. UTM only, forward only, no datum shift, no CRS registry: <c>HPS-03</c>'s
/// ban on a geoprocessing stack is unchanged, and an EPSG this cannot handle is a refusal rather
/// than a best effort. The known answers live in the shared corpus (<c>projection.lonLatToUtm</c>),
/// because a zone or a false northing that is wrong places geometry kilometres away while every
/// test that does not check numbers still passes.
/// </para>
/// <para>
/// Snyder's series (USGS Professional Paper 1395, eq. 8-9 … 8-15) to the sixth order, which is
/// sub-decimetre inside a zone — ample for road centrelines, and the same implementation the Unreal
/// reference carries. The two hosts share the vectors, never the code (<c>HPS-43</c>).
/// </para>
/// </remarks>
public static class GeoProjection
{
    private const double SemiMajorMetres = 6378137.0;
    private const double Flattening = 1.0 / 298.257223563;
    private const double ScaleFactor = 0.9996;
    private const double FalseEastingMetres = 500000.0;

    /// <summary>The false northing UTM applies south of the equator, so northings stay positive.</summary>
    private const double SouthernFalseNorthingMetres = 10000000.0;

    /// <summary>Outside this band UTM is not defined, and the series stops being trustworthy anyway.</summary>
    private const double MaxLatitudeDeg = 84.0;

    /// <summary>Whether an EPSG code names a WGS84 UTM zone — <c>326xx</c> north, <c>327xx</c> south.</summary>
    public static bool IsUtmEpsg(int epsg)
        => (epsg is >= 32601 and <= 32660) || (epsg is >= 32701 and <= 32760);

    /// <summary>
    /// Projects WGS84 lon/lat degrees into the UTM zone <paramref name="epsg"/> names, in metres.
    /// </summary>
    /// <returns>
    /// <c>false</c> — leaving the outputs at zero — for an EPSG that is not a UTM zone or a
    /// coordinate outside the projection's validity band. A refusal, never a guess: the caller's
    /// correct response is to skip the layer with a stated reason, not to place it approximately.
    /// </returns>
    public static bool TryLonLatToUtm(
        double lonDeg,
        double latDeg,
        int epsg,
        out double eastingMetres,
        out double northingMetres)
    {
        eastingMetres = 0.0;
        northingMetres = 0.0;

        bool north = epsg is >= 32601 and <= 32660;
        if (!IsUtmEpsg(epsg)
            || double.IsNaN(lonDeg)
            || double.IsNaN(latDeg)
            || latDeg < -MaxLatitudeDeg
            || latDeg > MaxLatitudeDeg
            || lonDeg < -180.0
            || lonDeg > 180.0)
        {
            return false;
        }

        int zone = epsg - (north ? 32600 : 32700);
        double centralMeridianRad = ToRadians(-183.0 + (6.0 * zone));

        double latRad = ToRadians(latDeg);
        double lonRad = ToRadians(lonDeg);

        double e2 = Flattening * (2.0 - Flattening);
        double ep2 = e2 / (1.0 - e2);

        double sinLat = Math.Sin(latRad);
        double cosLat = Math.Cos(latRad);
        double tanLat = Math.Tan(latRad);

        double n = SemiMajorMetres / Math.Sqrt(1.0 - (e2 * sinLat * sinLat));
        double t = tanLat * tanLat;
        double c = ep2 * cosLat * cosLat;
        double a = (lonRad - centralMeridianRad) * cosLat;

        double a2 = a * a;
        double a3 = a2 * a;
        double a4 = a3 * a;
        double a5 = a4 * a;
        double a6 = a5 * a;

        eastingMetres = FalseEastingMetres
            + (ScaleFactor * n * (a
                + ((1.0 - t + c) * a3 / 6.0)
                + ((5.0 - (18.0 * t) + (t * t) + (72.0 * c) - (58.0 * ep2)) * a5 / 120.0)));

        northingMetres = ScaleFactor * (MeridionalArc(latRad, e2)
            + (n * tanLat * ((a2 / 2.0)
                + ((5.0 - t + (9.0 * c) + (4.0 * c * c)) * a4 / 24.0)
                + ((61.0 - (58.0 * t) + (t * t) + (600.0 * c) - (330.0 * ep2)) * a6 / 720.0))));

        if (!north)
        {
            northingMetres += SouthernFalseNorthingMetres;
        }

        return true;
    }

    /// <summary>
    /// Parses an <c>EPSG:NNNN</c> authority string, or a bare code, into its numeric code.
    /// </summary>
    /// <remarks>
    /// The manifest spells a CRS both ways — <c>revit.georeference.crs_projected</c> is
    /// <c>"EPSG:32613"</c> while <c>revit.georeference.origin.projected.epsg</c> is <c>32613</c> —
    /// so the comparison has to normalise rather than string-match. Anything else is <c>null</c>,
    /// meaning UNKNOWN: a CRS this cannot read must not compare equal to one it can.
    /// </remarks>
    public static int? TryParseEpsg(string? authority)
    {
        if (string.IsNullOrWhiteSpace(authority))
        {
            return null;
        }

        ReadOnlySpan<char> text = authority.AsSpan().Trim();
        if (text.StartsWith("EPSG:", StringComparison.OrdinalIgnoreCase))
        {
            text = text[5..].Trim();
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int code) ? code : null;
    }

    /// <summary>Distance along the meridian from the equator (Snyder eq. 3-21).</summary>
    private static double MeridionalArc(double latRad, double e2)
    {
        double e4 = e2 * e2;
        double e6 = e4 * e2;

        return SemiMajorMetres * (
            ((1.0 - (e2 / 4.0) - (3.0 * e4 / 64.0) - (5.0 * e6 / 256.0)) * latRad)
            - (((3.0 * e2 / 8.0) + (3.0 * e4 / 32.0) + (45.0 * e6 / 1024.0)) * Math.Sin(2.0 * latRad))
            + (((15.0 * e4 / 256.0) + (45.0 * e6 / 1024.0)) * Math.Sin(4.0 * latRad))
            - ((35.0 * e6 / 3072.0) * Math.Sin(6.0 * latRad)));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
