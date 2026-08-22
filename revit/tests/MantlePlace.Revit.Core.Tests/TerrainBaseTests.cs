using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Where the toposolid's underside goes, and which type it is built from.
/// </summary>
/// <remarks>
/// The numbers in the named cases are the real ones from the order that first surfaced the failure:
/// a 285 × 284 point grid spanning −1.594 m to 221.469 m, 33,006 of its 80,940 points below zero,
/// against a project whose only level sits at 0. Synthetic round numbers would have passed on the old
/// code too.
/// </remarks>
internal static class TerrainBaseTests
{
    private const double Tight = 1e-9;

    /// <summary>The real relief, converted to Revit's internal feet.</summary>
    private const double MinZFt = -1.594 / 0.3048;

    private const double MaxZFt = 221.469 / 0.3048;

    /// <summary>A 300 mm toposolid type and a ~3 mm host minimum, in internal feet.</summary>
    private const double ThinTypeFt = 0.984252;

    private const double MinimumLayerFt = 0.0104166;

    internal static int Run()
    {
        TestRun run = new();

        RunBasePlanner(run);
        RunTypeChoice(run);

        return run.Report("terrain base");
    }

    private static void RunBasePlanner(TestRun run)
    {
        TerrainRelief bayArea = new(MinZFt, MaxZFt, 80_940);

        run.Case("the failing case: one level at 0 and terrain below sea level takes an offset", () =>
        {
            // This is exactly what the first real import had, and exactly what it died on.
            CandidateLevel[] levels = [new(311, "Level 1", 0.0)];
            TerrainBasePlan plan = TerrainBasePlanner.Decide(levels, bayArea, ThinTypeFt, MinimumLayerFt);

            run.Equal(
                plan.Strategy == TerrainBaseStrategy.ExistingLevelWithOffset,
                true,
                "a level at 0 cannot host terrain that goes below 0 — it must be offset down");
            run.True(plan.HeightOffset < 0.0, "the offset pushes the base plane DOWN");
            run.True(
                plan.BasePlane <= bayArea.MinZ - plan.RequiredClearance + Tight,
                "the base plane clears the lowest point by at least the required clearance");
            run.Equal(plan.LevelId == 311, true, "it hangs off the level that exists");
        });

        run.Case("clearance is the larger of the type total and three host minimums", () =>
        {
            // DrapeLayering.Split refuses unless total - min >= 2 x min, i.e. total >= 3 x min. The
            // clearance reuses that number so a base that clears the terrain also leaves a type the
            // drape can still split. If these two ever disagree, this case is what says so.
            run.Within(
                TerrainBasePlanner.ClearanceFor(0.1, 1.0),
                3.0,
                Tight,
                "a thin type is floored at three host minimums");
            run.Within(
                TerrainBasePlanner.ClearanceFor(5.0, 1.0),
                5.0,
                Tight,
                "a thick type sets its own clearance");
            run.True(
                DrapeLayering.Split(TerrainBasePlanner.ClearanceFor(0.1, MinimumLayerFt), MinimumLayerFt).Ok,
                "a type as thick as the floor is one the drape can split");
        });

        run.Case("a level already below the terrain is used as-is, with no offset", () =>
        {
            CandidateLevel[] levels = [new(1, "Level 1", 0.0), new(2, "Basement", -50.0)];
            TerrainBasePlan plan = TerrainBasePlanner.Decide(levels, bayArea, ThinTypeFt, MinimumLayerFt);

            run.Equal(plan.Strategy == TerrainBaseStrategy.ExistingLevel, true, "no offset is needed");
            run.Within(plan.HeightOffset, 0.0, Tight, "and none is applied");
            run.Equal(plan.LevelId == 2, true, "the basement is the one that clears it");
        });

        run.Case("among levels that clear it, the HIGHEST wins", () =>
        {
            // The lowest would work too, and would bury the terrain in a solid nobody asked for that
            // every section through the project would then cut.
            CandidateLevel[] levels =
            [
                new(1, "Way down", -900.0),
                new(2, "Just below", -40.0),
                new(3, "Too high", 0.0),
            ];
            TerrainBasePlan plan = TerrainBasePlanner.Decide(levels, bayArea, ThinTypeFt, MinimumLayerFt);

            run.Equal(plan.LevelId == 2, true, "the highest level that still clears the terrain");
        });

        run.Case("no levels at all is stated, not guessed", () =>
        {
            TerrainBasePlan plan = TerrainBasePlanner.Decide([], bayArea, ThinTypeFt, MinimumLayerFt);

            run.Equal(plan.Strategy == TerrainBaseStrategy.NoLevelAvailable, true, "nothing to sit on");
            run.Contains(plan.Explanation, "no level", "and the log says why");
        });

        run.Case("escalation puts a dedicated level at the same plane the offset was aiming for", () =>
        {
            CandidateLevel[] levels = [new(311, "Level 1", 0.0)];
            TerrainBasePlan first = TerrainBasePlanner.Decide(levels, bayArea, ThinTypeFt, MinimumLayerFt);
            TerrainBasePlan second = TerrainBasePlanner.Escalate(first, bayArea);

            run.Equal(second.Strategy == TerrainBaseStrategy.DedicatedLevel, true, "the retry arm");
            run.Within(second.BasePlane, first.BasePlane, Tight,
                "the retry aims at the same plane — only the mechanism changes");
            run.Within(second.HeightOffset, 0.0, Tight, "a dedicated level needs no offset");
            run.Equal(second.LevelName, TerrainBasePlanner.DedicatedLevelName, "named so a re-import finds it");
        });

        run.Case("an inland site whose ground is all above the level needs no offset", () =>
        {
            // Stated so the regression is legible: this is the shape that has always worked, and it
            // must keep working without a created level or an offset.
            TerrainRelief inland = new(120.0, 400.0, 5_000);
            CandidateLevel[] levels = [new(1, "Level 1", 0.0)];
            TerrainBasePlan plan = TerrainBasePlanner.Decide(levels, inland, ThinTypeFt, MinimumLayerFt);

            run.Equal(plan.Strategy == TerrainBaseStrategy.ExistingLevel, true, "nothing special is needed");
        });

        run.Case("a level with a non-finite elevation is ignored rather than propagated", () =>
        {
            CandidateLevel[] levels = [new(1, "Broken", double.NaN), new(2, "Level 1", 0.0)];
            TerrainBasePlan plan = TerrainBasePlanner.Decide(levels, bayArea, ThinTypeFt, MinimumLayerFt);

            run.Equal(plan.LevelId == 2, true, "the usable level is chosen");
            run.True(double.IsFinite(plan.HeightOffset), "and the offset is a real number");
        });

        run.Case("relief is read off the points, empty is a count of zero and not a throw", () =>
        {
            SurfacePoint[] points = [new(0, 0, 5.0), new(1, 0, -2.5), new(0, 1, 40.0)];
            TerrainRelief relief = TerrainBasePlanner.ReliefOf(points);
            run.Within(relief.MinZ, -2.5, Tight, "min");
            run.Within(relief.MaxZ, 40.0, Tight, "max");
            run.Equal(relief.PointCount, 3, "count");

            run.Equal(TerrainBasePlanner.ReliefOf([]).PointCount, 0, "empty is empty");
        });
    }

    private static void RunTypeChoice(TestRun run)
    {
        run.Case("the thinnest type the drape can still split wins", () =>
        {
            CandidateToposolidType[] types =
            [
                new(10, "Thick", 4.0, 3),
                new(11, "Comfortable", 0.984252, 1),
                new(12, "Hair", 0.02, 1),
            ];
            CandidateToposolidType? best = ToposolidTypeChoice.Best(types, MinimumLayerFt);

            run.Equal(best is { Id: 11 }, true,
                "\"Hair\" is thinner but the drape cannot split it, so \"Comfortable\" wins");
        });

        run.Case("when nothing is splittable, the thinnest positive type is still used", () =>
        {
            // Terrain that cannot wear the photograph is better than no terrain. The drape's own
            // refusal path says so in the log when it happens.
            // Both are under 3 x the host minimum, so DrapeLayering.Split refuses both.
            CandidateToposolidType[] types = [new(12, "Hair", 0.02, 1), new(13, "Wisp", 0.03, 1)];
            CandidateToposolidType? best = ToposolidTypeChoice.Best(types, MinimumLayerFt);

            run.Equal(best is { Id: 12 }, true, "the thinnest of the unsplittable types");
        });

        run.Case("a type with no compound structure cannot be split but can still build", () =>
        {
            CandidateToposolidType[] types = [new(20, "Structureless", 1.0, 0)];
            CandidateToposolidType? best = ToposolidTypeChoice.Best(types, MinimumLayerFt);

            run.Equal(best is { Id: 20 }, true, "it is still the only type there is");
        });

        run.Case("ties break by ordinal name so two runs choose the same type", () =>
        {
            CandidateToposolidType[] forward = [new(1, "Bravo", 1.0, 1), new(2, "Alpha", 1.0, 1)];
            CandidateToposolidType[] reversed = [new(2, "Alpha", 1.0, 1), new(1, "Bravo", 1.0, 1)];

            run.Equal(
                ToposolidTypeChoice.Best(forward, MinimumLayerFt)?.Id
                    == ToposolidTypeChoice.Best(reversed, MinimumLayerFt)?.Id,
                true,
                "collector order must not change the answer");
            run.Equal(ToposolidTypeChoice.Best(forward, MinimumLayerFt) is { Name: "Alpha" }, true, "and it is Alpha");
        });

        run.Case("no types, and zero-thickness types, are null rather than a bad choice", () =>
        {
            run.Equal(ToposolidTypeChoice.Best([], MinimumLayerFt) is null, true, "nothing to choose from");
            run.Equal(
                ToposolidTypeChoice.Best([new(1, "Zero", 0.0, 1), new(2, "NaN", double.NaN, 1)], MinimumLayerFt) is null,
                true,
                "a type with no thickness is not a type this can build on");
        });
    }
}
