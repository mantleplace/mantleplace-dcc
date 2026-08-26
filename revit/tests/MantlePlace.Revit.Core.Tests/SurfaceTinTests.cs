using System.Globalization;
using System.Text;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The TIN topo path: reading <c>3DFACE</c>s, reducing them to the site's own frame, and telling a
/// producer's nodata fill from genuinely flat ground.
/// </summary>
/// <remarks>
/// The numbers pinned in the frame case are measured off a real bundle
/// (<c>Surface/Surface.dxf</c> of order <c>f93bc782</c> — Mantle Place's own first-party test site,
/// not a customer's, which is what licenses committing it — EPSG:32610), not invented: its first face's
/// first corner is 545177.5 E / 4187930.5 N against a published origin of 545888.5 / 4187221.5, and
/// the points file's first row for the same ground is −712.0 / 709.0. Two independent artifacts
/// agreeing to the metre is what says the subtraction is the right one.
/// </remarks>
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
            // DXF has no triangle primitive, so this is how every TIN emitter writes one. Reading it
            // as a quad would add a degenerate face and a phantom vertex.
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
            // ⛔ Geometry in BLOCKS is a definition placed by an INSERT with its own transform.
            // Reading its corners as world coordinates scatters the site.
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

            // ⛔ Z is an ABSOLUTE orthometric height on every artifact, so nothing is subtracted
            // from it. Reducing it against the origin would put the terrain underground.
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
            // ⛔ The Bay case, and the reason the discriminator is a MEDIAN. Genuinely flat ground at
            // the edge of a site routinely has a headland somewhere along it; a rule keyed on the
            // largest neighbouring drop would condemn the water along with the fill. Measured on the
            // real bundle the two separate by two orders of magnitude — 152.3 m against 0.000 m.
            (SurfaceTin tin, List<SurfacePoint> vertices) = Mesh(12, 20);
            Flatten(vertices, [0, 1, 2, 3, 4, 5], -0.406);

            // Everything the patch touches is shoreline, except one vertex that is a cliff.
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
            // A reservoir, a car park, a flat roof. The defect this guards is a raster sliver
            // overhanging the AOI, so it reaches an edge by construction.
            (SurfaceTin tin, List<SurfacePoint> vertices) = Mesh(12, 20);
            Flatten(vertices, [105, 106, 107], 9.372);

            IReadOnlyList<SurfacePoint> kept = SurfaceTinSanitiser.Clean(
                tin, vertices, window: null, out SurfaceCleanReport report);

            run.Equal(report.DroppedFilledEdge, 0, "an interior plateau is not a fill");
            run.Equal(kept.Count, vertices.Count, "and every vertex survives");
        });

        run.Case("a fill larger than the cap is reported rather than removed", () =>
        {
            // ⛔ A guard that can eat the terrain is worse than the artefact it removes, so above
            // SurfaceGrid.MaxDroppedFraction it drops nothing and says so.
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

            // The window starts east of column 0, so the fill patch is outside it as well as on it.
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

    /// <summary>
    /// A rectangular mesh of <paramref name="columns"/> × <paramref name="rows"/> vertices, 10 m
    /// apart, each cell split into two triangles.
    /// </summary>
    /// <remarks>
    /// Every Z is distinct, so any bit-identical group in a case is one the case put there. Column 0
    /// is the western edge, which is where the real defect lives.
    /// </remarks>
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
