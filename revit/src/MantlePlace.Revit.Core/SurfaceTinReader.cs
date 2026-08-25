using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>One triangle of a TIN, as indices into <see cref="SurfaceTin.Vertices"/>.</summary>
public readonly record struct SurfaceTriangle(int A, int B, int C);

/// <summary>
/// A triangulated irregular network: unique vertices, plus the faces that join them.
/// </summary>
/// <remarks>
/// ⛔ <b>The faces are not for Revit.</b> <c>Toposolid.Create</c> takes an <c>IList&lt;XYZ&gt;</c>
/// and nothing else — there is nowhere to hand it a triangulation, so Revit re-triangulates whatever
/// points it is given. What the TIN actually buys is <em>where the vertices are</em>: adaptive,
/// dense on slopes and sparse on flats, and therefore in general position. The faces are carried
/// because <see cref="SurfaceTinSanitiser"/> needs adjacency to tell a producer's fill from
/// genuinely flat ground — the points-file path had no adjacency and had to infer it from grid
/// topology instead.
/// </remarks>
public sealed class SurfaceTin
{
    public required IReadOnlyList<SurfacePoint> Vertices { get; init; }

    public required IReadOnlyList<SurfaceTriangle> Triangles { get; init; }
}

/// <summary>
/// Parses the <c>3DFACE</c> entities of <c>Surface/Surface.dxf</c> into a TIN. Pure.
/// </summary>
/// <remarks>
/// <para>
/// The file is ASCII DXF: alternating lines of group code and value. Only <c>3DFACE</c> entities in
/// the <c>ENTITIES</c> section are read; a face's corners are codes <c>10/20/30</c>,
/// <c>11/21/31</c>, <c>12/22/32</c> and <c>13/23/33</c>. DXF has no triangle primitive, so a
/// triangle is written as a face whose fourth corner repeats the third.
/// </para>
/// <para>
/// ⛔ <b>The vertices come out in the file's own frame, which the manifest declares as
/// <c>absolute_projected</c> — they are NOT usable as they stand.</b> They are eastings and
/// northings around 500 000 m, which is where Revit's precision warnings start.
/// <see cref="SurfaceTinFrame"/> converts them, by subtracting the origin the manifest publishes
/// and doing nothing else.
/// </para>
/// <para>
/// Reading is kept apart from cleaning for the reason it is in <see cref="SurfacePointsReader"/>:
/// parsing answers "can this file be read", cleaning answers "should these points be built", and
/// conflating them would make the reader's fail-closed contract negotiable.
/// </para>
/// </remarks>
public static class SurfaceTinReader
{
    /// <summary>Corners per DXF face — three for a triangle, four for a quad.</summary>
    private const int MaxCorners = 4;

    /// <summary>Slots covering the first three corners, which every face must carry.</summary>
    private const int TriangleSlots = 9;

    /// <summary>
    /// Parses the whole file, streaming. A malformed face fails the read rather than being dropped:
    /// a silently-skipped face is a hole in the terrain.
    /// </summary>
    /// <returns><c>null</c> on success, or a user-facing reason the file could not be read.</returns>
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
                // An entity boundary is what closes a face. Faces are flushed here rather than on a
                // corner count, because DXF never says how many corners are coming.
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
                // ⛔ ENTITIES only. A 3DFACE inside BLOCKS is a block DEFINITION — geometry placed
                // by an INSERT carrying its own transform — so reading its corners as world
                // coordinates would scatter the terrain across the site.
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

    /// <summary>Maps a DXF corner group code onto its slot, refusing one that is not a corner.</summary>
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

        // The fourth corner is what makes a face a quad rather than a triangle. A TIN emitter writes
        // it equal to the third; interning first means that repeat is one integer comparison and
        // costs no vertex.
        if (present[9] && present[10] && present[11])
        {
            int d = Intern(Corner(corners, 3), seenVertices, vertices);
            Add(triangles, a, c, d);
        }

        return null;
    }

    private static SurfacePoint Corner(ReadOnlySpan<double> corners, int corner)
        => new(corners[corner * 3], corners[(corner * 3) + 1], corners[(corner * 3) + 2]);

    /// <summary>The index of a vertex, adding it if this is the first face to use it.</summary>
    /// <remarks>
    /// Exact equality, deliberately. A TIN's shared corners are written from one value each time, so
    /// they round-trip identically, and a tolerance here would weld genuinely distinct vertices
    /// together on a dense slope. It is also what lets <see cref="SurfaceTinSanitiser"/> reason
    /// about bit-identity afterwards.
    /// </remarks>
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

    /// <summary>Records a face, dropping the degenerate ones a decimator leaves behind.</summary>
    private static void Add(List<SurfaceTriangle> triangles, int a, int b, int c)
    {
        if (a != b && b != c && a != c)
        {
            triangles.Add(new SurfaceTriangle(a, b, c));
        }
    }
}
