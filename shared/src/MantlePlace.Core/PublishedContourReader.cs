using System.Globalization;

namespace MantlePlace.Core;

public readonly record struct ContourVertex(double X, double Y);

public sealed class PublishedContour
{
    public required string ElevationText { get; init; }

    public required double Elevation { get; init; }

    public required bool IsClosed { get; init; }

    public required IReadOnlyList<ContourVertex> Vertices { get; init; }

    public string Layer { get; init; } = "0";
}

public static class PublishedContourReader
{
    private const int ClosedFlag = 1;

    private static readonly HashSet<string> SkippedEntityKinds = new(StringComparer.Ordinal) { "TEXT", "MTEXT" };

    public static string? TryParse(TextReader dxf, out IReadOnlyList<PublishedContour>? contours)
        => TryParse(dxf, out contours, out _);

    public static string? TryParse(TextReader dxf, out IReadOnlyList<PublishedContour>? contours, out int skippedLabels)
        => TryParse(dxf, "contours", out contours, out skippedLabels);

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
