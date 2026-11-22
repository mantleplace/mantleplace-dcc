using System.Text.Json;

namespace MantlePlace.Core;

[Flags]
public enum SiteGeometryKinds
{
    None = 0,

    Lines = 1,

    Areas = 2,
}

public static class SiteGeometryFamilies
{
    public static SiteGeometryKinds? Holds(string? geometryFamily) => geometryFamily switch
    {
        "line" => SiteGeometryKinds.Lines,
        "polygon" => SiteGeometryKinds.Areas,
        "point" => SiteGeometryKinds.None,
        _ => null,
    };
}

public readonly record struct SiteVertex(double EastM, double NorthM, double? ElevationM);

public sealed class SiteFeature
{
    public required IReadOnlyList<SiteVertex> Vertices { get; init; }

    public bool IsClosed { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Classification { get; init; } = string.Empty;

    public double? WidthM { get; init; }

    public string Subtype { get; init; } = string.Empty;

    public bool IsHole { get; init; }

    public int PolygonOrdinal { get; init; }

    public string FloodZone { get; init; } = string.Empty;

    public string FloodZoneSubtype { get; init; } = string.Empty;

    public string Threshold { get; init; } = string.Empty;

    public string? LowestLevel { get; init; }
}

public static class SiteVectorReader
{
    public const int MinimumLineVertices = 2;

    public const int MinimumRingVertices = 3;

    public static string? TryParse(
        string geoJsonText,
        SiteFrame frame,
        SiteGeometryKinds accept,
        string label,
        out IReadOnlyList<SiteFeature> features)
        => TryParse(geoJsonText, frame, LayerFrame.Geographic, accept, label, out features);

    public static string? TryParse(
        string geoJsonText,
        SiteFrame frame,
        LayerFrame layer,
        SiteGeometryKinds accept,
        string label,
        out IReadOnlyList<SiteFeature> features,
        string? levelField = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Placement placement = new(frame, layer);

        List<SiteFeature> parsed = [];
        features = parsed;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(geoJsonText ?? string.Empty);
        }
        catch (JsonException)
        {
            return $"The {label} layer is not valid GeoJSON.";
        }

        using (document)
        {
            if (document.RootElement.Array("features") is not { } collection)
            {
                return $"The {label} layer carries no \"features\" array, so it is not a GeoJSON "
                    + "FeatureCollection. Re-download this bundle from your vault at mantle.place/vault.";
            }

            int polygons = 0;
            foreach (JsonElement feature in collection.EnumerateArray())
            {
                if (feature.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                AppendFeature(feature, placement, accept, levelField, parsed, ref polygons);
            }
        }

        return null;
    }

    private static void AppendFeature(
        JsonElement feature,
        Placement placement,
        SiteGeometryKinds accept,
        string? levelField,
        List<SiteFeature> parsed,
        ref int polygons)
    {
        JsonElement? properties = feature.Object("properties");
        FeatureProperties carried = new(
            properties?.Str("name") ?? string.Empty,
            properties?.Str("class") ?? string.Empty,
            properties?.OptionalDouble("width_m_estimated"),
            properties?.Str("subtype") ?? string.Empty,
            properties?.Str("fld_zone") ?? string.Empty,
            properties?.Str("zone_subty") ?? string.Empty,
            properties?.RawNumber("threshold_deg") ?? string.Empty,
            levelField is null ? null : properties?.OptionalStr(levelField));

        if (feature.Object("geometry") is not { } geometry)
        {
            return;
        }

        string type = geometry.Str("type");
        if (geometry.Array("coordinates") is not { } coordinates)
        {
            return;
        }

        bool closed = type is "Polygon" or "MultiPolygon";
        SiteGeometryKinds kind = closed ? SiteGeometryKinds.Areas : SiteGeometryKinds.Lines;
        if ((accept & kind) == 0)
        {
            return;
        }

        switch (type)
        {
            case "LineString":
                AppendPath(coordinates, placement, closed: false, isHole: false, polygon: 0, carried, parsed);
                break;

            case "MultiLineString":
                foreach (JsonElement path in coordinates.EnumerateArray())
                {
                    if (path.ValueKind == JsonValueKind.Array)
                    {
                        AppendPath(path, placement, closed: false, isHole: false, polygon: 0, carried, parsed);
                    }
                }

                break;

            case "Polygon":
                AppendRings(coordinates, placement, carried, parsed, ++polygons);
                break;

            case "MultiPolygon":
                foreach (JsonElement polygon in coordinates.EnumerateArray())
                {
                    if (polygon.ValueKind == JsonValueKind.Array)
                    {
                        AppendRings(polygon, placement, carried, parsed, ++polygons);
                    }
                }

                break;

            default:
                break;
        }
    }

    private static void AppendRings(
        JsonElement polygon,
        Placement placement,
        FeatureProperties carried,
        List<SiteFeature> parsed,
        int ordinal)
    {
        int ring = 0;
        foreach (JsonElement path in polygon.EnumerateArray())
        {
            if (path.ValueKind == JsonValueKind.Array)
            {
                AppendPath(path, placement, closed: true, isHole: ring > 0, ordinal, carried, parsed);
            }

            ring++;
        }
    }

    private static void AppendPath(
        JsonElement path,
        Placement placement,
        bool closed,
        bool isHole,
        int polygon,
        FeatureProperties carried,
        List<SiteFeature> parsed)
    {
        List<SiteVertex> vertices = [];
        foreach (JsonElement position in path.EnumerateArray())
        {
            if (TryReadPosition(position, placement, out SiteVertex vertex))
            {
                vertices.Add(vertex);
            }
        }

        if (!TryFinishPath(vertices, closed))
        {
            return;
        }

        parsed.Add(new SiteFeature
        {
            Vertices = vertices,
            IsClosed = closed,
            Name = carried.Name,
            Classification = carried.Classification,
            WidthM = carried.WidthM,
            Subtype = carried.Subtype,
            FloodZone = carried.FloodZone,
            FloodZoneSubtype = carried.FloodZoneSubtype,
            Threshold = carried.Threshold,
            LowestLevel = carried.LowestLevel,
            IsHole = isHole,
            PolygonOrdinal = polygon,
        });
    }

    private readonly record struct FeatureProperties(
        string Name,
        string Classification,
        double? WidthM,
        string Subtype,
        string FloodZone,
        string FloodZoneSubtype,
        string Threshold,
        string? LowestLevel);

    private static bool TryReadPosition(JsonElement position, Placement placement, out SiteVertex vertex)
    {
        vertex = default;

        if (position.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        double? first = null;
        double? second = null;
        double? elevation = null;

        int ordinal = 0;
        foreach (JsonElement value in position.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number))
            {
                return false;
            }

            switch (ordinal)
            {
                case 0:
                    first = number;
                    break;
                case 1:
                    second = number;
                    break;
                case 2:
                    elevation = number;
                    break;
                default:
                    break;
            }

            ordinal++;
        }

        return first is { } x
            && second is { } y
            && TryPlaceVertex(placement.Frame, placement.Layer, x, y, elevation, out vertex);
    }

    public static bool TryPlaceVertex(SiteFrame frame, LayerFrame layer, double x, double y, double? z, out SiteVertex vertex)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (!frame.TryPlace(layer, x, y, out double east, out double north))
        {
            vertex = default;
            return false;
        }

        vertex = new SiteVertex(east, north, z * LinearUnits.MetresPerUnit(layer.Unit));
        return true;
    }

    public static bool TryFinishPath(List<SiteVertex> vertices, bool closed)
    {
        ArgumentNullException.ThrowIfNull(vertices);

        if (!closed)
        {
            return vertices.Count >= MinimumLineVertices;
        }

        if (vertices.Count > 1 && SamePlanPosition(vertices[0], vertices[^1]))
        {
            vertices.RemoveAt(vertices.Count - 1);
        }

        return vertices.Count >= MinimumRingVertices;
    }

    private readonly record struct Placement(SiteFrame Frame, LayerFrame Layer);

    private static bool SamePlanPosition(SiteVertex left, SiteVertex right)
        => Math.Abs(left.EastM - right.EastM) < 1e-6 && Math.Abs(left.NorthM - right.NorthM) < 1e-6;
}
