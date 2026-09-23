using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>One plan vertex of a published contour, in the file's own coordinates.</summary>
public readonly record struct ContourVertex(double X, double Y);

/// <summary>One published contour: a flat polyline at one elevation, exactly as the file states it.</summary>
public sealed class PublishedContour
{
    /// <summary>The elevation as the file wrote it, so an element name can carry it unreformatted.</summary>
    public required string ElevationText { get; init; }

    /// <summary>The elevation, in the file's vertical unit.</summary>
    public required double Elevation { get; init; }

    /// <summary>Whether the polyline closes on its first vertex (group 70, flag 1).</summary>
    public required bool IsClosed { get; init; }

    public required IReadOnlyList<ContourVertex> Vertices { get; init; }
}

/// <summary>
/// Parses the <c>LWPOLYLINE</c> entities of a bundle's contours DXF into published contours. Pure.
/// </summary>
/// <remarks>
/// <para>
/// The file is ASCII DXF, read the way <see cref="SurfaceTinReader"/> reads the surface: alternating
/// lines of group code and value, <c>ENTITIES</c> only. A contour is one <c>LWPOLYLINE</c>: its
/// elevation in group 38 (zero when absent, as the DXF reference defaults it), its vertex count in
/// 90, its closed flag in bit 1 of 70, and each vertex as a 10/20 pair.
/// </para>
/// <para>
/// ⛔ <b>Units are not read from the file.</b> <c>$INSUNITS</c> states one unit for the whole drawing,
/// and on one delivery tier the X/Y and the Z of this file are in two, so the header is wrong about
/// one of them. The manifest's pointer states each unit separately and is the authority; the header
/// is not consulted at all.
/// </para>
/// <para>
/// Anything in <c>ENTITIES</c> this reader cannot place exactly refuses the file by name rather than
/// being dropped: another entity type, a bulge (an arc, not a segment), or an extrusion other than
/// +Z (the vertices would then be in an object coordinate system, not world X/Y). A silently dropped
/// entity is a contour missing from the site with nothing on screen to say so.
/// </para>
/// </remarks>
public static class PublishedContourReader
{
    private const int ClosedFlag = 1;

    /// <summary>Parses the whole file, streaming.</summary>
    /// <returns><c>null</c> on success, or a user-facing reason the file could not be read.</returns>
    public static string? TryParse(TextReader dxf, out IReadOnlyList<PublishedContour>? contours)
    {
        ArgumentNullException.ThrowIfNull(dxf);
        contours = null;

        List<PublishedContour> read = [];
        Polyline? current = null;
        bool inEntities = false;
        bool expectSectionName = false;
        long lineNumber = 0;

        while (dxf.ReadLine() is { } codeLine)
        {
            lineNumber++;
            long pairStart = lineNumber;

            if (dxf.ReadLine() is not { } valueLine)
            {
                return $"The contours DXF is malformed: the group code on line {pairStart} has no value.";
            }

            lineNumber++;

            if (!int.TryParse(codeLine.AsSpan().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
            {
                return $"The contours DXF is malformed: line {pairStart} is not a group code.";
            }

            string value = valueLine.Trim();

            if (code == 0)
            {
                if (current is not null)
                {
                    if (current.Finish(out PublishedContour? contour) is { } polylineError)
                    {
                        return polylineError;
                    }

                    read.Add(contour!);
                    current = null;
                }

                expectSectionName = false;

                switch (value)
                {
                    case "SECTION":
                        expectSectionName = true;
                        break;
                    case "ENDSEC":
                        inEntities = false;
                        break;
                    case "LWPOLYLINE" when inEntities:
                        current = new Polyline();
                        break;
                    default:
                        if (inEntities)
                        {
                            return $"The contours DXF carries a {value} entity, which this plugin does not read: "
                                + "only LWPOLYLINE contours are placed, and dropping it would leave a gap.";
                        }

                        break;
                }

                continue;
            }

            if (expectSectionName && code == 2)
            {
                // ⛔ ENTITIES only. A polyline inside BLOCKS is a block DEFINITION, placed by an
                // INSERT carrying its own transform.
                inEntities = string.Equals(value, "ENTITIES", StringComparison.Ordinal);
                expectSectionName = false;
                continue;
            }

            if (current is not null && current.Take(code, value, lineNumber) is { } valueError)
            {
                return valueError;
            }
        }

        if (current is not null)
        {
            if (current.Finish(out PublishedContour? trailing) is { } trailingError)
            {
                return trailingError;
            }

            read.Add(trailing!);
        }

        if (read.Count == 0)
        {
            return "The contours DXF carries no LWPOLYLINE contours, so there are none in it to draw.";
        }

        contours = read;
        return null;
    }

    /// <summary>One <c>LWPOLYLINE</c> while its group codes are being read.</summary>
    private sealed class Polyline
    {
        private readonly List<ContourVertex> _vertices = [];
        private double? _pendingX;
        private int? _declaredCount;
        private int _flags;
        private string _elevationText = "0";
        private double _elevation;
        private double _extrusionX;
        private double _extrusionY;
        private double _extrusionZ = 1.0;

        /// <returns><c>null</c>, or why the value refuses the file.</returns>
        internal string? Take(int code, string value, long lineNumber)
        {
            switch (code)
            {
                case 10:
                    if (_pendingX is not null)
                    {
                        return "The contours DXF is malformed: an LWPOLYLINE vertex has an X and no Y.";
                    }

                    if (Number(value, lineNumber, out double x) is { } xError)
                    {
                        return xError;
                    }

                    _pendingX = x;
                    return null;

                case 20:
                    if (_pendingX is not { } pendingX)
                    {
                        return "The contours DXF is malformed: an LWPOLYLINE vertex has a Y and no X.";
                    }

                    if (Number(value, lineNumber, out double y) is { } yError)
                    {
                        return yError;
                    }

                    _vertices.Add(new ContourVertex(pendingX, y));
                    _pendingX = null;
                    return null;

                case 38:
                    _elevationText = value;
                    return Number(value, lineNumber, out _elevation);

                case 42:
                    if (Number(value, lineNumber, out double bulge) is { } bulgeError)
                    {
                        return bulgeError;
                    }

                    return bulge == 0.0
                        ? null
                        : "The contours DXF has an LWPOLYLINE with an arc segment (a bulge), which this plugin "
                            + "does not draw: a contour is placed as straight segments only.";

                case 70:
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _flags))
                    {
                        return $"The contours DXF is malformed: line {lineNumber} is not an integer.";
                    }

                    return null;

                case 90:
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
                    {
                        return $"The contours DXF is malformed: line {lineNumber} is not an integer.";
                    }

                    _declaredCount = count;
                    return null;

                case 210:
                    return Number(value, lineNumber, out _extrusionX);
                case 220:
                    return Number(value, lineNumber, out _extrusionY);
                case 230:
                    return Number(value, lineNumber, out _extrusionZ);

                default:
                    return null;
            }
        }

        internal string? Finish(out PublishedContour? contour)
        {
            contour = null;

            if (_pendingX is not null)
            {
                return "The contours DXF is malformed: an LWPOLYLINE vertex has an X and no Y.";
            }

            if (_declaredCount is { } declared && declared != _vertices.Count)
            {
                return $"The contours DXF is malformed: an LWPOLYLINE declares {declared} vertices and carries "
                    + $"{_vertices.Count}.";
            }

            // Exact, not a tolerance: an emitter writes the default extrusion as 0/0/1, and anything
            // else genuinely rotates the vertices out of world X/Y.
            if (_extrusionX != 0.0 || _extrusionY != 0.0 || _extrusionZ != 1.0)
            {
                return "The contours DXF has an LWPOLYLINE whose extrusion is not +Z, so its vertices are "
                    + "not world coordinates and this plugin cannot place it.";
            }

            contour = new PublishedContour
            {
                ElevationText = _elevationText,
                Elevation = _elevation,
                IsClosed = (_flags & ClosedFlag) != 0,
                Vertices = _vertices,
            };
            return null;
        }

        private static string? Number(string value, long lineNumber, out double number)
            => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number)
                ? null
                : $"The contours DXF is malformed: line {lineNumber} is not a finite number.";
    }
}
