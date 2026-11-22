using System.Globalization;

namespace MantlePlace.Core;

public sealed record ProjectAnchor(int? Epsg, double EastingM, double NorthingM, double AngleRadians);

public enum AnchorDecisionKind
{
    NoSurveyPoint,

    FirstBundle,

    Offset,

    Refused,
}

public enum AnchorRefusal
{
    None,

    DifferentCrs,

    UnknownCrs,

    DifferentAngle,

    RotatedGrid,

    TooFar,
}

public sealed class AnchorDecision
{
    public required AnchorDecisionKind Kind { get; init; }

    public AnchorRefusal Refusal { get; init; }

    public double OffsetEastM { get; init; }

    public double OffsetNorthM { get; init; }

    public string Message { get; init; } = string.Empty;

    public bool IsRefusal => Kind == AnchorDecisionKind.Refused;

    public bool MovesGeometry => Kind == AnchorDecisionKind.Offset && (OffsetEastM != 0.0 || OffsetNorthM != 0.0);
}

public static class AdjacentBundle
{
    public const double MaxOffsetM = 16_000.0;

    public const double AngleToleranceRadians = 1e-7;

    public const double SamePointToleranceM = 0.001;

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

        if (distanceM <= SamePointToleranceM)
        {
            return new AnchorDecision { Kind = AnchorDecisionKind.Offset };
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

public static class ProjectAnchorStorage
{
    public static readonly Guid SchemaGuid = new("fb674596-034f-4f59-8781-874fae664ced");

    public const string SchemaName = "MantlePlaceProjectAnchor";

    public const string VendorId = ProvenanceStorage.VendorId;

    public const string EpsgField = "Epsg";
}
