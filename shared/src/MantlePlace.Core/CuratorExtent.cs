using System.Globalization;
using System.Text;

namespace MantlePlace.Core;

public readonly record struct CuratorExtent(double West, double South, double East, double North)
{
    public bool IsValid
        => double.IsFinite(West) && double.IsFinite(South) && double.IsFinite(East) && double.IsFinite(North)
            && West >= -180.0 && East <= 180.0 && South >= -90.0 && North <= 90.0
            && West < East && South < North;
}

public readonly record struct SiteLocationPoint(double LatitudeDegrees, double LongitudeDegrees)
{
    private const double MaxLatitude = 89.9;

    public static SiteLocationPoint FromRadians(double latitudeRadians, double longitudeRadians)
        => new(latitudeRadians * 180.0 / Math.PI, longitudeRadians * 180.0 / Math.PI);

    public bool IsLocated
        => double.IsFinite(LatitudeDegrees) && double.IsFinite(LongitudeDegrees)
            && Math.Abs(LatitudeDegrees) <= MaxLatitude && Math.Abs(LongitudeDegrees) <= 180.0
            && !(LatitudeDegrees == 0.0 && LongitudeDegrees == 0.0);
}

public enum CuratorExtentSource
{
    None,

    PriorImport,

    SiteLocation,
}

public sealed class CuratorProjectState
{
    public string? ImportedOrderId { get; init; }

    public IReadOnlyList<VaultBundle>? VaultBundles { get; init; }

    public SiteLocationPoint? Site { get; init; }
}

public sealed record CuratorSizeChoice(string Label, double SideMetres, CuratorExtent Extent);

public sealed class CuratorResolution
{
    public required CuratorExtentSource Source { get; init; }

    public CuratorExtent? Extent { get; init; }

    public IReadOnlyList<CuratorSizeChoice> SizeChoices { get; init; } = [];

    public bool OrderExtentUnavailable { get; init; }
}

public static class CuratorExtents
{
    public static IReadOnlyList<double> SiteSizesMetres { get; } = [500.0, 1000.0, 2000.0, 4000.0];

    public static CuratorResolution Resolve(CuratorProjectState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        bool orderNamed = !string.IsNullOrWhiteSpace(state.ImportedOrderId);
        bool orderUnavailable = false;

        if (orderNamed)
        {
            CuratorExtent? fromOrder = ExtentOfOrder(state.ImportedOrderId!.Trim(), state.VaultBundles);
            if (fromOrder is { } extent)
            {
                return new CuratorResolution { Source = CuratorExtentSource.PriorImport, Extent = extent };
            }

            orderUnavailable = true;
        }

        if (state.Site is { IsLocated: true } site)
        {
            IReadOnlyList<CuratorSizeChoice> choices = ChoicesAround(site);
            if (choices.Count > 0)
            {
                return new CuratorResolution
                {
                    Source = CuratorExtentSource.SiteLocation,
                    SizeChoices = choices,
                    OrderExtentUnavailable = orderUnavailable,
                };
            }
        }

        return new CuratorResolution { Source = CuratorExtentSource.None, OrderExtentUnavailable = orderUnavailable };
    }

    public static CuratorExtent? Around(SiteLocationPoint center, double sideMetres)
    {
        if (!center.IsLocated || !double.IsFinite(sideMetres) || sideMetres <= 0.0)
        {
            return null;
        }

        double halfLatitude = sideMetres / 2.0 / MetresPerDegreeLatitude(center.LatitudeDegrees);
        double halfLongitude = sideMetres / 2.0 / MetresPerDegreeLongitude(center.LatitudeDegrees);

        CuratorExtent extent = new(
            center.LongitudeDegrees - halfLongitude,
            center.LatitudeDegrees - halfLatitude,
            center.LongitudeDegrees + halfLongitude,
            center.LatitudeDegrees + halfLatitude);

        return extent.IsValid ? extent : null;
    }

    private static CuratorExtent? ExtentOfOrder(string orderId, IReadOnlyList<VaultBundle>? bundles)
    {
        if (bundles is null)
        {
            return null;
        }

        foreach (VaultBundle bundle in bundles)
        {
            if (string.Equals(bundle.OrderId, orderId, StringComparison.OrdinalIgnoreCase))
            {
                return bundle.Extent is { IsValid: true } extent ? extent : null;
            }
        }

        return null;
    }

    private static IReadOnlyList<CuratorSizeChoice> ChoicesAround(SiteLocationPoint site)
    {
        List<CuratorSizeChoice> choices = [];
        foreach (double side in SiteSizesMetres)
        {
            if (Around(site, side) is { } extent)
            {
                choices.Add(new CuratorSizeChoice(SizeLabel(side), side, extent));
            }
        }

        return choices;
    }

    private static string SizeLabel(double sideMetres)
        => sideMetres >= 1000.0
            ? string.Create(CultureInfo.InvariantCulture, $"{sideMetres / 1000.0:0.##} km square")
            : string.Create(CultureInfo.InvariantCulture, $"{sideMetres:0} m square");

    private static double MetresPerDegreeLatitude(double latitudeDegrees)
    {
        double phi = latitudeDegrees * Math.PI / 180.0;
        return 111132.954 - 559.822 * Math.Cos(2.0 * phi) + 1.175 * Math.Cos(4.0 * phi);
    }

    private static double MetresPerDegreeLongitude(double latitudeDegrees)
    {
        double phi = latitudeDegrees * Math.PI / 180.0;
        return 111412.84 * Math.Cos(phi) - 93.5 * Math.Cos(3.0 * phi) + 0.118 * Math.Cos(5.0 * phi);
    }
}

public static class CuratorLink
{
    public const string From = "revit";

    private const double BoundScale = 10000.0;

    private const double RoundingSlack = 1e-6;

    public static string Build(string? siteBaseUrl, CuratorExtent? extent)
    {
        string link = (AuthUrls.NormaliseBaseUrl(siteBaseUrl) ?? FeedbackLink.DefaultSite) + "/curator";
        return extent is { IsValid: true } drawn
            ? link + "#aoi=" + Token(drawn) + "&from=" + From
            : link;
    }

    public static string Token(CuratorExtent extent)
        => FormatBound(extent.West, up: false) + "," + FormatBound(extent.South, up: false) + ","
            + FormatBound(extent.East, up: true) + "," + FormatBound(extent.North, up: true);

    public static string FormatBound(double value, bool up)
    {
        double scaled = value * BoundScale;
        long units = (long)(up ? Math.Ceiling(scaled - RoundingSlack) : Math.Floor(scaled + RoundingSlack));
        long whole = units / 10000;
        long fraction = Math.Abs(units % 10000);

        if (fraction == 0)
        {
            return whole.ToString(CultureInfo.InvariantCulture);
        }

        string digits = fraction.ToString("D4", CultureInfo.InvariantCulture).TrimEnd('0');

        StringBuilder text = new();
        text.Append(units < 0 && whole == 0 ? "-0" : whole.ToString(CultureInfo.InvariantCulture));
        text.Append('.').Append(digits);
        return text.ToString();
    }
}
