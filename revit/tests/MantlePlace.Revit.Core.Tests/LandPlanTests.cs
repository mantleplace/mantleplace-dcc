using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Land use and land cover, drawn as filled regions on a land plan of their own, one per layer per
/// build (<c>docs/adr/0015-revit-subdivisions-are-for-built-surfaces.md</c>): what the planner makes
/// of the two layers, the plans' identity and the never-redraw rule they share with the hazard plan,
/// how a published class is typed, and the key that quotes the classes.
/// </summary>
internal static class LandPlanTests
{
    private const string Stem = "order-7f3a";
    private const string JobId = "0d4cbfb1-9c14-4e47-aa0e-471cf6dae969";
    private const string Build = "0d4cbfb1";
    private const string OwnSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    internal static int Run()
    {
        TestRun run = new();

        RunPlannerCases(run);
        RunChecklistCases(run);
        RunIdentityCases(run);
        RunDecisionCases(run);
        RunStyleCases(run);
        RunKeyCases(run);

        return run.Report("land plan");
    }

    private static void RunPlannerCases(TestRun run)
    {
        run.Case("both land layers are planned as land plans, land use first, before the hazards", () =>
        {
            List<ImportStepKind> kinds = [.. Plan(Manifest()).Steps.Select(step => step.Kind)];

            int landUse = kinds.IndexOf(ImportStepKind.LandUse);
            int landCover = kinds.IndexOf(ImportStepKind.LandCover);
            run.True(landUse >= 0 && landCover >= 0, "both planned");
            run.True(landUse < landCover, "land use first, as the checklist lists them");
            run.True(landCover < kinds.IndexOf(ImportStepKind.FloodZones), "before the hazard plan");
        });

        run.Case("a land plan step carries the build and the published rectangle as its crop", () =>
        {
            foreach (ImportStepKind kind in (ImportStepKind[])[ImportStepKind.LandUse, ImportStepKind.LandCover])
            {
                ImportStep step = Find(Plan(Manifest()), kind)!;
                run.True(step.LandPlan is not null, $"{kind} has land plan facts");
                run.Equal(step.LandPlan!.Build, Build, $"{kind}: the job's token");
                run.True(step.LandPlan.Crop is { IsMeasurable: true }, $"{kind}: a crop");

                // The drape is 600 x 300 m about the origin, offset north by 50 m.
                FootprintExtent crop = step.LandPlan.Crop!.Value;
                run.Within(crop.MinEastM, -300.0, 1e-6, $"{kind}: west edge, local metres");
                run.Within(crop.MaxEastM, 300.0, 1e-6, $"{kind}: east edge");
                run.Within(crop.MinNorthM, -100.0, 1e-6, $"{kind}: south edge");
                run.Within(crop.MaxNorthM, 200.0, 1e-6, $"{kind}: north edge");
            }
        });

        run.Case("the land plans and the hazard plan are cropped to the same published rectangle", () =>
        {
            BundleImportPlan plan = Plan(Manifest());
            run.Equal(
                Find(plan, ImportStepKind.LandCover)!.LandPlan!.Crop?.ToString(),
                Find(plan, ImportStepKind.FloodZones)!.Hazard!.Crop?.ToString(),
                "one crop for every plan of the build");
        });

        run.Case("with no rectangle published, a land plan is still drawn, uncropped", () =>
        {
            ImportStep step = Find(Plan(Manifest(drape: false)), ImportStepKind.LandUse)!;
            run.True(step.LandPlan!.Crop is null, "no crop");
        });

        run.Case("neither land layer cuts the terrain: no step kind is a land subdivision", () =>
        {
            foreach (ImportStepKind kind in Enum.GetValues<ImportStepKind>())
            {
                run.True(
                    GroundCuts.LayerOf(kind) is not (GroundLayer.LandUse or GroundLayer.LandCover),
                    $"{kind} cuts no land subdivision");
            }

            run.True(GroundCuts.LayerOf(ImportStepKind.Water) == GroundLayer.Water, "water still cuts");
            run.True(GroundCuts.LayerOf(ImportStepKind.RoadPolygons) == GroundLayer.RoadSurface, "road surfaces still cut");
        });

        run.Case("a land plan step imports content and its file is done with at the commit", () =>
        {
            foreach (ImportStepKind kind in (ImportStepKind[])[ImportStepKind.LandUse, ImportStepKind.LandCover])
            {
                run.True(ImportStepKinds.ImportsContent(kind), $"{kind} is an import");
                run.Equal(ImportStepKinds.LifetimeOf(kind), ExtractionLifetime.Transient, $"{kind}'s file");
                run.False(Find(Plan(Manifest()), kind)!.DrapePlanned, $"{kind} is not draped");
            }
        });
    }

    private static void RunChecklistCases(TestRun run)
    {
        run.Case("each land layer is its own plan row, named on the glossary's land plan", () =>
        {
            run.Equal(ImportLayers.Of(ImportStepKind.LandUse) ?? ImportLayer.Terrain, ImportLayer.LandUsePlan, "land use's row");
            run.Equal(ImportLayers.Of(ImportStepKind.LandCover) ?? ImportLayer.Terrain, ImportLayer.LandCoverPlan, "land cover's row");
            run.Equal(WindowLabels.LayerName(ImportLayer.LandUsePlan), "Land Use Plan", "land use's name");
            run.Equal(WindowLabels.LayerName(ImportLayer.LandCoverPlan), "Land Cover Plan", "land cover's name");
            run.Equal(WindowLabels.StepName(ImportStepKind.LandCover), "Land Cover Plan", "the step reads the same");
        });

        run.Case("both rows need no terrain, start ticked like the hazard plan's, and warn of nothing", () =>
        {
            foreach (ImportLayer layer in (ImportLayer[])[ImportLayer.LandUsePlan, ImportLayer.LandCoverPlan])
            {
                run.True(ImportLayers.PrerequisiteOf(layer) is null, $"{layer} does not need the terrain");
                run.True(ImportLayers.OnByDefault(layer), $"{layer} starts ticked");
                run.False(SlowStepNotice.IsSlowBox(layer), $"{layer} is not a slow box");
            }

            ImportChecklist checklist = ImportChecklist.For(Plan(Manifest()), "2027");
            checklist.Set(ImportLayer.LandUsePlan, true);
            checklist.Set(ImportLayer.LandCoverPlan, true);
            checklist.Set(ImportLayer.Terrain, false);
            run.True(checklist.IsChecked(ImportLayer.LandUsePlan), "still ticked without the terrain");
            run.Equal(checklist.SlowLayerWarnings.Count, 0, "no warning below the rows");
        });

        run.Case("both boxes ticked plans two land plan steps and no land subdivision", () =>
        {
            BundleManifest manifest = Parse(Manifest());
            BundleImportPlan all = BundleImportPlanner.Plan(manifest, Entries, _ => new ImageSize(4000, 3000));
            ImportChecklist checklist = ImportChecklist.For(all);
            checklist.Set(ImportLayer.LandUsePlan, true);
            checklist.Set(ImportLayer.LandCoverPlan, true);

            BundleImportPlan chosen = BundleImportPlanner.Plan(manifest, Entries, _ => new ImageSize(4000, 3000), checklist.Choice);
            run.Equal(chosen.Steps.Count(step => step.LandPlan is not null), 2, "two land plans");
            run.Equal(
                string.Join(",", chosen.Steps.Select(step => GroundCuts.LayerOf(step.Kind)).OfType<GroundLayer>()),
                "Water",
                "the terrain is cut only for water here");
        });
    }

    private static void RunIdentityCases(TestRun run)
    {
        run.Case("each plan is named for its layer and its build, in a name Revit accepts", () =>
        {
            string landUse = LandPlan.ViewName(LandLayer.LandUse, Build);
            string landCover = LandPlan.ViewName(LandLayer.LandCover, Build);

            run.Equal(landUse, "Mantle Place Land Use Plan 0d4cbfb1", "land use");
            run.Equal(landCover, "Mantle Place Land Cover Plan 0d4cbfb1", "land cover");
            run.True(landUse != HazardPlan.ViewName(Build), "not the hazard plan");
            foreach (string name in (string[])[landUse, landCover])
            {
                run.True(name.IndexOfAny(['{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~', ':', '\\']) < 0, $"\"{name}\" is legal");
            }
        });

        run.Case("the build is the hazard plan's token, so one build's plans share it", () =>
        {
            run.Equal(LandPlan.BuildToken(JobId), HazardPlan.BuildToken(JobId), "the same token");
        });

        run.Case("a layer's stamp and its key's are the site-context filter's, name the order and build, and differ", () =>
        {
            string[] stamps =
            [
                LandPlan.Stamp(LandLayer.LandUse, Stem, Build),
                LandPlan.Stamp(LandLayer.LandCover, Stem, Build),
                LandPlan.KeyStamp(LandLayer.LandUse, Stem, Build),
                LandPlan.KeyStamp(LandLayer.LandCover, Stem, Build),
            ];

            foreach (string stamp in stamps)
            {
                run.True(stamp.StartsWith(SiteContext.StampPrefix, StringComparison.Ordinal), $"\"{stamp}\" is found by the filter");
                run.Contains(stamp, Stem + "/" + Build, "order and build");
            }

            run.Equal(stamps.Distinct(StringComparer.Ordinal).Count(), 4, "four different stamps");
        });

        run.Case("a region's stamp is never mistaken for a subdivision an earlier build cut", () =>
        {
            // Land cover subdivisions were stamped "Mantle Place Land Cover {stem}/{feature}", and the
            // drape still recognises them on a model imported before ADR 0015.
            foreach (LandLayer layer in Enum.GetValues<LandLayer>())
            {
                run.True(SiteBoundaryIdentity.Parse(LandPlan.Stamp(layer, Stem, Build), Stem) is null, $"{layer}'s region stamp");
                run.True(SiteBoundaryIdentity.Parse(LandPlan.KeyStamp(layer, Stem, Build), Stem) is null, $"{layer}'s key stamp");
            }
        });
    }

    private static void RunDecisionCases(TestRun run)
    {
        run.Case("a first import makes the plan and draws the layer", () =>
        {
            PlanDecision decision = LandPlan.Decide(PlanViewFound.None, [], LandLayer.LandUse, Stem, Build);
            run.True(decision.CreateView, "makes the plan");
            run.True(decision.Draw, "draws");
        });

        run.Case("a re-import of the same build leaves a layer already on its plan alone, and says so", () =>
        {
            string stamp = LandPlan.Stamp(LandLayer.LandCover, Stem, Build);
            PlanDecision decision = LandPlan.Decide(
                PlanViewFound.PlanView, [stamp, stamp, stamp, "a curator's note"], LandLayer.LandCover, Stem, Build);

            run.False(decision.Draw, "nothing drawn over it");
            run.False(decision.CreateView, "no second plan");
            run.Equal(decision.AlreadyDrawn, 3, "the regions there are counted");
            run.Contains(decision.Explanation, LandPlan.ViewName(LandLayer.LandCover, Build), "names the plan");
            run.Contains(decision.Explanation, "land cover is already", "in the layer's words");
        });

        run.Case("a later build gets plans of its own: another build's regions are not this one's", () =>
        {
            string earlier = LandPlan.Stamp(LandLayer.LandUse, Stem, "11111111");
            run.True(LandPlan.ViewName(LandLayer.LandUse, "11111111") != LandPlan.ViewName(LandLayer.LandUse, Build), "another name");

            PlanDecision decision = LandPlan.Decide(PlanViewFound.PlanView, [earlier], LandLayer.LandUse, Stem, Build);
            run.True(decision.Draw, "drawn: that region belongs to the earlier build's plan");
        });

        run.Case("the key's swatches do not count as the layer being drawn", () =>
        {
            PlanDecision decision = LandPlan.Decide(
                PlanViewFound.PlanView, [LandPlan.KeyStamp(LandLayer.LandUse, Stem, Build)], LandLayer.LandUse, Stem, Build);
            run.True(decision.Draw, "a key alone is not the layer");
        });

        run.Case("a view of another kind holding the name draws nothing and says so", () =>
        {
            PlanDecision decision = LandPlan.Decide(PlanViewFound.SomethingElse, [], LandLayer.LandUse, Stem, Build);
            run.False(decision.Draw, "nothing drawn");
            run.False(decision.CreateView, "no second view: Revit refuses the name");
            run.Contains(decision.Explanation, LandPlan.ViewName(LandLayer.LandUse, Build), "names the view");
        });

        run.Case("each published polygon is one region with its holes, and a stranded hole is counted", () =>
        {
            GroundCutPlan plan = LandPlan.Regions([Polygon(1, "forest"), Polygon(1, "forest", hole: true), Polygon(2, "grass"), Polygon(3, "grass", hole: true)]);
            run.Equal(plan.Cuts.Count, 2, "two regions");
            run.Equal(plan.Cuts[0].Holes.Count, 1, "the first keeps its hole");
            run.Equal(plan.StrandedHoles, 1, "a hole with no outer ring is counted, never attached to a neighbour");
        });
    }

    private static void RunStyleCases(TestRun run)
    {
        run.Case("a known class gets its own colour, in a type named for the layer and the class word", () =>
        {
            RegionStyle forest = LandStyles.For(LandLayer.LandCover, "forest");
            RegionStyle grass = LandStyles.For(LandLayer.LandCover, "grass");

            run.Equal(forest.TypeName, "Mantle Place Land Cover forest", "named with the published word");
            run.True(forest.Fill is { } fill && fill != LandStyles.Neutral, "a colour of its own, not the neutral one");
            run.True(forest.Fill != grass.Fill, "forest and grass are told apart");
            run.True(forest.Hatch is null, "a fill, no hatch");
            run.True(LandStyles.Knows(LandLayer.LandCover, "forest"), "the table knows it");
        });

        run.Case("a class this table does not know is drawn in a neutral type named with the class, never dropped", () =>
        {
            RegionStyle style = LandStyles.For(LandLayer.LandUse, "spaceport");
            run.Equal(style.Fill?.ToString(), LandStyles.Neutral.ToString(), "neutral");
            run.Equal(style.TypeName, "Mantle Place Land Use spaceport", "named with the class");
            run.False(LandStyles.Knows(LandLayer.LandUse, "spaceport"), "unknown");
        });

        run.Case("the class is matched as published, not searched", () =>
        {
            run.Equal(LandStyles.For(LandLayer.LandCover, "Forest").Fill?.ToString(), LandStyles.Neutral.ToString(), "Forest is not forest");
            run.Equal(LandStyles.For(LandLayer.LandCover, "Forest").TypeName, "Mantle Place Land Cover Forest", "and keeps its own name");
        });

        run.Case("one word in both layers is two types, so recolouring one leaves the other", () =>
        {
            run.True(
                LandStyles.For(LandLayer.LandUse, "grass").TypeName != LandStyles.For(LandLayer.LandCover, "grass").TypeName,
                "land use grass and land cover grass");
        });

        run.Case("a polygon publishing no class is drawn neutral and named as such", () =>
        {
            RegionStyle style = LandStyles.For(LandLayer.LandCover, string.Empty);
            run.Equal(style.Fill?.ToString(), LandStyles.Neutral.ToString(), "neutral");
            run.Equal(style.TypeName, "Mantle Place Land Cover (no class published)", "said");
        });

        run.Case("a type name is kept legal; the key keeps the published word", () =>
        {
            RegionStyle style = LandStyles.For(LandLayer.LandUse, "a:b{c}");
            run.True(style.TypeName.IndexOfAny(['{', '}', ':']) < 0, $"\"{style.TypeName}\" is legal");
            run.Equal(LandKey.Rows(LandLayer.LandUse, [Polygon(1, "a:b{c}")])[0].Text, "a:b{c}", "the key quotes it");
        });

        run.Case("every class the bundles on hand publish is in the table", () =>
        {
            // The two reference orders (Jackson, Savannah) and the platform's documented values.
            foreach (string word in (string[])["forest", "barren", "shrub", "urban", "grass", "crop", "wetland", "moss", "snow", "mangrove"])
            {
                run.True(LandStyles.Knows(LandLayer.LandCover, word), $"land cover {word}");
            }

            foreach (string word in (string[])["residential", "recreation", "park", "developed", "winter_sports", "cemetery", "medical", "horticulture", "managed", "protected"])
            {
                run.True(LandStyles.Knows(LandLayer.LandUse, word), $"land use {word}");
            }
        });
    }

    private static void RunKeyCases(TestRun run)
    {
        run.Case("the key lists each class drawn once, in a stable order, and a hole claims nothing", () =>
        {
            IReadOnlyList<KeyRow> rows = LandKey.Rows(
                LandLayer.LandCover,
                [Polygon(1, "shrub"), Polygon(2, "forest"), Polygon(3, "shrub"), Polygon(4, "barren", hole: true)]);

            run.Equal(string.Join(" | ", rows.Select(row => row.Text)), "forest | shrub", "each once, ordinal order");
            run.Equal(rows[0].Style?.TypeName, LandStyles.For(LandLayer.LandCover, "forest").TypeName, "each row's swatch is its class's type");
        });

        run.Case("a land use row quotes the classes drawn under its subtype", () =>
        {
            IReadOnlyList<KeyRow> rows = LandKey.Rows(
                LandLayer.LandUse,
                [
                    Polygon(1, "recreation", classification: "playground"),
                    Polygon(2, "recreation", classification: "pitch"),
                    Polygon(3, "residential", classification: "residential"),
                    Polygon(4, "recreation", classification: "pitch"),
                    Polygon(5, "park", classification: string.Empty),
                ]);

            run.Equal(
                string.Join(" | ", rows.Select(row => row.Text)),
                "park | recreation — pitch, playground | residential",
                "classes after a dash, each once; one that only repeats the subtype is not repeated");
        });

        run.Case("the heading says the classes are as published", () =>
        {
            run.Contains(string.Join("\n", LandKey.Heading(LandLayer.LandUse)), "Land use", "land use");
            run.Contains(string.Join("\n", LandKey.Heading(LandLayer.LandCover)), "as published", "as published");
        });

        run.Case("an empty layer has no rows", () =>
        {
            run.Equal(LandKey.Rows(LandLayer.LandUse, []).Count, 0, "none");
        });
    }

    // ---- fixtures -------------------------------------------------------------------------------

    private static SiteFeature Polygon(int ordinal, string subtype, bool hole = false, string classification = "") => new()
    {
        Vertices = [new(0, 0, null), new(1, 0, null), new(1, 1, null)],
        IsClosed = true,
        IsHole = hole,
        PolygonOrdinal = ordinal,
        Subtype = subtype,
        Classification = classification,
    };

    private static string OwnPath(string name) => $"Site/Vector/{name}.geojson";

    private static readonly string[] Layers = ["land_use", "land_cover", "water", "flood_zones"];

    private static readonly string[] Entries =
    [
        "Metadata/manifest.json",
        "Imagery/Drape.StatePlane.png",
        .. Layers.Select(OwnPath),
    ];

    /// <summary>
    /// A 1.4.0 manifest on a metre UTM delivery whose Revit block carries both land layers, the water
    /// and the flood zones, and a drape 600 × 300 m about the origin, offset north by 50 m.
    /// </summary>
    private static string Manifest(bool drape = true)
    {
        IEnumerable<string> layers = Layers.Select(name =>
            $$"""
            { "name": "{{name}}", "path": "{{OwnPath(name)}}", "horizontal_frame": "absolute_projected",
              "units": "m", "feature_count": 1, "sha256": "{{OwnSha}}" }
            """);

        string drapeBlock = drape
            ? """
              , "drape": {
                "path": "Imagery/Drape.StatePlane.png", "format": "png-rgb-8bit",
                "extent": [471295.0, 4256950.0, 471895.0, 4257250.0], "extent_crs": "EPSG:32613", "units": "m",
                "width": 4000, "height": 3000,
                "sha256": "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd" }
              """
            : string.Empty;

        return $$"""
            {
              "version": "1.4.0",
              "job_id": "{{JobId}}",
              "hosts": { "revit": {
                "georeference": {
                  "crs_projected": "EPSG:32613",
                  "origin": { "lon": -105.3, "lat": 38.4,
                    "projected": { "epsg": 32613, "easting": 471595.0, "northing": 4257050.0, "linear_unit": "m" } } },
                "file_frame": { "type": "projected", "crs": "EPSG:32613", "horizontal_unit": "m", "vertical_unit": "m" },
                "vectors": { "format": "geojson", "layers": [ {{string.Join(", ", layers)}} ] },
                "readiness": { "toposurface_points": { "present": false, "reason": "not_produced" },
                  "vectors": { "present": true }, "flood_zones": { "present": true },
                  "steep_slope": { "present": false, "reason": "no_features_in_aoi" } }{{drapeBlock}} } }
            }
            """;
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

    private static BundleImportPlan Plan(string manifestJson)
        => BundleImportPlanner.Plan(Parse(manifestJson), Entries, _ => new ImageSize(4000, 3000));

    private static ImportStep? Find(BundleImportPlan plan, ImportStepKind kind)
        => plan.Steps.FirstOrDefault(step => step.Kind == kind);
}
