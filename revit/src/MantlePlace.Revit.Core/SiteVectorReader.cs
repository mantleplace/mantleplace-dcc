using System.Text.Json;

namespace MantlePlace.Revit.Core;

/// <summary>Which GeoJSON geometries a caller wants out of a layer.</summary>
/// <remarks>
/// The two parity layers read the same file format for different things: road centrelines are
/// LineStrings and site boundaries are Polygon rings. Asking for one and silently getting the other
/// would put road linework into the property-boundary category, so the caller states which.
/// </remarks>
[Flags]
public enum SiteGeometryKinds
{
    None = 0,

    /// <summary><c>LineString</c> and each part of a <c>MultiLineString</c>.</summary>
    Lines = 1,

    /// <summary>Each ring of a <c>Polygon</c> or <c>MultiPolygon</c>, outer and inner alike.</summary>
    Areas = 2,
}

/// <summary>One vertex in the bundle's local frame — east/north metres, Z absolute orthometric.</summary>
/// <param name="ElevationM">
/// <c>null</c> when the position carried no third ordinate. Unknown, not zero (<c>HPS-20</c>): the
/// land-use polygons are 2-D by design and drape onto the terrain, where a literal 0.0 would place
/// them two kilometres below it.
/// </param>
public readonly record struct SiteVertex(double EastM, double NorthM, double? ElevationM);

/// <summary>One line or ring, with the properties worth carrying onto the element it becomes.</summary>
public sealed class SiteFeature
{
    public required IReadOnlyList<SiteVertex> Vertices { get; init; }

    /// <summary>True for a ring — the caller must close it, not repeat the first vertex.</summary>
    public bool IsClosed { get; init; }

    /// <summary>GeoJSON <c>properties.name</c>, or empty.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>GeoJSON <c>properties.class</c> — Overture's road/land-use class, or empty.</summary>
    public string Classification { get; init; } = string.Empty;

    /// <summary><c>properties.width_m_estimated</c>, or <c>null</c>. Roads only.</summary>
    public double? WidthM { get; init; }

    /// <summary>
    /// GeoJSON <c>properties.subtype</c> verbatim — Overture's physical or use subtype, such as
    /// <c>forest</c> — or empty. What <see cref="RendererKeywords"/> reads a keyword from.
    /// </summary>
    public string Subtype { get; init; } = string.Empty;

    /// <summary>
    /// True for an inner ring of a polygon: the ground where the polygon's subtype is <em>not</em>.
    /// </summary>
    /// <remarks>
    /// Every ring is still its own feature, as it always has been, so the stamps and their positions
    /// do not move. What this changes is what a hole may claim: a clearing cut out of a forest is
    /// not forest, and it must not be named as though it were.
    /// </remarks>
    public bool IsHole { get; init; }

    /// <summary>
    /// Which polygon of the layer this ring came out of, counting from one; zero for a line.
    /// </summary>
    /// <remarks>
    /// The rings stay flat, one feature each, and this says which of them belong together — what
    /// <see cref="GroundCuts"/> needs to cut a polygon as one subdivision with its holes in it. It is
    /// the polygon's position in the layer rather than the ring's, so a polygon whose outer ring the
    /// reader dropped leaves holes that belong to no polygon rather than holes that attach
    /// themselves to the previous one.
    /// </remarks>
    public int PolygonOrdinal { get; init; }

    /// <summary>
    /// <c>properties.fld_zone</c> verbatim — the flood map's zone, such as <c>AE</c> — or empty.
    /// Flood zones only.
    /// </summary>
    public string FloodZone { get; init; } = string.Empty;

    /// <summary><c>properties.zone_subty</c> verbatim, such as <c>FLOODWAY</c>, or empty. Flood zones only.</summary>
    public string FloodZoneSubtype { get; init; } = string.Empty;

    /// <summary>
    /// <c>properties.threshold_deg</c> exactly as the file wrote the number, or empty. Steep ground only.
    /// </summary>
    public string Threshold { get; init; } = string.Empty;
}

/// <summary>
/// Parses a bundle GeoJSON vector layer into features placed in a <see cref="SiteFrame"/>. Pure.
/// </summary>
/// <remarks>
/// <para>
/// A layer reaches the frame by one of the routes a <see cref="LayerFrame"/> names. The shared
/// <c>vector</c> set is WGS84 lon/lat, as RFC 7946 fixes it, so every position goes through the one
/// permitted forward projection (<c>HPS-45</c>). This host's own copy, <c>hosts.revit.vectors</c>, is
/// already in this host's frame — absolute in the origin's CRS, or offsets about it — and is placed
/// by subtraction and scaling alone (<c>spec/format.md</c> §6.5). Nothing here decides WHICH layer
/// is read, or by which route — the planner does, from the manifest (<c>HPS-32</c>).
/// </para>
/// <para>
/// One malformed feature is dropped and the layer survives; malformed JSON, or a document with no
/// <c>features</c> array, fails the read. The asymmetry is deliberate: a single bad Overture row
/// must not cost a curator the other forty-four, while a file that is not a feature collection at
/// all is a pointer aimed at the wrong thing and saying "0 roads imported" would hide it.
/// </para>
/// </remarks>
public static class SiteVectorReader
{
    /// <summary>Fewest vertices a line needs; below it there is nothing to draw.</summary>
    public const int MinimumLineVertices = 2;

    /// <summary>Fewest vertices a closed ring needs once its repeated closing position is dropped.</summary>
    public const int MinimumRingVertices = 3;

    /// <summary>Parses a lon/lat layer of the shared <c>vector</c> set.</summary>
    /// <inheritdoc cref="TryParse(string, SiteFrame, LayerFrame, SiteGeometryKinds, string, out IReadOnlyList{SiteFeature})"/>
    public static string? TryParse(
        string geoJsonText,
        SiteFrame frame,
        SiteGeometryKinds accept,
        string label,
        out IReadOnlyList<SiteFeature> features)
        => TryParse(geoJsonText, frame, LayerFrame.Geographic, accept, label, out features);

    /// <summary>
    /// Parses a layer.
    /// </summary>
    /// <param name="geoJsonText">The layer file's text.</param>
    /// <param name="frame">The frame to place vertices in.</param>
    /// <param name="layer">What the file's coordinates are, and the unit they and any Z are in.</param>
    /// <param name="accept">Which geometries to take.</param>
    /// <param name="label">What to call the layer in a failure message, in the user's words.</param>
    /// <param name="features">The parsed features; empty on failure.</param>
    /// <returns><c>null</c> on success, or a user-facing reason the layer could not be read.</returns>
    public static string? TryParse(
        string geoJsonText,
        SiteFrame frame,
        LayerFrame layer,
        SiteGeometryKinds accept,
        string label,
        out IReadOnlyList<SiteFeature> features)
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

                AppendFeature(feature, placement, accept, parsed, ref polygons);
            }
        }

        return null;
    }

    private static void AppendFeature(
        JsonElement feature,
        Placement placement,
        SiteGeometryKinds accept,
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
            properties?.RawNumber("threshold_deg") ?? string.Empty);

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

        // Every accepted type normalises to "an array of position lists": a LineString is one, a
        // MultiLineString and a Polygon are both a list of them, and a MultiPolygon is a list of
        // those. Rings become their own features, outer and inner alike — Revit needs each loop
        // separately. Which is which is marked (IsHole) and nothing more: what a hole means for the
        // element it becomes is the caller's question, not this reader's.
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
                // Points, GeometryCollections and anything Overture adds later are not linework.
                break;
        }
    }

    /// <summary>
    /// One polygon's rings, each its own feature. RFC 7946 puts the exterior ring first, so every
    /// ring after it is a hole.
    /// </summary>
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

        if (closed)
        {
            // GeoJSON requires a ring's last position to repeat its first. Revit's CurveLoop closes
            // itself, and the zero-length segment that repetition would create is a curve it rejects
            // outright — so the duplicate is dropped here, where a test can see it.
            if (vertices.Count > 1 && SamePlanPosition(vertices[0], vertices[^1]))
            {
                vertices.RemoveAt(vertices.Count - 1);
            }

            if (vertices.Count < MinimumRingVertices)
            {
                return;
            }
        }
        else if (vertices.Count < MinimumLineVertices)
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
            IsHole = isHole,
            PolygonOrdinal = polygon,
        });
    }

    /// <summary>The properties one GeoJSON feature hands to every line or ring it yields.</summary>
    private readonly record struct FeatureProperties(
        string Name,
        string Classification,
        double? WidthM,
        string Subtype,
        string FloodZone,
        string FloodZoneSubtype,
        string Threshold);

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

        // Lon then lat on the shared set, easting then northing on this host's own copy: GeoJSON
        // orders a position x first whatever its frame.
        if (first is not { } x
            || second is not { } y
            || !placement.Frame.TryPlace(placement.Layer, x, y, out double east, out double north))
        {
            return false;
        }

        // A Z is real orthometric height in the file's own unit, never an offset (spec/format.md
        // §6.5); the shared set's is metres.
        vertex = new SiteVertex(east, north, elevation * LinearUnits.MetresPerUnit(placement.Layer.Unit));
        return true;
    }

    /// <summary>The frame a layer is placed in, and the route its coordinates take into it.</summary>
    private readonly record struct Placement(SiteFrame Frame, LayerFrame Layer);

    /// <summary>
    /// Whether two vertices are the same position in plan, at the tolerance a closing repetition
    /// survives a round trip through degrees and back at.
    /// </summary>
    private static bool SamePlanPosition(SiteVertex left, SiteVertex right)
        => Math.Abs(left.EastM - right.EastM) < 1e-6 && Math.Abs(left.NorthM - right.NorthM) < 1e-6;
}
