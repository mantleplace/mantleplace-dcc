using System.Globalization;

namespace MantlePlace.Core;

public readonly record struct SurfaceTriangle(int A, int B, int C);

public sealed class SurfaceTin
{
    public required IReadOnlyList<SurfacePoint> Vertices { get; init; }

    public required IReadOnlyList<SurfaceTriangle> Triangles { get; init; }
}

public static class SurfaceTinReader
{
    private const int MaxCorners = 4;

    private const int TriangleSlots = 9;

    public static string? TryParse(TextReader dxf, out SurfaceTin? tin)
    {
        ArgumentNullException.ThrowIfNull(dxf);
        tin = null;

        Dictionary<SurfacePoint, int> seenVertices = [];
        List<SurfacePoint> vertices = [];
        List<SurfaceTriangle> triangles = [];

        Span<double> corners = stackalloc double[MaxCorners * 3];
        Span<bool> present = stackalloc bool[MaxCorners * 3];

        bool inEntities = false;
        bool expectSectionName = false;
        bool inFace = false;
        long lineNumber = 0;

        while (dxf.ReadLine() is { } codeLine)
        {
            lineNumber++;
            long pairStart = lineNumber;

            if (dxf.ReadLine() is not { } valueLine)
            {
                return $"The surface DXF is malformed: the group code on line {pairStart} has no value.";
            }

            lineNumber++;

            if (!int.TryParse(
                    codeLine.AsSpan().Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out int code))
            {
                return $"The surface DXF is malformed: line {pairStart} is not a group code.";
            }

            string value = valueLine.Trim();

            if (code == 0)
            {
                if (inFace && Flush(corners, present, seenVertices, vertices, triangles) is { } faceError)
                {
                    return faceError;
                }

                inFace = false;
                expectSectionName = false;

                switch (value)
                {
                    case "SECTION":
                        expectSectionName = true;
                        break;
                    case "ENDSEC":
                        inEntities = false;
                        break;
                    case "3DFACE" when inEntities:
                        inFace = true;
                        present.Clear();
                        break;
                    default:
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

            if (!inFace || !TrySlot(code, out int slot))
            {
                continue;
            }

            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ordinate)
                || !double.IsFinite(ordinate))
            {
                return $"The surface DXF is malformed: line {lineNumber} is not a finite number.";
            }

            corners[slot] = ordinate;
            present[slot] = true;
        }

        if (inFace && Flush(corners, present, seenVertices, vertices, triangles) is { } trailingError)
        {
            return trailingError;
        }

        if (triangles.Count == 0)
        {
            return "The surface DXF carries no 3DFACE entities, so there is no terrain in it to build.";
        }

        if (vertices.Count < SurfacePointsReader.MinimumPoints)
        {
            return $"The surface DXF has only {vertices.Count} distinct "
                + (vertices.Count == 1 ? "vertex" : "vertices")
                + $"; Revit needs at least {SurfacePointsReader.MinimumPoints} to build a surface.";
        }

        tin = new SurfaceTin { Vertices = vertices, Triangles = triangles };
        return null;
    }

    private static bool TrySlot(int code, out int slot)
    {
        slot = -1;
        int axis = code / 10;
        int corner = code % 10;

        if (axis is < 1 or > 3 || corner >= MaxCorners)
        {
            return false;
        }

        slot = (corner * 3) + (axis - 1);
        return true;
    }

    private static string? Flush(
        ReadOnlySpan<double> corners,
        ReadOnlySpan<bool> present,
        Dictionary<SurfacePoint, int> seenVertices,
        List<SurfacePoint> vertices,
        List<SurfaceTriangle> triangles)
    {
        for (int slot = 0; slot < TriangleSlots; slot++)
        {
            if (!present[slot])
            {
                return "The surface DXF is malformed: a 3DFACE is missing one of its first three corners.";
            }
        }

        int a = Intern(Corner(corners, 0), seenVertices, vertices);
        int b = Intern(Corner(corners, 1), seenVertices, vertices);
        int c = Intern(Corner(corners, 2), seenVertices, vertices);

        Add(triangles, a, b, c);

        if (present[9] && present[10] && present[11])
        {
            int d = Intern(Corner(corners, 3), seenVertices, vertices);
            Add(triangles, a, c, d);
        }

        return null;
    }

    private static SurfacePoint Corner(ReadOnlySpan<double> corners, int corner)
        => new(corners[corner * 3], corners[(corner * 3) + 1], corners[(corner * 3) + 2]);

    private static int Intern(
        SurfacePoint point,
        Dictionary<SurfacePoint, int> seenVertices,
        List<SurfacePoint> vertices)
    {
        if (seenVertices.TryGetValue(point, out int existing))
        {
            return existing;
        }

        int index = vertices.Count;
        vertices.Add(point);
        seenVertices.Add(point, index);
        return index;
    }

    private static void Add(List<SurfaceTriangle> triangles, int a, int b, int c)
    {
        if (a != b && b != c && a != c)
        {
            triangles.Add(new SurfaceTriangle(a, b, c));
        }
    }
}
