using System.Text.Json.Nodes;
using MantlePlace.Revit.Core;
using MantlePlace.Revit.Core.Tests;

namespace MantlePlace.AutoCad.Tests;

/// <summary>
/// The import plan's own decisions, over the <c>hosts.autocad</c> corpus fixtures: the drawing's
/// geographic location against the order's frame, parts skipped with their reasons, and the fail-closed
/// edges the corpus cases do not reach. Each fixture is read from the corpus and changed in one place.
/// </summary>
internal static class AutoCadImportPlanTests
{
    private const string Complete = "manifest/autocad-complete-block.json";
    private const string ContoursAbsent = "manifest/autocad-contours-absent.json";
    private const string LocalFrame = "manifest/autocad-local-frame-offsets.json";
    private const string SurfaceAbsent = "manifest/autocad-surface-absent.json";
    private const string RecordsPresent = "manifest/autocad-records-linework-present.json";
    private const string RecordsNotReady = "manifest/autocad-records-linework-not-ready.json";
    private const string RecordsAbsent = "manifest/autocad-records-linework-absent.json";

    internal static int Run()
    {
        TestRun run = new();

        if (ConformanceCorpus.FindCorpusDirectory() is not { } root)
        {
            run.Case("corpus loads", () => run.Fail("the conformance corpus is not in this tree"));
            return run.Report("AutoCAD import plan");
        }

        JsonObject Fixture(string file) =>
            (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar))))!;

        JsonObject Block(JsonObject manifest) => (JsonObject)manifest["hosts"]!["autocad"]!;

        AutoCadImportPlan Plan(JsonObject manifest, DrawingGeolocation drawing, bool civil3d = false) =>
            AutoCadImportPlanner.Plan(manifest.ToJsonString(), drawing, civil3d ? DrawingZone.None : null);

        AutoCadImportPlan InZone(JsonObject manifest, DrawingZone zone) =>
            AutoCadImportPlanner.Plan(manifest.ToJsonString(), DrawingGeolocation.None, zone);

        // --- Civil 3D: the drawing's coordinate zone against the order's frame ---

        run.Case("in Civil 3D a drawing whose zone is the order's CRS places, and a notice says they agree", () =>
        {
            AutoCadImportPlan plan = InZone(Fixture(Complete), new DrawingZone("CO83-NF", 2231));

            run.False(plan.IsRefused, $"planned ({plan.Refusal})");
            run.True(plan.Notices.Any(n => n.Contains("Civil 3D coordinate zone", StringComparison.Ordinal)
                && n.Contains("CO83-NF", StringComparison.Ordinal)), "the notice names the zone: " + string.Join(" | ", plan.Notices));
        });

        run.Case("in Civil 3D a drawing whose zone is another CRS refuses, naming both, and plans nothing", () =>
        {
            AutoCadImportPlan plan = InZone(Fixture(Complete), new DrawingZone("CO83-CF", 2232));

            run.True(plan.IsRefused, "refused");
            run.Contains(plan.Refusal, "CO83-CF", "the drawing's zone code");
            run.Contains(plan.Refusal, "EPSG:2232", "the drawing's CRS");
            run.Contains(plan.Refusal, "EPSG:2231", "the order's CRS");
            run.Contains(plan.Refusal, "Nothing was imported", "what happened");
            run.Contains(plan.Refusal, "does not change the zone", "and that the zone is left alone");
            run.True(plan.Parts.All(p => !p.Place), "no part is placed");
        });

        run.Case("in Civil 3D a zone AutoCAD gives no EPSG code for refuses rather than guessing", () =>
        {
            AutoCadImportPlan plan = InZone(Fixture(Complete), new DrawingZone("MY-CUSTOM-ZONE", null));

            run.True(plan.IsRefused, "refused");
            run.Contains(plan.Refusal, "MY-CUSTOM-ZONE", "the zone, named");
            run.Contains(plan.Refusal, "EPSG:2231", "the order's CRS");
        });

        run.Case("in Civil 3D a drawing with no zone places at the order's coordinates, and a notice says the zone is not set for you", () =>
        {
            foreach (string code in new[] { DrawingZone.NoZoneCode, string.Empty })
            {
                AutoCadImportPlan plan = InZone(Fixture(Complete), new DrawingZone(code, null));

                run.False(plan.IsRefused, $"planned for '{code}' ({plan.Refusal})");
                run.True(plan.Notices.Any(n => n.Contains("no Civil 3D coordinate zone", StringComparison.Ordinal)
                    && n.Contains("EPSG:2231", StringComparison.Ordinal)
                    && n.Contains("does not set it", StringComparison.Ordinal)), "the notice: " + string.Join(" | ", plan.Notices));
            }
        });

        run.Case("in Civil 3D a local-frame order refuses a drawing with a zone, and places into one with none", () =>
        {
            AutoCadImportPlan zoned = InZone(Fixture(LocalFrame), new DrawingZone("UTM84-13N", 32613));
            AutoCadImportPlan none = InZone(Fixture(LocalFrame), DrawingZone.None);

            run.True(zoned.IsRefused, "a zone cannot be a local frame's");
            run.Contains(zoned.Refusal, "UTM84-13N", "the zone, named");
            run.False(none.IsRefused, $"no zone places ({none.Refusal})");
        });

        run.Case("plain AutoCAD reads no zone and is never refused for one", () =>
        {
            AutoCadImportPlan plan = AutoCadImportPlanner.Plan(Fixture(Complete).ToJsonString(), DrawingGeolocation.None, civil3d: null);

            run.False(plan.IsRefused, $"planned ({plan.Refusal})");
            run.False(plan.Notices.Any(n => n.Contains("Civil 3D coordinate zone", StringComparison.Ordinal)), "no zone notice");
        });

        run.Case("the geographic location is still checked in Civil 3D, beside the zone", () =>
        {
            AutoCadImportPlan plan = AutoCadImportPlanner.Plan(Fixture(Complete).ToJsonString(), DrawingGeolocation.Located("UTM83-13", 26913), new DrawingZone("CO83-NF", 2231));

            run.True(plan.IsRefused, "refused on the location");
            run.Contains(plan.Refusal, "EPSG:26913", "the location's CRS");
        });

        // --- The drawing's geographic location against the order's frame ---

        run.Case("a drawing with no geographic location places at the order's coordinates, and a notice says so", () =>
        {
            AutoCadImportPlan plan = Plan(Fixture(Complete), DrawingGeolocation.None);

            run.False(plan.IsRefused, $"planned ({plan.Refusal})");
            run.True(plan.Notices.Any(n => n.Contains("no geographic location", StringComparison.Ordinal)
                && n.Contains("EPSG:2231", StringComparison.Ordinal)), "the notice names the order's CRS: " + string.Join(" | ", plan.Notices));
            run.True(plan.Notices.Any(n => n.Contains("does not set it", StringComparison.Ordinal)), "and that the plugin leaves the setting alone");
        });

        run.Case("a drawing located in the order's CRS places, and a notice says they agree", () =>
        {
            AutoCadImportPlan plan = Plan(Fixture(Complete), DrawingGeolocation.Located("CO83-NF", 2231));

            run.False(plan.IsRefused, $"planned ({plan.Refusal})");
            run.True(plan.Part(AutoCadPart.Contours).Place, "the contours are placed");
            run.True(plan.Notices.Any(n => n.Contains("CO83-NF", StringComparison.Ordinal)), "the notice names the drawing's setting");
        });

        run.Case("a drawing located in another CRS refuses, naming both, and plans nothing", () =>
        {
            AutoCadImportPlan plan = Plan(Fixture(Complete), DrawingGeolocation.Located("UTM83-13", 26913));

            run.True(plan.IsRefused, "refused");
            run.Contains(plan.Refusal, "UTM83-13", "the drawing's setting");
            run.Contains(plan.Refusal, "EPSG:26913", "the drawing's CRS");
            run.Contains(plan.Refusal, "EPSG:2231", "the order's CRS");
            run.Contains(plan.Refusal, "Nothing was imported", "and what happened");
            run.True(plan.Parts.All(p => !p.Place), "no part is placed");
        });

        run.Case("a drawing location that names no EPSG code refuses rather than guessing", () =>
        {
            AutoCadImportPlan plan = Plan(Fixture(Complete), DrawingGeolocation.Located("MY-CUSTOM-CS", null));

            run.True(plan.IsRefused, "refused");
            run.Contains(plan.Refusal, "MY-CUSTOM-CS", "the drawing's setting, named");
        });

        run.Case("a local-frame order places as offsets into a drawing with no location", () =>
        {
            AutoCadImportPlan plan = Plan(Fixture(LocalFrame), DrawingGeolocation.None);

            run.False(plan.IsRefused, $"planned ({plan.Refusal})");
            run.True(plan.Notices.Any(n => n.Contains("offsets", StringComparison.Ordinal)
                && n.Contains("EPSG:32613", StringComparison.Ordinal)), "the notice says the files are offsets about the origin: " + string.Join(" | ", plan.Notices));
        });

        run.Case("a local-frame order refuses a drawing that has a geographic location", () =>
        {
            AutoCadImportPlan plan = Plan(Fixture(LocalFrame), DrawingGeolocation.Located("UTM84-13N", 32613));

            run.True(plan.IsRefused, "refused: offsets in feet are in no CRS, so no location agrees with them");
            run.Contains(plan.Refusal, "UTM84-13N", "the drawing's setting");
            run.Contains(plan.Refusal, "offsets", "why");
            run.Contains(plan.Refusal, "Nothing was imported", "and what happened");
        });

        // --- Units come from the block ---

        run.Case("the plan's units are the block's, stated apart, and a notice names them", () =>
        {
            AutoCadImportPlan plan = Plan(Fixture(Complete), DrawingGeolocation.None);

            run.Equal(plan.HorizontalUnit, "ftUS", "X/Y");
            run.Equal(plan.VerticalUnit, "ft", "heights");
            run.True(plan.Notices.Any(n => n.Contains("US survey feet", StringComparison.Ordinal)
                && n.Contains("international feet", StringComparison.Ordinal)), "both units named: " + string.Join(" | ", plan.Notices));
        });

        run.Case("a file frame unit the plugin does not know refuses the import, naming it", () =>
        {
            JsonObject manifest = Fixture(Complete);
            Block(manifest)["file_frame"]!["horizontal_unit"] = "yd";

            AutoCadImportPlan plan = Plan(manifest, DrawingGeolocation.None);

            run.True(plan.IsRefused, "fail closed on an unknown unit");
            run.Contains(plan.Refusal, "yd", "the unit, named");
        });

        run.Case("a contours unit the plugin does not know skips the contours, naming it", () =>
        {
            JsonObject manifest = Fixture(Complete);
            Block(manifest)["contours"]!["vertical_units"] = "furlong";

            PlannedPart contours = Plan(manifest, DrawingGeolocation.None).Part(AutoCadPart.Contours);

            run.False(contours.Place, "skipped");
            run.Contains(contours.Sentence, "furlong", "the unit, named");
        });

        run.Case("contours whose frame is not the block's are skipped, never reprojected (HPS-53)", () =>
        {
            JsonObject manifest = Fixture(Complete);
            Block(manifest)["contours"]!["horizontal_frame"] = "local_enu";

            PlannedPart contours = Plan(manifest, DrawingGeolocation.None).Part(AutoCadPart.Contours);

            run.False(contours.Place, "skipped");
            run.Contains(contours.Sentence, "local_enu", "the file's frame, named");
        });

        // --- Readiness ---

        run.Case("an absent part is skipped with the bundle's reason, and the others are planned", () =>
        {
            AutoCadImportPlan plan = Plan(Fixture(ContoursAbsent), DrawingGeolocation.None);
            PlannedPart contours = plan.Part(AutoCadPart.Contours);

            run.False(plan.IsRefused, "an absent part is not an error");
            run.False(contours.Place, "skipped");
            run.Equal(contours.Reason, "not_produced", "the reason as stated");
            run.Contains(contours.Sentence, "Contours", "which part");
            run.Contains(contours.Sentence, ReadinessReasons.ClauseFor("not_produced")!, "the reason, shown");
            run.True(plan.Skipped.Any(p => p.Kind == AutoCadPart.Contours), "listed among the skipped parts");
        });

        run.Case("a reason the plugin does not know is still shown, never matched on", () =>
        {
            JsonObject manifest = Fixture(ContoursAbsent);
            Block(manifest)["readiness"]!["contours"]!["reason"] = "held_for_review";

            PlannedPart contours = Plan(manifest, DrawingGeolocation.None).Part(AutoCadPart.Contours);

            run.Equal(contours.Reason, "held_for_review", "echoed");
            run.Contains(contours.Sentence, "held_for_review", "shown");
        });

        run.Case("a part with no verdict at all is skipped and says the bundle gave none", () =>
        {
            JsonObject manifest = Fixture(Complete);
            ((JsonObject)Block(manifest)["readiness"]!).Remove("aerial");

            PlannedPart aerial = Plan(manifest, DrawingGeolocation.None).Part(AutoCadPart.Aerial);

            run.False(aerial.Place, "skipped");
            run.Contains(aerial.Sentence, "states no verdict", "why");
        });

        run.Case("a part the verdict calls present but with no pointer is skipped, not guessed", () =>
        {
            JsonObject manifest = Fixture(Complete);
            Block(manifest).Remove("contours");

            PlannedPart contours = Plan(manifest, DrawingGeolocation.None).Part(AutoCadPart.Contours);

            run.False(contours.Place, "skipped");
            run.Contains(contours.Sentence, "names no file", "why");
        });

        // --- The aerial ---

        run.Case("the aerial is placed at its published insertion, verbatim", () =>
        {
            PlannedPart aerial = Plan(Fixture(Complete), DrawingGeolocation.None).Part(AutoCadPart.Aerial);

            run.True(aerial.Place, aerial.Sentence);
            run.True(aerial.Insertion is { X: 3159000.5, Y: 1721000.25, Width: 4000.0, Height: 3200.0, RotationDeg: 0.25 }, "the five numbers as published");
            run.Equal(aerial.Sha256, "3333333333333333333333333333333333333333333333333333333333333333", "the digest it is checked against");
        });

        run.Case("an aerial with no insertion is skipped: the plugin does not work one out", () =>
        {
            JsonObject manifest = Fixture(Complete);
            ((JsonObject)Block(manifest)["aerial"]!).Remove("insertion");

            PlannedPart aerial = Plan(manifest, DrawingGeolocation.None).Part(AutoCadPart.Aerial);

            run.False(aerial.Place, "skipped");
            run.Contains(aerial.Sentence, "insertion", "why");
        });

        run.Case("an aerial in a format the plugin does not know is skipped, naming it", () =>
        {
            JsonObject manifest = Fixture(Complete);
            Block(manifest)["aerial"]!["format"] = "jp2-rgb-16bit";

            PlannedPart aerial = Plan(manifest, DrawingGeolocation.None).Part(AutoCadPart.Aerial);

            run.False(aerial.Place, "skipped");
            run.Contains(aerial.Sentence, "jp2-rgb-16bit", "the format, named");
        });

        // --- The surface ---

        run.Case("plain AutoCAD says the surface needs Civil 3D, from its own block", () =>
        {
            PlannedPart surface = Plan(Fixture(Complete), DrawingGeolocation.None, civil3d: false).Part(AutoCadPart.Surface);

            run.False(surface.Place, "skipped");
            run.Contains(surface.Sentence, "needs Civil 3D", "why");
        });

        run.Case("an absent surface still says it needs Civil 3D, with the stated reason", () =>
        {
            PlannedPart surface = Plan(Fixture(SurfaceAbsent), DrawingGeolocation.None, civil3d: false).Part(AutoCadPart.Surface);

            run.Equal(surface.Requires, "civil3d", "the verdict's host part");
            run.Contains(surface.Sentence, ReadinessReasons.ClauseFor("not_selected")!, "the reason, shown");
        });

        run.Case("in Civil 3D the surface is built as a TIN surface from the block's terrain source, on its layer", () =>
        {
            PlannedPart surface = Plan(Fixture(Complete), DrawingGeolocation.None, civil3d: true).Part(AutoCadPart.Surface);

            run.True(surface.Place, $"placed ({surface.Sentence})");
            run.Equal(surface.Path, "Surface/Terrain.xml", "from the block's own pointer");
            run.Equal(surface.Layer, AutoCadLayers.Surface, "on the surface layer");
            run.Contains(surface.Sentence, "TIN surface", "what it becomes");
            run.Contains(surface.Sentence, "Surface/Terrain.xml", "the file, named");
        });

        run.Case("in Civil 3D's \"as AutoCAD\" mode the part is off, so the surface is unavailable, as in plain AutoCAD", () =>
        {
            // The shell passes no zone when the presence test says the part may not load (InstalledNotLoaded),
            // so the plan is plain AutoCAD's.
            PlannedPart surface = Plan(Fixture(Complete), DrawingGeolocation.None, civil3d: false).Part(AutoCadPart.Surface);

            run.False(surface.Place, "skipped");
            run.Contains(surface.Sentence, "needs Civil 3D", "why");
        });

        run.Case("in Civil 3D a surface not shown to be in the block's frame is skipped, never reprojected", () =>
        {
            JsonObject manifest = Fixture(Complete);
            Block(manifest)["surface"]!["horizontal_frame"] = "local_enu";

            PlannedPart surface = Plan(manifest, DrawingGeolocation.None, civil3d: true).Part(AutoCadPart.Surface);

            run.False(surface.Place, "skipped");
            run.Contains(surface.Sentence, "HPS-53", "why");
        });

        run.Case("in Civil 3D a surface whose height unit is not the frame's is skipped, never converted", () =>
        {
            JsonObject manifest = Fixture(Complete);
            Block(manifest)["surface"]!["vertical_units"] = "m";

            PlannedPart surface = Plan(manifest, DrawingGeolocation.None, civil3d: true).Part(AutoCadPart.Surface);

            run.False(surface.Place, "skipped");
            run.Contains(surface.Sentence, "does not convert", "why");
        });

        run.Case("in Civil 3D a surface format the plugin does not know is declined, naming it", () =>
        {
            JsonObject manifest = Fixture(Complete);
            Block(manifest)["surface"]!["format"] = "geotiff-dem";

            PlannedPart surface = Plan(manifest, DrawingGeolocation.None, civil3d: true).Part(AutoCadPart.Surface);

            run.False(surface.Place, "skipped");
            run.Contains(surface.Sentence, "geotiff-dem", "the format, named");
        });

        run.Case("the surface's name is spelled out and carries the order, and a built surface says its point count", () =>
        {
            AutoCadImportPlan plan = Plan(Fixture(Complete), DrawingGeolocation.None, civil3d: true);

            run.Equal(AutoCadImportPlanner.SurfaceName(plan.OrderId), "MantlePlace-Surface-ord-autocad-complete-1", "the name");
            run.Equal(AutoCadImportPlanner.SurfaceName("a/b c"), "MantlePlace-Surface-a-b-c", "an id's other characters become dashes");
            string built = AutoCadImportPlanner.SurfacePlacedSentence(plan.Part(AutoCadPart.Surface), "MantlePlace-Surface-x", 1234);
            run.Contains(built, "1234 points", "the count");
            run.Contains(built, "MantlePlace-Surface-x", "the name");
        });

        run.Case("a surface needing a host part the plugin does not know is unavailable here", () =>
        {
            JsonObject manifest = Fixture(Complete);
            Block(manifest)["readiness"]!["surface"]!["requires"] = "infraworks";

            PlannedPart surface = Plan(manifest, DrawingGeolocation.None, civil3d: true).Part(AutoCadPart.Surface);

            run.False(surface.Place, "skipped");
            run.Contains(surface.Sentence, "infraworks", "the part, named");
        });

        // --- The public-record layers and the site linework (format.md §6.10) ---

        run.Case("each public-record layer is placed on its own layer, from the block's pointer", () =>
        {
            PlannedPart records = Plan(Fixture(RecordsPresent), DrawingGeolocation.None).Part(AutoCadPart.Records);

            run.True(records.Place, records.Sentence);
            run.Equal(records.Files.Count, 2, "the two layers the block lists");
            run.Equal(records.Files[0].Layer, "MantlePlace-Records-parcels", "parcels, spelled out");
            run.Equal(records.Files[1].Layer, "MantlePlace-Records-zoning", "zoning");
            run.Equal(records.Files[0].Sha256, "4444444444444444444444444444444444444444444444444444444444444444", "the digest it is checked against");
            run.Contains(records.Sentence, "parcels", "the layers, named");
            run.Contains(records.Sentence, "zoning", "the layers, named");
        });

        run.Case("the site linework is placed as polylines, its layers under the linework layer", () =>
        {
            PlannedPart linework = Plan(Fixture(RecordsPresent), DrawingGeolocation.None).Part(AutoCadPart.Linework);

            run.True(linework.Place, linework.Sentence);
            run.Equal(linework.Path, "Drawings/Linework.dxf", "the block's pointer");
            run.Equal(linework.Layer, AutoCadLayers.Linework, "the linework layer");
            run.Equal(AutoCadLayers.LineworkLayer("Roads"), "MantlePlace-Linework-Roads", "a layer the file names goes under it");
            run.Equal(AutoCadLayers.LineworkLayer("0"), AutoCadLayers.Linework, "layer 0 is the linework layer itself");
            run.Equal(AutoCadLayers.LineworkLayer(string.Empty), AutoCadLayers.Linework, "and so is no layer");
        });

        run.Case("a layer name carries only characters AutoCAD accepts in one", () =>
        {
            run.Equal(AutoCadLayers.Record("flood/zones:a"), "MantlePlace-Records-flood-zones-a", "slash and colon become dashes");
            run.Equal(AutoCadLayers.LineworkLayer("Roads <minor>"), "MantlePlace-Linework-Roads -minor-", "angle brackets too, spaces kept");
        });

        run.Case("a record layer not in the block's units skips the records, naming it, and the linework still goes in", () =>
        {
            JsonObject manifest = Fixture(RecordsPresent);
            Block(manifest)["records"]!["layers"]![1]!["horizontal_units"] = "m";

            AutoCadImportPlan plan = Plan(manifest, DrawingGeolocation.None);
            PlannedPart records = plan.Part(AutoCadPart.Records);

            run.False(plan.IsRefused, "a skipped part is not a refusal");
            run.False(records.Place, "skipped: the set is placed whole or not at all");
            run.Contains(records.Sentence, "zoning", "the layer, named");
            run.Contains(records.Sentence, "does not convert", "why");
            run.True(plan.Part(AutoCadPart.Linework).Place, "the linework is decided apart");
        });

        run.Case("a record layer in another frame is skipped, never reprojected (HPS-53)", () =>
        {
            JsonObject manifest = Fixture(RecordsPresent);
            Block(manifest)["records"]!["layers"]![0]!["horizontal_frame"] = "local_enu";

            PlannedPart records = Plan(manifest, DrawingGeolocation.None).Part(AutoCadPart.Records);

            run.False(records.Place, "skipped");
            run.Contains(records.Sentence, "HPS-53", "why");
        });

        run.Case("linework whose height unit the plugin does not know is skipped, naming it", () =>
        {
            JsonObject manifest = Fixture(RecordsPresent);
            Block(manifest)["linework"]!["vertical_units"] = "furlong";

            PlannedPart linework = Plan(manifest, DrawingGeolocation.None).Part(AutoCadPart.Linework);

            run.False(linework.Place, "skipped");
            run.Contains(linework.Sentence, "furlong", "the unit, named");
        });

        run.Case("records the verdict calls present but with no layer listed are skipped, not guessed", () =>
        {
            JsonObject manifest = Fixture(RecordsPresent);
            Block(manifest)["records"]!["layers"] = new JsonArray();

            PlannedPart records = Plan(manifest, DrawingGeolocation.None).Part(AutoCadPart.Records);

            run.False(records.Place, "skipped");
            run.Contains(records.Sentence, "names no file", "why");
        });

        run.Case("records and linework that are not ready are notices with the bundle's reasons, and the rest is planned", () =>
        {
            AutoCadImportPlan plan = Plan(Fixture(RecordsNotReady), DrawingGeolocation.None);
            string summary = plan.Summary();

            run.False(plan.IsRefused, "not a failure");
            run.True(plan.Part(AutoCadPart.Contours).Place, "the contours still go in");
            run.Contains(summary, "Public-record layers: not in this bundle: " + ReadinessReasons.ClauseFor("not_produced"), "the records' reason");
            run.Contains(summary, "Site linework: not in this bundle: " + ReadinessReasons.ClauseFor("not_selected"), "the linework's reason");
        });

        run.Case("a block from before 1.19.0 has neither, says the bundle gave no verdict, and imports the rest", () =>
        {
            AutoCadImportPlan plan = Plan(Fixture(RecordsAbsent), DrawingGeolocation.None);

            run.False(plan.IsRefused, "not a failure");
            run.True(plan.Part(AutoCadPart.Contours).Place, "the contours still go in");
            run.Contains(plan.Part(AutoCadPart.Records).Sentence, "states no verdict", "the records");
            run.Contains(plan.Part(AutoCadPart.Linework).Sentence, "states no verdict", "the linework");
        });

        run.Case("placed records and linework say what went in", () =>
        {
            AutoCadImportPlan plan = Plan(Fixture(RecordsPresent), DrawingGeolocation.None);

            string records = AutoCadImportPlanner.RecordsPlacedSentence(plan.Part(AutoCadPart.Records), 17);
            string linework = AutoCadImportPlanner.LineworkPlacedSentence(plan.Part(AutoCadPart.Linework), 230, 4);

            run.Contains(records, "17 polylines", "the count");
            run.Contains(records, "2 layers", "over the record layers");
            run.Contains(linework, "230 polylines", "the count");
            run.Contains(linework, "4 layers", "over the file's layers");
        });

        // --- The block itself ---

        run.Case("a bundle with no AutoCAD block refuses and never reads another host's", () =>
        {
            JsonObject manifest = Fixture(Complete);
            ((JsonObject)manifest["hosts"]!).Remove("autocad");

            AutoCadImportPlan plan = Plan(manifest, DrawingGeolocation.None);

            run.True(plan.IsRefused, "refused, though a hosts.revit block is beside it");
            run.Contains(plan.Refusal, "no AutoCAD block", "why");
            run.Equal(plan.OrderId, "ord-autocad-complete-1", "the order is still named (HPS-37)");
        });

        run.Case("a manifest the shared reader refuses is refused with its reason", () =>
        {
            JsonObject manifest = Fixture(Complete);
            manifest["version"] = "0.9.0";

            AutoCadImportPlan plan = Plan(manifest, DrawingGeolocation.None);

            run.True(plan.IsRefused, "below the floor");
            run.Contains(plan.Refusal, "0.9.0", "the version, named");
        });

        run.Case("text that is not JSON is refused, not thrown", () =>
            run.True(AutoCadImportPlanner.Plan("{not json", DrawingGeolocation.None, civil3d: null).IsRefused, "refused"));

        run.Case("a summary lists what was placed and what was skipped, with reasons", () =>
        {
            AutoCadImportPlan plan = Plan(Fixture(ContoursAbsent), DrawingGeolocation.None);
            string summary = plan.Summary();

            run.Contains(summary, "ord-autocad-contours-absent-1", "the order");
            run.Contains(summary, ReadinessReasons.ClauseFor("not_produced")!, "the contours' reason");
            run.Contains(summary, "needs Civil 3D", "the surface's");
        });

        run.Case("after an import, what running a part said replaces its plan sentence", () =>
        {
            AutoCadImportPlan plan = Plan(Fixture(Complete), DrawingGeolocation.None);
            Dictionary<AutoCadPart, string> outcomes = new()
            {
                [AutoCadPart.Contours] = AutoCadImportPlanner.ContoursPlacedSentence(plan.Part(AutoCadPart.Contours), 42),
                [AutoCadPart.Aerial] = AutoCadImportPlanner.FailedSentence(AutoCadPart.Aerial, "its bytes do not match the bundle's digest"),
            };

            string summary = plan.Summary(outcomes);

            run.Contains(summary, "42 polylines placed on MantlePlace-Contours", "the count");
            run.Contains(summary, "Aerial image: not imported, because its bytes do not match", "the failure");
            run.False(summary.Contains("placed on MantlePlace-Aerial", StringComparison.Ordinal), "and not the plan's claim");
        });

        run.Case("a part's file is found in the zip by its manifest path, under a single root folder too", () =>
        {
            run.Equal(AutoCadBundleEntries.Resolve(["Metadata/manifest.json", "Surface/Contours.dxf"], "Surface/Contours.dxf"), "Surface/Contours.dxf", "at the root");
            run.Equal(AutoCadBundleEntries.Resolve(["order/Metadata/manifest.json", "order/Surface/Contours.dxf"], "Surface/Contours.dxf"), "order/Surface/Contours.dxf", "under one folder");
            run.Equal(AutoCadBundleEntries.Resolve(["Metadata/manifest.json"], "Surface/Contours.dxf"), null, "absent");
        });

        return run.Report("AutoCAD import plan");
    }
}
