namespace MantlePlace.Core;

/// <summary>
/// A CRS as a 3.x manifest names it: whole, as a code, a PROJJSON object or a WKT2 string
/// (<c>spec/format.md</c> §9.4). No reader takes a compound code apart, so none is taken apart here:
/// each form the manifest states is kept as stated, and one the manifest does not state is
/// <c>null</c>.
/// </summary>
/// <param name="Code">The authority code as stated, such as <c>EPSG:32618</c>, or <c>null</c>.</param>
/// <param name="ProjJson">The PROJJSON object as its JSON text, or <c>null</c>.</param>
/// <param name="Wkt2">The WKT2 string as stated, or <c>null</c>.</param>
public sealed record FrameCrs(string? Code, string? ProjJson, string? Wkt2);

/// <summary>
/// A linear unit as the manifest states it: its name and its factor to metres, so no reader maps
/// "foot" to the wrong foot.
/// </summary>
/// <param name="Name">The unit's name as stated, for display; never matched on.</param>
/// <param name="ToMetres">The factor to metres as stated, and the value that settles the unit.</param>
/// <param name="Unit">The unit the factor is: the only ones this reader understands.</param>
public sealed record FrameUnit(string Name, double ToMetres, LinearUnit Unit);

/// <summary>The order of the horizontal axes of the georeferenced frame (<c>spec/format.md</c> §9.4).</summary>
public enum AxisOrder
{
    /// <summary>Easting, then northing.</summary>
    EastNorth,

    /// <summary>Northing, then easting.</summary>
    NorthEast,
}

/// <summary>
/// The georeferenced frame: what every georeferenced file is in (<c>spec/format.md</c> §9.4). Every
/// value is the manifest's, applied verbatim by a host and derived by none.
/// </summary>
/// <param name="HorizontalCrs">The order's projected CRS.</param>
/// <param name="VerticalCrs">The order's vertical CRS.</param>
/// <param name="GeoidModel">The geoid the vertical CRS uses, or <c>null</c> when the manifest names none.</param>
/// <param name="HorizontalUnit">The horizontal linear unit.</param>
/// <param name="VerticalUnit">The vertical linear unit.</param>
/// <param name="AxisOrder">The order of the horizontal axes.</param>
/// <param name="GridRotationDegrees">The angle between the CRS's grid north and true north at the site origin, in degrees.</param>
public sealed record GeoreferencedFrame(
    FrameCrs HorizontalCrs,
    FrameCrs VerticalCrs,
    string? GeoidModel,
    FrameUnit HorizontalUnit,
    FrameUnit VerticalUnit,
    AxisOrder AxisOrder,
    double GridRotationDegrees);

/// <summary>The site origin in the projected CRS whose grid the local frame's axes follow.</summary>
/// <param name="Crs">That projected CRS.</param>
/// <param name="X">The origin's easting or first axis, in <paramref name="Unit"/>.</param>
/// <param name="Y">The origin's northing or second axis, in <paramref name="Unit"/>.</param>
/// <param name="Unit">The unit of <paramref name="X"/> and <paramref name="Y"/>.</param>
public sealed record ProjectedOrigin(FrameCrs Crs, double X, double Y, FrameUnit Unit);

/// <summary>The site origin of the local frame, stated in both its geographic and its projected form.</summary>
/// <param name="Longitude">Longitude in degrees.</param>
/// <param name="Latitude">Latitude in degrees.</param>
/// <param name="EllipsoidalHeightMetres">Height above the ellipsoid, in metres.</param>
/// <param name="HeightMetres">Height in the local frame's vertical datum, in metres.</param>
/// <param name="Projected">The same point in the projected CRS.</param>
public sealed record SiteOrigin(
    double Longitude,
    double Latitude,
    double EllipsoidalHeightMetres,
    double HeightMetres,
    ProjectedOrigin Projected);

/// <summary>
/// The local site frame: what every host-ready file is in, offsets from the site origin with x east,
/// y north and z up (<c>spec/format.md</c> §9.4). Its unit is read, and refused when it is not the
/// metre, which it is on every order.
/// </summary>
/// <param name="Origin">The site origin.</param>
/// <param name="VerticalCrs">The vertical datum its heights are in.</param>
/// <param name="GeoidModel">The geoid that datum uses, or <c>null</c> when the manifest names none.</param>
/// <param name="Unit">The frame's unit.</param>
/// <param name="GridRotationDegrees">The angle between grid north and true north at the origin, in degrees.</param>
public sealed record LocalSiteFrame(
    SiteOrigin Origin,
    FrameCrs VerticalCrs,
    string? GeoidModel,
    FrameUnit Unit,
    double GridRotationDegrees);

/// <summary>
/// The two frames a 3.x manifest states once (<c>spec/format.md</c> §9.4), read from the manifest and
/// carried on <see cref="BundleContents.Frames"/> for a host to apply verbatim. No value is derived.
/// </summary>
/// <param name="Georeferenced">The frame of every georeferenced file.</param>
/// <param name="Local">The frame of every host-ready file.</param>
public sealed record BundleFrames(GeoreferencedFrame Georeferenced, LocalSiteFrame Local);
