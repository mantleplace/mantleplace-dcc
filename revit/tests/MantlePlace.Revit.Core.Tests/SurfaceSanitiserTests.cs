using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The two guards that keep a producer's nodata fill out of the model, and the limits on both.
/// </summary>
/// <remarks>
/// The fixture is the real shape of the defect: a regular 5 m grid with the two westernmost columns
/// pinned to one identical elevation while the ground beside them climbs past 200 m. What matters
/// most here are the NEGATIVE cases — a genuine cliff must survive, and neither guard may ever eat
/// the site.
/// </remarks>
internal static class SurfaceSanitiserTests
{
    private const double Spacing = 5.0;

    private const double FillZ = 9.372;

    internal static int Run()
    {
        TestRun run = new();

        run.Case("the real defect: two filled edge columns are dropped, nothing else is", () =>
        {
            // 150 columns keeps two filled ones at 1.3% of the surface — the real bundle's two
            // columns are 400 of 80,940 points, 0.49%, comfortably under the cap.
            IReadOnlyList<SurfacePoint> points = Grid(columns: 150, rows: 20, filledColumns: 2);
            IReadOnlyList<SurfacePoint> cleaned = SurfacePointsSanitiser.Clean(points, null, out SurfaceCleanReport report);

            run.Equal(report.DroppedFilledEdge, 2 * 20, "both filled columns go, every point of them");
            run.Equal(cleaned.Count, points.Count - (2 * 20), "and nothing else does");
            run.Contains(report.Explanation, "fill value", "the log names what was removed and why");
        });

        run.Case("a genuine cliff column with distinct values is KEPT", () =>
        {
            // This is the case a magnitude threshold would fail. The column is 200 m from its
            // neighbour, which on this site is real ground — what makes a fill a fill is that every
            // point in the line carries the same bits.
            List<SurfacePoint> points = [.. Grid(columns: 30, rows: 20, filledColumns: 0)];
            double edgeX = 0.0;
            for (int i = 0; i < points.Count; i++)
            {
                if (points[i].X == edgeX)
                {
                    points[i] = points[i] with { Z = 205.0 + (points[i].Y * 0.01) };
                }
            }

            SurfacePointsSanitiser.Clean(points, null, out SurfaceCleanReport report);
            run.Equal(report.DroppedFilledEdge, 0, "a steep but varying edge is terrain, not fill");
        });

        run.Case("a flat edge that JOINS its neighbour is kept", () =>
        {
            // A lake, a runway or the Bay. Identical values are not enough on their own; the line
            // also has to be somewhere its neighbour is not.
            List<SurfacePoint> points = [];
            for (int cx = 0; cx < 30; cx++)
            {
                for (int ry = 0; ry < 20; ry++)
                {
                    double z = cx < 2 ? 3.25 : 3.25 + ((cx - 2) * 0.05);
                    points.Add(new SurfacePoint(cx * Spacing, ry * Spacing, z));
                }
            }

            SurfacePointsSanitiser.Clean(points, null, out SurfaceCleanReport report);
            run.Equal(report.DroppedFilledEdge, 0, "flat ground that meets the terrain beside it stays");
        });

        run.Case("the guard refuses rather than removing more than 2% of the surface", () =>
        {
            // Three filled columns out of a hundred is 3%. A guard that can eat the terrain is worse
            // than the artefact, so above the cap it drops nothing and says so.
            IReadOnlyList<SurfacePoint> points = Grid(columns: 100, rows: 20, filledColumns: 3);
            IReadOnlyList<SurfacePoint> cleaned = SurfacePointsSanitiser.Clean(points, null, out SurfaceCleanReport report);

            run.Equal(report.DroppedFilledEdge, 0, "nothing is removed above the cap");
            run.Equal(cleaned.Count, points.Count, "the surface comes back whole");
            run.Contains(report.Explanation, "report the order", "and the curator is told to escalate it");
        });

        run.Case("the crop drops points outside the window and keeps the boundary itself", () =>
        {
            IReadOnlyList<SurfacePoint> points = Grid(columns: 30, rows: 20, filledColumns: 0);
            // Exclude the westernmost column only; the window's west edge sits ON the second column.
            SurfaceCropWindow window = new(WestM: Spacing, SouthM: -1.0, EastM: 1000.0, NorthM: 1000.0);
            IReadOnlyList<SurfacePoint> cleaned = SurfacePointsSanitiser.Clean(points, window, out SurfaceCleanReport report);

            run.Equal(report.DroppedOutsideAoi, 20, "exactly the one column outside");
            run.Equal(cleaned.Count, points.Count - 20, "and the column exactly on the edge is inside");
        });

        run.Case("with no crop window the terrain is still built, and the log says the crop was unavailable", () =>
        {
            IReadOnlyList<SurfacePoint> points = Grid(columns: 30, rows: 20, filledColumns: 0);
            IReadOnlyList<SurfacePoint> cleaned = SurfacePointsSanitiser.Clean(points, null, out SurfaceCleanReport report);

            run.Equal(cleaned.Count, points.Count, "a missing bbox is a degradation, never a refusal");
            run.Contains(report.Explanation, "area of interest", "and it is stated rather than silent");
        });

        run.Case("a crop that would leave nothing is refused and the points come back whole", () =>
        {
            IReadOnlyList<SurfacePoint> points = Grid(columns: 30, rows: 20, filledColumns: 0);
            SurfaceCropWindow absurd = new(WestM: 9_000.0, SouthM: 9_000.0, EastM: 9_100.0, NorthM: 9_100.0);
            IReadOnlyList<SurfacePoint> cleaned = SurfacePointsSanitiser.Clean(points, absurd, out SurfaceCleanReport report);

            run.Equal(cleaned.Count, points.Count, "a terrain of three points is not the honest answer");
            run.Contains(report.Explanation, "report the order", "the disagreement is escalated");
        });

        run.Case("an irregular point set is left alone by the grid guard", () =>
        {
            SurfacePoint[] scattered =
            [
                new(0.0, 0.0, 1.0), new(3.1, 0.4, 2.0), new(7.9, 1.1, 3.0),
                new(11.2, 4.7, 4.0), new(0.5, 9.3, 5.0), new(6.6, 12.0, 6.0),
                new(2.2, 15.5, 7.0), new(9.1, 18.2, 8.0), new(4.4, 21.0, 9.0),
            ];

            SurfacePointsSanitiser.Clean(scattered, null, out SurfaceCleanReport report);
            run.Equal(report.DroppedFilledEdge, 0, "line-by-line reasoning needs a grid to reason about");
        });

        run.Case("the grid detector reads back the real lattice", () =>
        {
            SurfaceGridShape? shape = SurfaceGrid.Detect(Grid(columns: 30, rows: 20, filledColumns: 0));
            run.Equal(shape is { ColumnCount: 30, RowCount: 20 }, true, "columns and rows");
            run.Within(shape?.Spacing ?? 0.0, Spacing, 1e-9, "spacing");
        });

        run.Case("an unusable crop window is treated as no window at all", () =>
        {
            SurfaceCropWindow inverted = new(WestM: 10.0, SouthM: 10.0, EastM: 0.0, NorthM: 0.0);
            run.False(inverted.IsUsable, "east west of west is not a rectangle");

            IReadOnlyList<SurfacePoint> points = Grid(columns: 30, rows: 20, filledColumns: 0);
            IReadOnlyList<SurfacePoint> cleaned = SurfacePointsSanitiser.Clean(points, inverted, out _);
            run.Equal(cleaned.Count, points.Count, "and it crops nothing rather than everything");
        });

        return run.Report("surface sanitiser");
    }

    /// <summary>
    /// A regular grid whose ground climbs steeply west-to-east, with the westernmost
    /// <paramref name="filledColumns"/> pinned to one identical elevation — the shape of the defect.
    /// </summary>
    private static IReadOnlyList<SurfacePoint> Grid(int columns, int rows, int filledColumns)
    {
        List<SurfacePoint> points = new(columns * rows);
        for (int cx = 0; cx < columns; cx++)
        {
            for (int ry = 0; ry < rows; ry++)
            {
                double z = cx < filledColumns
                    ? FillZ
                    : 120.0 + ((cx - filledColumns) * 3.0) + (ry * 0.25);
                points.Add(new SurfacePoint(cx * Spacing, ry * Spacing, z));
            }
        }

        return points;
    }
}
