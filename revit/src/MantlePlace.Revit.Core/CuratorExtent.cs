using System.Globalization;
using System.Text;

namespace MantlePlace.Revit.Core;

/// <summary>A WGS84 extent in degrees: west, south, east, north — the order of the curator link's <c>aoi</c> token.</summary>
public readonly record struct CuratorExtent(double West, double South, double East, double North)
{
    /// <summary>Finite, on the globe, and with area: west &lt; east and south &lt; north.</summary>
    public bool IsValid
        => double.IsFinite(West) && double.IsFinite(South) && double.IsFinite(East) && double.IsFinite(North)
            && West >= -180.0 && East <= 180.0 && South >= -90.0 && North <= 90.0
            && West < East && South < North;
}

/// <summary>The project's site location, in degrees.</summary>
public readonly record struct SiteLocationPoint(double LatitudeDegrees, double LongitudeDegrees)
{
    /// <summary>
    /// A latitude this close to a pole has no east-west metre worth the name, so a box around it
    /// cannot be drawn.
    /// </summary>
    private const double MaxLatitude = 89.9;

    /// <summary>
    /// Revit's <c>SiteLocation.Latitude</c> and <c>Longitude</c> are radians; the curator's link is degrees.
    /// </summary>
    public static SiteLocationPoint FromRadians(double latitudeRadians, double longitudeRadians)
        => new(latitudeRadians * 180.0 / Math.PI, longitudeRadians * 180.0 / Math.PI);

    /// <summary>
    /// A located point: on the globe, away from the poles, and not exactly (0, 0), which is how a
    /// project nobody has located reads and is refused rather than sent to the Gulf of Guinea.
    /// </summary>
    public bool IsLocated
        => double.IsFinite(LatitudeDegrees) && double.IsFinite(LongitudeDegrees)
            && Math.Abs(LatitudeDegrees) <= MaxLatitude && Math.Abs(LongitudeDegrees) <= 180.0
            && !(LatitudeDegrees == 0.0 && LongitudeDegrees == 0.0);
}

/// <summary>Where the extent Open in Curator uses came from, in the order they are tried.</summary>
public enum CuratorExtentSource
{
    /// <summary>The project knows no extent: the plain curator opens.</summary>
    None,

    /// <summary>The order id the project's provenance stores, whose extent the vault record carries.</summary>
    PriorImport,

    /// <summary>The project's site latitude and longitude, with a size the curator picks.</summary>
    SiteLocation,
}

/// <summary>What the project says, already read out of the model and the vault.</summary>
public sealed class CuratorProjectState
{
    /// <summary>
    /// The vault order id the project's provenance stores (<see cref="ProjectProvenance.OrderId"/>);
    /// <c>null</c> or blank when the project was never imported into.
    /// </summary>
    public string? ImportedOrderId { get; init; }

    /// <summary>
    /// The curator's vault listing; <c>null</c> when it could not be read (signed out, offline), which
    /// is not the same as an empty vault.
    /// </summary>
    public IReadOnlyList<VaultBundle>? VaultBundles { get; init; }

    /// <summary>The project's site location; <c>null</c> when it has none to read.</summary>
    public SiteLocationPoint? Site { get; init; }
}

/// <summary>One size the curator may pick around the site location.</summary>
/// <param name="Label">What the picker says.</param>
/// <param name="SideMetres">The square's side.</param>
/// <param name="Extent">The extent of that square around the site location.</param>
public sealed record CuratorSizeChoice(string Label, double SideMetres, CuratorExtent Extent);

/// <summary>What Open in Curator will do, decided.</summary>
public sealed class CuratorResolution
{
    public required CuratorExtentSource Source { get; init; }

    /// <summary>The extent to open on; set for <see cref="CuratorExtentSource.PriorImport"/> only.</summary>
    public CuratorExtent? Extent { get; init; }

    /// <summary>
    /// For <see cref="CuratorExtentSource.SiteLocation"/>: the sizes to offer, smallest first.
    /// </summary>
    public IReadOnlyList<CuratorSizeChoice> SizeChoices { get; init; } = [];

    /// <summary>
    /// The project names an order whose extent could not be read (the vault listing was unavailable,
    /// does not hold the order, or states no drawable extent), so the source is the next one down.
    /// What the shim says so the curator is not left guessing why the import was not used.
    /// </summary>
    public bool OrderExtentUnavailable { get; init; }
}

/// <summary>
/// Which extent the project already knows. A pure function from the project's state to the extent
/// (or the sizes to choose from), so it is asserted without a Revit.
/// </summary>
/// <remarks>
/// Open in Curator is not a placement: nothing here is applied to the project. It reads what the
/// project already states — the order its provenance names, the vault record's own extent, the
/// site location — and hands it to the web, where the curator draws it. The plugin derives no
/// extent from geometry; the one computation, a square of a chosen size around a point, is the
/// curator's choice of how much to look at, not a fact about the model.
/// </remarks>
public static class CuratorExtents
{
    /// <summary>
    /// The sizes offered around a site location, as the side of a square in metres. The curator's
    /// floor is about 316 m a side and its ceiling 8 km, so every entry is inside both.
    /// </summary>
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

    /// <summary>
    /// The square of <paramref name="sideMetres"/> around <paramref name="center"/>, or <c>null</c> when
    /// it would leave the world (across the antimeridian or past a pole) and so has no W,S,E,N the
    /// curator can draw. It is refused rather than clipped to a smaller box than was asked for.
    /// </summary>
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

    /// <summary>Metres in one degree of latitude at this latitude (WGS84, the standard series).</summary>
    private static double MetresPerDegreeLatitude(double latitudeDegrees)
    {
        double phi = latitudeDegrees * Math.PI / 180.0;
        return 111132.954 - 559.822 * Math.Cos(2.0 * phi) + 1.175 * Math.Cos(4.0 * phi);
    }

    /// <summary>Metres in one degree of longitude at this latitude (WGS84, the standard series).</summary>
    private static double MetresPerDegreeLongitude(double latitudeDegrees)
    {
        double phi = latitudeDegrees * Math.PI / 180.0;
        return 111412.84 * Math.Cos(phi) - 93.5 * Math.Cos(3.0 * phi) + 0.118 * Math.Cos(5.0 * phi);
    }
}

/// <summary>
/// The address Open in Curator opens in the system browser: the curator, and the extent as
/// <c>#aoi=W,S,E,N&amp;from=revit</c> when one is known.
/// </summary>
/// <remarks>
/// Nothing else rides in the link: digits, minus signs, dots and commas, so nothing needs
/// percent-encoding. No account, order id or project name is in it.
/// </remarks>
public static class CuratorLink
{
    /// <summary>The <c>from</c> value the curator records for a handoff from this host.</summary>
    public const string From = "revit";

    // The curator keeps four decimals (about 11 m); a link carries no more than it keeps.
    private const double BoundScale = 10000.0;

    // Added before a floor and taken off before a ceil, so 12.3456 stays 12.3456 despite binary error.
    private const double RoundingSlack = 1e-6;

    /// <param name="siteBaseUrl">The deployment this plugin talks to; unusable falls back to <see cref="FeedbackLink.DefaultSite"/>.</param>
    /// <param name="extent">The extent to open on; <c>null</c>, or one that cannot be drawn, opens the plain curator.</param>
    public static string Build(string? siteBaseUrl, CuratorExtent? extent)
    {
        string link = (AuthUrls.NormaliseBaseUrl(siteBaseUrl) ?? FeedbackLink.DefaultSite) + "/curator";
        return extent is { IsValid: true } drawn
            ? link + "#aoi=" + Token(drawn) + "&from=" + From
            : link;
    }

    /// <summary>
    /// The <c>W,S,E,N</c> token, each bound at the curator's precision and rounded outward so the
    /// drawn extent always contains the one it was made from.
    /// </summary>
    public static string Token(CuratorExtent extent)
        => FormatBound(extent.West, up: false) + "," + FormatBound(extent.South, up: false) + ","
            + FormatBound(extent.East, up: true) + "," + FormatBound(extent.North, up: true);

    /// <summary>One bound at four decimals, shortest form. <paramref name="up"/> rounds toward +infinity, else toward -infinity.</summary>
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

        // Whole is 0 for -0.5, which would print as 0.5: the sign has to be put back by hand.
        StringBuilder text = new();
        text.Append(units < 0 && whole == 0 ? "-0" : whole.ToString(CultureInfo.InvariantCulture));
        text.Append('.').Append(digits);
        return text.ToString();
    }
}
