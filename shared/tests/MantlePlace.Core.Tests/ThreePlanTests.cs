using System.Text.Json;
using System.Text.Json.Nodes;

namespace MantlePlace.Core.Tests;

using CorpusCase = ConformanceCorpus.CorpusCase;

internal static class ThreePlanTests
{
    private const string FullBundle = "bundle/v3-full.json";

    private const string FeetBundle = "bundle/v3-frames-feet.json";

    private const string ReleaseNotes = "revit/docs/release-notes-1.2.0.md";

    private static readonly ImportStepKind[] EveryStep =
    [.. Enum.GetValues<ImportStepKind>().Where(kind => kind != ImportStepKind.ToposurfaceFromSurfaceDxf)];

    internal static int Run()
    {
        TestRun run = new();

        RunCorpusCases(run);
        RunPlacementCases(run);
        RunDrapeCases(run);
        RunAbsenceCases(run);
        RunReleaseNotesCases(run);

        return run.Report("MPB 3.0 import plan");
    }

    private static void RunCorpusCases(TestRun run)
    {
        string? loadError = ConformanceCorpus.LoadGroup("reader", out List<CorpusCase> cases);
        if (loadError is not null)
        {
            run.Case("corpus loads", () => run.Fail($"conformance corpus unusable: {loadError}"));
            return;
        }

        foreach (CorpusCase corpusCase in cases)
        {
            using JsonDocument body = JsonDocument.Parse(corpusCase.Payload);
            string bundle = body.RootElement.GetProperty("bundle").GetString() ?? string.Empty;
            if (!bundle.StartsWith("bundle/v3-", StringComparison.Ordinal))
            {
                continue;
            }

            run.Case($"{corpusCase.Id}: every step is planned, or skipped with a stated reason", () =>
            {
                BundleManifest manifest = Read(Load(bundle));
                BundleImportPlan plan = Plan(manifest);

                if (!manifest.IsValid)
                {
                    run.False(plan.CanImport, "a refused manifest plans nothing");
                    run.Equal(plan.BlockedReason, manifest.Error, "and says the reader's refusal");
                    return;
                }

                foreach (ImportStepKind kind in EveryStep)
                {
                    bool planned = plan.Steps.Any(step => step.Kind == kind);
                    SkippedImport? skipped = plan.Skipped.FirstOrDefault(skip => skip.Kind == kind);
                    run.True(planned || skipped is not null, $"{kind} is in the plan");
                    if (!planned && skipped is not null)
                    {
                        run.True(!string.IsNullOrWhiteSpace(skipped.Reason), $"{kind}'s skip states its reason");
                    }
                }

                foreach (ImportStep step in plan.Steps)
                {
                    run.True(step.Levels is null && step.Level is null, $"{step.Kind} carries no per-category level");
                    if (step.EntryName.Length > 0)
                    {
                        run.True(
                            manifest.Contents!.Find(step.EntryName) is not null,
                            $"{step.Kind} reads {step.EntryName}, a file the bundle's catalog names");
                    }
                }
            });

            run.Case($"{corpusCase.Id}: the plan is identified by the order id the manifest states", () =>
            {
                Dictionary<string, string> files = Load(bundle);
                BundleManifest manifest = Read(files);
                if (!manifest.IsValid)
                {
                    return;
                }

                string orderId = Manifest(files)["order"]!["id"]!.GetValue<string>();
                string token = LandPlan.BuildToken(orderId);
                run.True(orderId.Length > 0, "the corpus bundle states an order id");
                run.Equal(manifest.OrderId, orderId, "the reader carries the order id");
                run.Equal(manifest.JobId, string.Empty, "and the manifest has no job id");
                run.True(token != "unidentified", "so the build token is one the order id makes");

                BundleImportPlan plan = Plan(manifest);
                ImportStep? provenance = Step(plan, ImportStepKind.AttributionAndProvenance);
                if (provenance is not null)
                {
                    run.Equal(provenance.Provenance?.OrderId, orderId, "the provenance carries the order id");
                }

                foreach (ImportStep step in plan.Steps)
                {
                    if (step.LandPlan is not null)
                    {
                        run.Equal(step.LandPlan.Build, token, $"{step.Kind}'s land plan is built from the order id");
                    }

                    if (step.Hazard is not null)
                    {
                        run.Equal(step.Hazard.Build, HazardPlan.BuildToken(orderId), $"{step.Kind}'s hazard plan is built from the order id");
                    }
                }
            });
        }
    }

    private static void RunPlacementCases(TestRun run)
    {
        run.Case("each step reads the file 3.0 promises for it, in that file's frame and unit (an order in feet)", () =>
        {
            JsonObject feetFrames = Manifest(Load(FeetBundle))["frames"]!.DeepClone().AsObject();
            BundleImportPlan plan = Plan(Read(Load(FullBundle, manifest => manifest["frames"] = feetFrames)));

            ImportStep? point = Step(plan, ImportStepKind.SetSharedCoordinates);
            run.True(point?.SurveyPoint is not null, "the survey point is set");
            GeoOrigin? origin = point?.SurveyPoint?.Origin;
            run.Within(origin?.Easting ?? 0, 988000.5, 1e-9, "from the site origin's x, verbatim");
            run.Within(origin?.Northing ?? 0, 214000.25, 1e-9, "and its y, verbatim");
            run.Equal(origin?.LinearUnit ?? LinearUnit.Unspecified, LinearUnit.UsSurveyFoot, "in the unit the origin states");
            run.Equal(origin?.Epsg ?? 0, 2263, "on the georeferenced frame's CRS");
            run.Within(point?.SurveyPoint?.AngleRadians ?? 0, -1.2 * Math.PI / 180.0, 1e-12, "turned by the stated grid rotation");

            ImportStep? location = Step(plan, ImportStepKind.SetSiteLocation);
            run.Within(location?.SiteLocation?.LatitudeDeg ?? 0, 40.755, 1e-12, "the site location's latitude is the origin's");
            run.Within(location?.SiteLocation?.LongitudeDeg ?? 0, -73.975, 1e-12, "and its longitude");

            ImportStep? terrain = Step(plan, ImportStepKind.ToposurfaceFromSurfaceTin);
            run.Equal(terrain?.EntryName, "Elevation/Terrain.dxf", "the terrain is the surface's triangles");
            run.Equal(terrain?.Units ?? LinearUnit.Unspecified, LinearUnit.UsSurveyFoot, "across in the georeferenced frame's unit");
            run.Equal(terrain?.VerticalUnits ?? LinearUnit.Unspecified, LinearUnit.InternationalFoot, "and up in its vertical unit");
            run.True(terrain?.Frame is not null, "placed against the site origin");
            run.Equal(
                terrain?.ExpectedSha256,
                Manifest(Load(FullBundle))["catalog"]!["Elevation/Terrain.dxf"]!["sha256"]!.GetValue<string>(),
                "verified against the catalog's digest");

            ImportStep? contours = Step(plan, ImportStepKind.PublishedContours);
            run.Equal(contours?.EntryName, "Elevation/Contours.dxf", "the contours are the contour DXF");
            run.True(contours?.Layer == new LayerFrame(LayerCoordinates.AbsoluteProjected, LinearUnit.UsSurveyFoot), "absolute, in the order's unit");
            run.Equal(contours?.VerticalUnits ?? LinearUnit.Unspecified, LinearUnit.InternationalFoot, "their heights in the vertical unit");

            run.Equal(Step(plan, ImportStepKind.ContextBuildings)?.EntryName, "Site/Site.ifc", "the buildings are copied from the site model");
            run.Equal(Step(plan, ImportStepKind.LinkSiteIfc)?.EntryName, "Site/Site.ifc", "which is the model linked");

            (ImportStepKind Kind, string File, string Table)[] layers =
            [
                (ImportStepKind.RoadCentrelines, "Basemap/Roads.gpkg", "Roads"),
                (ImportStepKind.Water, "Basemap/Water.gpkg", "Water"),
                (ImportStepKind.RoadPolygons, "Basemap/Pavement.gpkg", "Pavement"),
                (ImportStepKind.LandUse, "Basemap/Landuse.gpkg", "Landuse"),
                (ImportStepKind.LandCover, "Landcover/Landcover.gpkg", "Landcover"),
                (ImportStepKind.FloodZones, "Records/Floodplain.gpkg", "Floodplain"),
                (ImportStepKind.SteepGround, "Elevation/Slope.gpkg", "Slope"),
            ];
            foreach ((ImportStepKind kind, string file, string table) in layers)
            {
                ImportStep? layer = Step(plan, kind);
                run.Equal(layer?.EntryName, file, $"{kind} reads {file}");
                run.Equal(layer?.Table, table, $"{kind} reads the file's one layer");
                run.True(layer?.SoleTable ?? false, $"{kind}'s file holds that layer alone");
                run.True(layer?.Layer == new LayerFrame(LayerCoordinates.AbsoluteProjected, LinearUnit.UsSurveyFoot), $"{kind} is absolute, in the order's unit");
            }

            ImportStep? trees = Step(plan, ImportStepKind.Vegetation);
            run.Equal(trees?.EntryName, "Landcover/Trees.csv", "the trees are the host-ready tree points");
            run.True(trees?.Layer == new LayerFrame(LayerCoordinates.LocalOffsets, LinearUnit.Metre), "offsets in the local site frame, in metres");
            run.Equal(trees?.Units ?? LinearUnit.Unspecified, LinearUnit.Metre, "their heights in metres");
            run.Within(trees?.HeightBaseM ?? double.NaN, 20.0, 1e-12, "above the site origin's stated height");

            SkippedImport? points = plan.Skipped.FirstOrDefault(skip => skip.Kind == ImportStepKind.ToposurfaceFromPointsFile);
            run.Equal(points?.ReasonCode ?? SkipReasonCode.LeftOutByChoice, SkipReasonCode.NotInFormat, "the points file is skipped: 3.0 has none");
        });

        run.Case("a 3.0 tree point lands where the toposolid is: offsets from the site origin, heights above its height", () =>
        {
            BundleImportPlan plan = Plan(Read(Load(FullBundle)));
            ImportStep trees = Step(plan, ImportStepKind.Vegetation)!;
            GeoOrigin origin = trees.Frame!.Origin;

            TreePointsParse parse = TreePointsReader.Parse(
                "x,y,ground_z,height_m,crown_radius_m\n"
                + "12.5,-7.25,5.0,4,1\n"
                + "-300,410,-2.5,6,2\n",
                trees.Frame,
                trees.FoliageTypeVocabulary,
                trees.Units,
                layer: trees.Layer,
                groundBaseM: trees.HeightBaseM ?? 0.0);

            run.True(parse.Failure is null, $"the tree file parses ({parse.Failure})");
            run.Equal(parse.Points.Count, 2, "both rows are points");
            run.Within(parse.Points[0].EastM, 12.5, 1e-9, "east is the offset the file states, not the offset less the origin");
            run.Within(parse.Points[0].NorthM, -7.25, 1e-9, "north is the offset the file states");
            run.Within(parse.Points[1].EastM, -300.0, 1e-9, "another row's east");
            run.Within(parse.Points[1].NorthM, 410.0, 1e-9, "another row's north");
            run.True(
                parse.Points.All(point => Math.Abs(point.EastM) < 1000.0 && Math.Abs(point.NorthM) < 1000.0),
                $"every tree is within the site's footprint, not {origin.Easting:N0} m from it");
            run.Within(parse.Points[0].GroundElevationM, 25.0, 1e-9, "ground_z is above the origin's height of 20 m, so it lands at 25 m");
            run.Within(parse.Points[1].GroundElevationM, 17.5, 1e-9, "a row below the origin's height lands below it, not below zero");
        });

        run.Case("the foliage vocabulary the Trees.csv entry states rides on the vegetation step, so a shrub row is a shrub", () =>
        {
            const string vocabulary = "1";
            BundleManifest manifest = Read(Load(FullBundle, document =>
                document["catalog"]!["Landcover/Trees.csv"]!["table"]!.AsObject()["foliage_types"] =
                    new JsonObject { ["vocabulary"] = vocabulary, ["values"] = new JsonArray("tree", "shrub") }));

            run.Equal(
                manifest.Contents!.Find("Landcover/Trees.csv")?.FoliageTypeVocabulary,
                vocabulary,
                "the shared reader exposes the vocabulary the entry states, verbatim");
            run.Equal(
                manifest.Contents!.Find("Elevation/Terrain.dxf")?.FoliageTypeVocabulary,
                null,
                "and no other file has one");

            ImportStep? trees = Step(Plan(manifest), ImportStepKind.Vegetation);
            run.Equal(trees?.FoliageTypeVocabulary, vocabulary, "the vegetation step carries it, as a 2.x step does");

            TreePointsParse parse = TreePointsReader.Parse(
                "x,y,ground_z,height_m,crown_radius_m,foliage_type\n"
                + "1,2,3,4,1,tree\n"
                + "5,6,7,1,0.5,shrub\n"
                + "9,8,7,6,2,\n",
                trees!.Frame!,
                trees.FoliageTypeVocabulary,
                trees.Units);
            run.True(parse.Failure is null, $"the tree file parses ({parse.Failure})");
            run.Equal(parse.Points.Count, 3, "every row is a point");
            run.Equal(parse.Points[0].FoliageType, FoliageType.Tree, "a tree row is a tree");
            run.Equal(parse.Points[1].FoliageType, FoliageType.Shrub, "a shrub row is a shrub");
            run.Equal(parse.Points[2].FoliageType, FoliageType.Tree, "an empty cell is a tree");
            run.Equal(parse.EmptyFoliageCells, 1, "and is counted");
            run.Equal(
                PlantingFamilies.FamilyName(parse.Points[1].FoliageType),
                ShrubFamily.FamilyName,
                "so the shrub is planted with the shrub family");

            BundleManifest unstated = Read(Load(FullBundle));
            run.Equal(
                unstated.Contents!.Find("Landcover/Trees.csv")?.FoliageTypeVocabulary,
                null,
                "an entry that states none reads none");
            run.Equal(
                Step(Plan(unstated), ImportStepKind.Vegetation)?.FoliageTypeVocabulary,
                null,
                "and its step carries none");
        });

        run.Case("a 3.0 bundle imported again over its own earlier import follows the re-import rule", () =>
        {
            static ProjectAnchor AnchorOf(SurveyPointPlacement placement) =>
                new(placement.Origin.Epsg, placement.Origin.Easting!.Value, placement.Origin.Northing!.Value, placement.AngleRadians);

            SurveyPointPlacement level = Step(
                Plan(Read(Load(FullBundle, manifest => manifest["frames"]!["local"]!["grid_rotation_deg"] = 0.0))),
                ImportStepKind.SetSharedCoordinates)!.SurveyPoint!;
            AnchorDecision again = AdjacentBundle.Decide(AnchorOf(level), level);
            run.False(again.IsRefusal, $"a level grid re-imports at the same anchor: {again.Message}");
            run.Equal(again.Kind, AnchorDecisionKind.Offset, "placed against the anchor it set");
            run.False(again.MovesGeometry, "at no offset");

            SurveyPointPlacement turned = Step(Plan(Read(Load(FullBundle))), ImportStepKind.SetSharedCoordinates)!.SurveyPoint!;
            AnchorDecision turnedAgain = AdjacentBundle.Decide(AnchorOf(turned), turned);
            run.False(turnedAgain.IsRefusal, $"a stated non-zero rotation re-imports too: a zero offset takes no rotation: {turnedAgain.Message}");
            run.Equal(turnedAgain.Kind, AnchorDecisionKind.Offset, "placed against the anchor it set");
            run.False(turnedAgain.MovesGeometry, "at no offset");
        });

        run.Case("the terrain's uncropped notice reads as one sentence", () =>
        {
            ImportStep? terrain = Step(Plan(Read(Load(FullBundle))), ImportStepKind.ToposurfaceFromSurfaceTin);
            string notice = SurfaceCrop.NoWindowNotice(terrain?.CropUnavailable, "every vertex in the surface");

            run.False(notice.Contains("..", StringComparison.Ordinal), $"no doubled full stop: {notice}");
        });

        run.Case("a layer file whose catalog entry names its tables is read at the table of the step's geometry", () =>
        {
            BundleImportPlan plan = Plan(Read(Load(FullBundle, manifest =>
            {
                manifest["catalog"]!["Basemap/Water.gpkg"]!["tables"] = new JsonArray("Water_line", "Water_polygon");
                manifest["catalog"]!["Basemap/Roads.gpkg"]!["tables"] = new JsonArray("RoadSplines");
            })));

            ImportStep? water = Step(plan, ImportStepKind.Water);
            run.Equal(water?.Table, "Water_polygon", "the water bodies are the polygon table");
            run.False(water?.SoleTable ?? true, "read by that name, not as the file's one table");

            ImportStep? roads = Step(plan, ImportStepKind.RoadCentrelines);
            run.Equal(roads?.Table, "RoadSplines", "a file that names one table is read at it, whatever the file is called");
            run.False(roads?.SoleTable ?? true, "by name");
        });

        run.Case("a 3.0 vertical CRS is stated in the datum words a 2.x import of the order recorded", () =>
        {
            BundleImportPlan egm = Plan(Read(Load(FullBundle)));
            run.Equal(
                Step(egm, ImportStepKind.ToposurfaceFromSurfaceTin)?.HeightDatum?.Stated,
                HeightDatums.Egm2008,
                "EPSG:3855 on EGM2008 is the EGM2008 height a 2.x manifest named");

            BundleImportPlan navd = Plan(Read(Load(FullBundle, manifest =>
            {
                foreach (string frame in new[] { "georeferenced", "local" })
                {
                    manifest["frames"]![frame]!["vertical_crs"] = new JsonObject { ["code"] = "EPSG:5703" };
                    manifest["frames"]![frame]!["geoid_model"] = "GEOID18";
                }
            })));
            run.Equal(
                Step(navd, ImportStepKind.ToposurfaceFromSurfaceTin)?.HeightDatum?.Stated,
                HeightDatums.Navd88Geoid18,
                "EPSG:5703 on GEOID18 is the NAVD88 (GEOID18) height a 2.x manifest named");
            run.True(navd.Steps.Any(step => step.Kind == ImportStepKind.ContextBuildings), "and the site model's buildings are planned on it");

            BundleImportPlan otherGeoid = Plan(Read(Load(FullBundle, manifest =>
            {
                manifest["frames"]!["georeferenced"]!["vertical_crs"] = new JsonObject { ["code"] = "EPSG:5703" };
                manifest["frames"]!["georeferenced"]!["geoid_model"] = "GEOID12B";
            })));
            run.Equal(
                Step(otherGeoid, ImportStepKind.ToposurfaceFromSurfaceTin)?.HeightDatum?.Stated,
                "EPSG:5703",
                "NAVD88 on another geoid is not the pair a 2.x manifest named, so it stays the code and fails closed");
        });

        run.Case("a 3.0 bundle's credits are its attribution", () =>
        {
            BundleManifest manifest = Read(Load(FullBundle, root =>
            {
                root["credits"] = new JsonArray(
                    new JsonObject { ["text"] = "Elevation: a survey", ["link"] = "https://example.org/survey" },
                    new JsonObject { ["text"] = "Imagery: an agency" });
            }));

            run.Equal(manifest.AttributionSources.Count, 2, "one source per credit");
            run.Equal(manifest.AttributionSources[0].AttributionText, "Elevation: a survey", "its text, verbatim");
            run.Equal(manifest.AttributionSources[0].LicenseUrl, "https://example.org/survey", "and its link");
            run.Equal(manifest.AttributionSources[1].AttributionText, "Imagery: an agency", "a credit with no link still credits");
        });

        run.Case("a 3.0 plan carries no per-category levels, whatever the choice names", () =>
        {
            Dictionary<ImportLayer, FidelityLevel> levels = new()
            {
                [ImportLayer.Planting] = FidelityLevel.Min,
                [ImportLayer.ImageryDrape] = FidelityLevel.Med,
            };
            BundleImportPlan plan = BundleImportPlanner.Plan(
                Read(Load(FullBundle)),
                Load(FullBundle).Keys,
                _ => new ImageSize(1024, 1024),
                ImportLayerChoice.Only(Enum.GetValues<ImportLayer>(), levels));

            run.True(plan.Steps.Count > 0, "planned");
            run.True(plan.Steps.All(step => step.Levels is null && step.Level is null), "no step carries a level");
        });

        run.Case("a CRS stated by no EPSG code places nothing absolute, and still sets the site location", () =>
        {
            BundleImportPlan plan = Plan(Read(Load(FullBundle, manifest =>
            {
                manifest["frames"]!["georeferenced"]!["horizontal_crs"] = new JsonObject { ["wkt2"] = "PROJCRS[\"Synthetic\"]" };
            })));

            run.Equal(Skip(plan, ImportStepKind.SetSharedCoordinates)?.ReasonCode ?? SkipReasonCode.LeftOutByChoice, SkipReasonCode.NoSurveyPoint, "no survey point");
            run.Equal(Skip(plan, ImportStepKind.RoadCentrelines)?.ReasonCode ?? SkipReasonCode.LeftOutByChoice, SkipReasonCode.NoSiteFrame, "the roads are not placed");
            run.True(Step(plan, ImportStepKind.SetSiteLocation) is not null, "the site location is still set");
        });
    }

    private static void RunDrapeCases(TestRun run)
    {
        run.Case("the drape is placed on the corpus catalog's rectangle in the local site frame, verbatim", () =>
        {
            BundleImportPlan plan = Plan(Read(Load(FullBundle)));

            ImportStep? drape = Step(plan, ImportStepKind.ImageryDrape);
            run.Equal(drape?.EntryName, "Imagery/Imagery.png", "the drape is the host-ready imagery");
            run.Within(drape?.Drape?.LeftM ?? 0, -512.0, 1e-12, "west edge");
            run.Within(drape?.Drape?.BottomM ?? 0, -512.0, 1e-12, "south edge");
            run.Within(drape?.Drape?.RightM ?? 0, 512.0, 1e-12, "east edge");
            run.Within(drape?.Drape?.TopM ?? 0, 512.0, 1e-12, "north edge");
            run.True(drape?.Drape?.ExtentFromDrapeBlock ?? false, "a stated rectangle, not an inferred one");
            run.Equal(
                Step(plan, ImportStepKind.ToposurfaceFromSurfaceTin)?.ToposolidType ?? TerrainToposolidType.Project,
                TerrainToposolidType.Imagery,
                "the terrain is built on the imagery type, since the drape runs");
        });

        run.Case("a drape whose image is not the size the catalog states is skipped, saying so", () =>
        {
            Dictionary<string, string> files = Load(FullBundle);
            BundleImportPlan plan = BundleImportPlanner.Plan(Read(files), files.Keys, _ => new ImageSize(2048, 2048));

            SkippedImport? skip = Skip(plan, ImportStepKind.ImageryDrape);
            run.Equal(skip?.ReasonCode ?? SkipReasonCode.LeftOutByChoice, SkipReasonCode.ExtentNotCorroborated, "not corroborated");
            run.Contains(skip?.Reason, "1024", "naming the size the catalog states");
        });

        run.Case("a drape whose catalog entry states no rectangle is skipped, saying so", () =>
        {
            BundleImportPlan plan = Plan(Read(Load(FullBundle, WithoutRectangle)));

            SkippedImport? skip = Skip(plan, ImportStepKind.ImageryDrape);
            run.Equal(skip?.ReasonCode ?? SkipReasonCode.LeftOutByChoice, SkipReasonCode.ExtentNotCorroborated, "no rectangle to place it on");
            run.Contains(skip?.Reason, "Imagery/Imagery.png", "naming the file");
            run.Equal(
                Step(plan, ImportStepKind.ToposurfaceFromSurfaceTin)?.ToposolidType ?? TerrainToposolidType.Imagery,
                TerrainToposolidType.Project,
                "and the terrain keeps the project's type");
        });
    }

    private static void RunAbsenceCases(TestRun run)
    {
        run.Case("a file the manifest lists as not held is skipped with the manifest's reason, verbatim", () =>
        {
            const string NotHeld = "bundle/v3-records-not-held.json";
            JsonObject manifest = Manifest(Load(NotHeld));
            string stated = manifest["not_held"]!.AsArray()
                .First(entry => entry!["path"]!.GetValue<string>() == "Records/Floodplain.gpkg")!["reason"]!.GetValue<string>();

            BundleImportPlan plan = Plan(Read(Load(NotHeld)));
            SkippedImport? skip = Skip(plan, ImportStepKind.FloodZones);
            run.Equal(skip?.ReasonCode ?? SkipReasonCode.LeftOutByChoice, SkipReasonCode.NotHeld, "not held");
            run.Contains(skip?.Reason, stated, "the stated reason, shown as stated");
        });

        run.Case("a file the catalog names and the bundle lacks is skipped as missing from the bundle", () =>
        {
            BundleImportPlan plan = Plan(Read(Load("bundle/v3-catalog-file-missing.json")));

            run.Equal(Skip(plan, ImportStepKind.ImageryDrape)?.ReasonCode ?? SkipReasonCode.LeftOutByChoice, SkipReasonCode.EntryNotInArchive, "missing from the bundle");
        });

        run.Case("a file the catalog does not name is skipped as not in this download", () =>
        {
            BundleImportPlan plan = Plan(Read(Load(FullBundle, manifest => manifest["catalog"]!.AsObject().Remove("Landcover/Trees.csv"))));

            SkippedImport? skip = Skip(plan, ImportStepKind.Vegetation);
            run.Equal(skip?.ReasonCode ?? SkipReasonCode.LeftOutByChoice, SkipReasonCode.ArtifactNotInManifest, "not in the catalog");
            run.Contains(skip?.Reason, "Landcover/Trees.csv", "naming the file");
        });

        run.Case("a level download without the layers the site lacks plans each of their steps as skipped, naming the file", () =>
        {
            (ImportStepKind Kind, string File)[] absent =
            [
                (ImportStepKind.RoadCentrelines, "Basemap/Roads.gpkg"),
                (ImportStepKind.Water, "Basemap/Water.gpkg"),
                (ImportStepKind.LandUse, "Basemap/Landuse.gpkg"),
                (ImportStepKind.LandCover, "Landcover/Landcover.gpkg"),
            ];
            Dictionary<string, string> files = Load(FullBundle, manifest =>
            {
                foreach ((_, string file) in absent)
                {
                    manifest["catalog"]!.AsObject().Remove(file);
                }
            });
            foreach ((_, string file) in absent)
            {
                files.Remove(file);
            }

            BundleImportPlan plan = Plan(Read(files));
            run.True(plan.CanImport, $"the rest still imports ({plan.BlockedReason})");
            run.True(Step(plan, ImportStepKind.ToposurfaceFromSurfaceTin) is not null, "the terrain among it");
            foreach ((ImportStepKind kind, string file) in absent)
            {
                run.True(Step(plan, kind) is null, $"no {kind} step");
                SkippedImport? skip = Skip(plan, kind);
                run.True(skip is not null, $"{kind} is shown as skipped");
                run.Contains(skip?.Reason, file, $"{kind}'s skip names {file}");
            }

            run.True(!plan.Steps.Any(step => absent.Any(gone => gone.File == step.EntryName)), "no step reads a file the download lacks");
        });
    }

    private static void RunReleaseNotesCases(TestRun run)
    {
        run.Case("each item a 3.0 plan leaves out is named in the release notes for 1.2.0", () =>
        {
            string? notes = ReadRepoFile(ReleaseNotes);
            if (notes is null)
            {
                run.Fail($"{ReleaseNotes} is missing");
                return;
            }

            run.True(BundleImportPlanner.ThreeLeftOut.Count > 0, "the plan names what it leaves out");
            foreach (ThreeLeftOutItem item in BundleImportPlanner.ThreeLeftOut)
            {
                run.Contains(notes, item.Item, $"the notes name {item.Item}");
                run.Contains(notes, item.Why, $"and say why {item.Item} is left out");
            }
        });

        run.Case("a step skipped for want of a 3.0 file says why in the release notes' words", () =>
        {
            BundleImportPlan plan = Plan(Read(Load(FullBundle)));
            foreach (ThreeLeftOutItem item in BundleImportPlanner.ThreeLeftOut.Where(item => item.Kind is not null))
            {
                SkippedImport? skip = Skip(plan, item.Kind!.Value);
                run.Equal(skip?.ReasonCode ?? SkipReasonCode.LeftOutByChoice, SkipReasonCode.NotInFormat, $"{item.Kind} is skipped as not in the format");
                run.Contains(skip?.Reason, item.Why, $"{item.Kind}'s skip gives the notes' reason");
            }
        });
    }

    internal static ImportStep? Step(BundleImportPlan plan, ImportStepKind kind) =>
        plan.Steps.FirstOrDefault(step => step.Kind == kind);

    private static SkippedImport? Skip(BundleImportPlan plan, ImportStepKind kind) =>
        plan.Skipped.FirstOrDefault(skip => skip.Kind == kind);

    internal static BundleImportPlan Plan(BundleManifest manifest) =>
        BundleImportPlanner.Plan(
            manifest,
            manifest.Contents!.Files.Select(file => file.Path),
            _ => new ImageSize(1024, 1024));

    internal static BundleManifest Read(Dictionary<string, string> files) =>
        BundleReader.Read(files.Keys, entry => files.TryGetValue(entry, out string? text) ? text : null)
        ?? throw new InvalidOperationException("the reader found no manifest");

    private static void WithoutRectangle(JsonObject manifest) =>
        manifest["catalog"]!["Imagery/Imagery.png"]!["raster"]!.AsObject().Remove("rectangle");

    internal static JsonObject Manifest(Dictionary<string, string> files) =>
        JsonNode.Parse(files["Manifest.json"])!.AsObject();

    internal static Dictionary<string, string> Load(string bundleFile, Action<JsonObject>? edit = null)
    {
        string root = ConformanceCorpus.FindCorpusDirectory() ?? throw new InvalidOperationException("the corpus is not found");
        JsonObject fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(root, bundleFile.Replace('/', Path.DirectorySeparatorChar))))!.AsObject();

        Dictionary<string, string> files = new(StringComparer.Ordinal);
        foreach ((string name, JsonNode? value) in fixture["files"]!.AsObject())
        {
            if (value is JsonValue text && text.TryGetValue(out string? content))
            {
                files[name] = content;
                continue;
            }

            JsonObject document = value!.DeepClone().AsObject();
            if (name == "Manifest.json")
            {
                edit?.Invoke(document);
            }

            files[name] = document.ToJsonString();
        }

        return files;
    }

    private static string? ReadRepoFile(string relativePath)
    {
        string directory = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar))!;
        string? found = RepoTree.Find(directory.Replace(Path.DirectorySeparatorChar, '/'), candidate => File.Exists(Path.Combine(candidate, Path.GetFileName(relativePath))));
        return found is null ? null : File.ReadAllText(Path.Combine(found, Path.GetFileName(relativePath)));
    }
}
