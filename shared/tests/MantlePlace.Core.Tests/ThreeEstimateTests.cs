using System.Text.Json.Nodes;

namespace MantlePlace.Core.Tests;

internal static class ThreeEstimateTests
{
    private const string FullBundle = "bundle/v3-full.json";

    private const string PavementPath = "Basemap/Pavement.gpkg";

    private const string TreesPath = "Landcover/Trees.csv";

    private const string SiteModelPath = "Site/Site.ifc";

    internal static int Run()
    {
        TestRun run = new();

        run.Case("the plan carries each file's catalog count, as the unit its measured cost is per", () =>
        {
            BundleImportPlan plan = PlanWith(pavement: 2_000, trees: 5_000);

            CostDriver? roads = ThreePlanTests.Step(plan, ImportStepKind.RoadPolygons)?.Cost;
            run.True(roads is { Unit: CostUnit.RoadSurfaces, Count: 2_000 }, "the road surfaces' features are road surfaces, not the subdivisions Revit cuts them into");

            CostDriver? trees = ThreePlanTests.Step(plan, ImportStepKind.Vegetation)?.Cost;
            run.True(trees is { Unit: CostUnit.Elements, Count: 5_000 }, "the trees' rows are elements");

            CostDriver? buildings = ThreePlanTests.Step(plan, ImportStepKind.ContextBuildings)?.Cost;
            run.True(buildings is { Unit: CostUnit.Elements, Count: 450 }, "the site model's features are elements");

            run.True(
                plan.Steps.Where(step => step.Kind is not (ImportStepKind.RoadPolygons or ImportStepKind.Vegetation or ImportStepKind.ContextBuildings))
                    .All(step => step.Cost is null),
                "a step with no measured cost per unit has no cost to estimate from");
        });

        run.Case("the measured order's 13 road surfaces break the budget in every Revit it was timed in, as they did", () =>
        {
            (string Version, string Estimate)[] measured = [("2025", "about 18 minutes"), ("2026", "about 22 minutes"), ("2027", "about 8 minutes")];
            foreach ((string version, string estimate) in measured)
            {
                ImportChecklist checklist = ImportChecklist.For(PlanWith(pavement: 13, trees: 7_302), version);
                run.True(checklist.IsOverBudget(ImportLayer.RoadSubdivisions), $"Revit {version}: over five minutes");
                run.False(checklist.IsChecked(ImportLayer.RoadSubdivisions), $"Revit {version}: so the box starts unticked");
                string note = checklist.EstimateNote(ImportLayer.RoadSubdivisions) ?? string.Empty;
                run.Contains(note, "13 road surfaces", $"Revit {version}: the note says what was counted");
                run.Contains(note, estimate, $"Revit {version}: and the time the 1.3.0 gate measured");
            }
        });

        run.Case("a layer over the five-minute budget starts unticked with its estimate and a plain line; one inside starts ticked", () =>
        {
            ImportChecklist checklist = ImportChecklist.For(PlanWith(pavement: 2_000, trees: 5_000), "2026");

            run.True(checklist.Layers.Contains(ImportLayer.RoadSubdivisions), "the bundle offers the road surfaces");
            run.True(checklist.IsOverBudget(ImportLayer.RoadSubdivisions), "2,000 road surfaces break the budget");
            run.False(checklist.IsChecked(ImportLayer.RoadSubdivisions), "so the box starts unticked");
            run.True(checklist.IsEnabled(ImportLayer.RoadSubdivisions), "and the curator can tick it");
            string note = checklist.EstimateNote(ImportLayer.RoadSubdivisions) ?? string.Empty;
            run.Contains(note, "2,000 road surfaces", "the note says what was counted");
            run.Contains(note, "five minutes", "and why it starts unticked");

            run.False(checklist.IsOverBudget(ImportLayer.Planting), "5,000 trees at 3.3 to 5.7 ms each fit");
            run.True(checklist.IsChecked(ImportLayer.Planting), "so the trees start ticked");
            run.Contains(checklist.EstimateNote(ImportLayer.Planting) ?? string.Empty, "about 17 to 28 seconds", "with their estimate");
            run.False((checklist.EstimateNote(ImportLayer.Planting) ?? string.Empty).Contains("unticked", StringComparison.Ordinal), "and no warning");

            run.True(checklist.IsChecked(ImportLayer.Terrain), "a layer with no estimate is untouched");
            run.True(checklist.EstimateNote(ImportLayer.Terrain) is null, "and says nothing");
        });

        run.Case("a bundle whose road surfaces fit starts them ticked", () =>
        {
            ImportChecklist checklist = ImportChecklist.For(PlanWith(pavement: 2, trees: 5_000), "2026");
            run.True(checklist.IsChecked(ImportLayer.RoadSubdivisions), "2 road surfaces take about 3 minutes");
            run.Contains(checklist.EstimateNote(ImportLayer.RoadSubdivisions) ?? string.Empty, "about 3 minutes", "and show the estimate");
        });

        run.Case("the trees' estimate holds the 1.3.0 gate's slowest Revit 2025 run", () =>
        {
            ImportChecklist checklist = ImportChecklist.For(PlanWith(pavement: 2, trees: 7_302), "2025");
            run.Contains(checklist.EstimateNote(ImportLayer.Planting) ?? string.Empty, "7,302 elements, about 24 to 47 seconds", "47.2 s was measured");
        });

        run.Case("the context buildings are estimated per building, and a large order's start unticked in Revit 2026", () =>
        {
            ImportChecklist measured = ImportChecklist.For(PlanWith(pavement: 2, trees: 7_302, buildings: 450), "2025");
            run.True(measured.IsChecked(ImportLayer.ContextBuildings), "the measured order's 450 fit");
            run.Contains(measured.EstimateNote(ImportLayer.ContextBuildings) ?? string.Empty, "450 elements, about 11 to 20 seconds", "with their estimate");

            ImportChecklist slow = ImportChecklist.For(PlanWith(pavement: 2, trees: 7_302, buildings: 450), "2026");
            run.True(slow.IsChecked(ImportLayer.ContextBuildings), "Revit 2026's slowest run of them still fits");
            run.Contains(slow.EstimateNote(ImportLayer.ContextBuildings) ?? string.Empty, "about 1 to 2 minutes", "and says so");

            ImportChecklist large = ImportChecklist.For(PlanWith(pavement: 2, trees: 7_302, buildings: 1_319), "2026");
            run.True(large.IsOverBudget(ImportLayer.ContextBuildings), "1,319 buildings at up to 0.29 s each break the budget");
            run.False(large.IsChecked(ImportLayer.ContextBuildings), "so they start unticked");
        });

        run.Case("ticking the layer imports all of it: the plan reads the whole file, with its count unchanged", () =>
        {
            BundleManifest manifest = Manifest(pavement: 2_000, trees: 5_000);
            BundleImportPlan offered = Plan(manifest, ImportLayerChoice.All);
            ImportChecklist checklist = ImportChecklist.For(offered, "2026");

            run.False(checklist.Choice.Includes(ImportLayer.RoadSubdivisions), "left unticked, it is not chosen");
            run.False(Plan(manifest, checklist.Choice).Steps.Any(step => step.Kind == ImportStepKind.RoadPolygons), "and not planned");

            checklist.Set(ImportLayer.RoadSubdivisions, true);
            run.True(checklist.IsChecked(ImportLayer.RoadSubdivisions), "ticked, it shows ticked");
            BundleImportPlan ticked = Plan(manifest, checklist.Choice);
            ImportStep? roads = ThreePlanTests.Step(ticked, ImportStepKind.RoadPolygons);
            run.True(roads is not null, "it is planned");
            run.Equal(roads?.EntryName, PavementPath, "from the one file the bundle holds for it");
            run.True(roads?.Cost is { Count: 2_000 }, "every feature of it, none thinned");
            run.Equal(roads?.EntryName, ThreePlanTests.Step(offered, ImportStepKind.RoadPolygons)?.EntryName, "the file the full plan reads");
        });

        run.Case("what the curator ticked last time does not tick a layer this bundle's estimate puts over the budget", () =>
        {
            BundleImportPlan plan = PlanWith(pavement: 2_000, trees: 5_000);
            ImportChecklist ticked = ImportChecklist.For(plan, "2026");
            ticked.Set(ImportLayer.RoadSubdivisions, true);
            RememberedChoice remembered = RememberedChoice.Of(ticked, ticked.Choice);

            ImportChecklist next = ImportChecklist.For(plan, "2026", remembered);
            run.False(next.IsChecked(ImportLayer.RoadSubdivisions), "it opens unticked again");
            next.Set(ImportLayer.RoadSubdivisions, true);
            run.True(next.IsChecked(ImportLayer.RoadSubdivisions), "and can be ticked");
        });

        run.Case("a Revit not timed is estimated from the newest one that was, and the note says so", () =>
        {
            ImportChecklist checklist = ImportChecklist.For(PlanWith(pavement: 2_000, trees: 5_000), "2028");
            run.Contains(checklist.EstimateNote(ImportLayer.RoadSubdivisions) ?? string.Empty, "as timed in Revit 2027", "it names the version");
            run.True(checklist.IsOverBudget(ImportLayer.RoadSubdivisions), "and still breaks the budget");
        });

        run.Case("a catalog that states no count has no estimate, and the layer starts ticked", () =>
        {
            BundleManifest manifest = ThreePlanTests.Read(ThreePlanTests.Load(FullBundle, root =>
                root["catalog"]!.AsObject()[PavementPath]!.AsObject().Remove("count")));
            ImportChecklist checklist = ImportChecklist.For(Plan(manifest, ImportLayerChoice.All), "2026");

            run.True(checklist.IsChecked(ImportLayer.RoadSubdivisions), "ticked");
            run.True(checklist.EstimateNote(ImportLayer.RoadSubdivisions) is null, "with nothing to say");
        });

        return run.Report("MPB 3.0 estimate");
    }

    private static BundleImportPlan PlanWith(long pavement, long trees, long buildings = 450)
        => Plan(Manifest(pavement, trees, buildings), ImportLayerChoice.All);

    private static BundleImportPlan Plan(BundleManifest manifest, ImportLayerChoice choice)
        => BundleImportPlanner.Plan(
            manifest,
            manifest.Contents!.Files.Select(file => file.Path),
            _ => new ImageSize(1024, 1024),
            choice);

    private static BundleManifest Manifest(long pavement, long trees, long buildings = 450)
        => ThreePlanTests.Read(ThreePlanTests.Load(FullBundle, root =>
        {
            JsonObject catalog = root["catalog"]!.AsObject();
            catalog[PavementPath]!["count"]!["value"] = pavement;
            catalog[TreesPath]!["count"]!["value"] = trees;
            catalog[SiteModelPath]!["count"]!["value"] = buildings;
        }));
}
