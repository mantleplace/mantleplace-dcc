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
    public int Polygon { get; init; }
}

/// <summary>
/// Parses a bundle <c>vector</c> GeoJSON layer into features placed in a <see cref="SiteFrame"/>.
/// Pure.
/// </summary>
/// <remarks>
/// <para>
/// RFC 7946 fixes the coordinate reference system at WGS84 lon/lat, which is what the bundle's
/// layers ship (<c>"crs": … CRS84</c>), so every position goes through <see cref="SiteFrame"/>'s
/// one permitted forward projection (<c>HPS-45</c>). Nothing here decides WHICH layer is read — the
/// path comes from a manifest pointer and the planner picks it (<c>HPS-32</c>).
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

    /// <summary>
    /// Parses a layer.
    /// </summary>
    /// <param name="geoJsonText">The layer file's text.</param>
    /// <param name="frame">The frame to place vertices in.</param>
    /// <param name="accept">Which geometries to take.</param>
    /// <param name="label">What to call the layer in a failure message, in the user's words.</param>
    /// <param name="features">The parsed features; empty on failure.</param>
    /// <returns><c>null</c> on success, or a user-facing reason the layer could not be read.</returns>
    public static string? TryParse(
        string geoJsonText,
        SiteFrame frame,
        SiteGeometryKinds accept,
        string label,
        out IReadOnlyList<SiteFeature> features)
    {
        ArgumentNullException.ThrowIfNull(frame);

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

                AppendFeature(feature, frame, accept, parsed, ref polygons);
            }
        }

        return null;
    }

    private static void AppendFeature(
        JsonElement feature,
        SiteFrame frame,
        SiteGeometryKinds accept,
        List<SiteFeature> parsed,
        ref int polygons)
    {
        JsonElement? properties = feature.Object("properties");
        FeatureProperties carried = new(
            properties?.Str("name") ?? string.Empty,
            properties?.Str("class") ?? string.Empty,
            properties?.OptionalDouble("width_m_estimated"),
            properties?.Str("subtype") ?? string.Empty);

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
                AppendPath(coordinates, frame, closed: false, isHole: false, polygon: 0, carried, parsed);
                break;

            case "MultiLineString":
                foreach (JsonElement path in coordinates.EnumerateArray())
                {
                    if (path.ValueKind == JsonValueKind.Array)
                    {
                        AppendPath(path, frame, closed: false, isHole: false, polygon: 0, carried, parsed);
                    }
                }

                break;

            case "Polygon":
                AppendRings(coordinates, frame, carried, parsed, ++polygons);
                break;

            case "MultiPolygon":
                foreach (JsonElement polygon in coordinates.EnumerateArray())
                {
                    if (polygon.ValueKind == JsonValueKind.Array)
                    {
                        AppendRings(polygon, frame, carried, parsed, ++polygons);
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
        SiteFrame frame,
        FeatureProperties carried,
        List<SiteFeature> parsed,
        int ordinal)
    {
        int ring = 0;
        foreach (JsonElement path in polygon.EnumerateArray())
        {
            if (path.ValueKind == JsonValueKind.Array)
            {
                AppendPath(path, frame, closed: true, isHole: ring > 0, ordinal, carried, parsed);
            }

            ring++;
        }
    }

    private static void AppendPath(
        JsonElement path,
        SiteFrame frame,
        bool closed,
        bool isHole,
        int polygon,
        FeatureProperties carried,
        List<SiteFeature> parsed)
    {
        List<SiteVertex> vertices = [];
        foreach (JsonElement position in path.EnumerateArray())
        {
            if (TryReadPosition(position, frame, out SiteVertex vertex))
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
            IsHole = isHole,
            Polygon = polygon,
        });
    }

    /// <summary>The properties one GeoJSON feature hands to every line or ring it yields.</summary>
    private readonly record struct FeatureProperties(
        string Name,
        string Classification,
        double? WidthM,
        string Subtype);

    private static bool TryReadPosition(JsonElement position, SiteFrame frame, out SiteVertex vertex)
    {
        vertex = default;

        if (position.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        double? lon = null;
        double? lat = null;
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
                    lon = number;
                    break;
                case 1:
                    lat = number;
                    break;
                case 2:
                    elevation = number;
                    break;
                default:
                    break;
            }

            ordinal++;
        }

        if (lon is not { } lonDeg
            || lat is not { } latDeg
            || !frame.TryProjectToLocalMetres(lonDeg, latDeg, out double east, out double north))
        {
            return false;
        }

        vertex = new SiteVertex(east, north, elevation);
        return true;
    }

    /// <summary>
    /// Whether two vertices are the same position in plan, at the tolerance a closing repetition
    /// survives a round trip through degrees and back at.
    /// </summary>
    private static bool SamePlanPosition(SiteVertex left, SiteVertex right)
        => Math.Abs(left.EastM - right.EastM) < 1e-6 && Math.Abs(left.NorthM - right.NorthM) < 1e-6;
}
