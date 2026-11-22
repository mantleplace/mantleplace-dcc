namespace MantlePlace.Core;

public readonly record struct SurfaceCropWindow(double WestM, double SouthM, double EastM, double NorthM)
{
    public bool IsUsable
        => double.IsFinite(WestM) && double.IsFinite(SouthM)
            && double.IsFinite(EastM) && double.IsFinite(NorthM)
            && EastM > WestM && NorthM > SouthM;

    public bool Contains(double eastM, double northM, double tolerance)
        => eastM >= WestM - tolerance && eastM <= EastM + tolerance
            && northM >= SouthM - tolerance && northM <= NorthM + tolerance;
}

public static class SurfaceCrop
{
    public static SurfaceCropWindow? For(BundleManifest manifest, SiteFrame? frame)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (frame is null || !manifest.HasBbox)
        {
            return null;
        }

        if (!frame.TryProjectToLocalMetres(manifest.BboxWestDeg, manifest.BboxSouthDeg, out double swE, out double swN)
            || !frame.TryProjectToLocalMetres(manifest.BboxWestDeg, manifest.BboxNorthDeg, out double nwE, out double nwN)
            || !frame.TryProjectToLocalMetres(manifest.BboxEastDeg, manifest.BboxSouthDeg, out double seE, out double seN)
            || !frame.TryProjectToLocalMetres(manifest.BboxEastDeg, manifest.BboxNorthDeg, out double neE, out double neN))
        {
            return null;
        }

        SurfaceCropWindow window = new(
            WestM: Math.Min(swE, nwE),
            SouthM: Math.Min(swN, seN),
            EastM: Math.Max(seE, neE),
            NorthM: Math.Max(nwN, neN));

        return window.IsUsable ? window : null;
    }

    public static string? Unavailable(BundleManifest manifest, SiteFrame? frame)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (For(manifest, frame) is not null)
        {
            return null;
        }

        if (!manifest.HasBbox)
        {
            return "this bundle publishes no area of interest";
        }

        if (frame is null)
        {
            return "this bundle publishes no origin to place its area of interest against";
        }

        if (!frame.CanPlaceGeographic)
        {
            string unit = LinearUnits.ToManifestToken(frame.Origin.LinearUnit);
            string stated = unit.Length == 0 ? $"EPSG:{frame.Epsg}" : $"EPSG:{frame.Epsg} in {unit}";
            return $"this bundle publishes its area of interest only as longitude and latitude, and its frame "
                + $"({stated}) is not a metric UTM zone, the only frame this plugin projects longitude and "
                + "latitude into — converting between coordinate systems is the platform's work, never a host's";
        }

        return "this bundle's area of interest does not project to a usable rectangle in its frame";
    }

    public static string NoWindowNotice(string? reason, string builtFrom)
        => "The terrain was not cropped to the area you ordered: "
            + (reason ?? "this plugin could not make a crop window from this bundle's area of interest")
            + $". It was built from {builtFrom}.";
}
