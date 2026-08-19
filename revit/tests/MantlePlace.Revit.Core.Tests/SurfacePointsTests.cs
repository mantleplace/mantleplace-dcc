namespace MantlePlace.Revit.Core.Tests;

/// <summary>The points-file reader and the unit table it feeds.</summary>
internal static class SurfacePointsTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("a headerless X,Y,Z file parses verbatim", () =>
        {
            string? error = SurfacePointsReader.TryParse(
                "-712.500,-700.500,2577.826\n0.000,0.000,2640.955\n712.500,700.500,2962.586\n",
                out IReadOnlyList<SurfacePoint> points);

            run.True(error is null, $"parsed ({error})");
            run.Equal(points.Count, 3, "point count");
            run.Within(points[1].Z, 2640.955, 1e-9, "elevation read verbatim");
            run.Within(points[2].X, 712.5, 1e-9, "easting offset read verbatim");
        });

        run.Case("blank lines and a trailing newline are tolerated", () =>
        {
            string? error = SurfacePointsReader.TryParse(
                "1,2,3\n\n4,5,6\n\n7,8,9\n",
                out IReadOnlyList<SurfacePoint> points);
            run.True(error is null, $"parsed ({error})");
            run.Equal(points.Count, 3, "point count");
        });

        run.Case("fewer than three points is a read failure, not a caller's problem", () =>
        {
            // Toposolid.Create throws ArgumentException below three points. That threshold used to
            // live in the Revit shim, where the headless suite could not reach it and where it fired
            // AFTER the planner had already reported CanImport. It is a property of the file, so it
            // belongs to the reader (HPS-02).
            string? one = SurfacePointsReader.TryParse("1,2,3\n", out IReadOnlyList<SurfacePoint> onePoint);
            run.True(one is not null, "one point rejected");
            run.Contains(one, "at least 3", "says how many are needed");
            run.Contains(one, "1 point", "says how many there were");
            run.Equal(onePoint.Count, 1, "the parsed points are still handed back for diagnosis");

            string? two = SurfacePointsReader.TryParse("1,2,3\n4,5,6\n", out _);
            run.True(two is not null, "two points rejected");

            run.True(
                SurfacePointsReader.TryParse("1,2,3\n4,5,6\n7,8,9\n", out _) is null,
                "three points is the boundary and it is inclusive");
        });

        run.Case("a short row fails the read rather than leaving a hole in the terrain", () =>
        {
            string? error = SurfacePointsReader.TryParse("1,2,3\n4,5\n", out _);
            run.True(error is not null, "rejected");
            run.Contains(error, "line 2", "names the offending line");
        });

        run.Case("a non-numeric row fails the read", () =>
        {
            string? error = SurfacePointsReader.TryParse("1,2,3\n4,five,6\n", out _);
            run.True(error is not null, "rejected");
            run.Contains(error, "not three numbers", "says why");
        });

        run.Case("an empty file is a named failure, not zero points", () =>
        {
            run.True(SurfacePointsReader.TryParse(string.Empty, out _) is not null, "empty text rejected");
            run.True(SurfacePointsReader.TryParse("\n\n", out _) is not null, "whitespace-only rejected");
        });

        run.Case("both foot definitions are exact", () =>
        {
            run.Within(LinearUnits.MetresPerUnit(LinearUnit.Metre), 1.0, 0.0, "metre");
            run.Within(LinearUnits.MetresPerUnit(LinearUnit.InternationalFoot), 0.3048, 0.0, "international foot");
            run.Within(
                LinearUnits.MetresPerUnit(LinearUnit.UsSurveyFoot),
                1200.0 / 3937.0,
                0.0,
                "US survey foot");
            run.True(
                LinearUnits.MetresPerUnit(LinearUnit.UsSurveyFoot) != 0.3048,
                "the two foot definitions are not conflated");
            run.Within(
                LinearUnits.MetresPerUnit(LinearUnit.Unspecified),
                1.0,
                0.0,
                "an unstated unit is metric, as every pre-delivery-block bundle was");
        });

        return run.Report("surface points");
    }
}
