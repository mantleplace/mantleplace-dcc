using System.Globalization;
using MantlePlace.Core;

namespace MantlePlace.Revit.CarrierSpike;

public readonly record struct CarrierPosition(double X, double Y, double? Z);

public sealed record CarrierPart(IReadOnlyList<CarrierPosition> Positions, bool Closed, bool IsHole, int Polygon);

public static class CarrierFeatureBuilder
{
    public static readonly IReadOnlyList<string> AttributeNames =
    [
        "name", "class", "width_m_estimated", "subtype", "fld_zone", "zone_subty", "threshold_deg",
    ];

    public static void Append(
        List<SiteFeature> into,
        CarrierPart part,
        IReadOnlyDictionary<string, string> attributes,
        SiteFrame frame,
        LayerFrame layer,
        SiteGeometryKinds accept,
        string? levelField)
    {
        ArgumentNullException.ThrowIfNull(into);
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(frame);

        SiteGeometryKinds kind = part.Closed ? SiteGeometryKinds.Areas : SiteGeometryKinds.Lines;
        if ((accept & kind) == 0)
        {
            return;
        }

        double metresPerUnit = LinearUnits.MetresPerUnit(layer.Unit);
        List<SiteVertex> vertices = new(part.Positions.Count);
        foreach (CarrierPosition position in part.Positions)
        {
            if (frame.TryPlace(layer, position.X, position.Y, out double east, out double north))
            {
                vertices.Add(new SiteVertex(east, north, position.Z * metresPerUnit));
            }
        }

        if (part.Closed)
        {
            if (vertices.Count > 1
                && Math.Abs(vertices[0].EastM - vertices[^1].EastM) < 1e-6
                && Math.Abs(vertices[0].NorthM - vertices[^1].NorthM) < 1e-6)
            {
                vertices.RemoveAt(vertices.Count - 1);
            }

            if (vertices.Count < SiteVectorReader.MinimumRingVertices)
            {
                return;
            }
        }
        else if (vertices.Count < SiteVectorReader.MinimumLineVertices)
        {
            return;
        }

        into.Add(new SiteFeature
        {
            Vertices = vertices,
            IsClosed = part.Closed,
            IsHole = part.IsHole,
            PolygonOrdinal = part.Polygon,
            Name = Get(attributes, "name"),
            Classification = Get(attributes, "class"),
            WidthM = double.TryParse(Get(attributes, "width_m_estimated"), NumberStyles.Float, CultureInfo.InvariantCulture, out double width) ? width : null,
            Subtype = Get(attributes, "subtype"),
            FloodZone = Get(attributes, "fld_zone"),
            FloodZoneSubtype = Get(attributes, "zone_subty"),
            Threshold = Get(attributes, "threshold_deg"),
            LowestLevel = levelField is not null && attributes.TryGetValue(levelField, out string? level) ? level : null,
        });
    }

    private static string Get(IReadOnlyDictionary<string, string> attributes, string key)
        => attributes.TryGetValue(key, out string? value) ? value : string.Empty;
}
