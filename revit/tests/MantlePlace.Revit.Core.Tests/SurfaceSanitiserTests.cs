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

        run.Case("the crop takes the AOI's BOUNDING box, not the rectangle inscribed in it", () =>
        {
            // ⛔ The regression, with the real numbers. The first version took the inner extremes of
            // the four projected corners, reasoning that a window strictly inside the published AOI
            // was conservative. Convergence tilts the quadrilateral by about 8 m over this AOI, so
            // that rectangle cut two extra rows off the north and south edges of good terrain: it
            // dropped 1,980 of 80,940 points (2.45%) to remove 400 bad ones. The bounding box drops
            // 852 and still removes every one of them, because the defect lives in the raster's
            // overhang and the overhang is outside either rectangle.
            //
            // Simulated here on the real lattice: 285 x 284 at 5 m, spanning -712..708 by -706..709.
            const double west = -703.76, east = 703.81, south = -708.81, north = 708.60;

            List<SurfacePoint> points = [];
            for (int ix = 0; ix < 285; ix++)
            {
                for (int iy = 0; iy < 284; iy++)
                {
                    points.Add(new SurfacePoint(-712.0 + (ix * 5.0), -706.0 + (iy * 5.0), 100.0 + ix + iy));
                }
            }

            SurfacePointsSanitiser.Clean(points, new SurfaceCropWindow(west, south, east, north), out SurfaceCleanReport report);

            // 3 columns of 284 (x = -712, -707, 708) + 1 row of 285 (y = 709), less the 3 they share.
            // The row at y = 709 is only 0.4 m outside the AOI; it goes because the tolerance stays
            // tight on purpose. Widening it to keep that row would have to reach 3.3 m, which would
            // re-admit the x = -707 column — and that column carries 116 of the 400 filled points.
            run.Equal(report.DroppedOutsideAoi, 1134, "one outer ring, no more");
            run.True(
                report.DroppedOutsideAoi < 80_940 * 2 / 100,
                "and comfortably under the 2.45% the inscribed rectangle cost");
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

        RunCropUnavailableCases(run);

        return run.Report("surface sanitiser");
    }

    /// <summary>
    /// Why there is no crop window, said in the log rather than "no area of interest this plugin can
    /// project" — which fired on both 2026-10-03 imperial reference bundles (EPSG:6616 and
    /// EPSG:6445, ftUS) and read as a defect. It is not one: the manifest publishes the AOI only as
    /// lon/lat, and projecting that into a State Plane frame is CRS machinery a host never carries.
    /// </summary>
    private static void RunCropUnavailableCases(TestRun run)
    {
        run.Case("an ftUS State Plane bundle has no crop window, and the reason names its frame and why", () =>
        {
            BundleManifest manifest = Parse(ImperialManifest(withBbox: true));
            SiteFrame? frame = SiteFrame.For(manifest);

            run.True(frame is not null, "the State Plane origin is still a frame");
            run.True(SurfaceCrop.For(manifest, frame) is null, "no window");

            string? reason = SurfaceCrop.Unavailable(manifest, frame);
            run.Contains(reason, "longitude and latitude", "the AOI is published only as lon/lat");
            run.Contains(reason, "EPSG:6616", "names the frame");
            run.Contains(reason, "ftUS", "and its unit");
            run.Contains(reason, "UTM", "says which frames this plugin projects into");
        });

        run.Case("a metric UTM bundle has a window and no reason", () =>
        {
            BundleManifest manifest = Parse(MetricManifest());
            SiteFrame? frame = SiteFrame.For(manifest);

            run.True(SurfaceCrop.For(manifest, frame) is { IsUsable: true }, "a window");
            run.Equal(SurfaceCrop.Unavailable(manifest, frame), null, "and so no reason");
        });

        run.Case("a bundle with no bbox says it publishes no area of interest", () =>
        {
            BundleManifest manifest = Parse(ImperialManifest(withBbox: false));

            run.Contains(SurfaceCrop.Unavailable(manifest, SiteFrame.For(manifest)), "publishes no area of interest", "the plain reason");
        });

        run.Case("the planner carries the reason on the terrain step, and the sanitiser logs it", () =>
        {
            BundleManifest manifest = Parse(ImperialManifest(withBbox: true));
            BundleImportPlan plan = BundleImportPlanner.Plan(manifest, ["Metadata/manifest.json", "Surface/SurfacePoints.csv"], _ => new ImageSize(1, 1));
            ImportStep? step = plan.Steps.FirstOrDefault(candidate => candidate.Kind == ImportStepKind.ToposurfaceFromPointsFile);

            run.True(step is { Crop: null }, "the terrain step, with no window");
            run.Equal(step?.CropUnavailable, SurfaceCrop.Unavailable(manifest, SiteFrame.For(manifest)), "and the reason");

            SurfacePointsSanitiser.Clean(Grid(columns: 30, rows: 20, filledColumns: 0), null, out SurfaceCleanReport report, step?.CropUnavailable);
            run.Contains(report.Explanation, "EPSG:6616", "the log line says why");
            run.Contains(report.Explanation, "every point", "and what was built instead");
        });
    }

    private static BundleManifest Parse(string json)
    {
        BundleManifest manifest = BundleManifestReader.Parse(json);
        if (!manifest.IsValid)
        {
            throw new InvalidOperationException($"fixture refused: {manifest.Error}");
        }

        return manifest;
    }

    /// <summary>The Jackson reference bundle's frame: State Plane EPSG:6616 in US survey feet.</summary>
    private static string ImperialManifest(bool withBbox)
        => Manifest(
            withBbox ? "\"bbox\": { \"west\": -110.769253195258, \"south\": 43.4681979807655, \"east\": -110.751746804742, \"north\": 43.4809020192345 }," : string.Empty,
            "EPSG:6616",
            "{ \"epsg\": 6616, \"easting\": 2444921.346921498, \"northing\": 1412709.3759964514, \"linear_unit\": \"ftUS\" }",
            "-110.76049814296273",
            "43.474551661372026",
            "ftUS");

    private static string MetricManifest()
        => Manifest(
            "\"bbox\": { \"west\": -105.33, \"south\": 38.455, \"east\": -105.32, \"north\": 38.465 },",
            "EPSG:32613",
            "{ \"epsg\": 32613, \"easting\": 471595.0, \"northing\": 4257050.0, \"linear_unit\": \"m\" }",
            "-105.32557885004304",
            "38.46130517000308",
            "m");

    private static string Manifest(string bbox, string crs, string projected, string lon, string lat, string unit)
        => $$"""
            {
              "version": "1.4.0",
              {{bbox}}
              "layout": { "points_csv": "Surface/SurfacePoints.csv" },
              "hosts": { "revit": {
                "georeference": { "crs_projected": "{{crs}}", "origin": { "lon": {{lon}}, "lat": {{lat}}, "projected": {{projected}} } },
                "readiness": { "toposurface_points": { "present": true } },
                "toposurface_points": { "path": "Surface/SurfacePoints.csv", "format": "csv", "horizontal_frame": "local_enu",
                  "units": "{{unit}}", "sha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc" }
              } }
            }
            """;

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
