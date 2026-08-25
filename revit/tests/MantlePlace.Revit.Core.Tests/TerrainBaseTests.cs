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
        // The real metric template, as the first probe reported it. Layer functions, not names, are
        // what separate ground from paving — and what exclude this plugin's own derived type.
        //
        // The fourth argument is layer 0's OWN width, which is what the drape splits. For the
        // single-layer types it equals the total; for the multi-layer ones it does not, and that gap
        // is the whole reason the field exists.
        CandidateToposolidType Generic = new(1570125, "Generic - 1000mm", 3.2808, 3.2808, 1, true);
        CandidateToposolidType Grassland = new(1570127, "Grassland - 1200mm", 3.937, 0.4921, 3, true);
        CandidateToposolidType Water = new(1570129, "Water - 2000mm", 6.5617, 0.9843, 2, true);
        CandidateToposolidType WoodPath = new(1570131, "Path - 150mm Wood Planks", 0.4921, 0.4921, 1, false);
        CandidateToposolidType ConcretePath = new(1570133, "Path - 350mm Concrete", 1.1483, 0.164, 2, false);

        // ⛔ This fixture used to claim 150 mm and no Structure layer. Both were fictions, and they
        // were the fictions that made the exclusion below look easy. What TryLayerImagery actually
        // derives PRESERVES the source type's total (the imagery sliver is subtracted from the layer
        // beneath it) and COPIES layer 0's function — so a type derived from "Generic - 1000mm" is
        // 1,000 mm of Structure, not 150 mm of Finish1. Layer 0 is the sliver: exactly the host
        // minimum.
        CandidateToposolidType OurDrapeType =
            new(1669969, "Mantle Place Site Imagery f93bc782", 3.2808, MinimumLayerFt, 2, true);

        run.Case("the real template: a paving type never wins on thinness alone", () =>
        {
            // ⛔ The regression. Thickness-first put "Path - 150mm Wood Planks" under the terrain on
            // the first live run — it is 150 mm against Generic's 1 m, and it splits fine for the
            // drape. Nothing about it is ground.
            CandidateToposolidType[] types = [Generic, Grassland, Water, WoodPath, ConcretePath];
            CandidateToposolidType? best = ToposolidTypeChoice.Best(types, MinimumLayerFt);

            run.Equal(best is { Id: 1570125 }, true,
                $"expected \"Generic - 1000mm\", got \"{best?.Name}\"");
        });

        run.Case("this plugin's own drape type is excluded on a re-import", () =>
        {
            // The second-order trap: a re-import must not build the terrain on the type the PREVIOUS
            // import derived. What actually excludes it is layer 0 — the derived type's top layer is
            // the imagery sliver, exactly the host minimum, and the drape cannot split a sliver
            // again. Splittability outranks thinness, so Generic wins on the merits.
            //
            // ⛔ Predicting splittability from the TOTAL width could not see this: the derived type
            // preserves the source's total, so on that number it looks exactly as splittable as the
            // type it came from, and the exclusion fell through to the ordinal name tie-break —
            // which a locale or a rename would have broken silently.
            CandidateToposolidType[] types = [Generic, Grassland, OurDrapeType];
            CandidateToposolidType? best = ToposolidTypeChoice.Best(types, MinimumLayerFt);

            run.Equal(best is { Id: 1570125 }, true, $"expected Generic, got \"{best?.Name}\"");
        });

        run.Case("a fat multi-layer type whose top layer is a sliver loses to a thinner splittable one", () =>
        {
            // The defect the TopLayerThickness field exists to prevent: judged on its 2 m total this
            // type looks like the most splittable thing in the project, and the drape would then
            // refuse it — rolling the WHOLE drape back — because layer 0 has nothing to give.
            CandidateToposolidType fat = new(31, "Fat - thin skin", 6.5617, MinimumLayerFt, 3, true);
            CandidateToposolidType? best = ToposolidTypeChoice.Best([fat, Generic], MinimumLayerFt);

            run.Equal(best is { Id: 1570125 }, true,
                $"expected Generic, got \"{best?.Name}\" — splittability must read layer 0, not the total");
        });

        run.Case("among ground types the thinnest wins", () =>
        {
            CandidateToposolidType[] types = [Water, Grassland, Generic];
            run.Equal(ToposolidTypeChoice.Best(types, MinimumLayerFt) is { Id: 1570125 }, true,
                "1 m beats 1.2 m beats 2 m");
        });

        run.Case("splittability outranks thinness within the same class", () =>
        {
            CandidateToposolidType hair = new(12, "Hair", 0.02, 0.02, 1, true);
            CandidateToposolidType comfortable = new(11, "Comfortable", 0.984252, 0.984252, 1, true);
            CandidateToposolidType? best = ToposolidTypeChoice.Best([hair, comfortable], MinimumLayerFt);

            run.Equal(best is { Id: 11 }, true,
                "\"Hair\" is thinner but the drape cannot split it");
        });

        run.Case("a project with nothing but paving still gets terrain", () =>
        {
            // Every preference is a tie-break, not a filter. Refusing to build ground because the
            // photograph would not fit is the wrong trade.
            CandidateToposolidType[] types = [WoodPath, ConcretePath];
            run.Equal(ToposolidTypeChoice.Best(types, MinimumLayerFt) is { Id: 1570131 }, true,
                "the thinnest of them, rather than nothing");
        });

        run.Case("a type with no compound structure can still build", () =>
        {
            CandidateToposolidType[] types = [new(20, "Structureless", 1.0, 1.0, 0, false)];
            run.Equal(ToposolidTypeChoice.Best(types, MinimumLayerFt) is { Id: 20 }, true,
                "it is still the only type there is");
        });

        run.Case("ties break by ordinal name so collector order cannot change the answer", () =>
        {
            CandidateToposolidType alpha = new(2, "Alpha", 1.0, 1.0, 1, true);
            CandidateToposolidType bravo = new(1, "Bravo", 1.0, 1.0, 1, true);

            run.Equal(
                ToposolidTypeChoice.Best([bravo, alpha], MinimumLayerFt)?.Id
                    == ToposolidTypeChoice.Best([alpha, bravo], MinimumLayerFt)?.Id,
                true,
                "same answer either way round");
            run.Equal(ToposolidTypeChoice.Best([bravo, alpha], MinimumLayerFt) is { Name: "Alpha" }, true, "and it is Alpha");
        });

        run.Case("no types, and zero-thickness types, are null rather than a bad choice", () =>
        {
            run.Equal(ToposolidTypeChoice.Best([], MinimumLayerFt) is null, true, "nothing to choose from");
            run.Equal(
                ToposolidTypeChoice.Best(
                    [new(1, "Zero", 0.0, 0.0, 1, true), new(2, "NaN", double.NaN, double.NaN, 1, true)],
                    MinimumLayerFt) is null,
                true,
                "a type with no thickness is not a type this can build on");
        });
    }
}
