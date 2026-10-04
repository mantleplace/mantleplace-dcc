using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Fidelity levels (MPB 1.9.0): each entry's <c>levels</c> block read into its four levels, the level
/// a curator chooses applied verbatim — the first rows, the features a field keeps, or the file a
/// pointer names — and what each level is estimated to cost in this Revit.
/// </summary>
/// <remarks>
/// The plugin never computes a subset, re-ranks or filters spatially: every count asserted here is
/// one the fixture publishes, and the cases that matter most are the ones where a level cannot be
/// imported as published and is offered as unavailable rather than imported as the whole file.
/// </remarks>
internal static class FidelityLevelTests
{
    private const string OwnSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string LevelSha = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private const string DrapeSha = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private const string TreeSha = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string Stem = "order-7f3a";

    private static readonly ImageSize DrapePixels = new(4000, 3000);

    internal static int Run()
    {
        TestRun run = new();

        RunReaderCases(run);
        RunUnknownValueCases(run);
        RunCutCases(run);
        RunPlanCases(run);
        RunTreeCases(run);
        RunEstimateCases(run);
        RunChecklistCases(run);

        return run.Report("fidelity levels");
    }

    private static void RunReaderCases(TestRun run)
    {
        run.Case("a pointer level is a file of its own, with its own hash and frame", () =>
        {
            BundleManifest manifest = Parse(Fixture());
            FidelityLevels? levels = manifest.RevitDrape?.Levels;
            run.True(levels is not null, "the drape publishes levels");

            PublishedLevel med = levels![FidelityLevel.Med];
            run.Equal(med.Kind, FidelityLevelKind.Pointer, "MED is a pointer");
            run.Equal(med.File?.Path, "Imagery/Drape.StatePlane.MED.png", "to its own file");
            run.Equal(med.File?.Sha256, LevelSha, "verified by its own hash");
            run.Equal(med.File?.Units, "ftUS", "stating its own unit");
            run.Equal(med.File?.HorizontalFrame, "EPSG:2231", "and its own frame");
            run.Within(med.Extent?.Left ?? 0, 1449131.2, 1e-6, "with its own extent");
        });

        run.Case("a row cut keeps its row count and its cost driver", () =>
        {
            FidelityLevels levels = Parse(Fixture()).TreePoints!.Levels!;
            PublishedLevel med = levels[FidelityLevel.Med];
            run.Equal(med.Kind, FidelityLevelKind.RowCut, "MED is a row cut");
            run.Equal(med.Rows ?? -1, 2, "of the first two rows");
            run.Equal(med.Cost?.Unit ?? CostUnit.Unknown, CostUnit.Elements, "costed in elements");
            run.Equal((int)(med.Cost?.Count ?? -1), 2, "two of them");

            PublishedLevel max = levels[FidelityLevel.Max];
            run.Equal(max.Kind, FidelityLevelKind.Max, "MAX is the entry itself");
            run.Equal((int)(max.Cost?.Count ?? -1), 3, "with its own count");
        });

        run.Case("a field cut names the field each feature's lowest level is in", () =>
        {
            FidelityLevels levels = Parse(Fixture()).RoadPolygons!.Levels!;
            PublishedLevel min = levels[FidelityLevel.Min];
            run.Equal(min.Kind, FidelityLevelKind.FieldCut, "MIN is a field cut");
            run.Equal(min.LowestLevelField, "lowest_level", "naming the field");
            run.Equal(min.Cost?.Unit ?? CostUnit.Unknown, CostUnit.Cuts, "costed in cuts");
            run.Equal((int)(min.Cost?.Count ?? -1), 1, "one of them");
        });

        run.Case("a same_as level names its target and its reason, for every reason", () =>
        {
            (string Token, SameAsReason Reason)[] reasons =
            [
                ("max_only", SameAsReason.MaxOnly),
                ("not_derived", SameAsReason.NotDerived),
                ("cap_not_reached", SameAsReason.CapNotReached),
                ("no_reduction", SameAsReason.NoReduction),
                ("smallest_valid_size", SameAsReason.SmallestValidSize),
            ];

            foreach ((string token, SameAsReason reason) in reasons)
            {
                FidelityLevels? levels = SplineLevels($$"""
                    "RAW": { "same_as": "MAX", "reason": "{{token}}" }, "MAX": {},
                    "MED": { "same_as": "MAX", "reason": "{{token}}" }, "MIN": { "same_as": "MAX", "reason": "{{token}}" }
                    """);
                PublishedLevel? med = levels?[FidelityLevel.Med];
                run.Equal(med?.Kind ?? FidelityLevelKind.Unavailable, FidelityLevelKind.SameAs, $"{token}: MED is a same_as");
                run.True(med?.SameAs == FidelityLevel.Max, $"{token}: naming MAX");
                run.True(med?.Reason == reason, $"{token}: with its reason");
                run.True(levels?.Resolve(FidelityLevel.Min).Level == FidelityLevel.Max, $"{token}: MIN imports MAX");
            }
        });

        run.Case("an entry with no levels block is MAX only", () =>
        {
            BundleManifest manifest = Parse(Fixture());
            run.True(manifest.Water!.Levels is null, "no block, no levels");

            LevelApplication applied = FidelityCuts.Apply(manifest.Water!, ImportStepKind.Water, FidelityLevel.Min, "water bodies");
            run.Equal(applied.File?.Path, "Site/Vector/water.geojson", "MIN on an entry without levels reads the entry");
            run.True(applied.Applied is null, "and applies no level");
            run.True(applied.Refusal is null, "and refuses nothing");
        });
    }

    private static void RunUnknownValueCases(TestRun run)
    {
        run.Case("an unknown level named by same_as makes the whole block absent", () =>
        {
            FidelityLevels? levels = SplineLevels("""
                "RAW": { "same_as": "MAX", "reason": "max_only" }, "MAX": {},
                "MED": { "same_as": "ULTRA", "reason": "max_only" }, "MIN": { "same_as": "MAX", "reason": "max_only" }
                """);
            run.True(levels is null, "MAX only");
        });

        run.Case("a block missing a level is absent", () =>
            run.True(SplineLevels("""
                "RAW": { "same_as": "MAX", "reason": "max_only" }, "MAX": {}, "MED": { "same_as": "MAX", "reason": "max_only" }
                """) is null, "all four are required"));

        run.Case("an unknown cut form makes that level unavailable, never the whole block", () =>
        {
            FidelityLevels? levels = SplineLevels("""
                "RAW": { "same_as": "MAX", "reason": "max_only" }, "MAX": {},
                "MED": { "cut": { "every_nth": 2 } }, "MIN": { "cut": { "rows": 1 } }
                """);
            run.True(levels is not null, "the block is read");
            run.Equal(levels![FidelityLevel.Med].Kind, FidelityLevelKind.Unavailable, "MED is unavailable");
            run.Equal(levels[FidelityLevel.Min].Kind, FidelityLevelKind.RowCut, "MIN is still read");
            run.Equal(levels[FidelityLevel.Raw].Kind, FidelityLevelKind.SameAs, "and RAW");
        });

        run.Case("a same_as naming another same_as is unavailable: same_as never chains", () =>
        {
            FidelityLevels? levels = SplineLevels("""
                "RAW": { "same_as": "MAX", "reason": "max_only" }, "MAX": {},
                "MED": { "same_as": "MAX", "reason": "max_only" }, "MIN": { "same_as": "MED", "reason": "smallest_valid_size" }
                """);
            run.Equal(levels![FidelityLevel.Min].Kind, FidelityLevelKind.Unavailable, "MIN is unavailable");
            run.Equal(levels[FidelityLevel.Med].Kind, FidelityLevelKind.SameAs, "MED is still the same as MAX");
        });

        run.Case("an unknown reason is read as the same, without a reason", () =>
        {
            FidelityLevels? levels = SplineLevels("""
                "RAW": { "same_as": "MAX", "reason": "max_only" }, "MAX": {},
                "MED": { "same_as": "MAX", "reason": "too_tired" }, "MIN": { "same_as": "MAX", "reason": "max_only" }
                """);
            PublishedLevel med = levels![FidelityLevel.Med];
            run.Equal(med.Kind, FidelityLevelKind.SameAs, "still the same as");
            run.True(med.SameAs == FidelityLevel.Max, "as MAX");
            run.True(med.Reason is null, "with no reason");
            run.Equal(
                WindowLabels.LevelOption(FidelityLevel.Med, med, true, null, null),
                "MED: Same as MAX",
                "and the window says so without one");
        });

        run.Case("a cost in an unknown unit keeps its count and is never estimated", () =>
        {
            FidelityLevels? levels = SplineLevels("""
                "RAW": { "same_as": "MAX", "reason": "max_only" }, "MAX": { "cost_driver": { "unit": "hectares", "count": 3 } },
                "MED": { "same_as": "MAX", "reason": "max_only" }, "MIN": { "same_as": "MAX", "reason": "max_only" }
                """);
            CostDriver? cost = levels![FidelityLevel.Max].Cost;
            run.Equal(cost?.Unit ?? CostUnit.Elements, CostUnit.Unknown, "an unknown unit");
            run.Equal(cost?.RawUnit, "hectares", "kept verbatim");
            run.Equal((int)(cost?.Count ?? -1), 3, "with its count");
            run.True(SlowStepNotice.EstimateLevel(ImportStepKind.RoadCentrelines, cost, "2025") is null, "and no estimate");
            run.Equal(
                WindowLabels.LevelOption(FidelityLevel.Max, levels[FidelityLevel.Max], true, cost, null),
                "MAX — 3",
                "the count, without a unit or a cost");
        });
    }

    private static void RunCutCases(TestRun run)
    {
        run.Case("a field cut keeps a feature whose lowest level is the chosen one or lighter", () =>
        {
            run.True(FidelityCuts.Keeps("MIN", FidelityLevel.Med), "MIN is in MED");
            run.True(FidelityCuts.Keeps("MED", FidelityLevel.Med), "MED is in MED");
            run.False(FidelityCuts.Keeps("MAX", FidelityLevel.Med), "MAX is not in MED");
            run.False(FidelityCuts.Keeps("MED", FidelityLevel.Min), "MED is not in MIN");
            run.True(FidelityCuts.Keeps("MAX", FidelityLevel.Max), "everything is in MAX");
            run.False(FidelityCuts.Keeps("med", FidelityLevel.Max), "a token is compared verbatim");
            run.False(FidelityCuts.Keeps(null, FidelityLevel.Max), "a feature with no value is not kept");
            run.False(FidelityCuts.Keeps("ULTRA", FidelityLevel.Max), "nor one naming no level this host knows");
        });

        run.Case("a road cut at MIN cuts only MIN's features, with stamps named over the whole layer", () =>
        {
            string?[] levels = ["MAX", "MIN", "MED", "MAX"];
            NewSiteBoundary[] missing =
            [
                new(1, "stamp-1"), new(2, "stamp-2"), new(3, "stamp-3"), new(4, "stamp-4"),
            ];
            AppliedLevel atMin = new(FidelityLevel.Min, FidelityLevel.Min, FidelityLevelKind.FieldCut, LowestLevelField: "lowest_level");

            LevelCutSubdivisions cut = FidelityCuts.Subdivisions(levels, missing, atMin);
            run.Equal(cut.ToCut.Count, 1, "one cut");
            run.Equal(cut.ToCut[0].Stamp, "stamp-2", "the MIN feature, under its whole-layer stamp");
            run.Equal(cut.LeftOut, 3, "three left out");
            run.Equal(cut.KeptAboveLevel, 0, "nothing earlier to keep");
            run.Equal(
                FidelityCuts.LevelClause(atMin, cut.LeftOut, cut.KeptAboveLevel, "subdivisions"),
                "; 3 subdivisions above the MIN level were left out",
                "the summary says what was left out");
        });

        run.Case("a road cut at MED after MIN cuts only the rest of MED", () =>
        {
            string?[] levels = ["MAX", "MIN", "MED", "MAX"];
            NewSiteBoundary[] missing = [new(1, "stamp-1"), new(3, "stamp-3"), new(4, "stamp-4")];
            AppliedLevel atMed = new(FidelityLevel.Med, FidelityLevel.Med, FidelityLevelKind.FieldCut, LowestLevelField: "lowest_level");

            LevelCutSubdivisions cut = FidelityCuts.Subdivisions(levels, missing, atMed);
            run.Equal(cut.ToCut.Count, 1, "only the MED feature is new");
            run.Equal(cut.ToCut[0].Ordinal, 3, "the third");
            run.Equal(cut.AlreadyPresent, 1, "the MIN feature is already cut");
            run.Equal(cut.LeftOut, 2, "the MAX features are left out");
        });

        run.Case("a road cut at MIN after MAX deletes nothing and says it kept the rest", () =>
        {
            string?[] levels = ["MAX", "MIN", "MED", "MAX"];
            AppliedLevel atMin = new(FidelityLevel.Min, FidelityLevel.Min, FidelityLevelKind.FieldCut, LowestLevelField: "lowest_level");

            LevelCutSubdivisions cut = FidelityCuts.Subdivisions(levels, [], atMin);
            run.Equal(cut.ToCut.Count, 0, "nothing to cut");
            run.Equal(cut.AlreadyPresent, 1, "MIN's one is there");
            run.Equal(cut.LeftOut, 0, "nothing left out");
            run.Equal(cut.KeptAboveLevel, 3, "the three above MIN are kept");
            run.Contains(
                FidelityCuts.LevelClause(atMin, cut.LeftOut, cut.KeptAboveLevel, "subdivisions"),
                "3 subdivisions above the MIN level, from an earlier import of this bundle, were kept",
                "and the summary says why");
        });

        run.Case("a layer with no level applied cuts every missing feature", () =>
        {
            LevelCutSubdivisions cut = FidelityCuts.Subdivisions([null, null], [new(1, "a"), new(2, "b")], applied: null);
            run.Equal(cut.ToCut.Count, 2, "both");
            run.Equal(FidelityCuts.LevelClause(null, cut.LeftOut, cut.KeptAboveLevel, "subdivisions"), string.Empty, "and says nothing");
        });
    }

    private static void RunPlanCases(TestRun run)
    {
        run.Case("a plan at MAX reads every entry as it always has, and records MAX", () =>
        {
            BundleImportPlan plan = Plan(Fixture());
            ImportStep road = Find(plan, ImportStepKind.RoadPolygons);
            run.Equal(road.EntryName, "Site/Vector/road_polygons.geojson", "the entry's own file");
            run.Equal(road.Level?.Kind ?? FidelityLevelKind.Unavailable, FidelityLevelKind.Max, "at MAX");
            run.True(road.Levels is not null, "carrying the entry's levels for the window");
        });

        run.Case("a field cut reads the entry's file and names the field the step cuts by", () =>
        {
            BundleImportPlan plan = Plan(Fixture(), Levels((ImportLayer.RoadSubdivisions, FidelityLevel.Med)));
            ImportStep road = Find(plan, ImportStepKind.RoadPolygons);
            run.Equal(road.EntryName, "Site/Vector/road_polygons.geojson", "the same file");
            run.Equal(road.ExpectedSha256, OwnSha, "and hash");
            run.Equal(road.Level?.Kind ?? FidelityLevelKind.Unavailable, FidelityLevelKind.FieldCut, "a field cut");
            run.Equal(road.Level?.LowestLevelField, "lowest_level", "by its field");
            run.True(road.Level?.Effective == FidelityLevel.Med, "at MED");
        });

        run.Case("a row cut reads the entry's file and names the rows", () =>
        {
            BundleImportPlan plan = Plan(Fixture(), Levels((ImportLayer.Planting, FidelityLevel.Min)));
            ImportStep trees = Find(plan, ImportStepKind.Vegetation);
            run.Equal(trees.EntryName, "Landcover/TreePoints.csv", "the ranked file");
            run.Equal(trees.Level?.Kind ?? FidelityLevelKind.Unavailable, FidelityLevelKind.RowCut, "a row cut");
            run.Equal(trees.Level?.Rows ?? -1, 2, "MIN is the same as MED, two rows");
            run.True(trees.Level?.Chosen == FidelityLevel.Min && trees.Level?.Effective == FidelityLevel.Med, "chosen MIN, imported MED");
        });

        run.Case("a pointer level reads its own file, under its own hash", () =>
        {
            BundleImportPlan plan = Plan(Fixture(), Levels((ImportLayer.Planting, FidelityLevel.Raw)));
            ImportStep trees = Find(plan, ImportStepKind.Vegetation);
            run.Equal(trees.EntryName, "Landcover/TreePoints.RAW.csv", "RAW's own file");
            run.Equal(trees.ExpectedSha256, LevelSha, "verified by RAW's hash");
            run.Equal(trees.Level?.Kind ?? FidelityLevelKind.Unavailable, FidelityLevelKind.Pointer, "a pointer");
            run.True(
                trees.LevelFiles.Contains(TreeSha) && trees.LevelFiles.Contains(LevelSha) && trees.LevelFiles.Count == 2,
                "the step knows every file the entry publishes: MAX's own and RAW's");
        });

        run.Case("a pointer level in another frame is refused like any other file, never swapped for MAX", () =>
        {
            string fixture = Fixture().Replace(
                "\"path\": \"Landcover/TreePoints.RAW.csv\", \"sha256\": \"" + LevelSha + "\", \"crs\": \"EPSG:2231\"",
                "\"path\": \"Landcover/TreePoints.RAW.csv\", \"sha256\": \"" + LevelSha + "\", \"crs\": \"EPSG:32613\"",
                StringComparison.Ordinal);
            BundleImportPlan plan = Plan(fixture, Levels((ImportLayer.Planting, FidelityLevel.Raw)));
            run.False(plan.Steps.Any(step => step.Kind == ImportStepKind.Vegetation), "no trees planned");
            run.True(plan.Skipped.Any(skip => skip.Kind == ImportStepKind.Vegetation), "and a skip says why");
        });

        run.Case("a drape pointer is placed by its own extent", () =>
        {
            BundleImportPlan plan = Plan(Fixture(), Levels((ImportLayer.ImageryDrape, FidelityLevel.Med)));
            ImportStep drape = Find(plan, ImportStepKind.ImageryDrape);
            const double UsFootM = 1200.0 / 3937.0;
            run.Equal(drape.EntryName, "Imagery/Drape.StatePlane.MED.png", "MED's file");
            run.Within(drape.Drape?.LeftM ?? 0, -1000.0 * UsFootM, 1e-6, "west edge, from MED's extent");
            run.Within(drape.Drape?.TopM ?? 0, 750.0 * UsFootM, 1e-6, "north edge");
            run.True(drape.Drape?.ExtentFromDrapeBlock ?? false, "a declared extent");
        });

        run.Case("a level the step cannot take is skipped, never imported whole", () =>
        {
            BundleImportPlan plan = Plan(Fixture(), Levels((ImportLayer.RoadCentrelines, FidelityLevel.Min)));
            run.False(plan.Steps.Any(step => step.Kind == ImportStepKind.RoadCentrelines), "no centrelines planned");
            SkippedImport? skip = plan.Skipped.FirstOrDefault(s => s.Kind == ImportStepKind.RoadCentrelines);
            run.Equal(skip?.ReasonCode ?? SkipReasonCode.LeftOutByChoice, SkipReasonCode.LevelUnavailable, "skipped as unavailable");
            run.Contains(skip?.Reason, "MIN level", "naming the level");
        });

        run.Case("an unattended import is at MAX", () =>
        {
            BundleImportPlan plan = Plan(Fixture());
            ImportStep trees = Find(plan, ImportStepKind.Vegetation);
            run.Equal(trees.EntryName, "Landcover/TreePoints.csv", "the entry's own file");
            run.True(trees.Level?.Effective == FidelityLevel.Max, "at MAX");
        });
    }

    private static void RunTreeCases(TestRun run)
    {
        const string Csv = """
            x,y,ground_z,height_m,crown_radius_m
            1450131.2,13171825.6,1000.0,10.0,2.0
            1450141.2,13171825.6,,10.0,2.0
            1450151.2,13171825.6,1000.0,10.0,2.0
            1450161.2,13171825.6,1000.0,10.0,2.0
            """;

        run.Case("a row cut reads the first N rows of the file, a dropped row included", () =>
        {
            TreePointsParse whole = TreePointsReader.Parse(Csv, FootFrame, null, LinearUnit.UsSurveyFoot);
            TreePointsParse cut = TreePointsReader.Parse(Csv, FootFrame, null, LinearUnit.UsSurveyFoot, rowLimit: 3);

            run.Equal(whole.Points.Count, 3, "the whole file is three points, one row without ground");
            run.Equal(cut.Points.Count, 2, "the first three rows are two points");
            run.Equal(cut.RowsWithoutGround, 1, "the dropped row is counted, not replaced");
            run.Within(cut.Points[1].EastM, whole.Points[1].EastM, 1e-9, "a cut's points are the whole file's first points");
        });

        run.Case("a row cut of zero rows reads none", () =>
            run.Equal(TreePointsReader.Parse(Csv, FootFrame, null, LinearUnit.UsSurveyFoot, rowLimit: 0).Points.Count, 0, "none"));

        run.Case("a tree re-import at a higher level creates only the missing rows", () =>
        {
            List<string?> atMed = [.. Enumerable.Range(1, 10267).Select(row => (string?)TreeIdentity.Stamp(Stem, TreeSha, row))];
            TreeDecision decision = TreeIdentity.Decide(atMed, Stem, TreeSha, 20534);

            run.True(decision.Disposition == TreeDisposition.Create, "create, not refuse: the same file");
            run.Equal(decision.AlreadyPresent, 10267, "MED's trees are reused");
            run.Equal(decision.RowsToCreate.Count, 10267, "only MAX's rest is created");
            run.Equal(decision.RowsToCreate[0], 10267, "starting after MED's last row");
            run.Equal(decision.KeptAboveLevel, 0, "nothing above MAX");
        });

        run.Case("a tree re-import at a lower level creates nothing and deletes nothing", () =>
        {
            List<string?> atMax = [.. Enumerable.Range(1, 20534).Select(row => (string?)TreeIdentity.Stamp(Stem, TreeSha, row))];
            TreeDecision decision = TreeIdentity.Decide(atMax, Stem, TreeSha, 5134);

            run.True(decision.Disposition == TreeDisposition.Create, "not a refusal");
            run.Equal(decision.RowsToCreate.Count, 0, "nothing created");
            run.Equal(decision.AlreadyPresent, 5134, "MIN's rows are there");
            run.Equal(decision.KeptAboveLevel, 15400, "and the rest are kept, counted for the summary");
        });
        run.Case("trees from another level's own file are refused as that, not as an earlier build", () =>
        {
            List<string?> atMax = [.. Enumerable.Range(1, 3).Select(row => (string?)TreeIdentity.Stamp(Stem, TreeSha, row))];
            TreeDecision decision = TreeIdentity.Decide(atMax, Stem, LevelSha, 5, [TreeSha, LevelSha]);

            run.True(decision.Disposition == TreeDisposition.RefuseOtherLevel, "refused: two files' rows cannot be matched");
            run.Equal(decision.RowsToCreate.Count, 0, "nothing created");
            run.True(decision.Explanation.Contains("another fidelity level", StringComparison.Ordinal), "the refusal names the level, not a rebuild");
            run.False(decision.Explanation.Contains("EARLIER build", StringComparison.Ordinal), "and never calls them stale");
        });

        run.Case("trees from a build outside the entry's level files are still an earlier build", () =>
        {
            List<string?> old = [TreeIdentity.Stamp(Stem, OwnSha, 1)];
            TreeDecision decision = TreeIdentity.Decide(old, Stem, LevelSha, 5, [TreeSha, LevelSha]);
            run.True(decision.Disposition == TreeDisposition.RefuseStale, "stale");
        });

        run.Case("the planting step says the rows its level read and the trees it kept", () =>
        {
            AppliedLevel atMed = new(FidelityLevel.Med, FidelityLevel.Med, FidelityLevelKind.RowCut, Rows: 10267);
            run.Equal(
                FidelityCuts.RowCutSentence(atMed, 0),
                "The tree points were read at the MED level: the first 10,267 rows of the ranked file.",
                "the rows read");
            run.Equal(
                FidelityCuts.RowCutSentence(atMed, 10267),
                "The tree points were read at the MED level: the first 10,267 rows of the ranked file. "
                    + "10,267 trees above the MED level, from an earlier import of this bundle, were kept, because a lower level never deletes.",
                "and the trees kept");
            run.Equal(
                FidelityCuts.RowCutSentence(new AppliedLevel(FidelityLevel.Max, FidelityLevel.Max, FidelityLevelKind.Max), 0),
                string.Empty,
                "MAX with nothing kept says nothing");
            run.Equal(FidelityCuts.RowCutSentence(null, 0), string.Empty, "nor tree points with no levels");
        });

        run.Case("the log names every step imported below MAX, and the level it is the same as", () =>
        {
            run.True(FidelityCuts.LevelLogLine("Planting", null) is null, "no levels, no line");
            run.True(
                FidelityCuts.LevelLogLine("Planting", new AppliedLevel(FidelityLevel.Max, FidelityLevel.Max, FidelityLevelKind.Max)) is null,
                "MAX, no line");
            run.Equal(
                FidelityCuts.LevelLogLine("Road Subdivisions", new AppliedLevel(FidelityLevel.Med, FidelityLevel.Med, FidelityLevelKind.FieldCut)),
                "Road Subdivisions: imported at the MED level.",
                "a level imported as itself");
            run.Equal(
                FidelityCuts.LevelLogLine("Planting", new AppliedLevel(FidelityLevel.Min, FidelityLevel.Med, FidelityLevelKind.RowCut, 2)),
                "Planting: imported at the MIN level, which the bundle publishes as the same as MED.",
                "a same_as level names its target");
        });
    }

    private static void RunEstimateCases(TestRun run)
    {
        run.Case("a level's estimate is its count times this Revit's measured cost per unit", () =>
        {
            SlowStepNotice.LevelEstimate? trees = SlowStepNotice.EstimateLevel(
                ImportStepKind.Vegetation, new CostDriver(CostUnit.Elements, "elements", 20534), "2025");
            run.True(trees is not null, "trees are estimated");
            run.Within(trees!.Seconds.Low, 20534 * 0.00328, 1e-9, "fastest");
            run.Within(trees.Seconds.High, 20534 * 0.00561, 1e-9, "slowest");
            run.True(trees.Timed && trees.Version == 2025, "from this Revit's own measurement");

            SlowStepNotice.LevelEstimate? roads = SlowStepNotice.EstimateLevel(
                ImportStepKind.RoadPolygons, new CostDriver(CostUnit.Cuts, "cuts", 342), "2027");
            run.Within(roads!.Seconds.Low, 342 * 1.85, 1e-9, "roads, 2027, fastest");
            run.Within(roads.Seconds.High, 342 * 2.46, 1e-9, "roads, 2027, slowest");
        });

        run.Case("a Revit never timed is estimated from the newest one that was, and says so", () =>
        {
            SlowStepNotice.LevelEstimate? roads = SlowStepNotice.EstimateLevel(
                ImportStepKind.RoadPolygons, new CostDriver(CostUnit.Cuts, "cuts", 342), "2028");
            run.False(roads!.Timed, "not this Revit's");
            run.Equal(roads.Version, 2027, "the newest measured");
            run.Equal(
                WindowLabels.LevelOption(
                    FidelityLevel.Max,
                    Parse(Fixture()).RoadPolygons!.Levels![FidelityLevel.Max],
                    true,
                    new CostDriver(CostUnit.Cuts, "cuts", 342),
                    roads),
                "MAX — 342 subdivisions, about 11 to 14 minutes (as timed in Revit 2027)",
                "the window says which Revit it was timed in");
        });

        run.Case("a count in another unit than the step was measured in is not estimated", () =>
        {
            run.True(
                SlowStepNotice.EstimateLevel(ImportStepKind.Vegetation, new CostDriver(CostUnit.Cuts, "cuts", 10), "2025") is null,
                "cuts are not trees");
            run.True(
                SlowStepNotice.EstimateLevel(ImportStepKind.RoadCentrelines, new CostDriver(CostUnit.Elements, "elements", 10), "2025") is null,
                "a step with no unit cost is not estimated");
            run.True(SlowStepNotice.EstimateLevel(ImportStepKind.Vegetation, null, "2025") is null, "nor a level with no count");
        });

        run.Case("the window says seconds under two minutes and minutes above", () =>
        {
            SlowStepNotice.LevelEstimate trees = SlowStepNotice.EstimateLevel(
                ImportStepKind.Vegetation, new CostDriver(CostUnit.Elements, "elements", 20534), "2025")!;
            PublishedLevel max = Parse(Fixture()).TreePoints!.Levels![FidelityLevel.Max];
            run.Equal(
                WindowLabels.LevelOption(FidelityLevel.Max, max, true, new CostDriver(CostUnit.Elements, "elements", 20534), trees),
                "MAX — 20,534 elements, about 67 to 115 seconds",
                "seconds");

            PublishedLevel med = Parse(Fixture()).TreePoints!.Levels![FidelityLevel.Min];
            run.Equal(
                WindowLabels.LevelOption(FidelityLevel.Min, med, true, null, null),
                "MIN: Same as MED (already the smallest useful size)",
                "a same_as level names its target and reason");
            run.Equal(
                WindowLabels.LevelOption(FidelityLevel.Min, med, false, null, null),
                "MIN: Same as MED (already the smallest useful size) — not available in this version of Mantle Place",
                "and says when it cannot be imported");
        });

        run.Case("every same_as reason has words of its own", () =>
        {
            foreach (SameAsReason reason in Enum.GetValues<SameAsReason>())
            {
                run.False(WindowLabels.SameAsReasonPhrase(reason) == reason.ToString(), $"{reason} is worded");
            }
        });
    }

    private static void RunChecklistCases(TestRun run)
    {
        run.Case("the checklist offers a level list on each row whose entry publishes levels", () =>
        {
            ImportChecklist checklist = ImportChecklist.For(Plan(Fixture()), "2025");
            run.True(checklist.OffersLevels, "the column is shown");
            run.True(checklist.HasLevels(ImportLayer.RoadSubdivisions), "roads");
            run.True(checklist.HasLevels(ImportLayer.Planting), "trees");
            run.True(checklist.HasLevels(ImportLayer.ImageryDrape), "drape");
            run.False(checklist.HasLevels(ImportLayer.WaterSubdivisions), "water publishes none");

            IReadOnlyList<LevelOption> options = checklist.LevelOptions(ImportLayer.Planting);
            run.Equal(options.Count, 4, "all four levels");
            run.True(options.Select(option => option.Level).SequenceEqual(FidelityLevelNames.All), "RAW to MIN");
            run.Equal(options[3].Label, "MIN: Same as MED (already the smallest useful size) — 2 elements, about 1 second", "MIN says MED's count");
            run.True(checklist.ChosenLevel(ImportLayer.Planting) == FidelityLevel.Min, "trees open on their default, MIN");
            run.True(checklist.ChosenLevel(ImportLayer.RoadSubdivisions) == FidelityLevel.Min, "and so do the road surfaces");
            run.True(checklist.ChosenLevel(ImportLayer.ImageryDrape) == FidelityLevel.Max, "a row whose default is MAX opens on MAX");
            run.True(checklist.AllLevels is null, "so set-all reads mixed");
        });

        run.Case("each row opens on its measured default, and on MAX where that default cannot be imported", () =>
        {
            foreach (ImportLayer layer in Enum.GetValues<ImportLayer>())
            {
                FidelityLevel expected = layer is ImportLayer.Planting or ImportLayer.RoadSubdivisions ? FidelityLevel.Min : FidelityLevel.Max;
                run.True(ImportLayers.DefaultLevel(layer) == expected, $"{layer} defaults to {expected}");
            }

            // Road surfaces whose MIN is published as a row cut, which their step cannot take.
            string rowCutRoads = Fixture().Replace(
                "\"MIN\": { \"cut\": { \"lowest_level_field\": \"lowest_level\" }",
                "\"MIN\": { \"cut\": { \"rows\": 1 }",
                StringComparison.Ordinal);
            run.False(rowCutRoads == Fixture(), "the fixture has the road surfaces' MIN to replace");
            ImportChecklist checklist = ImportChecklist.For(Plan(rowCutRoads), "2025");
            run.True(checklist.ChosenLevel(ImportLayer.RoadSubdivisions) == FidelityLevel.Max, "an unavailable default falls back to MAX");

            BundleImportPlan plan = Plan(Fixture(), ImportChecklist.For(Plan(Fixture()), "2025").Choice);
            run.True(Find(plan, ImportStepKind.Vegetation).Level?.Chosen == FidelityLevel.Min, "the untouched window plants the trees at MIN");
        });

        run.Case("a row's level is chosen, and an unavailable one is not", () =>
        {
            ImportChecklist checklist = ImportChecklist.For(Plan(Fixture()), "2025");
            run.False(checklist.LevelOptions(ImportLayer.RoadCentrelines)[3].IsAvailable, "the centrelines' row cut is unavailable");

            checklist.SetLevel(ImportLayer.RoadCentrelines, FidelityLevel.Min);
            run.True(checklist.ChosenLevel(ImportLayer.RoadCentrelines) == FidelityLevel.Max, "choosing it does nothing");

            checklist.SetLevel(ImportLayer.RoadSubdivisions, FidelityLevel.Med);
            run.True(checklist.ChosenLevel(ImportLayer.RoadSubdivisions) == FidelityLevel.Med, "roads at MED");
            run.True(checklist.AllLevels is null, "the rows differ");
            run.True(checklist.Choice.LevelOf(ImportLayer.RoadSubdivisions) == FidelityLevel.Med, "the choice carries it");
        });

        run.Case("set all to sets every row it can, and the plan imports the chosen levels", () =>
        {
            ImportChecklist checklist = ImportChecklist.For(Plan(Fixture()), "2025");
            checklist.Set(ImportLayer.RoadSubdivisions, true);
            checklist.SetAllLevels(FidelityLevel.Min);
            run.True(checklist.ChosenLevel(ImportLayer.RoadSubdivisions) == FidelityLevel.Min, "roads at MIN");
            run.True(checklist.ChosenLevel(ImportLayer.Planting) == FidelityLevel.Min, "trees at MIN");
            run.True(checklist.ChosenLevel(ImportLayer.RoadCentrelines) == FidelityLevel.Max, "centrelines skipped: MIN is unavailable");

            BundleImportPlan plan = Plan(Fixture(), checklist.Choice);
            run.True(Find(plan, ImportStepKind.RoadPolygons).Level?.Effective == FidelityLevel.Min, "the plan cuts roads at MIN");
            run.Equal(Find(plan, ImportStepKind.Vegetation).Level?.Rows ?? -1, 2, "and plants MED's two rows");
            run.True(plan.Steps.Any(step => step.Kind == ImportStepKind.RoadCentrelines), "and the centrelines whole, at MAX");
        });

        run.Case("a bundle with no levels offers no level column", () =>
        {
            ImportChecklist checklist = ImportChecklist.For(Plan(Fixture(levels: false)), "2025");
            run.False(checklist.OffersLevels, "no column");
            run.Equal(checklist.LevelOptions(ImportLayer.Planting).Count, 0, "no list");
            run.True(checklist.AllLevels is null, "nothing for set-all to read");
        });
    }

    // ---- fixtures -------------------------------------------------------------------------------

    private static readonly SiteFrame FootFrame = new()
    {
        Origin = new GeoOrigin { Epsg = 2231, Easting = 1450131.2, Northing = 13171825.6, LinearUnit = LinearUnit.UsSurveyFoot },
    };

    private static IReadOnlyDictionary<ImportLayer, FidelityLevel> Levels(params (ImportLayer Layer, FidelityLevel Level)[] levels)
        => levels.ToDictionary(entry => entry.Layer, entry => entry.Level);

    /// <summary>
    /// A State Plane delivery whose road surfaces are cut by a level field, whose trees are a ranked
    /// row file with a RAW file of their own, whose drape has a MED file of its own, and whose road
    /// centrelines carry a row cut no centreline step can take.
    /// </summary>
    /// <param name="levels">Whether any entry publishes levels.</param>
    /// <param name="splineLevels">The road centrelines' block's members in place of the fixture's own, for a reader case.</param>
    private static string Fixture(bool levels = true, string? splineLevels = null)
    {
        string roadLevels = levels
            ? """
              , "levels": {
                "RAW": { "same_as": "MAX", "reason": "not_derived" },
                "MAX": { "cost_driver": { "unit": "cuts", "count": 4, "area_m2": 1000.0 } },
                "MED": { "cut": { "lowest_level_field": "lowest_level" }, "cost_driver": { "unit": "cuts", "count": 2 } },
                "MIN": { "cut": { "lowest_level_field": "lowest_level" }, "cost_driver": { "unit": "cuts", "count": 1 } } }
              """
            : string.Empty;
        string splineBlock = splineLevels is not null
            ? $", \"levels\": {{ {splineLevels} }}"
            : levels
            ? """
              , "levels": {
                "RAW": { "same_as": "MAX", "reason": "max_only" }, "MAX": {},
                "MED": { "same_as": "MAX", "reason": "max_only" }, "MIN": { "cut": { "rows": 1 } } }
              """
            : string.Empty;
        string drapeLevels = levels
            ? $$"""
              , "levels": {
                "RAW": { "same_as": "MAX", "reason": "no_reduction" }, "MAX": {},
                "MED": { "path": "Imagery/Drape.StatePlane.MED.png", "sha256": "{{LevelSha}}", "format": "png-rgb-8bit",
                         "extent": [1449131.2, 13171075.6, 1451131.2, 13172575.6], "extent_crs": "EPSG:2231", "units": "ftUS",
                         "width": 2000, "height": 1500 },
                "MIN": { "same_as": "MED", "reason": "smallest_valid_size" } }
              """
            : string.Empty;
        string treeLevels = levels
            ? $$"""
              , "levels": {
                "RAW": { "path": "Landcover/TreePoints.RAW.csv", "sha256": "{{LevelSha}}", "crs": "EPSG:2231", "units": "ftUS" },
                "MAX": { "cost_driver": { "unit": "elements", "count": 3 } },
                "MED": { "cut": { "rows": 2 }, "cost_driver": { "unit": "elements", "count": 2 } },
                "MIN": { "same_as": "MED", "reason": "smallest_valid_size" } }
              """
            : string.Empty;

        return $$"""
            {
              "version": "1.10.0",
              "layout": { "imagery_drape": "Imagery/Drape.png", "tree_points": "Landcover/TreePoints.csv" },
              "hosts": { "revit": {
                "georeference": {
                  "crs_projected": "EPSG:2231",
                  "origin": {
                    "lon": -105.32557885004304, "lat": 38.46130517000308,
                    "projected": { "epsg": 2231, "easting": 1450131.2, "northing": 13171825.6, "linear_unit": "ftUS" }
                  }
                },
                "file_frame": { "type": "projected", "crs": "EPSG:2231", "horizontal_unit": "ftUS", "vertical_unit": "ftUS" },
                "vectors": { "format": "geojson", "layers": [
                  { "name": "road_splines", "path": "Site/Vector/road_splines.geojson", "horizontal_frame": "absolute_projected",
                    "units": "ftUS", "feature_count": 1, "sha256": "{{OwnSha}}" {{splineBlock}} },
                  { "name": "water", "path": "Site/Vector/water.geojson", "horizontal_frame": "absolute_projected",
                    "units": "ftUS", "feature_count": 1, "sha256": "{{OwnSha}}" },
                  { "name": "road_polygons", "path": "Site/Vector/road_polygons.geojson", "horizontal_frame": "absolute_projected",
                    "units": "ftUS", "feature_count": 4, "sha256": "{{OwnSha}}" {{roadLevels}} }
                ] },
                "drape": {
                  "path": "Imagery/Drape.StatePlane.png", "format": "png-rgb-8bit",
                  "extent": [1448131.2, 13170325.6, 1452131.2, 13173325.6], "extent_crs": "EPSG:2231", "units": "ftUS",
                  "width": 4000, "height": 3000, "effective_gsd_m": 0.3048006096, "sha256": "{{DrapeSha}}" {{drapeLevels}}
                },
                "readiness": { "toposurface_points": { "present": false, "reason": "not_produced" }, "vectors": { "present": true } }
              } },
              "landcover": { "tree_points": {
                "path": "Landcover/TreePoints.csv", "sha256": "{{TreeSha}}", "crs": "EPSG:2231", "units": "ftUS", "point_count": 3 {{treeLevels}}
              } },
              "vector": { "layers": [] },
              "imagery": { "present": true, "gsd_m": 0.3, "drape": {
                "extent": [470880.0, 4256340.0, 472310.0, 4257760.0], "extent_crs": "EPSG:32613" } }
            }
            """;
    }

    private static readonly string[] Entries =
    [
        "Metadata/manifest.json",
        "Imagery/Drape.png",
        "Imagery/Drape.StatePlane.png",
        "Imagery/Drape.StatePlane.MED.png",
        "Site/Vector/road_splines.geojson",
        "Site/Vector/water.geojson",
        "Site/Vector/road_polygons.geojson",
        "Landcover/TreePoints.csv",
        "Landcover/TreePoints.RAW.csv",
    ];

    /// <summary>The road centrelines' levels, read from a fixture whose only levels block is this one.</summary>
    private static FidelityLevels? SplineLevels(string members)
        => Parse(Fixture(levels: false, splineLevels: members)).RoadSplines!.Levels;

    private static BundleManifest Parse(string json)
    {
        BundleManifest manifest = BundleManifestReader.Parse(json);
        if (!manifest.IsValid)
        {
            throw new InvalidOperationException($"fixture refused: {manifest.Error}");
        }

        return manifest;
    }

    private static BundleImportPlan Plan(string json, IReadOnlyDictionary<ImportLayer, FidelityLevel>? levels = null)
        => Plan(json, ImportLayerChoice.Only(Enum.GetValues<ImportLayer>(), levels));

    private static BundleImportPlan Plan(string json, ImportLayerChoice choice)
        => BundleImportPlanner.Plan(Parse(json), Entries, _ => DrapePixels, choice);

    private static ImportStep Find(BundleImportPlan plan, ImportStepKind kind)
        => plan.Steps.FirstOrDefault(step => step.Kind == kind)
           ?? throw new InvalidOperationException(
               $"{kind} was not planned: {string.Join(" | ", plan.Skipped.Where(skip => skip.Kind == kind).Select(skip => skip.Reason))}");
}
