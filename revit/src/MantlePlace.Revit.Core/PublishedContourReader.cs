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

    /// <summary>The layer the file puts the polyline on (group 8), verbatim; <c>0</c>, DXF's default, when it names none.</summary>
    public string Layer { get; init; } = "0";
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
/// entity is a contour missing from the site with nothing on screen to say so. A paper-space entity
/// (group 67 = 1, a plan sheet's viewport or frame) is not the drawing's and is skipped either way.
/// </para>
/// <para>
/// The same reader takes any drawing of flat polylines the AutoCAD block points at (the public-record
/// layers and the site linework, format.md §6.10): the drawing's name names the file in what the
/// user is told, and each polyline keeps the layer the file puts it on (group 8).
/// </para>
/// <para>
/// The one exception is a label: <c>TEXT</c> and <c>MTEXT</c> are skipped and counted, never
/// refused. They are annotation, not geometry — dropping one loses no contour — and the complete
/// bundles of 2026-10-03 carry one beside every few polylines, so refusing them refused every
/// contour in the file. Drawing the labels is a separate question. Every other kind still refuses.
/// </para>
/// </remarks>
public static class PublishedContourReader
{
    private const int ClosedFlag = 1;

    /// <summary>
    /// The entity kinds skipped rather than refused: labels, which carry no contour geometry.
    /// </summary>
    private static readonly HashSet<string> SkippedEntityKinds = new(StringComparer.Ordinal) { "TEXT", "MTEXT" };

    /// <summary>Parses the whole file, streaming.</summary>
    /// <returns><c>null</c> on success, or a user-facing reason the file could not be read.</returns>
    public static string? TryParse(TextReader dxf, out IReadOnlyList<PublishedContour>? contours)
        => TryParse(dxf, out contours, out _);

    /// <summary>Parses the whole file, streaming, and counts the labels it skipped.</summary>
    /// <param name="skippedLabels">How many <c>TEXT</c> and <c>MTEXT</c> labels were passed over.</param>
    /// <returns><c>null</c> on success, or a user-facing reason the file could not be read.</returns>
    public static string? TryParse(TextReader dxf, out IReadOnlyList<PublishedContour>? contours, out int skippedLabels)
        => TryParse(dxf, "contours", out contours, out skippedLabels);

    /// <summary>Parses any drawing of flat polylines, streaming, naming it <paramref name="drawing"/> in a refusal.</summary>
    /// <param name="dxf">The file.</param>
    /// <param name="drawing">What the user calls the file (<c>contours</c>, <c>parcels</c>), as in "the parcels DXF".</param>
    /// <param name="contours">The polylines, in file order.</param>
    /// <param name="skippedLabels">How many <c>TEXT</c> and <c>MTEXT</c> labels were passed over.</param>
    /// <returns><c>null</c> on success, or a user-facing reason the file could not be read.</returns>
    public static string? TryParse(TextReader dxf, string drawing, out IReadOnlyList<PublishedContour>? contours, out int skippedLabels)
    {
        ArgumentNullException.ThrowIfNull(dxf);
        ArgumentNullException.ThrowIfNull(drawing);
        string name = $"The {drawing} DXF";
        contours = null;
        skippedLabels = 0;

        List<PublishedContour> read = [];
        Polyline? current = null;
        string? unreadable = null;
        bool paperSpace = false;
        bool inEntities = false;
        bool expectSectionName = false;
        long lineNumber = 0;

        while (dxf.ReadLine() is { } codeLine)
        {
            lineNumber++;
            long pairStart = lineNumber;

            if (dxf.ReadLine() is not { } valueLine)
            {
                return $"{name} is malformed: the group code on line {pairStart} has no value.";
            }

            lineNumber++;

            if (!int.TryParse(codeLine.AsSpan().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
            {
                return $"{name} is malformed: line {pairStart} is not a group code.";
            }

            string value = valueLine.Trim();

            if (code == 0)
            {
                // An entity is judged at its end, once its group 67 has been seen: a paper-space one is a
                // sheet's (a viewport, a title frame), not the drawing's, and is neither placed nor refused.
                if (current is not null)
                {
                    if (!paperSpace)
                    {
                        if (current.Finish(out PublishedContour? contour) is { } polylineError)
                        {
                            return polylineError;
                        }

                        read.Add(contour!);
                    }

                    current = null;
                }

                if (unreadable is not null && !paperSpace)
                {
                    return Unreadable(name, unreadable);
                }

                unreadable = null;
                paperSpace = false;
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
                        current = new Polyline(name);
                        break;
                    case var label when inEntities && SkippedEntityKinds.Contains(label):
                        // Its own group codes, the 10/20 insertion point included, fall through
                        // below with no polyline open, so none of them can land on a contour.
                        skippedLabels++;
                        break;
                    default:
                        if (inEntities)
                        {
                            unreadable = value;
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

            if (code == 67 && (current is not null || unreadable is not null))
            {
                paperSpace = string.Equals(value, "1", StringComparison.Ordinal);
                continue;
            }

            if (current is not null && current.Take(code, value, lineNumber) is { } valueError)
            {
                return valueError;
            }
        }

        if (unreadable is not null && !paperSpace)
        {
            return Unreadable(name, unreadable);
        }

        if (current is not null && !paperSpace)
        {
            if (current.Finish(out PublishedContour? trailing) is { } trailingError)
            {
                return trailingError;
            }

            read.Add(trailing!);
        }

        if (read.Count == 0)
        {
            return $"{name} carries no LWPOLYLINE polylines, so there are none in it to draw.";
        }

        contours = read;
        return null;
    }

    private static string Unreadable(string name, string kind)
        => $"{name} carries a {kind} entity, which this plugin does not read: "
            + "only LWPOLYLINE polylines are placed, and dropping it would leave a gap.";

    /// <summary>One <c>LWPOLYLINE</c> while its group codes are being read.</summary>
    private sealed class Polyline(string name)
    {
        private readonly List<ContourVertex> _vertices = [];
        private string _layer = "0";
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
                case 8:
                    _layer = value;
                    return null;

                case 10:
                    if (_pendingX is not null)
                    {
                        return $"{name} is malformed: an LWPOLYLINE vertex has an X and no Y.";
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
                        return $"{name} is malformed: an LWPOLYLINE vertex has a Y and no X.";
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
                        : $"{name} has an LWPOLYLINE with an arc segment (a bulge), which this plugin "
                            + "does not draw: a polyline is placed as straight segments only.";

                case 70:
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _flags))
                    {
                        return $"{name} is malformed: line {lineNumber} is not an integer.";
                    }

                    return null;

                case 90:
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
                    {
                        return $"{name} is malformed: line {lineNumber} is not an integer.";
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
                return $"{name} is malformed: an LWPOLYLINE vertex has an X and no Y.";
            }

            if (_declaredCount is { } declared && declared != _vertices.Count)
            {
                return $"{name} is malformed: an LWPOLYLINE declares {declared} vertices and carries "
                    + $"{_vertices.Count}.";
            }

            // Exact, not a tolerance: an emitter writes the default extrusion as 0/0/1, and anything
            // else genuinely rotates the vertices out of world X/Y.
            if (_extrusionX != 0.0 || _extrusionY != 0.0 || _extrusionZ != 1.0)
            {
                return $"{name} has an LWPOLYLINE whose extrusion is not +Z, so its vertices are "
                    + "not world coordinates and this plugin cannot place it.";
            }

            contour = new PublishedContour
            {
                ElevationText = _elevationText,
                Elevation = _elevation,
                IsClosed = (_flags & ClosedFlag) != 0,
                Vertices = _vertices,
                Layer = _layer.Length == 0 ? "0" : _layer,
            };
            return null;
        }

        private string? Number(string value, long lineNumber, out double number)
            => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number)
                ? null
                : $"{name} is malformed: line {lineNumber} is not a finite number.";
    }
}
