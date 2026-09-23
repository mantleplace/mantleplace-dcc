namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The import window's checklist: which layers a bundle import brings in, what each one needs, and
/// what the plan does with the ones a curator leaves out.
/// </summary>
/// <remarks>
/// The window only copies these answers onto check boxes, and CI never builds it, so every rule the
/// checklist appears to enforce — a disabled prerequisite, the reason beside it, the Import button
/// going dark — is decided here (<c>HPS-02</c>, <c>HPS-42</c>).
/// </remarks>
internal static class ImportLayerTests
{
    internal static int Run()
    {
        TestRun run = new();

        RunLayerCases(run);
        RunChecklistCases(run);
        RunPlannerCases(run);

        return run.Report("import layers");
    }

    private static void RunLayerCases(TestRun run)
    {
        run.Case("every step kind that builds something belongs to a layer, and the terrain is one", () =>
        {
            run.True(ImportLayers.Of(ImportStepKind.ToposurfaceFromSurfaceTin) == ImportLayer.Terrain, "the TIN path");
            run.True(ImportLayers.Of(ImportStepKind.ToposurfaceFromPointsFile) == ImportLayer.Terrain, "the points path");
            run.True(ImportLayers.Of(ImportStepKind.ToposurfaceFromSurfaceDxf) == ImportLayer.Terrain, "the linked DXF");
            run.True(ImportLayers.Of(ImportStepKind.ContextBuildings) == ImportLayer.ContextBuildings, "the buildings copied from the IFC");
            run.True(ImportLayers.Of(ImportStepKind.LinkSiteIfc) == ImportLayer.SiteModel, "the IFC, linked");
            run.True(ImportLayers.Of(ImportStepKind.RoadCentrelines) == ImportLayer.RoadCentrelines, "the roads");
            run.True(ImportLayers.Of(ImportStepKind.SiteBoundaries) == ImportLayer.LandUseSubdivisions, "the land-use polygons");
            run.True(ImportLayers.Of(ImportStepKind.LandCover) == ImportLayer.LandCoverSubdivisions, "the land-cover polygons");
            run.True(ImportLayers.Of(ImportStepKind.Water) == ImportLayer.WaterSubdivisions, "the water bodies");
            run.True(ImportLayers.Of(ImportStepKind.RoadPolygons) == ImportLayer.RoadSubdivisions, "the road surfaces");
            run.True(ImportLayers.Of(ImportStepKind.Vegetation) == ImportLayer.Planting, "the tree points");
            run.True(ImportLayers.Of(ImportStepKind.ImageryDrape) == ImportLayer.ImageryDrape, "the drape");

            // Placing the project builds nothing, so there is nothing to leave out.
            run.True(ImportLayers.Of(ImportStepKind.SetSharedCoordinates) is null, "shared coordinates is not a layer");

            // Crediting the data is the licence's condition on importing any of it.
            run.True(ImportLayers.Of(ImportStepKind.AttributionAndProvenance) is null, "attribution is not a layer");
        });

        run.Case("every layer has a step kind, so none is a box that controls nothing", () =>
        {
            HashSet<ImportLayer> reached = [.. Enum.GetValues<ImportStepKind>()
                .Select(ImportLayers.Of)
                .OfType<ImportLayer>()];

            foreach (ImportLayer layer in Enum.GetValues<ImportLayer>())
            {
                run.True(reached.Contains(layer), $"{layer} is reached by some step kind");
            }
        });

        run.Case("the subdivisions and the drape need the terrain, and nothing else needs anything", () =>
        {
            run.True(ImportLayers.PrerequisiteOf(ImportLayer.LandUseSubdivisions) == ImportLayer.Terrain, "land-use subdivisions are cut into the ground");
            run.True(ImportLayers.PrerequisiteOf(ImportLayer.LandCoverSubdivisions) == ImportLayer.Terrain, "and so are land-cover ones");
            run.True(ImportLayers.PrerequisiteOf(ImportLayer.WaterSubdivisions) == ImportLayer.Terrain, "and the water bodies");
            run.True(ImportLayers.PrerequisiteOf(ImportLayer.RoadSubdivisions) == ImportLayer.Terrain, "and the road surfaces");
            run.True(ImportLayers.PrerequisiteOf(ImportLayer.ImageryDrape) == ImportLayer.Terrain, "the drape is worn by the ground");
            run.True(ImportLayers.PrerequisiteOf(ImportLayer.Terrain) is null, "the terrain");
            run.True(ImportLayers.PrerequisiteOf(ImportLayer.SiteModel) is null, "the site model is linked on its own");
            run.True(ImportLayers.PrerequisiteOf(ImportLayer.ContextBuildings) is null, "context buildings carry their own Z");
            run.True(ImportLayers.PrerequisiteOf(ImportLayer.RoadCentrelines) is null, "roads carry their own Z");
            run.True(ImportLayers.PrerequisiteOf(ImportLayer.Planting) is null, "trees carry their own Z");
        });

        run.Case("every layer but the site model's link is on by default", () =>
        {
            // The reason HPS-51 asks for before a row starts unchecked: the site model's buildings are
            // already copied in, and a link as well shows each one twice.
            foreach (ImportLayer layer in Enum.GetValues<ImportLayer>())
            {
                run.Equal(ImportLayers.OnByDefault(layer), layer != ImportLayer.SiteModel, $"{layer}'s box");
            }
        });

        run.Case("a layer's name is its step's name, so the checklist and the steps agree", () =>
        {
            foreach (ImportStepKind kind in Enum.GetValues<ImportStepKind>())
            {
                if (ImportLayers.Of(kind) is { } layer)
                {
                    run.Equal(WindowLabels.LayerName(layer), WindowLabels.StepName(kind), $"{kind} and its layer");
                }
            }

            run.Equal(WindowLabels.LayerName(ImportLayer.Terrain), "Terrain", "the glossary's word, not the host's");
            run.Equal(WindowLabels.LayerName(ImportLayer.SiteModel), "Site Model", "the site model");
            run.Equal(WindowLabels.LayerName(ImportLayer.ContextBuildings), "Context Buildings", "the glossary's context buildings");
            run.Equal(WindowLabels.LayerName(ImportLayer.LandUseSubdivisions), "Land Use Subdivisions", "the glossary's subdivision, told apart by its layer");
            run.Equal(WindowLabels.LayerName(ImportLayer.LandCoverSubdivisions), "Land Cover Subdivisions", "the other layer's subdivisions");
            run.Equal(WindowLabels.LayerName(ImportLayer.WaterSubdivisions), "Water Subdivisions", "the water bodies (HPS-51)");
            run.Equal(
                WindowLabels.LayerName(ImportLayer.RoadSubdivisions),
                "Road Subdivisions",
                "the road surfaces, told apart from the Road Centrelines row above them");
            run.Equal(WindowLabels.LayerName(ImportLayer.ImageryDrape), "Imagery Drape", "the drape");
        });

        run.Case("the checklist says HPS-51's words", () =>
        {
            run.Equal(WindowLabels.IncludeHeading, "Include", "the heading over the list");
            run.Equal(WindowLabels.Import, "Import", "the button that starts the steps");
            run.Equal(WindowLabels.Needs(ImportLayer.Terrain), "Needs Terrain", "why a box is disabled");
        });
    }

    private static void RunChecklistCases(TestRun run)
    {
        run.Case("the checklist lists what the bundle carries, in the order the steps run", () =>
        {
            ImportChecklist checklist = ImportChecklist.For(PlanFor(Everything, EverythingBundle));

            run.Equal(
                string.Join(", ", checklist.Layers),
                "Terrain, ContextBuildings, SiteModel, RoadCentrelines, LandUseSubdivisions, LandCoverSubdivisions, "
                    + "WaterSubdivisions, RoadSubdivisions, Planting, ImageryDrape",
                "every layer the plan has a step for, and no other");
        });

        run.Case("a layer the bundle does not carry is not offered", () =>
        {
            ImportChecklist checklist = new([ImportLayer.Terrain, ImportLayer.Planting]);

            run.Equal(string.Join(", ", checklist.Layers), "Terrain, Planting", "only what is there");
            run.False(checklist.IsChecked(ImportLayer.SiteModel), "a layer that is not offered is never chosen");
        });

        run.Case("everything but the link starts checked, all of it enabled, and can be imported", () =>
        {
            ImportChecklist checklist = new(Enum.GetValues<ImportLayer>());

            foreach (ImportLayer layer in checklist.Layers)
            {
                run.Equal(checklist.IsChecked(layer), layer != ImportLayer.SiteModel, $"{layer}'s box");
                run.True(checklist.IsEnabled(layer), $"{layer} is enabled");
                run.True(checklist.MissingPrerequisite(layer) is null, $"{layer} needs nothing it lacks");
            }

            run.True(checklist.CanImport, "Import is lit");
        });

        run.Case("unchecking the terrain disables what needs it, and says why", () =>
        {
            ImportChecklist checklist = new(Enum.GetValues<ImportLayer>());
            checklist.Set(ImportLayer.Terrain, false);

            foreach (ImportLayer dependent in new[]
                     {
                         ImportLayer.LandUseSubdivisions,
                         ImportLayer.LandCoverSubdivisions,
                         ImportLayer.WaterSubdivisions,
                         ImportLayer.RoadSubdivisions,
                         ImportLayer.ImageryDrape,
                     })
            {
                run.False(checklist.IsEnabled(dependent), $"{dependent} cannot be toggled");
                run.False(checklist.IsChecked(dependent), $"{dependent} reads unchecked");
                run.True(checklist.MissingPrerequisite(dependent) == ImportLayer.Terrain, $"{dependent} names the terrain");
                run.False(checklist.Choice.Includes(dependent), $"{dependent} is not imported");
            }

            run.True(checklist.IsEnabled(ImportLayer.Planting), "trees need no terrain");
            run.True(checklist.Choice.Includes(ImportLayer.Planting), "and are still imported");
        });

        run.Case("checking the terrain again gives back what the curator had chosen", () =>
        {
            ImportChecklist checklist = new(Enum.GetValues<ImportLayer>());
            checklist.Set(ImportLayer.ImageryDrape, false);
            checklist.Set(ImportLayer.Terrain, false);
            checklist.Set(ImportLayer.Terrain, true);

            run.True(checklist.IsChecked(ImportLayer.LandUseSubdivisions), "subdivisions were on, and are again");
            run.False(checklist.IsChecked(ImportLayer.ImageryDrape), "the drape was off before, and stays off");
        });

        run.Case("a disabled box cannot be checked around its prerequisite", () =>
        {
            ImportChecklist checklist = new(Enum.GetValues<ImportLayer>());
            checklist.Set(ImportLayer.Terrain, false);
            checklist.Set(ImportLayer.LandUseSubdivisions, true);

            run.False(checklist.Choice.Includes(ImportLayer.LandUseSubdivisions), "subdivisions without the terrain are not chosen");
        });

        run.Case("a write to a disabled box does not change what the curator had chosen", () =>
        {
            // The window listens to a box's Checked and Unchecked, which its own repaint of a disabled
            // box raises too. The window drops those itself; this is the rule that makes a slip there
            // harmless, rather than costing the curator what checking the terrain again gives back.
            ImportChecklist checklist = new(Enum.GetValues<ImportLayer>());
            checklist.Set(ImportLayer.Terrain, false);
            checklist.Set(ImportLayer.LandUseSubdivisions, false);
            checklist.Set(ImportLayer.Terrain, true);

            run.True(checklist.IsChecked(ImportLayer.LandUseSubdivisions), "subdivisions were on before the terrain went, and are again");
        });

        run.Case("a prerequisite the bundle does not carry disables nothing", () =>
        {
            // There is no terrain box to check, so a disabled drape would be a box nobody could ever
            // light. The drape step finds the project's own toposolid, as it did before the checklist.
            ImportChecklist checklist = new([ImportLayer.ImageryDrape]);

            run.True(checklist.IsEnabled(ImportLayer.ImageryDrape), "the drape can be toggled");
            run.True(checklist.MissingPrerequisite(ImportLayer.ImageryDrape) is null, "and says it lacks nothing");
            run.True(checklist.Choice.Includes(ImportLayer.ImageryDrape), "and is imported");
        });

        run.Case("with nothing checked there is nothing to import", () =>
        {
            ImportChecklist checklist = new([ImportLayer.Terrain, ImportLayer.Planting]);
            checklist.Set(ImportLayer.Terrain, false);
            checklist.Set(ImportLayer.Planting, false);

            run.False(checklist.CanImport, "Import goes dark");
        });

        run.Case("only the dependents checked, under an unchecked terrain, is nothing to import", () =>
        {
            ImportChecklist checklist = new([ImportLayer.Terrain, ImportLayer.LandUseSubdivisions]);
            checklist.Set(ImportLayer.Terrain, false);

            run.False(checklist.CanImport, "a checked box that is disabled imports nothing");
        });
    }

    private static void RunPlannerCases(TestRun run)
    {
        run.Case("the fixture carries every layer, so the cases below are about the choice", () =>
        {
            BundleImportPlan plan = PlanFor(Everything, EverythingBundle);

            run.True(plan.CanImport, "can import");
            run.Equal(
                string.Join(", ", plan.Steps.Select(step => step.Kind)),
                "ToposurfaceFromPointsFile, ContextBuildings, LinkSiteIfc, SetSharedCoordinates, SetSiteLocation, "
                    + "RoadCentrelines, SiteBoundaries, LandCover, Water, RoadPolygons, Vegetation, "
                    + "AttributionAndProvenance, SiteContextView, ImageryDrape",
                "every step");
        });

        run.Case("choosing everything plans what no choice plans", () =>
        {
            BundleImportPlan unchosen = PlanFor(Everything, EverythingBundle);
            BundleImportPlan all = PlanFor(Everything, EverythingBundle, ImportLayerChoice.All);

            run.Equal(
                string.Join(", ", all.Steps.Select(step => step.Kind)),
                string.Join(", ", unchosen.Steps.Select(step => step.Kind)),
                "the unattended path imports what it always did");
            run.False(all.Skipped.Any(skip => skip.ReasonCode == SkipReasonCode.LeftOutByChoice), "nothing was left out");
        });

        run.Case("a layer left out has no step, and the plan says it was a choice", () =>
        {
            BundleImportPlan plan = PlanFor(Everything, EverythingBundle, AllBut(ImportLayer.Planting));

            run.False(plan.Steps.Any(step => step.Kind == ImportStepKind.Vegetation), "no trees are planned");

            SkippedImport? skip = plan.Skipped.SingleOrDefault(skip => skip.Kind == ImportStepKind.Vegetation);
            run.True(skip?.ReasonCode == SkipReasonCode.LeftOutByChoice, "the skip is a choice, not a defect");
            run.Equal(skip?.Reason, "Left out of this import by choice: Planting.", "and says so in the checklist's word");
        });

        run.Case("a drape left out builds the terrain on the project's own type", () =>
        {
            BundleImportPlan plan = PlanFor(Everything, EverythingBundle, AllBut(ImportLayer.ImageryDrape));

            run.False(plan.Steps.Any(step => step.Kind == ImportStepKind.ImageryDrape), "no drape is planned");
            run.True(
                plan.Steps.Single(step => step.Kind == ImportStepKind.ToposurfaceFromPointsFile).ToposolidType
                    == TerrainToposolidType.Project,
                "an imagery type with no photograph to carry would be a blank layer on the ground");
        });

        run.Case("a terrain left out is one line, not one per tier it would have tried", () =>
        {
            BundleImportPlan plan = PlanFor(
                Everything,
                EverythingBundle,
                ImportLayerChoice.Only([ImportLayer.SiteModel, ImportLayer.Planting]));

            run.False(plan.Steps.Any(step => ImportLayers.Of(step.Kind) == ImportLayer.Terrain), "no terrain step");
            run.Equal(
                plan.Skipped.Count(skip => ImportLayers.Of(skip.Kind) == ImportLayer.Terrain),
                1,
                "the tiers the terrain passed over are not news once it was left out");
            run.True(plan.Steps.Any(step => step.Kind == ImportStepKind.SetSharedCoordinates), "the project is still placed");
            run.True(plan.Steps.Any(step => step.Kind == ImportStepKind.AttributionAndProvenance), "and the data still credited");
            run.True(plan.CanImport, "the site model and the trees are an import");
        });

        run.Case("a layer the bundle lacks keeps its own reason, left out or not", () =>
        {
            string[] withoutTrees = [.. EverythingBundle.Where(entry => !entry.EndsWith("TreePoints.csv", StringComparison.Ordinal))];
            BundleImportPlan plan = PlanFor(Everything, withoutTrees, AllBut(ImportLayer.Planting));

            run.True(
                plan.Skipped.SingleOrDefault(skip => skip.Kind == ImportStepKind.Vegetation)?.ReasonCode
                    == SkipReasonCode.EntryNotInArchive,
                "\"missing from the zip\" is the truer thing to say");
        });

        run.Case("leaving everything out is nothing to import, and says it was a choice", () =>
        {
            BundleImportPlan plan = PlanFor(Everything, EverythingBundle, ImportLayerChoice.Only([]));

            run.False(plan.CanImport, "placing the project alone is not an import");
            run.Contains(plan.BlockedReason, "left out", "the refusal names the choice rather than blaming the bundle");
        });
    }

    private static ImportLayerChoice AllBut(ImportLayer layer)
        => ImportLayerChoice.Only(Enum.GetValues<ImportLayer>().Where(candidate => candidate != layer));

    private static readonly string[] EverythingBundle =
    [
        "Metadata/manifest.json",
        "Surface/SurfacePoints.csv",
        "Site/Site.ifc",
        "Imagery/Drape.png",
        "Vector/RoadSplines.geojson",
        "Vector/LandUse.geojson",
        "Vector/LandCover.geojson",
        "Vector/Water.geojson",
        "Vector/RoadPolygons.geojson",
        "Landcover/TreePoints.csv",
    ];

    /// <summary>
    /// A bundle carrying one of every layer: a terrain, the site model, the three parity layers, the
    /// three other polygon layers cut as subdivisions, and a drape the image's own grid corroborates.
    /// </summary>
    private const string Everything = """
        {
          "version": "1.0.0",
          "layout": {
            "points_csv": "Surface/SurfacePoints.csv",
            "buildings_ifc": "Site/Site.ifc",
            "imagery_drape": "Imagery/Drape.png",
            "tree_points": "Landcover/TreePoints.csv"
          },
          "hosts": {
            "revit": {
              "georeference": {
                "crs_projected": "EPSG:32613",
                "origin": {
                  "lon": -105.32557885004304,
                  "lat": 38.46130517000308,
                  "projected": { "epsg": 32613, "easting": 471595.0, "northing": 4257050.0, "linear_unit": "m" }
                }
              }
            }
          },
          "imagery": { "present": true, "gsd_m": 0.3 },
          "elevation": { "dem": { "crs": "EPSG:32613",
            "bounds_target_crs": [470880.0, 4256340.0, 472310.0, 4257760.0] } },
          "landcover": { "tree_points": { "path": "Landcover/TreePoints.csv", "crs": "EPSG:32613" } },
          "vector": {
            "layers": [
              { "name": "road_splines", "formats": [{ "format": "geojson", "path": "Vector/RoadSplines.geojson", "sha256": "aa" }] },
              { "name": "land_use", "formats": [{ "format": "geojson", "path": "Vector/LandUse.geojson", "sha256": "bb" }] },
              { "name": "land_cover", "formats": [{ "format": "geojson", "path": "Vector/LandCover.geojson", "sha256": "cc" }] },
              { "name": "water", "formats": [{ "format": "geojson", "path": "Vector/Water.geojson", "sha256": "dd" }] },
              { "name": "road", "formats": [{ "format": "geojson", "path": "Vector/Road.geojson", "sha256": "ee" }] },
              { "name": "road_polygons", "formats": [{ "format": "geojson", "path": "Vector/RoadPolygons.geojson", "sha256": "ff" }] }
            ]
          }
        }
        """;

    /// <summary>The fixture bundle's real drape grid, which the extent above corroborates.</summary>
    private static readonly ImageSize DrapePixels = new(4767, 4733);

    private static BundleImportPlan PlanFor(string manifestJson, IReadOnlyList<string> entries)
        => BundleImportPlanner.Plan(BundleManifestReader.Parse(manifestJson), entries, _ => DrapePixels);

    private static BundleImportPlan PlanFor(string manifestJson, IReadOnlyList<string> entries, ImportLayerChoice choice)
        => BundleImportPlanner.Plan(BundleManifestReader.Parse(manifestJson), entries, _ => DrapePixels, choice);
}
