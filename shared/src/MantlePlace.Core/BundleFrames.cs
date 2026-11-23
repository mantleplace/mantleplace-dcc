using System.Text.Json;

namespace MantlePlace.Core;

public sealed record FrameCrs(string? Code, string? ProjJson, string? Wkt2)
{
    public int? Epsg => GeoProjection.TryParseEpsg(Code) ?? EpsgOfProjJson(ProjJson);

    private static int? EpsgOfProjJson(string? projJson)
    {
        if (string.IsNullOrWhiteSpace(projJson))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(projJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("id", out JsonElement id)
                && id.ValueKind == JsonValueKind.Object
                && id.TryGetProperty("authority", out JsonElement authority)
                && string.Equals(authority.GetString(), "EPSG", StringComparison.OrdinalIgnoreCase)
                && id.TryGetProperty("code", out JsonElement code)
                && code.ValueKind == JsonValueKind.Number
                && code.TryGetInt32(out int epsg)
                    ? epsg
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record FrameUnit(string Name, double ToMetres, LinearUnit Unit);

public enum AxisOrder
{
    EastNorth,

    NorthEast,
}

public sealed record GeoreferencedFrame(
    FrameCrs HorizontalCrs,
    FrameCrs VerticalCrs,
    string? GeoidModel,
    FrameUnit HorizontalUnit,
    FrameUnit VerticalUnit,
    AxisOrder AxisOrder,
    double GridRotationDegrees);

public sealed record ProjectedOrigin(FrameCrs Crs, double X, double Y, FrameUnit Unit);

public sealed record SiteOrigin(
    double Longitude,
    double Latitude,
    double EllipsoidalHeightMetres,
    double HeightMetres,
    ProjectedOrigin Projected);

public sealed record LocalSiteFrame(
    SiteOrigin Origin,
    FrameCrs VerticalCrs,
    string? GeoidModel,
    FrameUnit Unit,
    double GridRotationDegrees);

public sealed record BundleFrames(GeoreferencedFrame Georeferenced, LocalSiteFrame Local);
