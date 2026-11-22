using System.Globalization;
using System.Text;
using MantlePlace.Core;

namespace MantlePlace.Core.Tests;

internal static class SurfaceTinTests
{
    private static readonly SiteFrame MetricFrame = new()
    {
        Origin = new GeoOrigin
        {
            Epsg = 32610,
            Easting = 545888.5,
            Northing = 4187221.5,
            LinearUnit = LinearUnit.Metre,
        },
    };

    internal static int Run()
    {
        TestRun run = new();

        RunReaderCases(run);
        RunFrameCases(run);
        RunFillCases(run);

        return run.Report("surface TIN");
    }

    private static void RunReaderCases(TestRun run)
    {
        run.Case("two 3DFACEs share their edge rather than duplicating its vertices", () =>
        {
            string dxf = Entities(
                Face([0, 0, 1], [10, 0, 2], [0, 10, 3]),
                Face([10, 0, 2], [10, 10, 4], [0, 10, 3]));

            run.Equal(SurfaceTinReader.TryParse(new StringReader(dxf), out SurfaceTin? tin), null, "read");
            run.Equal(tin!.Vertices.Count, 4, "four distinct vertices across two faces");
            run.Equal(tin.Triangles.Count, 2, "two triangles");
        });

        run.Case("a fourth corner repeating the third stays one triangle", () =>
        {
            string dxf = Entities(Face([0, 0, 1], [10, 0, 2], [0, 10, 3], [0, 10, 3]));

            run.Equal(SurfaceTinReader.TryParse(new StringReader(dxf), out SurfaceTin? tin), null, "read");
            run.Equal(tin!.Vertices.Count, 3, "three vertices");
            run.Equal(tin.Triangles.Count, 1, "one triangle, not two");
        });

        run.Case("a genuine quad becomes two triangles", () =>
        {
            string dxf = Entities(Face([0, 0, 1], [10, 0, 2], [10, 10, 3], [0, 10, 4]));

            run.Equal(SurfaceTinReader.TryParse(new StringReader(dxf), out SurfaceTin? tin), null, "read");
            run.Equal(tin!.Vertices.Count, 4, "four vertices");
            run.Equal(tin.Triangles.Count, 2, "the quad is split");
        });

        run.Case("a 3DFACE outside the ENTITIES section is not terrain", () =>
        {
            StringBuilder dxf = new();
            Section(dxf, "BLOCKS", Face([0, 0, 1], [10, 0, 2], [0, 10, 3]));
            Section(dxf, "ENTITIES", string.Empty);
            dxf.Append(Pair(0, "EOF"));

            string? error = SurfaceTinReader.TryParse(new StringReader(dxf.ToString()), out SurfaceTin? tin);
            run.True(tin is null, "nothing was read");
            run.Contains(error, "no 3DFACE entities", "and it says the surface is empty");
        });

        run.Case("a face missing a corner fails the read rather than leaving a hole", () =>
        {
            string dxf = Entities(
                Pair(0, "3DFACE") + Pair(10, "0.0") + Pair(20, "0.0") + Pair(30, "1.0")
                + Pair(11, "10.0") + Pair(21, "0.0") + Pair(31, "2.0"));

            string? error = SurfaceTinReader.TryParse(new StringReader(dxf), out SurfaceTin? tin);
            run.True(tin is null, "nothing was read");
            run.Contains(error, "missing one of its first three corners", "and it says which");
        });

        run.Case("a coordinate that is not a finite number fails the read", () =>
        {
            string dxf = Entities(
                Pair(0, "3DFACE") + Pair(10, "0.0") + Pair(20, "0.0") + Pair(30, "nan")
                + Pair(11, "10.0") + Pair(21, "0.0") + Pair(31, "2.0")
                + Pair(12, "0.0") + Pair(22, "10.0") + Pair(32, "3.0"));

            string? error = SurfaceTinReader.TryParse(new StringReader(dxf), out SurfaceTin? tin);
            run.True(tin is null, "nothing was read");
            run.Contains(error, "not a finite number", "and it says why");
        });

        run.Case("a group code with no value fails the read", () =>
        {
            string? error = SurfaceTinReader.TryParse(new StringReader("  0\n"), out SurfaceTin? tin);
            run.True(tin is null, "nothing was read");
            run.Contains(error, "has no value", "and it says which line");
        });

        run.Case("a face whose corners collapse contributes no triangle", () =>
        {
            string dxf = Entities(Face([5, 5, 1], [5, 5, 1], [5, 5, 1]));

            string? error = SurfaceTinReader.TryParse(new StringReader(dxf), out SurfaceTin? tin);
            run.True(tin is null, "a decimator's degenerate face is not terrain");
            run.Contains(error, "no 3DFACE entities", "and the surface reads as empty");
        });
    }

    private static void RunFrameCases(TestRun run)
    {
        run.Case("absolute projected vertices are reduced by the published origin alone", () =>
        {
            SurfaceTin tin = Tin(
                [new SurfacePoint(545177.5, 4187930.5, 134.1718)],
                []);

            IReadOnlyList<SurfacePoint>? local =
                SurfaceTinFrame.TryToLocalMetres(tin, MetricFrame, LinearUnit.Metre, out string? reason);

            run.True(reason is null, "no refusal");
            run.Within(local![0].X, -711.0, 0.001, "east of the origin");
            run.Within(local[0].Y, 709.0, 0.001, "north of the origin");

            run.Within(local[0].Z, 134.1718, 0.0001, "Z is left absolute");
        });

        run.Case("a unit that disagrees with the origin's fails closed", () =>
        {
            SurfaceTin tin = Tin([new SurfacePoint(545177.5, 4187930.5, 134.1718)], []);

            IReadOnlyList<SurfacePoint>? local =
                SurfaceTinFrame.TryToLocalMetres(tin, MetricFrame, LinearUnit.InternationalFoot, out string? reason);

            run.True(local is null, "not placed");
            run.Contains(reason, "different linear unit", "and it says why");
        });

        run.Case("a frame with no plan coordinates places nothing", () =>
        {
            SiteFrame bare = new() { Origin = new GeoOrigin { Epsg = 32610 } };
            SurfaceTin tin = Tin([new SurfacePoint(545177.5, 4187930.5, 134.1718)], []);

            IReadOnlyList<SurfacePoint>? local =
                SurfaceTinFrame.TryToLocalMetres(tin, bare, LinearUnit.Metre, out string? reason);

            run.True(local is null, "not placed");
            run.Contains(reason, "no origin", "and it says why");
        });
    }

    private static void RunFillCases(TestRun run)
    {
        run.Case("a bit-identical patch on the edge that does not join the terrain is a fill", () =>
        {
            (SurfaceTin tin, List<SurfacePoint> vertices) = Mesh(12, 20);
            Flatten(vertices, [0, 1, 2], 9.372);

            IReadOnlyList<SurfacePoint> kept = SurfaceTinSanitiser.Clean(
                tin, vertices, window: null, out SurfaceCleanReport report);

            run.Equal(report.DroppedFilledEdge, 3, "the fill patch is dropped");
            run.Equal(kept.Count, vertices.Count - 3, "and nothing else is");
            run.Contains(report.Explanation, "producer's fill value", "and it says what it removed");
        });

        run.Case("flat water on the edge is kept even with a cliff on one side of it", () =>
        {
            (SurfaceTin tin, List<SurfacePoint> vertices) = Mesh(12, 20);
            Flatten(vertices, [0, 1, 2, 3, 4, 5], -0.406);

            foreach (int neighbour in new[] { 20, 21, 22, 23, 24, 25, 6 })
            {
                vertices[neighbour] = With(vertices[neighbour], -0.30);
            }

            vertices[26] = With(vertices[26], 220.0);

            IReadOnlyList<SurfacePoint> kept = SurfaceTinSanitiser.Clean(
                tin, vertices, window: null, out SurfaceCleanReport report);

            run.Equal(report.DroppedFilledEdge, 0, "the water is not a fill");
            run.Equal(kept.Count, vertices.Count, "and every vertex survives");
        });

        run.Case("a bit-identical patch away from every edge is terrain", () =>
        {
            (SurfaceTin tin, List<SurfacePoint> vertices) = Mesh(12, 20);
            Flatten(vertices, [105, 106, 107], 9.372);

            IReadOnlyList<SurfacePoint> kept = SurfaceTinSanitiser.Clean(
                tin, vertices, window: null, out SurfaceCleanReport report);

            run.Equal(report.DroppedFilledEdge, 0, "an interior plateau is not a fill");
            run.Equal(kept.Count, vertices.Count, "and every vertex survives");
        });

        run.Case("a fill larger than the cap is reported rather than removed", () =>
        {
            (SurfaceTin tin, List<SurfacePoint> vertices) = Mesh(12, 20);
            Flatten(vertices, [.. Enumerable.Range(0, 20)], 9.372);

            IReadOnlyList<SurfacePoint> kept = SurfaceTinSanitiser.Clean(
                tin, vertices, window: null, out SurfaceCleanReport report);

            run.Equal(report.DroppedFilledEdge, 0, "nothing was removed");
            run.Equal(kept.Count, vertices.Count, "the terrain is intact");
            run.Contains(report.Explanation, "more than this plugin is willing to remove", "and it says so");
        });

        run.Case("a vertex outside the crop is counted once, against the crop", () =>
        {
            (SurfaceTin tin, List<SurfacePoint> vertices) = Mesh(12, 20);
            Flatten(vertices, [0, 1, 2], 9.372);

            SurfaceCropWindow window = new(WestM: 5.0, SouthM: -1000.0, EastM: 1000.0, NorthM: 1000.0);

            IReadOnlyList<SurfacePoint> kept = SurfaceTinSanitiser.Clean(
                tin, vertices, window, out SurfaceCleanReport report);

            run.Equal(report.DroppedFilledEdge, 0, "the fill is not double-counted");
            run.Equal(report.DroppedOutsideAoi, 20, "the whole outer column is outside the window");
            run.Equal(kept.Count, vertices.Count - 20, "and only that column is gone");
        });

        run.Case("a crop that would leave almost nothing is refused", () =>
        {
            (SurfaceTin tin, List<SurfacePoint> vertices) = Mesh(12, 20);
            SurfaceCropWindow window = new(WestM: 5000.0, SouthM: 5000.0, EastM: 6000.0, NorthM: 6000.0);

            IReadOnlyList<SurfacePoint> kept = SurfaceTinSanitiser.Clean(
                tin, vertices, window, out SurfaceCleanReport report);

            run.Equal(kept.Count, vertices.Count, "the terrain is handed back whole");
            run.Contains(report.Explanation, "report the order", "with the disagreement stated");
        });
    }

    private static (SurfaceTin Tin, List<SurfacePoint> Vertices) Mesh(int columns, int rows)
    {
        List<SurfacePoint> vertices = new(columns * rows);
        for (int column = 0; column < columns; column++)
        {
            for (int row = 0; row < rows; row++)
            {
                vertices.Add(new SurfacePoint(column * 10.0, row * 10.0, 100.0 + (vertices.Count * 0.13)));
            }
        }

        List<SurfaceTriangle> triangles = [];
        for (int column = 0; column < columns - 1; column++)
        {
            for (int row = 0; row < rows - 1; row++)
            {
                int corner = (column * rows) + row;
                triangles.Add(new SurfaceTriangle(corner, corner + 1, corner + rows));
                triangles.Add(new SurfaceTriangle(corner + 1, corner + rows + 1, corner + rows));
            }
        }

        return (Tin(vertices, triangles), vertices);
    }

    private static void Flatten(List<SurfacePoint> vertices, int[] indices, double elevation)
    {
        foreach (int index in indices)
        {
            vertices[index] = With(vertices[index], elevation);
        }
    }

    private static SurfacePoint With(SurfacePoint point, double elevation)
        => new(point.X, point.Y, elevation);

    private static SurfaceTin Tin(IReadOnlyList<SurfacePoint> vertices, IReadOnlyList<SurfaceTriangle> triangles)
        => new() { Vertices = vertices, Triangles = triangles };

    private static string Pair(int code, string value)
        => string.Format(CultureInfo.InvariantCulture, "{0,3}\n{1}\n", code, value);

    private static string Face(double[] a, double[] b, double[] c, double[]? d = null)
    {
        double[][] corners = [a, b, c, d ?? c];
        StringBuilder face = new();
        face.Append(Pair(0, "3DFACE")).Append(Pair(8, "0"));

        for (int corner = 0; corner < corners.Length; corner++)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                face.Append(Pair(
                    ((axis + 1) * 10) + corner,
                    corners[corner][axis].ToString("R", CultureInfo.InvariantCulture)));
            }
        }

        return face.ToString();
    }

    private static string Entities(params string[] faces)
    {
        StringBuilder dxf = new();
        Section(dxf, "HEADER", string.Empty);
        Section(dxf, "ENTITIES", string.Concat(faces));
        dxf.Append(Pair(0, "EOF"));
        return dxf.ToString();
    }

    private static void Section(StringBuilder dxf, string name, string body)
        => dxf.Append(Pair(0, "SECTION")).Append(Pair(2, name)).Append(body).Append(Pair(0, "ENDSEC"));
}
