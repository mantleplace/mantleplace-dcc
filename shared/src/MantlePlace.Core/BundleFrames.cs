namespace MantlePlace.Core;

public sealed record FrameCrs(string? Code, string? ProjJson, string? Wkt2);

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
