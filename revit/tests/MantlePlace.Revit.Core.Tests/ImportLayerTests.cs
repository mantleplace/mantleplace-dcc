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
        RunUnavailableCases(run);

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

        run.Case("every layer but the site model's link and the published contours is on by default", () =>
        {
            // The reason HPS-51 asks for before a row starts unchecked: the site model's buildings are
            // already copied in, and a link as well shows each one twice; the toposolid already draws
            // contours of its own (ADR 0013).
            foreach (ImportLayer layer in Enum.GetValues<ImportLayer>())
            {
                run.Equal(ImportLayers.OnByDefault(layer), !StartsUnchecked(layer), $"{layer}'s box");
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

        run.Case("everything but the link and the contours starts checked, all of it enabled, and can be imported", () =>
        {
            ImportChecklist checklist = new(Enum.GetValues<ImportLayer>());

            foreach (ImportLayer layer in checklist.Layers)
            {
                run.Equal(checklist.IsChecked(layer), !StartsUnchecked(layer), $"{layer}'s box");
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

    private static void RunUnavailableCases(TestRun run)
    {
        run.Case("every skip reason has a decided sentence for the window, or is decided to have none", () =>
        {
            // Null is a decision, not a gap: the window lists what a bundle holds and cannot offer,
            // so an absence, a choice, a tier another tier replaced and a skip that is no row's say
            // nothing there. A code added without a line here fails, so none is shown by accident.
            Dictionary<SkipReasonCode, string?> expected = new()
            {
                [SkipReasonCode.NoSiteFrame] = BuiltBefore,
                [SkipReasonCode.CoordinateSystemNotSupported] = BuiltBefore,
                [SkipReasonCode.EntryNotInArchive] = "This bundle is missing the file for this. Download the bundle again from your vault to get it.",
                [SkipReasonCode.UnitNotUnderstood] = "This bundle measures this in a unit this version of Mantle Place cannot read. Update Mantle Place to import it.",
                [SkipReasonCode.ExtentNotCorroborated] = "Mantle Place could not confirm which ground this covers. Download the bundle again from your vault to get it.",
                [SkipReasonCode.ArtifactNotInManifest] = "This bundle does not carry this. Add it to the order in your vault, then download the bundle again.",
                [SkipReasonCode.DeclaredAbsent] = null,
                [SkipReasonCode.DerivedLayerNotPublished] = null,
                [SkipReasonCode.SupersededByFallback] = null,
                [SkipReasonCode.FallbackSuppressed] = null,
                [SkipReasonCode.NoSurveyPoint] = null,
                [SkipReasonCode.NoGeographicOrigin] = null,
                [SkipReasonCode.GeographicOriginOutOfRange] = null,
                [SkipReasonCode.LeftOutByChoice] = null,
            };

            foreach (SkipReasonCode code in Enum.GetValues<SkipReasonCode>())
            {
                if (!expected.TryGetValue(code, out string? sentence))
                {
                    run.Fail($"{code} has no decided sentence for the window");
                    continue;
                }

                run.Equal(WindowLabels.UnavailableReason(code, 1), sentence, $"{code}'s sentence");
            }
        });

        run.Case("a sentence over more than one row speaks of them all", () =>
        {
            run.Equal(
                WindowLabels.UnavailableReason(SkipReasonCode.CoordinateSystemNotSupported, 3),
                "This bundle was built before Revit could receive these. Download the bundle again from your vault to get them.",
                "the bundle that predates a format change");
            run.Equal(
                WindowLabels.UnavailableReason(SkipReasonCode.EntryNotInArchive, 2),
                "This bundle is missing the files for these. Download the bundle again from your vault to get them.",
                "the incomplete bundle");
            run.Equal(
                WindowLabels.UnavailableReason(SkipReasonCode.UnitNotUnderstood, 2),
                "This bundle measures these in a unit this version of Mantle Place cannot read. Update Mantle Place to import them.",
                "the unreadable unit");
            run.Equal(
                WindowLabels.UnavailableReason(SkipReasonCode.ArtifactNotInManifest, 2),
                "This bundle does not carry these. Add them to the order in your vault, then download the bundle again.",
                "the bundle cut without them");
            run.Equal(
                WindowLabels.UnavailableReason(SkipReasonCode.ExtentNotCorroborated, 2),
                "Mantle Place could not confirm which ground these cover. Download the bundle again from your vault to get them.",
                "the ground nobody could confirm");
        });

        run.Case("the window's sentences speak to the curator, and keep the planner's words out", () =>
        {
            // The log keeps the technical register — EPSG codes, file paths, unit tokens. The window
            // says what is missing and what to do, and says it without the glossary's avoided word.
            foreach (SkipReasonCode code in Enum.GetValues<SkipReasonCode>())
            {
                foreach (int count in new[] { 1, 2 })
                {
                    if (WindowLabels.UnavailableReason(code, count) is not { } sentence)
                    {
                        continue;
                    }

                    run.False(sentence.Contains("EPSG", StringComparison.Ordinal), $"{code} names no CRS");
                    run.False(sentence.Contains('"', StringComparison.Ordinal), $"{code} quotes no token");
                    run.False(sentence.Contains("layer", StringComparison.OrdinalIgnoreCase), $"{code} keeps \"layer\" off the page");
                }
            }
        });

        run.Case("the list below the checklist says HPS-51's word", () =>
        {
            run.Equal(WindowLabels.UnavailableHeading, "Unavailable", "the heading over what cannot be offered");
        });

        run.Case("a bundle that carries every layer shows nothing unavailable", () =>
        {
            ImportChecklist checklist = ImportChecklist.For(PlanFor(Everything, EverythingBundle));

            run.Equal(checklist.Unavailable.Count, 0, "nothing extra below the checklist");
        });

        run.Case("a layer the bundle was cut without is listed, with the vault's remedy", () =>
        {
            // The shared set cannot say whether an unlisted layer had no features or was never asked
            // for, and the planner's own sentence sends the curator to the vault; the window agrees.
            string withoutWater = Everything.Replace(
                """{ "name": "water", "formats": [{ "format": "geojson", "path": "Vector/Water.geojson", "sha256": "dd" }] },""",
                string.Empty,
                StringComparison.Ordinal);
            BundleImportPlan plan = PlanFor(withoutWater, EverythingBundle);
            ImportChecklist checklist = ImportChecklist.For(plan);

            run.True(
                plan.Skipped.Any(skip => skip.Kind == ImportStepKind.Water && skip.ReasonCode == SkipReasonCode.ArtifactNotInManifest),
                "the fixture lost its water from the manifest");
            run.False(checklist.Layers.Contains(ImportLayer.WaterSubdivisions), "no row is offered");
            run.Equal(
                string.Join(" | ", checklist.Unavailable.Select(group => $"{string.Join(", ", group.Layers)}: {group.Reason}")),
                "WaterSubdivisions: This bundle does not carry this. Add it to the order in your vault, then download the bundle again.",
                "it is listed as something the order could have");
        });

        run.Case("a layer the bundle says it does not have is not listed as withheld", () =>
        {
            // The producer's own "no imagery for this site" is not a thing the bundle holds and
            // cannot offer, and no vault can change it. The planner still says so, in the log.
            string noImagery = Everything.Replace(
                "\"imagery\": { \"present\": true, \"gsd_m\": 0.3 },",
                "\"imagery\": { \"present\": false },",
                StringComparison.Ordinal);
            BundleImportPlan plan = PlanFor(noImagery, EverythingBundle);

            run.True(
                plan.Skipped.Any(skip => skip.Kind == ImportStepKind.ImageryDrape && skip.ReasonCode == SkipReasonCode.DeclaredAbsent),
                "the fixture declares its imagery absent");
            run.Equal(ImportChecklist.For(plan).Unavailable.Count, 0, "nothing is listed as unavailable");
        });

        run.Case("a State Plane bundle from before Revit had its own copies lists what it holds and cannot offer, once", () =>
        {
            // The case the issue is about: the bundle holds the shared lon/lat set and the UTM drape,
            // this host cannot place either on a foot origin, and the curator used to see a shorter
            // checklist and no reason until the import had run.
            BundleImportPlan plan = PlanFor(StatePlaneBeforeOwnCopies, EverythingBundle);
            ImportChecklist checklist = ImportChecklist.For(plan);

            run.Equal(
                string.Join(", ", checklist.Layers),
                "Terrain, ContextBuildings, SiteModel, Planting",
                "only what this host can place is offered");
            run.Equal(checklist.Unavailable.Count, 1, "one reason, so one sentence");

            UnavailableLayers? group = checklist.Unavailable.FirstOrDefault();
            run.Equal(
                string.Join(", ", group?.Layers ?? []),
                "RoadCentrelines, LandUseSubdivisions, LandCoverSubdivisions, WaterSubdivisions, RoadSubdivisions, ImageryDrape",
                "every withheld row, in the order the steps run");
            run.Equal(
                group?.Reason,
                "This bundle was built before Revit could receive these. Download the bundle again from your vault to get them.",
                "in the curator's register");

            // The log keeps the planner's register, EPSG codes included.
            run.Contains(
                plan.Skipped.Single(skip => skip.Kind == ImportStepKind.ImageryDrape).Reason,
                "EPSG:2231",
                "the technical sentence is untouched");
        });

        run.Case("different reasons are different sentences, each over its own rows", () =>
        {
            string[] withoutTrees = [.. EverythingBundle.Where(entry => !entry.EndsWith("TreePoints.csv", StringComparison.Ordinal))];
            BundleImportPlan plan = BundleImportPlanner.Plan(
                BundleManifestReader.Parse(Everything),
                withoutTrees,
                _ => null);
            ImportChecklist checklist = ImportChecklist.For(plan);

            run.Equal(checklist.Unavailable.Count, 2, "a missing file and an image nobody could read");
            run.Equal(
                string.Join(" | ", checklist.Unavailable.Select(group => $"{string.Join(", ", group.Layers)}: {group.Reason}")),
                "Planting: This bundle is missing the file for this. Download the bundle again from your vault to get it. | "
                    + "ImageryDrape: Mantle Place could not confirm which ground this covers. Download the bundle again from your vault to get it.",
                "in the order the steps run");
        });

        run.Case("a terrain no tier could build is listed with the first reason a curator can act on", () =>
        {
            // Three tiers, three skips, one row. The TIN's absence comes first and is the weakest
            // reason; the suppressed fallback says nothing of its own. The unreadable unit that
            // suppressed it is the reason.
            string chainPoints = Everything.Replace(
                "\"elevation\": { \"dem\":",
                "\"elevation\": { \"points_csv\": { \"units\": \"chain\" }, \"dem\":",
                StringComparison.Ordinal);
            BundleImportPlan plan = PlanFor(chainPoints, EverythingBundle);
            ImportChecklist checklist = ImportChecklist.For(plan);

            run.False(checklist.Layers.Contains(ImportLayer.Terrain), "the fixture has no terrain to offer");
            UnavailableLayers? terrain = checklist.Unavailable.FirstOrDefault(group => group.Layers.Contains(ImportLayer.Terrain));
            run.Equal(
                terrain?.Reason,
                "This bundle measures this in a unit this version of Mantle Place cannot read. Update Mantle Place to import it.",
                "the unit, once");
        });

        run.Case("a layer offered is never also listed as unavailable", () =>
        {
            // The TIN's skip beside a points-file terrain is a tier passed over, not a row withheld.
            ImportChecklist checklist = new(
                [ImportLayer.Terrain],
                [new SkippedImport { Kind = ImportStepKind.ToposurfaceFromSurfaceTin, ReasonCode = SkipReasonCode.NoSiteFrame, Reason = "technical" }]);

            run.Equal(checklist.Unavailable.Count, 0, "the terrain has its box");
        });

        run.Case("a skip that is no row's is not listed", () =>
        {
            ImportChecklist checklist = new(
                [ImportLayer.Terrain],
                [new SkippedImport { Kind = ImportStepKind.SetSharedCoordinates, ReasonCode = SkipReasonCode.NoSiteFrame, Reason = "technical" }]);

            run.Equal(checklist.Unavailable.Count, 0, "placing the project is not a row");
        });
    }

    private const string BuiltBefore =
        "This bundle was built before Revit could receive this. Download the bundle again from your vault to get it.";

    /// <summary>
    /// <see cref="Everything"/> on a State Plane foot origin, cut before MPB 1.3.0 gave this host its
    /// own copies: the tree points follow the delivery CRS and are placed, while the lon/lat vector
    /// set and the UTM drape cannot be.
    /// </summary>
    private static readonly string StatePlaneBeforeOwnCopies = Everything
        .Replace("\"crs_projected\": \"EPSG:32613\"", "\"crs_projected\": \"EPSG:2231\"", StringComparison.Ordinal)
        .Replace(
            "\"projected\": { \"epsg\": 32613, \"easting\": 471595.0, \"northing\": 4257050.0, \"linear_unit\": \"m\" }",
            "\"projected\": { \"epsg\": 2231, \"easting\": 1450131.2, \"northing\": 13171825.6, \"linear_unit\": \"ftUS\" }",
            StringComparison.Ordinal)
        .Replace("\"crs\": \"EPSG:32613\" } },", "\"crs\": \"EPSG:2231\" } },", StringComparison.Ordinal);

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

    private static bool StartsUnchecked(ImportLayer layer)
        => layer is ImportLayer.SiteModel or ImportLayer.PublishedContours;
}
