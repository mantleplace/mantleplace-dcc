using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Flood zones and steep ground, drawn as context on a hazard plan: what the reader takes from the
/// manifest, what the planner makes of it, and the plan's identity, styles and zone key.
/// </summary>
/// <remarks>
/// MPB 1.4.0 hands this host its own copy of both hazard layers in <c>hosts.revit.vectors</c>, beside
/// the GeoPackages every earlier bundle carried and this host could not read. The decisions these
/// cases pin are the ones settled for issues 159 and 239: a 2D plan per build, never the terrain; the
/// published wording verbatim; context, never a determination.
/// </remarks>
internal static class HazardPlanTests
{
    private const string Stem = "order-7f3a";
    private const string JobId = "0d4cbfb1-9c14-4e47-aa0e-471cf6dae969";
    private const string OwnSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    internal static int Run()
    {
        TestRun run = new();

        RunReaderCases(run);
        RunPlannerCases(run);
        RunChecklistCases(run);
        RunIdentityCases(run);
        RunStyleCases(run);
        RunKeyCases(run);
        RunLayoutCases(run);

        return run.Report("hazard plan");
    }

    private static void RunReaderCases(TestRun run)
    {
        run.Case("both hazard layers are read from the Revit block's own copy, and only from there", () =>
        {
            BundleManifest manifest = Parse(Manifest());

            run.Equal(manifest.FloodZones?.Path, OwnPath("flood_zones"), "flood zones");
            run.Equal(manifest.SteepGround?.Path, OwnPath("steep_slope"), "steep ground");
            run.True(manifest.FloodZones?.FromOwnBlock == true, "flood zones are the own copy");
            run.Equal(manifest.FloodZones?.Sha256, OwnSha, "its hash is the layer's");
            run.Equal(manifest.LandUse?.Path, OwnPath("land_use"), "the other layers are unaffected");
        });

        run.Case("a shared layer of the same name is never read as a hazard layer", () =>
        {
            // The shared set is lon/lat, and flood zones are not in it by the schema's own words; a
            // bundle that put one there anyway must not be placed as though it were this host's.
            BundleManifest manifest = Parse(Manifest(ownLayers: ["land_use"], sharedFlood: true));
            run.True(manifest.FloodZones is null, "no own copy, no flood zones");
        });

        run.Case("the hazard readiness verdicts are read beside the others", () =>
        {
            BundleManifest manifest = Parse(Manifest(
                ownLayers: ["land_use"],
                floodReadiness: "{ \"present\": false, \"reason\": \"outside_coverage\" }",
                steepReadiness: "{ \"present\": false, \"reason\": \"available_on_request\" }"));

            run.True(manifest.Readiness.FloodZones.Declared, "flood declared");
            run.False(manifest.Readiness.FloodZones.Present, "flood absent");
            run.Equal(manifest.Readiness.FloodZones.Reason, "outside_coverage", "flood's reason, verbatim");
            run.Equal(manifest.Readiness.SteepGround.Reason, "available_on_request", "steep's reason, verbatim");
        });

        run.Case("the flood map's provenance is carried verbatim, a panel with no date included", () =>
        {
            FloodMap? map = Parse(Manifest()).FloodMap;

            run.True(map is not null, "the flood block is read");
            run.Equal(string.Join(",", map!.Zones), "AE,X", "zones, in the published order");
            run.Equal(string.Join(",", map.DfirmIds), "28049C", "the study ids");
            run.Equal(map.Panels.Count, 2, "both panels");
            run.Equal(map.Panels[0].Panel, "28049C0311J", "the panel number");
            run.Equal(map.Panels[0].EffectiveDate, "2024-03-01", "its date, as published");
            run.True(map.Panels[1].EffectiveDate is null, "a panel FEMA dates nowhere keeps no date");
            run.Equal(map.Source, "fema-nfhl", "the source");
        });

        run.Case("the steep-ground threshold is kept as the manifest wrote the number", () =>
        {
            // 35.0 printed as a double is "35". The key shows the manifest's own number.
            run.Equal(Parse(Manifest()).SteepGroundThreshold, "35.0", "the raw token");
        });

        run.Case("a hazard feature carries its zone, subtype and threshold as published", () =>
        {
            string? error = SiteVectorReader.TryParse(
                """
                { "type": "FeatureCollection", "features": [
                  { "type": "Feature",
                    "properties": { "fld_zone": "AE", "zone_subty": "FLOODWAY", "sfha": 1, "static_bfe_ft": null, "threshold_deg": 35.0 },
                    "geometry": { "type": "Polygon", "coordinates": [[ [0,0], [10,0], [10,10], [0,0] ]] } }
                ] }
                """,
                LocalFrame,
                new LayerFrame(LayerCoordinates.LocalOffsets, LinearUnit.Metre),
                SiteGeometryKinds.Areas,
                "flood zones",
                out IReadOnlyList<SiteFeature> features);

            run.True(error is null, $"parsed: {error}");
            run.Equal(features[0].FloodZone, "AE", "fld_zone");
            run.Equal(features[0].FloodZoneSubtype, "FLOODWAY", "zone_subty");
            run.Equal(features[0].Threshold, "35.0", "threshold_deg, as written");
        });
    }

    private static void RunPlannerCases(TestRun run)
    {
        run.Case("both layers are planned, after the trees and before the attribution, flood first", () =>
        {
            BundleImportPlan plan = Plan(Manifest());
            List<ImportStepKind> kinds = [.. plan.Steps.Select(step => step.Kind)];

            int flood = kinds.IndexOf(ImportStepKind.FloodZones);
            int steep = kinds.IndexOf(ImportStepKind.SteepGround);
            run.True(flood >= 0 && steep >= 0, "both planned");
            run.True(flood < steep, "flood before steep, so steep ground is drawn over it");
            run.True(kinds.IndexOf(ImportStepKind.AttributionAndProvenance) > steep, "before the attribution");
        });

        run.Case("a hazard step carries the build, the drape's rectangle as its crop, and the published facts", () =>
        {
            ImportStep flood = Find(Plan(Manifest()), ImportStepKind.FloodZones)!;

            run.True(flood.Hazard is not null, "hazard facts");
            run.Equal(flood.Hazard!.Build, "0d4cbfb1", "the job's token");
            run.True(flood.Hazard.Crop is { } crop && crop.IsMeasurable, "a crop");
            run.Within(flood.Hazard.Crop!.Value.MinEastM, -300.0, 1e-6, "west edge, local metres");
            run.Within(flood.Hazard.Crop!.Value.MaxNorthM, 200.0, 1e-6, "north edge, local metres");
            run.Equal(flood.Hazard.FloodMap?.Zones.Count ?? 0, 2, "the flood map rides along");
            run.Equal(Find(Plan(Manifest()), ImportStepKind.SteepGround)!.Hazard!.Threshold, "35.0", "the threshold rides along");
        });

        run.Case("with no drape to crop to, the plan is still drawn, uncropped", () =>
        {
            ImportStep flood = Find(Plan(Manifest(drape: false)), ImportStepKind.FloodZones)!;
            run.True(flood.Hazard!.Crop is null, "no crop");
        });

        run.Case("a hazard layer is placed only in this host's frame, like every own-block layer", () =>
        {
            // A foot layer in a metre frame: the file contradicts the frame its block declares.
            SkippedImport? skip = Skip(Plan(Manifest(floodUnits: "ft")), ImportStepKind.FloodZones);
            run.Equal(skip?.ReasonCode ?? SkipReasonCode.LeftOutByChoice, SkipReasonCode.CoordinateSystemNotSupported, "refused");
        });

        run.Case("an area outside the flood map's coverage is said and not listed as unavailable", () =>
        {
            SkippedImport? skip = Skip(
                Plan(Manifest(ownLayers: ["land_use"], floodReadiness: "{ \"present\": false, \"reason\": \"outside_coverage\" }")),
                ImportStepKind.FloodZones);

            run.Equal(skip?.ReasonCode ?? SkipReasonCode.LeftOutByChoice, SkipReasonCode.DeclaredAbsent, "declared absent");
            run.Contains(skip?.Reason, "outside the source dataset's coverage", "the bundle's own reason, in words");
            run.True(WindowLabels.UnavailableReason(skip!.ReasonCode, 1) is null, "the window says nothing: no vault can change it");
        });

        run.Case("no flood features in the area is the same: said, not listed", () =>
        {
            SkippedImport? skip = Skip(
                Plan(Manifest(ownLayers: ["land_use"], floodReadiness: "{ \"present\": false, \"reason\": \"no_features_in_aoi\" }")),
                ImportStepKind.FloodZones);
            run.Equal(skip?.ReasonCode ?? SkipReasonCode.LeftOutByChoice, SkipReasonCode.DeclaredAbsent, "declared absent");
        });

        run.Case("hazard layers not yet picked in the vault are listed, with the vault's remedy", () =>
        {
            SkippedImport? skip = Skip(
                Plan(Manifest(ownLayers: ["land_use"], steepReadiness: "{ \"present\": false, \"reason\": \"available_on_request\" }")),
                ImportStepKind.SteepGround);

            run.Equal(skip?.ReasonCode ?? SkipReasonCode.LeftOutByChoice, SkipReasonCode.ArtifactNotInManifest, "not in the bundle yet");
            run.Contains(skip?.Reason, "pick it in your vault", "the bundle's own remedy");
        });

        run.Case("a bundle from before 1.4.0 carrying only the GeoPackage is listed as built too early", () =>
        {
            SkippedImport? skip = Skip(Plan(Manifest(ownLayers: ["land_use"], hazardReadiness: false)), ImportStepKind.FloodZones);

            run.Equal(skip?.ReasonCode ?? SkipReasonCode.LeftOutByChoice, SkipReasonCode.PredatesHostCopy, "predates Revit's copy");
            run.Contains(
                WindowLabels.UnavailableReason(skip!.ReasonCode, 1),
                "built before Revit could receive this",
                "the standard's sentence for it");
        });

        run.Case("a bundle from before 1.4.0 with no flood block at all is silent about flood zones", () =>
        {
            SkippedImport? skip = Skip(
                Plan(Manifest(ownLayers: ["land_use"], hazardReadiness: false, floodBlock: false, steepBlock: false)),
                ImportStepKind.FloodZones);

            run.Equal(skip?.ReasonCode ?? SkipReasonCode.LeftOutByChoice, SkipReasonCode.DeclaredAbsent, "nothing to fetch");
            run.True(WindowLabels.UnavailableReason(skip!.ReasonCode, 1) is null, "not listed");
        });

        run.Case("a hazard step imports content and its file is done with at the commit", () =>
        {
            foreach (ImportStepKind kind in (ImportStepKind[])[ImportStepKind.FloodZones, ImportStepKind.SteepGround])
            {
                run.True(ImportStepKinds.ImportsContent(kind), $"{kind} is an import");
                run.Equal(ImportStepKinds.LifetimeOf(kind), ExtractionLifetime.Transient, $"{kind}'s file");
            }
        });
    }

    private static void RunChecklistCases(TestRun run)
    {
        run.Case("each hazard layer is its own row, named with the glossary's words", () =>
        {
            run.Equal(ImportLayers.Of(ImportStepKind.FloodZones) ?? ImportLayer.Terrain, ImportLayer.FloodZones, "flood's row");
            run.Equal(ImportLayers.Of(ImportStepKind.SteepGround) ?? ImportLayer.Terrain, ImportLayer.SteepGround, "steep's row");
            run.Equal(WindowLabels.LayerName(ImportLayer.FloodZones), "Flood Zones", "flood's name");
            run.Equal(WindowLabels.LayerName(ImportLayer.SteepGround), "Steep Ground", "steep's name");
            run.Equal(WindowLabels.StepName(ImportStepKind.SteepGround), "Steep Ground", "the step reads the same");
        });

        run.Case("both rows start unticked and need nothing: the visualiser's model is left alone", () =>
        {
            foreach (ImportLayer layer in (ImportLayer[])[ImportLayer.FloodZones, ImportLayer.SteepGround])
            {
                run.False(ImportLayers.OnByDefault(layer), $"{layer} starts unticked");
                run.True(ImportLayers.PrerequisiteOf(layer) is null, $"{layer} does not need the terrain");
            }

            ImportChecklist checklist = ImportChecklist.For(Plan(Manifest()));
            run.True(checklist.Layers.Contains(ImportLayer.FloodZones), "offered");
            run.False(checklist.IsChecked(ImportLayer.FloodZones), "not checked");
            checklist.Set(ImportLayer.Terrain, false);
            run.True(checklist.IsEnabled(ImportLayer.SteepGround), "still available without the terrain");
        });

        run.Case("an unattended import brings them in; an unticked one leaves them out by choice", () =>
        {
            BundleManifest manifest = Parse(Manifest());
            BundleImportPlan all = BundleImportPlanner.Plan(manifest, Entries, _ => new ImageSize(4000, 3000));
            run.True(Find(all, ImportStepKind.FloodZones) is not null, "All includes flood zones");

            BundleImportPlan chosen = BundleImportPlanner.Plan(
                manifest,
                Entries,
                _ => new ImageSize(4000, 3000),
                ImportChecklist.For(all).Choice);
            run.True(Find(chosen, ImportStepKind.FloodZones) is null, "the default choice leaves them out");
            run.Equal(
                Skip(chosen, ImportStepKind.SteepGround)?.ReasonCode ?? SkipReasonCode.DeclaredAbsent,
                SkipReasonCode.LeftOutByChoice,
                "said as a choice");
        });
    }

    private static void RunIdentityCases(TestRun run)
    {
        run.Case("the plan is named for its build, in a name Revit accepts", () =>
        {
            string name = HazardPlan.ViewName("0d4cbfb1");
            run.Equal(name, "Mantle Place Hazard Plan 0d4cbfb1", "the name");
            run.True(name.IndexOfAny(['{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~', ':', '\\']) < 0, "legal");
        });

        run.Case("the build is the job's leading token; a missing job id is said, not invented", () =>
        {
            run.Equal(HazardPlan.BuildToken(JobId), "0d4cbfb1", "a uuid's first group");
            run.Equal(HazardPlan.BuildToken("job:42/b"), "job-42-b", "characters a name refuses are replaced");
            run.Equal(HazardPlan.BuildToken(string.Empty), "unidentified", "no job id");
        });

        run.Case("each hazard layer's stamp is the site-context filter's, and names its order and build", () =>
        {
            string flood = HazardPlan.Stamp(HazardLayer.FloodZones, Stem, "0d4cbfb1");
            string steep = HazardPlan.Stamp(HazardLayer.SteepGround, Stem, "0d4cbfb1");
            string key = HazardPlan.KeyStamp(Stem, "0d4cbfb1");

            foreach (string stamp in (string[])[flood, steep, key])
            {
                run.True(stamp.StartsWith(SiteContext.StampPrefix, StringComparison.Ordinal), $"\"{stamp}\" is found by the filter");
                run.Contains(stamp, Stem + "/0d4cbfb1", "order and build");
            }

            run.True(flood != steep && steep != key && flood != key, "three different stamps");
        });

        run.Case("a first import makes the plan and draws the layer", () =>
        {
            HazardPlanDecision decision = HazardPlan.Decide(HazardViewFound.None, [], HazardLayer.FloodZones, Stem, "0d4cbfb1");
            run.True(decision.CreateView, "makes the plan");
            run.True(decision.Draw, "draws");
        });

        run.Case("a re-import adds a layer the plan does not hold yet, and never a second of one it does", () =>
        {
            string[] onPlan = [HazardPlan.Stamp(HazardLayer.FloodZones, Stem, "0d4cbfb1"), HazardPlan.Stamp(HazardLayer.FloodZones, Stem, "0d4cbfb1"), "a curator's note"];

            HazardPlanDecision steep = HazardPlan.Decide(HazardViewFound.PlanView, onPlan, HazardLayer.SteepGround, Stem, "0d4cbfb1");
            run.False(steep.CreateView, "the plan is there");
            run.True(steep.Draw, "steep ground is added");

            HazardPlanDecision flood = HazardPlan.Decide(HazardViewFound.PlanView, onPlan, HazardLayer.FloodZones, Stem, "0d4cbfb1");
            run.False(flood.Draw, "flood zones are not drawn twice");
            run.Equal(flood.AlreadyDrawn, 2, "and the ones there are counted");
            run.Contains(flood.Explanation, "already", "said");
        });

        run.Case("another build's regions do not count as this build's", () =>
        {
            string[] onPlan = [HazardPlan.Stamp(HazardLayer.FloodZones, Stem, "11111111")];
            HazardPlanDecision decision = HazardPlan.Decide(HazardViewFound.PlanView, onPlan, HazardLayer.FloodZones, Stem, "0d4cbfb1");
            run.True(decision.Draw, "drawn: that region belongs to another plan");
        });

        run.Case("a view of another kind holding the name draws nothing and says so", () =>
        {
            HazardPlanDecision decision = HazardPlan.Decide(HazardViewFound.SomethingElse, [], HazardLayer.FloodZones, Stem, "0d4cbfb1");
            run.False(decision.Draw, "nothing drawn");
            run.False(decision.CreateView, "no second view: Revit refuses the name");
            run.Contains(decision.Explanation, HazardPlan.ViewName("0d4cbfb1"), "names the view");
        });
    }

    private static void RunStyleCases(TestRun run)
    {
        run.Case("a special flood hazard zone is filled, and a floodway is hatched over its zone's fill", () =>
        {
            HazardStyle ae = HazardStyles.ForFloodZone("AE", string.Empty);
            HazardStyle floodway = HazardStyles.ForFloodZone("AE", "FLOODWAY");

            run.True(ae.Fill is not null, "AE is filled");
            run.True(ae.Hatch is null, "and not hatched");
            run.Equal(floodway.Fill?.ToString(), ae.Fill?.ToString(), "the floodway keeps AE's colour");
            run.True(floodway.Hatch is not null, "and is hatched over it");
            run.True(floodway.TypeName != ae.TypeName, "its own type");
            run.True(HazardStyles.ForFloodZone("AE", "ADMINISTRATIVE FLOODWAY").Hatch is not null, "every floodway subtype is hatched");
        });

        run.Case("shaded X and minimal-hazard X are told apart; an unknown subtype takes its zone's colour", () =>
        {
            HazardStyle shaded = HazardStyles.ForFloodZone("X", "0.2 PCT ANNUAL CHANCE FLOOD HAZARD");
            HazardStyle minimal = HazardStyles.ForFloodZone("X", "AREA OF MINIMAL FLOOD HAZARD");
            HazardStyle unknown = HazardStyles.ForFloodZone("X", "SOMETHING FEMA ADDS LATER");

            run.True(shaded.Fill?.ToString() != minimal.Fill?.ToString(), "two colours");
            run.True(
                HazardStyles.ForFloodZone("X", "1 PCT FUTURE CONDITIONS").Fill?.ToString() != minimal.Fill?.ToString(),
                "a future-conditions 1 % zone does not read as minimal hazard");
            run.Equal(unknown.Fill?.ToString(), HazardStyles.ForFloodZone("X", string.Empty).Fill?.ToString(), "the zone's own colour");
            run.Contains(unknown.TypeName, "SOMETHING FEMA ADDS LATER", "named with the published words");
        });

        run.Case("a zone this table does not know is drawn in a neutral type named with its code, never dropped", () =>
        {
            HazardStyle style = HazardStyles.ForFloodZone("ZZ", string.Empty);
            run.True(style.Fill is not null, "drawn");
            run.Equal(style.Fill?.ToString(), HazardStyles.Neutral.ToString(), "neutral");
            run.Contains(style.TypeName, "ZZ", "named with the code");
        });

        run.Case("steep ground is a hatch with no fill, at an angle the floodway's is not", () =>
        {
            HazardStyle steep = HazardStyles.SteepGround;
            HazardStyle floodway = HazardStyles.ForFloodZone("AE", "FLOODWAY");

            run.True(steep.Fill is null, "no fill: the flood colour shows through");
            run.True(steep.Hatch is not null, "hatched");
            run.True(Math.Abs(steep.HatchAngleDeg - floodway.HatchAngleDeg) > 1.0, "a different angle");
            run.True(steep.Hatch?.ToString() != floodway.Hatch?.ToString(), "a different colour");
        });

        run.Case("a type name is kept legal; the zone key keeps the published words", () =>
        {
            HazardStyle style = HazardStyles.ForFloodZone("A:1", "{odd}");
            run.True(style.TypeName.IndexOfAny(['{', '}', ':']) < 0, $"\"{style.TypeName}\" is a legal name");
            run.Equal(ZoneKey.FloodRowText("A:1", "{odd}"), "A:1 — {odd}", "the key says what was published");
        });
    }

    private static void RunKeyCases(TestRun run)
    {
        run.Case("the key lists only the zones drawn, in the flood map's order, each once", () =>
        {
            SiteFeature[] drawn =
            [
                Ring("X", "AREA OF MINIMAL FLOOD HAZARD"),
                Ring("AE", "FLOODWAY"),
                Ring("AE", string.Empty),
                Ring("X", "AREA OF MINIMAL FLOOD HAZARD"),
                Ring("AE", "FLOODWAY", hole: true),
            ];
            FloodMap map = Parse(Manifest()).FloodMap!;

            IReadOnlyList<ZoneKeyRow> rows = ZoneKey.FloodRows(drawn, map);
            run.Equal(
                string.Join(" | ", rows.Select(row => row.Text)),
                "AE — FLOODWAY | AE | X — AREA OF MINIMAL FLOOD HAZARD",
                "AE before X, as zones lists them; a hole claims nothing");
        });

        run.Case("the flood heading says it is context and names the panels to verify against", () =>
        {
            string heading = string.Join("\n", ZoneKey.FloodHeading(Parse(Manifest()).FloodMap));
            run.Contains(heading, "not a flood determination", "the caveat");
            run.Contains(heading, "28049C0311J, effective 2024-03-01", "a dated panel");
            run.Contains(heading, "28049C0315J, no effective date published", "an undated one, said as such");
        });

        run.Case("with no panels published, the heading names the study ids and states less", () =>
        {
            FloodMap map = new() { Zones = ["AE"], DfirmIds = ["28049C"], Panels = [], Source = "fema-nfhl" };
            string heading = string.Join("\n", ZoneKey.FloodHeading(map));
            run.Contains(heading, "28049C", "the study id");
            run.False(heading.Contains("effective", StringComparison.Ordinal), "no guessed panel");
        });

        run.Case("steep ground's row is its published threshold, in degrees, claiming no comparison", () =>
        {
            IReadOnlyList<ZoneKeyRow> rows = ZoneKey.SteepRows([RingWithThreshold("35.0"), RingWithThreshold("35.0")], "35.0");
            run.Equal(rows.Count, 1, "one row");
            run.Equal(rows[0].Text, "Steep ground — threshold 35.0°", "the published number, verbatim");
        });

        run.Case("features stating different thresholds get a row each; one stating none takes the manifest's", () =>
        {
            IReadOnlyList<ZoneKeyRow> rows = ZoneKey.SteepRows([RingWithThreshold("35.0"), RingWithThreshold("30"), RingWithThreshold(string.Empty)], "35.0");
            run.Equal(string.Join(" | ", rows.Select(row => row.Text)), "Steep ground — threshold 35.0° | Steep ground — threshold 30°", "each as written");
        });

        run.Case("an empty layer has no rows", () =>
        {
            run.Equal(ZoneKey.FloodRows([], null).Count, 0, "flood");
            run.Equal(ZoneKey.SteepRows([], "35.0").Count, 0, "steep");
        });
    }

    private static void RunLayoutCases(TestRun run)
    {
        run.Case("the scale is the first standard one that fits the site on a sheet", () =>
        {
            run.Equal(HazardPlan.ScaleFor(new FootprintExtent(0, 0, 1000, 800)), 2000, "a kilometre at 1:2000");
            run.Equal(HazardPlan.ScaleFor(new FootprintExtent(0, 0, 50, 40)), 100, "a small site at 1:100");
            run.Equal(HazardPlan.ScaleFor(null), HazardPlan.DefaultScale, "nothing to fit");
        });

        run.Case("a first key starts beside the crop, level with its top", () =>
        {
            FootprintExtent crop = new(-300, -100, 300, 200);
            KeyAnchor anchor = HazardPlan.KeyAnchorFor(crop, drawn: null, existingKey: null, scale: 1000);
            run.True(anchor.EastM > 300, "east of the crop");
            run.Within(anchor.NorthM, 200, 1e-9, "level with its top");
        });

        run.Case("with no crop, the key starts beside what was drawn", () =>
        {
            KeyAnchor anchor = HazardPlan.KeyAnchorFor(null, new FootprintExtent(0, 0, 100, 50), existingKey: null, scale: 500);
            run.True(anchor.EastM > 100, "east of the drawing");
            run.Within(anchor.NorthM, 50, 1e-9, "level with its top");
        });

        run.Case("a re-import's rows continue below the key already there", () =>
        {
            FootprintExtent existing = new(400, 150, 420, 200);
            KeyAnchor anchor = HazardPlan.KeyAnchorFor(new FootprintExtent(-300, -100, 300, 200), null, existing, scale: 1000);
            run.Within(anchor.EastM, 400, 1e-9, "the same column");
            run.True(anchor.NorthM < 150, "below it");
        });

        run.Case("each published polygon is one region with its holes, and a stranded hole is counted", () =>
        {
            GroundCutPlan plan = HazardPlan.Regions(
            [
                Polygon(1, hole: false),
                Polygon(1, hole: true),
                Polygon(2, hole: false),
                Polygon(3, hole: true),
            ]);

            run.Equal(plan.Cuts.Count, 2, "two regions");
            run.Equal(plan.Cuts[0].Holes.Count, 1, "the first keeps its hole");
            run.Equal(plan.StrandedHoles, 1, "a hole with no outer ring is counted, not attached");
        });

        run.Case("the crop takes in the key's swatches and nothing else moves", () =>
        {
            FootprintExtent crop = new(-300, -100, 300, 200);
            FootprintExtent widened = HazardPlan.CropWithKey(crop, new FootprintExtent(315, 150, 325, 196));
            run.Within(widened.MaxEastM, 325, 1e-9, "east edge takes in the swatches");
            run.Within(widened.MinEastM, -300, 1e-9, "west edge as published");
            run.Within(widened.MaxNorthM, 200, 1e-9, "north edge as published");
            run.Within(HazardPlan.CropWithKey(crop, null).MaxEastM, 300, 1e-9, "no key, no change");
        });

        run.Case("rows step down by a fixed paper pitch, scaled into the model", () =>
        {
            IReadOnlyList<KeyRowPlacement> rows = HazardPlan.LayoutKey(new KeyAnchor(0, 0), rowCount: 3, scale: 1000);
            run.Equal(rows.Count, 3, "three rows");
            run.Within(rows[0].NorthM - rows[1].NorthM, HazardPlan.RowPitchMm, 1e-9, "one pitch apart at 1:1000 (mm on paper = m in the model)");
            run.True(rows[0].Swatch.IsMeasurable, "a swatch");
            run.True(rows[0].TextEastM > rows[0].Swatch.MaxEastM, "text right of its swatch");
        });
    }

    // ---- fixtures -------------------------------------------------------------------------------

    private static readonly SiteFrame LocalFrame = new()
    {
        Origin = new GeoOrigin { Epsg = 32613, Easting = 0.0, Northing = 0.0, LinearUnit = LinearUnit.Metre },
    };

    private static SiteFeature Ring(string zone, string subtype, bool hole = false) => new()
    {
        Vertices = [new(0, 0, null), new(1, 0, null), new(1, 1, null)],
        IsClosed = true,
        IsHole = hole,
        FloodZone = zone,
        FloodZoneSubtype = subtype,
    };

    private static SiteFeature Polygon(int ordinal, bool hole) => new()
    {
        Vertices = [new(0, 0, null), new(1, 0, null), new(1, 1, null)],
        IsClosed = true,
        IsHole = hole,
        PolygonOrdinal = ordinal,
    };

    private static SiteFeature RingWithThreshold(string threshold) => new()
    {
        Vertices = [new(0, 0, null), new(1, 0, null), new(1, 1, null)],
        IsClosed = true,
        Threshold = threshold,
    };

    private static string OwnPath(string name) => $"Site/Vector/{name}.geojson";

    private static readonly string[] Entries =
    [
        "Metadata/manifest.json",
        "Imagery/Drape.StatePlane.png",
        .. new[] { "land_use", "flood_zones", "steep_slope" }.Select(OwnPath),
    ];

    /// <summary>
    /// A 1.4.0 manifest on a metre UTM delivery whose Revit block carries the named own layers and a
    /// drape 600 × 300 m about the origin, offset north by 50 m.
    /// </summary>
    private static string Manifest(
        string[]? ownLayers = null,
        string floodUnits = "m",
        bool drape = true,
        bool sharedFlood = false,
        string? floodReadiness = "{ \"present\": true }",
        string? steepReadiness = "{ \"present\": true }",
        bool hazardReadiness = true,
        bool floodBlock = true,
        bool steepBlock = true)
    {
        ownLayers ??= ["land_use", "flood_zones", "steep_slope"];
        IEnumerable<string> layers = ownLayers.Select(name =>
            $$"""
            { "name": "{{name}}", "path": "{{OwnPath(name)}}", "horizontal_frame": "absolute_projected",
              "units": "{{(name == "flood_zones" ? floodUnits : "m")}}", "feature_count": 1, "sha256": "{{OwnSha}}" }
            """);

        string readiness = "\"toposurface_points\": { \"present\": false, \"reason\": \"not_produced\" }, \"vectors\": { \"present\": true }";
        if (hazardReadiness)
        {
            readiness += $", \"flood_zones\": {floodReadiness}, \"steep_slope\": {steepReadiness}";
        }

        string drapeBlock = drape
            ? """
              , "drape": {
                "path": "Imagery/Drape.StatePlane.png", "format": "png-rgb-8bit",
                "extent": [471295.0, 4256950.0, 471895.0, 4257250.0], "extent_crs": "EPSG:32613", "units": "m",
                "width": 4000, "height": 3000,
                "sha256": "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd" }
              """
            : string.Empty;

        string flood = floodBlock
            ? """
              , "flood": { "nfhl": {
                "path": "Flood/FloodZones.gpkg", "source": "fema-nfhl", "license": "public-domain",
                "feature_count": 65, "zones": ["AE", "X"], "dfirm_ids": ["28049C"],
                "panels": [
                  { "firm_pan": "28049C0311J", "dfirm_id": "28049C", "effective_date": "2024-03-01", "panel_type": "Countywide, Panel Printed" },
                  { "firm_pan": "28049C0315J", "dfirm_id": "28049C", "effective_date": null, "panel_type": null } ],
                "sha256": "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee" } }
              """
            : string.Empty;

        string steep = steepBlock
            ? ", \"elevation\": { \"steep_slope\": { \"path\": \"Elevation/SteepSlope.gpkg\", \"threshold_deg\": 35.0, \"feature_count\": 32 } }"
            : string.Empty;

        string shared = sharedFlood
            ? ", \"vector\": { \"layers\": [ { \"name\": \"flood_zones\", \"formats\": [{ \"format\": \"geojson\", \"path\": \"Vector/FloodZones.geojson\" }] } ] }"
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
                "readiness": { {{readiness}} }{{drapeBlock}} } }{{flood}}{{steep}}{{shared}}
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

    private static SkippedImport? Skip(BundleImportPlan plan, ImportStepKind kind)
        => plan.Skipped.FirstOrDefault(skip => skip.Kind == kind);
}
