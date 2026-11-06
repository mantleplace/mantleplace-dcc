using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>
/// The shared coordinates a project already holds, read back from it: the point every later bundle
/// is placed against.
/// </summary>
/// <param name="Epsg">
/// The EPSG code the project's first Mantle Place bundle named, or <c>null</c> for a project that
/// was imported into before the add-in recorded it (<see cref="ProjectAnchorStorage"/>).
/// </param>
/// <param name="EastingM">The survey point's easting, in metres.</param>
/// <param name="NorthingM">The survey point's northing, in metres.</param>
/// <param name="AngleRadians">The project's angle to true north, as Revit holds it.</param>
public sealed record ProjectAnchor(int? Epsg, double EastingM, double NorthingM, double AngleRadians);

/// <summary>What <see cref="AdjacentBundle.Decide"/> concluded.</summary>
public enum AnchorDecisionKind
{
    /// <summary>The bundle publishes no survey point, so there is nothing to anchor. The model goes in the project's own frame.</summary>
    NoSurveyPoint,

    /// <summary>The project holds no Mantle Place import: this bundle's survey point becomes the project position.</summary>
    FirstBundle,

    /// <summary>The project's position stands, and the bundle is built at <see cref="AnchorDecision.OffsetEastM"/> / <see cref="AnchorDecision.OffsetNorthM"/> from the internal origin.</summary>
    Offset,

    /// <summary>The bundle cannot be placed against what the project holds; nothing is imported.</summary>
    Refused,
}

/// <summary>Why a bundle was refused, one per rule.</summary>
public enum AnchorRefusal
{
    None,

    /// <summary>The two name different EPSG codes; relating them takes a reprojection.</summary>
    DifferentCrs,

    /// <summary>The project's code was never recorded and the bundle is not at the project's point, so there is nothing to compare.</summary>
    UnknownCrs,

    /// <summary>The two publish different angles to true north.</summary>
    DifferentAngle,

    /// <summary>Both publish one non-zero angle; offsetting along a rotated grid is a rotation the plugin does not compute.</summary>
    RotatedGrid,

    /// <summary>The bundle's point is farther from the anchor than <see cref="AdjacentBundle.MaxOffsetM"/>.</summary>
    TooFar,
}

/// <summary>Where a bundle goes against the project's anchor, or the one reason it does not go.</summary>
public sealed class AnchorDecision
{
    public required AnchorDecisionKind Kind { get; init; }

    public AnchorRefusal Refusal { get; init; }

    /// <summary>
    /// Metres east of the internal origin the bundle's own origin is built at. Only for
    /// <see cref="AnchorDecisionKind.Offset"/>; zero otherwise.
    /// </summary>
    public double OffsetEastM { get; init; }

    /// <summary>Metres north of the internal origin the bundle's own origin is built at. Zero unless <see cref="AnchorDecisionKind.Offset"/>.</summary>
    public double OffsetNorthM { get; init; }

    /// <summary>The single sentence a refusal is shown as. Empty unless <see cref="IsRefusal"/>.</summary>
    public string Message { get; init; } = string.Empty;

    public bool IsRefusal => Kind == AnchorDecisionKind.Refused;

    /// <summary>Whether the importer has to build away from the internal origin.</summary>
    public bool MovesGeometry => Kind == AnchorDecisionKind.Offset && (OffsetEastM != 0.0 || OffsetNorthM != 0.0);
}

/// <summary>
/// Whether a bundle goes in against the shared coordinates the project already holds, and where.
/// </summary>
/// <remarks>
/// <para>
/// Every bundle's geometry is built about its own site origin, so two bundles imported into one
/// project would sit on top of each other about the project's internal origin, and the second
/// import used to move the survey point under the first. The project's first Mantle Place bundle
/// now fixes the position, and is the <b>anchor</b>; a later bundle is built at the difference of
/// the two published survey points.
/// </para>
/// <para>
/// This subtracts two numbers the manifests publish. It reprojects nothing and derives no placement
/// (<c>HPS-33</c>), which is why it is only ever done for one EPSG code and one angle: in any other
/// case relating the two would take a reprojection or a rotation, and the answer is a refusal, not
/// a computation.
/// </para>
/// <para>
/// <b>Height is not part of the offset.</b> Every artifact's Z is an absolute orthometric height and
/// the survey point's elevation is zero (<see cref="SurveyPointPlacement.ElevationM"/>), so two
/// adjacent bundles already agree on height; adding the difference of two elevations would
/// double-count it. Matching heights across a tile seam is platform-side work.
/// </para>
/// </remarks>
public static class AdjacentBundle
{
    /// <summary>
    /// The farthest a bundle's survey point may be from the anchor's, in metres, as a straight-line
    /// distance.
    /// </summary>
    /// <remarks>
    /// Autodesk's guidance is to keep a model within about 20 miles (32 km) of the internal origin:
    /// past that, Revit's graphics and its geometry tolerances degrade. The anchor is built at the
    /// origin and the later bundle is built about a point this far out, then reaches beyond it by
    /// its own half-extent (a few kilometres for a tile order). Half the guidance, 16 km, leaves the
    /// other half for that reach and for the anchor's own spread, and is far beyond any order of
    /// adjacent tiles. A bundle past it is refused rather than built where Revit is unreliable.
    /// </remarks>
    public const double MaxOffsetM = 16_000.0;

    /// <summary>
    /// How far two published angles may differ and still be the same angle, in radians.
    /// </summary>
    /// <remarks>
    /// The angle round-trips through Revit as a double and through a degrees-to-radians conversion,
    /// so exact equality is too strict. 1e-7 rad is two hundredths of an arc-second; over the 16 km
    /// limit it moves a point by under 2 mm, below anything a seam shows.
    /// </remarks>
    public const double AngleToleranceRadians = 1e-7;

    /// <summary>
    /// How close two points must be to be the same point, in metres. Used only where the project's EPSG
    /// was never recorded, to recognise a re-import of the bundle that set the position.
    /// </summary>
    public const double SamePointToleranceM = 0.001;

    /// <param name="anchor">What the project holds, or <c>null</c> when it holds no Mantle Place import.</param>
    /// <param name="incoming">The bundle's survey point, or <c>null</c> when it publishes none.</param>
    public static AnchorDecision Decide(ProjectAnchor? anchor, SurveyPointPlacement? incoming)
    {
        if (incoming is not { Origin.IsUsable: true })
        {
            return new AnchorDecision { Kind = AnchorDecisionKind.NoSurveyPoint };
        }

        if (anchor is null)
        {
            return new AnchorDecision { Kind = AnchorDecisionKind.FirstBundle };
        }

        GeoOrigin origin = incoming.Origin;
        double metresPerUnit = LinearUnits.MetresPerUnit(origin.LinearUnit);
        double eastM = (origin.Easting ?? 0.0) * metresPerUnit;
        double northM = (origin.Northing ?? 0.0) * metresPerUnit;
        double offsetEastM = eastM - anchor.EastingM;
        double offsetNorthM = northM - anchor.NorthingM;
        double distanceM = Math.Sqrt((offsetEastM * offsetEastM) + (offsetNorthM * offsetNorthM));

        if (anchor.Epsg is { } projectEpsg && origin.Epsg is { } bundleEpsg && projectEpsg != bundleEpsg)
        {
            return Refuse(
                AnchorRefusal.DifferentCrs,
                $"This project's shared coordinates are in EPSG:{projectEpsg} and this bundle is in EPSG:{bundleEpsg}. "
                + "Placing one beside the other would take a reprojection, which this plugin never does.");
        }

        if (anchor.Epsg is null && distanceM > SamePointToleranceM)
        {
            return Refuse(
                AnchorRefusal.UnknownCrs,
                "This project was imported into before it recorded which EPSG code its shared coordinates are in, "
                + "and this bundle is not at the same point, so the two cannot be checked against each other.");
        }

        if (Math.Abs(anchor.AngleRadians - incoming.AngleRadians) > AngleToleranceRadians)
        {
            return Refuse(
                AnchorRefusal.DifferentAngle,
                $"This project's angle to true north is {Degrees(anchor.AngleRadians)} degrees and this bundle's is "
                + $"{Degrees(incoming.AngleRadians)}. Placing one beside the other would take a rotation, which this plugin never computes.");
        }

        if (Math.Abs(incoming.AngleRadians) > AngleToleranceRadians)
        {
            return Refuse(
                AnchorRefusal.RotatedGrid,
                $"Both this project and this bundle are rotated {Degrees(incoming.AngleRadians)} degrees from true north. "
                + "Offsetting along a rotated grid would take a rotation, which this plugin never computes.");
        }

        if (!double.IsFinite(distanceM) || distanceM > MaxOffsetM)
        {
            return Refuse(
                AnchorRefusal.TooFar,
                $"This bundle is {Kilometres(distanceM)} km from the first bundle imported into this project, beyond the "
                + $"{Kilometres(MaxOffsetM)} km this plugin builds away from the project origin, where Revit loses accuracy.");
        }

        return new AnchorDecision
        {
            Kind = AnchorDecisionKind.Offset,
            OffsetEastM = offsetEastM,
            OffsetNorthM = offsetNorthM,
        };
    }

    /// <summary>The line the import log carries for an offset, with the numbers.</summary>
    public static string Describe(AnchorDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return decision.Kind switch
        {
            AnchorDecisionKind.Offset when decision.MovesGeometry => string.Create(
                CultureInfo.InvariantCulture,
                $"This project already holds a Mantle Place import, so its shared coordinates are kept and this bundle is built "
                + $"{decision.OffsetEastM:0.###} m east and {decision.OffsetNorthM:0.###} m north of the project origin, where its survey point is."),
            AnchorDecisionKind.Offset => "This bundle is at the point the project's shared coordinates already name, so they are kept as they are.",
            AnchorDecisionKind.FirstBundle => string.Empty,
            AnchorDecisionKind.NoSurveyPoint => string.Empty,
            _ => decision.Message,
        };
    }

    private static AnchorDecision Refuse(AnchorRefusal reason, string because) => new()
    {
        Kind = AnchorDecisionKind.Refused,
        Refusal = reason,
        Message = because + " Nothing was imported and the project's shared coordinates are unchanged.",
    };

    private static string Degrees(double radians) => (radians * 180.0 / Math.PI).ToString("0.######", CultureInfo.InvariantCulture);

    private static string Kilometres(double metres) => (metres / 1000.0).ToString("0.#", CultureInfo.InvariantCulture);
}

/// <summary>
/// The ExtensibleStorage schema that remembers which EPSG code the project's shared coordinates
/// were set from.
/// </summary>
/// <remarks>
/// <para>
/// Revit holds the survey point but not the CRS it came from, and relating two bundles is only
/// allowed within one CRS. So the first import records the code beside the provenance record, on
/// Project Information, under a schema of its own: the provenance schema is permanent and its field
/// list cannot grow (<see cref="ProvenanceStorage"/>, ADR 0011). The numbers themselves are not
/// stored: the anchor is whatever the project's position is now, so a curator who moves the survey
/// point on purpose moves the anchor with it.
/// </para>
/// <para>
/// ⛔ The same rule as the provenance schema applies: <see cref="SchemaGuid"/> is permanent once any
/// document holds it, and a change to the fields is a new GUID.
/// </para>
/// </remarks>
public static class ProjectAnchorStorage
{
    /// <summary>Revit's key for the schema. Never reused, never changed.</summary>
    public static readonly Guid SchemaGuid = new("fb674596-034f-4f59-8781-874fae664ced");

    public const string SchemaName = "MantlePlaceProjectAnchor";

    /// <summary>The add-in manifest's own <c>VendorId</c>, which the headless suite checks.</summary>
    public const string VendorId = ProvenanceStorage.VendorId;

    public const string EpsgField = "Epsg";
}
