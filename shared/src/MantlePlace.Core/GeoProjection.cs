using System.Globalization;

namespace MantlePlace.Core;

public static class GeoProjection
{
    private const double SemiMajorMetres = 6378137.0;
    private const double Flattening = 1.0 / 298.257223563;
    private const double ScaleFactor = 0.9996;
    private const double FalseEastingMetres = 500000.0;

    private const double SouthernFalseNorthingMetres = 10000000.0;

    private const double MaxLatitudeDeg = 84.0;

    public static bool IsUtmEpsg(int epsg)
        => (epsg is >= 32601 and <= 32660) || (epsg is >= 32701 and <= 32760);

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
