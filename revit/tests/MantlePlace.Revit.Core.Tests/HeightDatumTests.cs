using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// A frame names its vertical datum (<c>HPS-53</c>): the ground records the datum it was built in,
/// and every step that places a height is skipped by name where its content's datum is another.
/// </summary>
internal static class HeightDatumTests
{
    private const string Stem = "eb00f56f-74f5-4134-9c8a-baf82215191a";
    private const string Sha = "9f2c41ab77de5310ac4b0e6612f8a7d3b1c05e94aa2f6d8813bb47c0192e5f6a";
    private const string Egm = HeightDatums.Egm2008;
    private const string Navd = HeightDatums.Navd88Geoid18;

    private static readonly StatedDatum Unstated17 = new(null, Owed: false);
    private static readonly StatedDatum Unstated18 = new(null, Owed: true);

    internal static int Run()
    {
        TestRun run = new();

        run.Case("the statement is owed from MPB 1.8.0, and a blank one is unstated", () =>
        {
            Same(run, HeightDatums.Statement(Navd, "1.8.0"), new StatedDatum(Navd, true), "1.8.0 states");
            Same(run, HeightDatums.Statement(null, "1.8.0"), Unstated18, "1.8.0 owes one");
            Same(run, HeightDatums.Statement("  ", "1.9.0"), new StatedDatum(null, true), "blank is unstated");
            Same(run, HeightDatums.Statement(null, "1.7.0"), Unstated17, "1.7.0 owes none");
            Same(run, HeightDatums.Statement(Navd, "1.7.0"), new StatedDatum(Navd, false), "a stated value is kept");
        });

        run.Case("no ground of this order: nothing to compare, so nothing is refused", () =>
        {
            run.Equal(HeightDatums.Refusal(null, new StatedDatum(Navd, true), "trees"), null, "stated");
            run.Equal(HeightDatums.Refusal(null, Unstated18, "trees"), null, "unstated on 1.8.0");
        });

        run.Case("stated and equal places", () =>
        {
            run.Equal(HeightDatums.Refusal(new GroundDatum(42, Navd), new StatedDatum(Navd, true), "trees"), null, "NAVD88");
            run.Equal(HeightDatums.Refusal(new GroundDatum(42, Egm), new StatedDatum(Egm, true), "trees"), null, "EGM2008");
        });

        run.Case("unstated against unstated places: two builds before 1.8.0 share one datum", () =>
            run.Equal(HeightDatums.Refusal(new GroundDatum(42, null), Unstated17, "trees"), null, "placed"));

        run.Case("an unstated ground against content stating EGM2008 places", () =>
            run.Equal(HeightDatums.Refusal(new GroundDatum(42, null), new StatedDatum(Egm, true), "trees"), null, "placed"));

        run.Case("a ground stating EGM2008 against content from before 1.8.0 places", () =>
            run.Equal(HeightDatums.Refusal(new GroundDatum(42, Egm), Unstated17, "trees"), null, "placed"));

        run.Case("an unstated ground against NAVD88 content is refused by name, naming the ground", () =>
        {
            string? reason = HeightDatums.Refusal(new GroundDatum(1721188, null), new StatedDatum(Navd, true), "trees");
            run.True(reason is not null, "refused");
            run.Contains(reason, Navd, "names the content's datum");
            run.Contains(reason, Egm, "names the ground's");
            run.Contains(reason, "toposolid 1721188", "names the ground");
            run.Contains(reason, "delete", "says what to do");
            run.Contains(reason, "trees", "names the content");
        });

        run.Case("a stated pair that differs is refused, either way round", () =>
        {
            run.Contains(HeightDatums.Refusal(new GroundDatum(7, Egm), new StatedDatum(Navd, true), "roads"), Navd, "NAVD88 on EGM2008");
            run.Contains(HeightDatums.Refusal(new GroundDatum(7, Navd), new StatedDatum(Egm, true), "roads"), Egm, "EGM2008 on NAVD88");
            run.Contains(HeightDatums.Refusal(new GroundDatum(7, Navd), Unstated17, "roads"), Navd, "pre-1.8.0 content on a NAVD88 ground");
        });

        run.Case("the datum is compared verbatim", () =>
            run.Contains(
                HeightDatums.Refusal(new GroundDatum(7, Egm), new StatedDatum("egm2008-orthometric", true), "roads"),
                "egm2008-orthometric",
                "a case difference is another datum"));

        run.Case("a datum this plugin does not know fails closed, naming it, even when both sides agree", () =>
        {
            const string Unknown = "a-datum-no-reader-knows";
            run.Contains(HeightDatums.Refusal(new GroundDatum(7, Egm), new StatedDatum(Unknown, true), "roads"), Unknown, "content");
            run.Contains(HeightDatums.Refusal(new GroundDatum(7, Unknown), new StatedDatum(Egm, true), "roads"), Unknown, "ground");
            run.Contains(HeightDatums.Refusal(new GroundDatum(7, Unknown), new StatedDatum(Unknown, true), "roads"), Unknown, "both");
        });

        run.Case("content unstated on a 1.8.0 manifest is skipped, not assumed to match", () =>
        {
            run.Contains(HeightDatums.Refusal(new GroundDatum(7, null), Unstated18, "roads"), "states no vertical datum", "unstated ground");
            run.Contains(HeightDatums.Refusal(new GroundDatum(7, Egm), Unstated18, "roads"), "states no vertical datum", "EGM2008 ground");
        });

        run.Case("the ground records the datum this host's block states, from 1.8.0 on only", () =>
        {
            run.Equal(HeightDatums.GroundStatement(Navd, "1.8.0"), Navd, "1.8.0 records it");
            run.Equal(HeightDatums.GroundStatement(Egm, "1.7.0"), null, "earlier: unstated, whatever the free string said");
            run.Equal(HeightDatums.GroundStatement(string.Empty, "1.8.0"), null, "nothing stated");
        });

        run.Case("the ground compared is this order's stamped one, the one this import used first", () =>
        {
            string old = TerrainIdentity.Stamp(Stem, Sha);
            string recorded = TerrainIdentity.Stamp(Stem, Sha, Navd);
            List<ExistingTerrain> grounds =
            [
                new(1, "a curator's own"),
                new(2, TerrainIdentity.Stamp("another-order", Sha, Egm)),
                new(3, old),
                new(4, recorded),
            ];

            Same(run, HeightDatums.GroundFor(grounds, Stem, used: 4), new GroundDatum(4, Navd), "the one in use");
            Same(run, HeightDatums.GroundFor(grounds, Stem, used: 0), new GroundDatum(3, null), "else the first stamped");
            Same(run, HeightDatums.GroundFor(grounds, Stem, used: 1), new GroundDatum(3, null), "a used ground not ours is not ours");
            Same(run, HeightDatums.GroundFor([new(1, null)], Stem, used: 1), null, "no ground of this order");
        });

        PlannerCarriesDatums(run);

        return run.Report("height datums");
    }

    /// <summary>
    /// The planner hands each step the datum its content states, so the shim compares and never goes
    /// looking in the manifest.
    /// </summary>
    private static void PlannerCarriesDatums(TestRun run)
    {
        string[] entries =
        [
            "Surface/SurfacePoints.csv",
            "Site/Vector/RoadSplines.geojson",
            "Vector/RoadSplines.geojson",
            "Surface/Contours.dxf",
            "Landcover/TreePoints.csv",
            "Buildings/Site.ifc",
        ];

        run.Case("a 1.8.0 US bundle: every height step carries NAVD88, owed, and so does the ground", () =>
        {
            BundleImportPlan plan = Plan(Manifest("1.8.0", Navd, ownRoads: true), entries);
            foreach (ImportStepKind kind in HeightKinds)
            {
                Same(run, Step(plan, kind)?.HeightDatum, new StatedDatum(Navd, true), kind.ToString());
            }

            Same(run, Step(plan, ImportStepKind.ToposurfaceFromPointsFile)?.HeightDatum, new StatedDatum(Navd, true), "the ground's");
        });

        run.Case("the shared road layer states its own datum, read from the layer", () =>
        {
            BundleImportPlan plan = Plan(Manifest("1.8.0", Navd, ownRoads: false), entries);
            Same(run, Step(plan, ImportStepKind.RoadCentrelines)?.HeightDatum, new StatedDatum("shared-layer-datum", true), "layer's own");
        });

        run.Case("a 1.7.0 bundle: the block's free string is not a statement, and nothing is owed", () =>
        {
            BundleImportPlan plan = Plan(Manifest("1.7.0", Egm, ownRoads: true, contents: false), entries);
            Same(run, Step(plan, ImportStepKind.ToposurfaceFromPointsFile)?.HeightDatum, Unstated17, "ground unstated");
            Same(run, Step(plan, ImportStepKind.RoadCentrelines)?.HeightDatum, Unstated17, "own-block roads ride the block");
            Same(run, Step(plan, ImportStepKind.Vegetation)?.HeightDatum, Unstated17, "trees");
        });

        run.Case("steps that place no height carry no datum", () =>
        {
            BundleImportPlan plan = Plan(Manifest("1.8.0", Navd, ownRoads: true), entries);
            Same(run, Step(plan, ImportStepKind.SetSharedCoordinates)?.HeightDatum, null, "survey point");
            run.True(Step(plan, ImportStepKind.SetSharedCoordinates) is not null, "the step is there to ask");
        });
    }

    /// <summary>Value equality for the records here, reported by their own <c>ToString</c>.</summary>
    private static void Same<T>(TestRun run, T actual, T expected, string detail)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
        {
            run.Fail($"{detail} — expected {expected?.ToString() ?? "null"}, got {actual?.ToString() ?? "null"}");
        }
    }

    private static readonly ImportStepKind[] HeightKinds =
    [
        ImportStepKind.RoadCentrelines,
        ImportStepKind.Vegetation,
        ImportStepKind.PublishedContours,
        ImportStepKind.ContextBuildings,
        ImportStepKind.LinkSiteIfc,
    ];

    private static BundleImportPlan Plan(string json, IReadOnlyList<string> entries)
    {
        BundleManifest manifest = BundleManifestReader.Parse(json);
        if (!manifest.IsValid)
        {
            throw new InvalidOperationException($"fixture refused: {manifest.Error}");
        }

        return BundleImportPlanner.Plan(manifest, entries, _ => null);
    }

    private static ImportStep? Step(BundleImportPlan plan, ImportStepKind kind)
        => plan.Steps.FirstOrDefault(step => step.Kind == kind);

    /// <param name="contents">Whether the host-neutral pointers state a datum of their own.</param>
    private static string Manifest(string version, string blockDatum, bool ownRoads, bool contents = true)
    {
        string datum(string value) => contents ? $", \"vertical_datum\": \"{value}\"" : string.Empty;
        string ownVectors = ownRoads
            ? """
              , "vectors": { "format": "geojson", "layers": [ { "name": "road_splines",
                  "path": "Site/Vector/RoadSplines.geojson", "horizontal_frame": "absolute_projected",
                  "units": "m", "vertical_reference": "absolute", "sha256": "aa" } ] }
              """
            : string.Empty;

        return $$"""
            {
              "version": "{{version}}",
              "layout": { "points_csv": "Surface/SurfacePoints.csv", "tree_points": "Landcover/TreePoints.csv",
                          "buildings_ifc": "Buildings/Site.ifc", "contours": "Surface/Contours.dxf" },
              "delivery": { "unit_system": "metric", "tier": "metric", "horizontal_epsg": 32613, "linear_unit": "m" },
              "landcover": { "tree_points": { "path": "Landcover/TreePoints.csv", "crs": "EPSG:32613",
                             "units": "m", "horizontal_units": "m"{{datum(blockDatum)}} } },
              "buildings": { "ifc": { "schema": "IFC4"{{datum(blockDatum)}} } },
              "vector": { "layers": [ { "name": "road_splines",
                  "formats": [ { "format": "geojson", "path": "Vector/RoadSplines.geojson", "sha256": "bb" } ]
                  {{datum("shared-layer-datum")}} } ] },
              "hosts": { "revit": {
                "georeference": { "crs_projected": "EPSG:32613", "vertical_datum": "{{blockDatum}}",
                  "grid_rotation_deg": 0.0,
                  "origin": { "lon": -105.6462, "lat": 36.2725,
                    "projected": { "epsg": 32613, "easting": 441959.5, "northing": 4014372.5, "linear_unit": "m" } } },
                "file_frame": { "type": "projected", "crs": "EPSG:32613", "horizontal_unit": "m", "vertical_unit": "m" },
                "toposurface_points": { "path": "Surface/SurfacePoints.csv", "format": "csv",
                  "horizontal_frame": "local_enu", "units": "m", "sha256": "cc" },
                "ifc_site": { "path": "Buildings/Site.ifc", "sha256": "dd" },
                "contours": { "path": "Surface/Contours.dxf", "horizontal_frame": "absolute_projected",
                  "vertical_reference": "absolute", "units": "m", "horizontal_units": "m", "vertical_units": "m",
                  "sha256": "ee"{{datum(blockDatum)}} }
                {{ownVectors}},
                "readiness": { "toposurface_points": { "present": true }, "ifc_site": { "present": true },
                               "contours": { "present": true } }
              } }
            }
            """;
    }
}
