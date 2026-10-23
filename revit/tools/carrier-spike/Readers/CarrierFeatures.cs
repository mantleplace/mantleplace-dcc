using System.Globalization;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.CarrierSpike;

/// <summary>One position as a carrier file wrote it: x first, whatever the frame; Z when there is one.</summary>
public readonly record struct CarrierPosition(double X, double Y, double? Z);

/// <summary>One line or ring out of a carrier file, before it is placed in a frame.</summary>
/// <param name="Positions">As written; a ring may repeat its first position at the end.</param>
/// <param name="Closed">A ring of an area, not a line.</param>
/// <param name="IsHole">An inner ring.</param>
/// <param name="Polygon">Which polygon of the layer it belongs to, counting from one; zero for a line.</param>
public sealed record CarrierPart(IReadOnlyList<CarrierPosition> Positions, bool Closed, bool IsHole, int Polygon);

/// <summary>
/// The one place a carrier's parts and attributes become the <see cref="SiteFeature"/> list the
/// importer already takes, so both prototype readers agree with <see cref="SiteVectorReader"/> by
/// construction and a parity test can hold each to it.
/// </summary>
/// <remarks>
/// It repeats SiteVectorReader's three rules on purpose: a ring's repeated closing position is
/// dropped, a line under two positions or a ring under three is no feature, and a position the
/// frame cannot place is passed over. Placement is the frame's own <see cref="SiteFrame.TryPlace"/>:
/// subtraction and scaling, nothing derived here.
/// </remarks>
public static class CarrierFeatureBuilder
{
    /// <summary>The attribute names the Revit importer reads today, the carrier's column and key names.</summary>
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
