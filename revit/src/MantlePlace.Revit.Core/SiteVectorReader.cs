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

            foreach (JsonElement feature in collection.EnumerateArray())
            {
                if (feature.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                AppendFeature(feature, frame, accept, parsed);
            }
        }

        return null;
    }

    private static void AppendFeature(
        JsonElement feature,
        SiteFrame frame,
        SiteGeometryKinds accept,
        List<SiteFeature> parsed)
    {
        JsonElement? properties = feature.Object("properties");
        string name = properties?.Str("name") ?? string.Empty;
        string classification = properties?.Str("class") ?? string.Empty;
        double? width = properties?.OptionalDouble("width_m_estimated");

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
        // separately, and which is which is the caller's geometry question, not this reader's.
        switch (type)
        {
            case "LineString":
                AppendPath(coordinates, frame, closed: false, name, classification, width, parsed);
                break;

            case "MultiLineString" or "Polygon":
                foreach (JsonElement path in coordinates.EnumerateArray())
                {
                    if (path.ValueKind == JsonValueKind.Array)
                    {
                        AppendPath(path, frame, closed, name, classification, width, parsed);
                    }
                }

                break;

            case "MultiPolygon":
                foreach (JsonElement polygon in coordinates.EnumerateArray())
                {
                    if (polygon.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (JsonElement ring in polygon.EnumerateArray())
                    {
                        if (ring.ValueKind == JsonValueKind.Array)
                        {
                            AppendPath(ring, frame, closed: true, name, classification, width, parsed);
                        }
                    }
                }

                break;

            default:
                // Points, GeometryCollections and anything Overture adds later are not linework.
                break;
        }
    }

    private static void AppendPath(
        JsonElement path,
        SiteFrame frame,
        bool closed,
        string name,
        string classification,
        double? width,
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
            Name = name,
            Classification = classification,
            WidthM = width,
        });
    }

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
