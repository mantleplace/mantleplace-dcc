using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The published contours reach the plan only through this host's own pointer
/// (<c>hosts.revit.contours</c>, MPB 1.4.0), checked against the block's <c>file_frame</c>
/// (<c>HPS-53</c>), and a bundle without that pointer keeps its "Also in this bundle" line
/// (ADR 0013).
/// </summary>
internal static class PublishedContourPlanTests
{
    private const string ContoursPath = "Surface/Contours.dxf";
    private const string Sha = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

    internal static int Run()
    {
        TestRun run = new();

        RunReaderCases(run);
        RunPlanCases(run);
        RunRefusalCases(run);
        RunNoteCases(run);
        RunLayerCases(run);

        return run.Report("published contours plan");
    }

    private static void RunReaderCases(TestRun run)
    {
        run.Case("the own pointer is read verbatim, both units apart", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse(Manifest(MetricFileFrame, Pointer("absolute_projected", "m", "m", "m")));

            run.True(manifest.IsValid, "accepted");
            BundleArtifact? contours = manifest.RevitContours;
            run.True(contours is not null, "read");
            run.Equal(contours!.Path, ContoursPath, "path");
            run.Equal(contours.Sha256, Sha, "sha256");
            run.Equal(contours.HorizontalFrame, "absolute_projected", "horizontal frame");
            run.Equal(contours.Units, "m", "units");
            run.Equal(contours.HorizontalUnits, "m", "horizontal units");
            run.Equal(contours.VerticalUnits, "m", "vertical units");
            run.Equal(contours.VerticalReference, "absolute", "vertical reference");
            run.True(contours.FromOwnBlock, "own block");
        });

        run.Case("an own contours pointer with no sha256 refuses the bundle", () =>
        {
            string pointer = Pointer("absolute_projected", "m", "m", "m").Replace($", \"sha256\": \"{Sha}\"", string.Empty, StringComparison.Ordinal);
            BundleManifest manifest = BundleManifestReader.Parse(Manifest(MetricFileFrame, pointer));

            run.False(manifest.IsValid, "refused");
            run.Contains(manifest.Error, "hosts.revit.contours", "names the pointer");
        });

        run.Case("readiness.contours is read", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse(Manifest(MetricFileFrame, null, "{ \"present\": false, \"reason\": \"not_produced\" }"));

            run.True(manifest.Readiness.Contours.Declared, "declared");
            run.False(manifest.Readiness.Contours.Present, "absent");
            run.Equal(manifest.Readiness.Contours.Reason, "not_produced", "reason");
        });

        run.Case("the file frame's vertical unit is read", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse(Manifest(LocalFootFileFrame, null));

            run.True(manifest.FileFrame?.VerticalUnit == LinearUnit.InternationalFoot, "ft");
        });
    }

    private static void RunPlanCases(TestRun run)
    {
        run.Case("a metric absolute pointer plans a contours step in the origin's frame", () =>
        {
            BundleImportPlan plan = Plan(Manifest(MetricFileFrame, Pointer("absolute_projected", "m", "m", "m")));
            ImportStep? step = Find(plan);

            run.True(step is not null, $"planned (skipped: {Skip(plan)?.Reason})");
            run.Equal(step!.EntryName, ContoursPath, "the pointer's entry");
            run.Equal(step.Units, LinearUnit.Metre, "horizontal unit");
            run.Equal(step.VerticalUnits, LinearUnit.Metre, "vertical unit");
            run.True(step.Layer?.Coordinates == LayerCoordinates.AbsoluteProjected, "absolute");
            run.Equal(step.ExpectedSha256, Sha, "verified against its hash");
            run.True(step.Frame is not null, "the site frame rides on the step");
        });

        run.Case("the terrain's crop window rides on the step", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse(Manifest(MetricFileFrame, Pointer("absolute_projected", "m", "m", "m")));
            ImportStep? step = Find(BundleImportPlanner.Plan(manifest, Entries, _ => new ImageSize(1, 1)));

            run.True(step!.Crop is { } crop && crop == SurfaceCrop.For(manifest, SiteFrame.For(manifest)), "the same window the terrain uses");
        });

        run.Case("a local_ft pointer is local offsets in feet, with its heights in feet", () =>
        {
            ImportStep? step = Find(Plan(Manifest(LocalFootFileFrame, Pointer("local_enu", "ft", "ft", "ft"))));

            run.True(step is not null, "planned");
            run.True(step!.Layer?.Coordinates == LayerCoordinates.LocalOffsets, "offsets about the origin");
            run.Equal(step.Units, LinearUnit.InternationalFoot, "horizontal ft");
            run.Equal(step.VerticalUnits, LinearUnit.InternationalFoot, "vertical ft");
        });

        run.Case("the contours step runs straight after the terrain", () =>
        {
            BundleImportPlan plan = Plan(Manifest(MetricFileFrame, Pointer("absolute_projected", "m", "m", "m"), withTerrain: true));
            int terrain = plan.Steps.ToList().FindIndex(step => ImportLayers.Of(step.Kind) == ImportLayer.Terrain);
            int contours = plan.Steps.ToList().FindIndex(step => step.Kind == ImportStepKind.PublishedContours);

            run.True(terrain >= 0, "terrain planned");
            run.Equal(contours, terrain + 1, "next");
        });

        run.Case("a curator who leaves the row unchecked gets no contours", () =>
        {
            BundleManifest manifest = BundleManifestReader.Parse(Manifest(MetricFileFrame, Pointer("absolute_projected", "m", "m", "m"), withTerrain: true));
            BundleImportPlan plan = BundleImportPlanner.Plan(
                manifest, Entries, _ => new ImageSize(1, 1), ImportLayerChoice.Only([ImportLayer.Terrain]));

            run.True(Find(plan) is null, "not planned");
            run.True(Skip(plan)?.ReasonCode == SkipReasonCode.LeftOutByChoice, "left out by choice");
        });
    }

    private static void RunRefusalCases(TestRun run)
    {
        void Refused(string label, string fileFrame, string pointer, SkipReasonCode code)
            => run.Case(label, () =>
            {
                BundleImportPlan plan = Plan(Manifest(fileFrame, pointer));
                run.True(Find(plan) is null, "not planned");
                run.True(Skip(plan)?.ReasonCode == code, $"refused as {code}, was {Skip(plan)?.ReasonCode}");
            });

        Refused(
            "horizontal units that disagree with the pointer's units refuse the step",
            MetricFileFrame,
            Pointer("absolute_projected", "m", "ft", "m"),
            SkipReasonCode.CoordinateSystemNotSupported);

        Refused(
            "vertical units that disagree with the file frame's refuse the step",
            MetricFileFrame,
            Pointer("absolute_projected", "m", "m", "ft"),
            SkipReasonCode.CoordinateSystemNotSupported);

        Refused(
            "a file frame with no height unit refuses the step",
            MetricFileFrame.Replace(", \"vertical_unit\": \"m\"", string.Empty, StringComparison.Ordinal),
            Pointer("absolute_projected", "m", "m", "m"),
            SkipReasonCode.CoordinateSystemNotSupported);

        Refused(
            "vertical units this plugin does not know refuse the step",
            MetricFileFrame,
            Pointer("absolute_projected", "m", "m", "yd"),
            SkipReasonCode.UnitNotUnderstood);

        Refused(
            "a pointer whose unit is not the file frame's refuses the step",
            MetricFileFrame,
            Pointer("absolute_projected", "ft", "ft", "m"),
            SkipReasonCode.CoordinateSystemNotSupported);

        Refused(
            "an unknown vertical reference refuses the step",
            MetricFileFrame,
            Pointer("absolute_projected", "m", "m", "m").Replace("\"absolute\"", "\"relative\"", StringComparison.Ordinal),
            SkipReasonCode.CoordinateSystemNotSupported);
    }

    private static void RunNoteCases(TestRun run)
    {
        run.Case("a bundle from before 1.4.0 keeps the line and says it predates Revit's contour support", () =>
        {
            BundleImportPlan plan = Plan(Manifest(MetricFileFrame, null, readiness: null));

            run.True(Find(plan) is null, "nothing planned");
            run.True(Skip(plan) is null, "and no row in the unavailable list");
            string? note = Note(plan);
            run.Contains(note, "Link CAD", "the line survives");
            run.Contains(note, "predates", "and says why it is not drawn");
        });

        run.Case("not_produced names both cases the platform uses it for", () =>
        {
            string? note = Note(Plan(Manifest(MetricFileFrame, null, "{ \"present\": false, \"reason\": \"not_produced\" }")));

            run.Contains(note, "predates", "a bundle from before 1.4.0");
            run.Contains(note, "no site origin", "or one with nothing to be local to");
        });

        run.Case("any other reason is the reason the bundle gives", () =>
        {
            string? note = Note(Plan(Manifest(MetricFileFrame, null, "{ \"present\": false, \"reason\": \"emit_failed\" }")));

            run.Contains(note, ReadinessReasons.ClauseFor("emit_failed")!, "the readiness clause");
            run.False(note?.Contains("predates", StringComparison.Ordinal) ?? false, "not the predates sentence");
        });

        run.Case("a bundle whose contours are drawn does not also list them as not imported", () =>
            run.True(Note(Plan(Manifest(MetricFileFrame, Pointer("absolute_projected", "m", "m", "m")))) is null, "no note"));
    }

    private static void RunLayerCases(TestRun run)
    {
        run.Case("the contours are their own layer, after the terrain, unchecked and standing alone", () =>
        {
            run.True(ImportLayers.Of(ImportStepKind.PublishedContours) == ImportLayer.PublishedContours, "own layer");
            run.False(ImportLayers.OnByDefault(ImportLayer.PublishedContours), "off by default");
            run.True(ImportLayers.PrerequisiteOf(ImportLayer.PublishedContours) is null, "not gated on the terrain");
            run.True(ImportLayer.PublishedContours == ImportLayer.Terrain + 1, "the row after the terrain");
            run.Equal(WindowLabels.LayerName(ImportLayer.PublishedContours), "Published Contours", "the glossary's words");
            run.Equal(ImportStepKinds.LifetimeOf(ImportStepKind.PublishedContours), ExtractionLifetime.Transient, "read, never linked");
        });

        run.Case("the checklist offers the row unchecked and enabled with the terrain off", () =>
        {
            ImportChecklist checklist = new([ImportLayer.Terrain, ImportLayer.PublishedContours]);
            checklist.Set(ImportLayer.Terrain, false);

            run.False(checklist.IsChecked(ImportLayer.PublishedContours), "unchecked");
            run.True(checklist.IsEnabled(ImportLayer.PublishedContours), "still enabled");
        });
    }

    private const string MetricGeoreference = """
        "georeference": {
          "crs_projected": "EPSG:32613",
          "origin": {
            "lon": -105.32557885004304,
            "lat": 38.46130517000308,
            "projected": { "epsg": 32613, "easting": 471595.0, "northing": 4257050.0, "linear_unit": "m" }
          }
        }
        """;

    private const string MetricFileFrame =
        "\"file_frame\": { \"type\": \"projected\", \"crs\": \"EPSG:32613\", \"horizontal_unit\": \"m\", \"vertical_unit\": \"m\" }";

    private const string LocalFootFileFrame =
        "\"file_frame\": { \"type\": \"local\", \"base_crs\": \"EPSG:32613\", "
        + "\"origin\": { \"easting\": 471595.0, \"northing\": 4257050.0, \"linear_unit\": \"m\" }, "
        + "\"horizontal_unit\": \"ft\", \"vertical_unit\": \"ft\" }";

    private static readonly string[] Entries =
    [
        "Metadata/manifest.json",
        ContoursPath,
        "Surface/SurfacePoints.csv",
    ];

    private static string Pointer(string horizontalFrame, string units, string horizontalUnits, string verticalUnits)
        => $$"""
            "contours": { "path": "{{ContoursPath}}", "horizontal_frame": "{{horizontalFrame}}",
              "vertical_reference": "absolute", "units": "{{units}}",
              "horizontal_units": "{{horizontalUnits}}", "vertical_units": "{{verticalUnits}}",
              "vertical_datum": "EGM2008-orthometric", "sha256": "{{Sha}}" }
            """;

    /// <param name="readiness"><c>null</c> for a bundle cut before <c>readiness.contours</c> existed.</param>
    private static string Manifest(string fileFrame, string? pointer, string? readiness = "{ \"present\": true }", bool withTerrain = false)
    {
        string readinessBlock = "\"readiness\": { \"toposurface_points\": "
            + (withTerrain ? "{ \"present\": true }" : "{ \"present\": false, \"reason\": \"not_produced\" }")
            + (readiness is null ? string.Empty : ", \"contours\": " + readiness)
            + " }";

        string terrain = withTerrain
            ? ", \"toposurface_points\": { \"path\": \"Surface/SurfacePoints.csv\", \"format\": \"csv\", "
              + "\"horizontal_frame\": \"local_enu\", \"units\": \"m\", \"sha256\": \"" + Sha + "\" }"
            : string.Empty;

        string revit = string.Join(
            ",\n",
            new[] { MetricGeoreference, fileFrame, pointer, readinessBlock }.Where(member => member is not null));

        return $$"""
            {
              "version": "1.4.0",
              "bbox": { "west": -105.33, "south": 38.455, "east": -105.32, "north": 38.465 },
              "layout": { "contours": "{{ContoursPath}}", "points_csv": "Surface/SurfacePoints.csv" },
              "elevation": { "contours": { "units": "m", "vertical_datum": "EGM2008-orthometric" } },
              "hosts": { "revit": { {{revit}}{{terrain}} } }
            }
            """;
    }

    private static BundleImportPlan Plan(string manifestJson)
    {
        BundleManifest manifest = BundleManifestReader.Parse(manifestJson);
        if (!manifest.IsValid)
        {
            throw new InvalidOperationException($"fixture refused: {manifest.Error}");
        }

        return BundleImportPlanner.Plan(manifest, Entries, _ => new ImageSize(1, 1));
    }

    private static ImportStep? Find(BundleImportPlan plan)
        => plan.Steps.FirstOrDefault(step => step.Kind == ImportStepKind.PublishedContours);

    private static SkippedImport? Skip(BundleImportPlan plan)
        => plan.Skipped.FirstOrDefault(skip => skip.Kind == ImportStepKind.PublishedContours);

    private static string? Note(BundleImportPlan plan)
        => plan.AvailableButNotImported.FirstOrDefault(line => line.StartsWith(ContoursPath, StringComparison.Ordinal));
}
