namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Import policy for Revit: which topo path wins, what happens when a pointer names an entry the
/// bundle does not carry, and when shared coordinates may be published.
/// </summary>
/// <remarks>
/// These are host semantics, not contract semantics, so they live here rather than in the shared
/// corpus — what a toposurface is belongs to Revit's own suite (HPS-03, <c>DOC-06</c>).
/// </remarks>
internal static class ImportPlannerTests
{
    private const string RevitLayout = """
        "layout": {
          "points_csv": "Surface/SurfacePoints.csv",
          "surface_dxf": "Surface/Surface.dxf",
          "buildings_ifc": "Site/Site.ifc",
          "landxml": "Surface/Surface.landxml",
          "contours": "Surface/Contours.dxf"
        }
        """;

    private static readonly string[] FullBundle =
    [
        "README.md",
        "Metadata/manifest.json",
        "Surface/SurfacePoints.csv",
        "Surface/Surface.dxf",
        "Surface/Surface.landxml",
        "Surface/Contours.dxf",
        "Site/Site.ifc",
    ];

    internal static int Run()
    {
        TestRun run = new();

        run.Case("the points file is the preferred toposurface path", () =>
        {
            BundleImportPlan plan = PlanFor($$"""{"version": "1.0.0", {{RevitLayout}}}""", FullBundle);

            run.True(plan.CanImport, "can import");
            run.True(HasStep(plan, ImportStepKind.ToposurfaceFromPointsFile), "points-file step planned");
            run.False(
                HasStep(plan, ImportStepKind.ToposurfaceFromSurfaceDxf),
                "the surface DXF is not also imported — that would build the same terrain twice");
            run.True(HasStep(plan, ImportStepKind.LinkSiteIfc), "IFC site linked");
        });

        run.Case("a pointer naming an entry the bundle lacks falls back to the surface DXF", () =>
        {
            string[] withoutPoints = Array.FindAll(
                FullBundle,
                entry => !entry.EndsWith("SurfacePoints.csv", StringComparison.Ordinal));
            BundleImportPlan plan = PlanFor($$"""{"version": "1.0.0", {{RevitLayout}}}""", withoutPoints);

            run.True(HasStep(plan, ImportStepKind.ToposurfaceFromSurfaceDxf), "fell back to the DXF");
            SkippedImport? skip = FindSkip(plan, ImportStepKind.ToposurfaceFromPointsFile);
            run.True(skip is not null, "the fallback is explained");
            run.Contains(skip?.Reason, "SurfacePoints.csv", "the skip names the missing entry");
        });

        run.Case("no topo artifact surfaces the manifest's own reason, TRANSLATED (HPS-36)", () =>
        {
            // Surfaced, not echoed. HPS-36 requires the manifest's reason rather than a generic
            // one; it does not require the producer's token. That distinction is the whole of the
            // defect this guards — `dcc_readiness` reasons are an open vocabulary until v19 and
            // include `emit_threw:<stage>`, so echoing put internal stage identifiers in dialogs.
            BundleImportPlan plan = PlanFor(
                """
                {
                  "version": "1.0.0",
                  "hosts": {
                    "revit": {
                      "readiness": {
                      "toposurface_points": { "present": false, "reason": "points_csv_not_produced" },
                      "ifc_site": { "present": false, "reason": "ifc_site_not_produced" },
                      "surface_dxf": { "present": false, "reason": "surface_dxf_not_produced" }
                    }
                    }
                  }
                }
                """,
                ["README.md"]);

            run.False(plan.CanImport, "nothing to import");
            SkippedImport? skip = FindSkip(plan, ImportStepKind.ToposurfaceFromPointsFile);
            run.Contains(
                skip?.Reason,
                "did not produce it for this order",
                "the manifest's stated reason is surfaced, as a sentence");
            run.False(
                skip?.Reason.Contains("points_csv_not_produced", StringComparison.Ordinal) ?? true,
                "and the producer's token is not");
            run.Equal(
                skip?.ReasonCode == SkipReasonCode.ArtifactNotInManifest,
                true,
                "the skip is classified for anything that needs to branch rather than print");

            // The generic sentence is what the reason REPLACES. Seeing both would mean the
            // translation ran and then got appended to the fallback.
            run.False(
                skip?.Reason.Contains("add the Revit deliverables", StringComparison.Ordinal) ?? true,
                "the generic fallback did not also fire");
        });

        run.Case("per-artifact units drive the import dialog", () =>
        {
            BundleImportPlan plan = PlanFor(
                $$"""
                {
                  "version": "1.0.0",
                  {{RevitLayout}},
                  "delivery": { "unit_system": "imperial", "tier": "sp_ftus", "linear_unit": "ftUS" },
                  "elevation": { "points_csv": { "path": "Surface/SurfacePoints.csv", "units": "ftUS" } }
                }
                """,
                FullBundle);

            ImportStep? step = FindStep(plan, ImportStepKind.ToposurfaceFromPointsFile);
            run.True(step?.Units == LinearUnit.UsSurveyFoot, $"US survey feet, got {step?.Units}");
        });

        run.Case("a stale copy under a non-root folder is NOT resolved", () =>
        {
            // Regression: a plain "any entry ending in /<pointer>" rule reached into Backup/ and
            // imported last month's terrain. Only a single archive-wide root folder is tolerated.
            string[] withBackupOnly =
            [
                "README.md",
                "Backup/Surface/SurfacePoints.csv",
                "Site/Site.ifc",
            ];
            BundleImportPlan plan = PlanFor($$"""{"version": "1.0.0", {{RevitLayout}}}""", withBackupOnly);

            run.False(
                HasStep(plan, ImportStepKind.ToposurfaceFromPointsFile),
                "the backup copy is not silently imported");
        });

        run.Case("an artifact unit this host does not know fails closed", () =>
        {
            BundleImportPlan plan = PlanFor(
                $$"""
                {
                  "version": "1.0.0",
                  {{RevitLayout}},
                  "elevation": { "points_csv": { "path": "Surface/SurfacePoints.csv", "units": "cubit" } }
                }
                """,
                FullBundle);

            run.False(
                HasStep(plan, ImportStepKind.ToposurfaceFromPointsFile),
                "not imported at a guessed scale");
            run.Contains(
                FindSkip(plan, ImportStepKind.ToposurfaceFromPointsFile)?.Reason,
                "cubit",
                "the skip names the unit it could not read");

            // Regression: the DXF fallback used to defeat the fail-closed check outright — same
            // terrain, same emitter, same suspect scale, imported anyway.
            run.False(
                HasStep(plan, ImportStepKind.ToposurfaceFromSurfaceDxf),
                "and the DXF is not used as a fallback around it");
        });

        run.Case("shared coordinates are published only from pre-derived values (HPS-33)", () =>
        {
            BundleImportPlan withoutOrigin = PlanFor($$"""{"version": "1.0.0", {{RevitLayout}}}""", FullBundle);
            run.False(
                HasStep(withoutOrigin, ImportStepKind.SetSharedCoordinates),
                "no survey point is invented when the manifest carries none");
            run.Contains(
                FindSkip(withoutOrigin, ImportStepKind.SetSharedCoordinates)?.Reason,
                "no pre-derived survey point",
                "the user is told the model sits in the project frame");

            BundleImportPlan withOrigin = PlanFor(
                $$"""
                {
                  "version": "1.0.0",
                  {{RevitLayout}},
                  "delivery": {
                    "unit_system": "imperial", "tier": "local_ft", "linear_unit": "ft",
                    "local_origin": {
                      "lon": -105.6462, "lat": 36.2725, "utm_epsg": 32613,
                      "easting_m": 441959.5, "northing_m": 4014372.5
                    }
                  }
                }
                """,
                FullBundle);

            ImportStep? step = FindStep(withOrigin, ImportStepKind.SetSharedCoordinates);
            run.True(step is not null, "the pre-derived origin is applied");
            run.Within(step?.SurveyPoint?.Origin.Easting ?? 0.0, 441959.5, 1e-6, "easting applied verbatim");
            run.Within(step?.SurveyPoint?.Origin.Northing ?? 0.0, 4014372.5, 1e-6, "northing applied verbatim");
            run.Equal(step?.SurveyPoint?.Origin.Epsg ?? 0, 32613, "EPSG applied verbatim");
        });

        run.Case("the placement's elevation and angle are DERIVED, not literal zeros", () =>
        {
            // The shim used to pass `0.0, 0.0`. Both were right for the bundles on hand and neither
            // came from anywhere, so a rotated grid would have been placed wrong with nothing to
            // catch it. The fixture states a rotation the ETL does not emit today for exactly that
            // reason: a hardcoded zero passes a zero-rotation fixture.
            BundleImportPlan plan = PlanFor(
                $$"""
                {
                  "version": "1.0.0",
                  {{RevitLayout}},
                  "hosts": {
                    "revit": {
                      "georeference": {
                        "crs_projected": "EPSG:2231", "grid_rotation_deg": 90.0,
                        "origin": {
                          "projected": {"epsg": 2231, "easting": 1450131.2,
                                        "northing": 13171825.6, "linear_unit": "ftUS"}
                        }
                      }
                    }
                  }
                }
                """,
                FullBundle);

            SurveyPointPlacement? placement = FindStep(plan, ImportStepKind.SetSharedCoordinates)?.SurveyPoint;
            run.True(placement is not null, "shared coordinates are planned from the own block");
            run.Within(placement?.AngleRadians ?? 0.0, Math.PI / 2.0, 1e-12, "grid_rotation_deg, in radians");
            run.Within(placement?.ElevationM ?? -1.0, 0.0, 1e-12,
                "elevation is zero because every artifact's Z is ABSOLUTE orthometric height");
            run.True(
                placement?.Origin.LinearUnit == LinearUnit.UsSurveyFoot,
                "and the origin keeps its own unit through to the shim");
        });

        run.Case("an unstated grid rotation is an axis-aligned grid", () =>
        {
            BundleImportPlan plan = PlanFor(
                $$"""
                {
                  "version": "1.0.0",
                  {{RevitLayout}},
                  "delivery": {
                    "tier": "local_ft", "linear_unit": "ft",
                    "local_origin": {"utm_epsg": 32613, "easting_m": 441959.5, "northing_m": 4014372.5}
                  }
                }
                """,
                FullBundle);

            run.Within(
                FindStep(plan, ImportStepKind.SetSharedCoordinates)?.SurveyPoint?.AngleRadians ?? -1.0,
                0.0,
                1e-12,
                "the tier that publishes no rotation has none to apply");
        });

        run.Case("each artifact step carries the hash its own manifest block declared", () =>
        {
            BundleImportPlan plan = PlanFor(
                $$"""
                {
                  "version": "1.0.0",
                  {{RevitLayout}},
                  "hosts": {
                    "revit": {
                      "toposurface_points": {
                        "path": "Surface/SurfacePoints.csv", "units": "m",
                        "sha256": "3faaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                      },
                      "ifc_site": {
                        "path": "Site/Site.ifc", "units": "m",
                        "sha256": "e1cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
                      }
                    }
                  }
                }
                """,
                FullBundle);

            // Bound per artifact, not per bundle: a planner that resolved one hash and reused it
            // would check the IFC's bytes against the CSV's digest and report a corruption that is
            // not there.
            run.Equal(
                FindStep(plan, ImportStepKind.ToposurfaceFromPointsFile)?.ExpectedSha256,
                "3faaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "points file");
            run.Equal(
                FindStep(plan, ImportStepKind.LinkSiteIfc)?.ExpectedSha256,
                "e1cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                "IFC site");
        });

        run.Case("a v18 step carries no hash — unknown, not empty (HPS-27)", () =>
        {
            BundleImportPlan plan = PlanFor($$"""{"version": "1.0.0", {{RevitLayout}}}""", FullBundle);

            run.True(
                FindStep(plan, ImportStepKind.ToposurfaceFromPointsFile)?.ExpectedSha256 is null,
                "below v19 nothing was published, so the check is skipped rather than failed");
        });

        run.Case("an enclosing folder in the zip still resolves", () =>
        {
            string[] rezipped = Array.ConvertAll(FullBundle, entry => "mantleplace_2026-08-09_abcd1234/" + entry);
            BundleImportPlan plan = PlanFor($$"""{"version": "1.0.0", {{RevitLayout}}}""", rezipped);

            ImportStep? step = FindStep(plan, ImportStepKind.ToposurfaceFromPointsFile);
            run.Equal(
                step?.EntryName,
                "mantleplace_2026-08-09_abcd1234/Surface/SurfacePoints.csv",
                "resolved to the archive's own spelling");
        });

        run.Case("an ambiguous suffix resolves to nothing rather than a coin flip", () =>
        {
            string[] ambiguous =
            [
                "a/Surface/SurfacePoints.csv",
                "b/Surface/SurfacePoints.csv",
                "Site/Site.ifc",
            ];
            BundleImportPlan plan = PlanFor($$"""{"version": "1.0.0", {{RevitLayout}}}""", ambiguous);

            run.False(
                HasStep(plan, ImportStepKind.ToposurfaceFromPointsFile),
                "two candidates means no match");
        });

        run.Case("a refused manifest blocks the plan and carries its reason forward", () =>
        {
            BundleImportPlan plan = PlanFor("""{"version": 17}""", FullBundle);

            run.False(plan.CanImport, "cannot import");
            run.Contains(plan.BlockedReason, "no longer supported", "the manifest's refusal is the blocker");
            run.Equal(plan.Steps.Count, 0, "no steps");
        });

        run.Case("Civil 3D and linework deliverables are named, not silently dropped", () =>
        {
            BundleImportPlan plan = PlanFor($$"""{"version": "1.0.0", {{RevitLayout}}}""", FullBundle);

            run.Equal(plan.AvailableButNotImported.Count, 2, "LandXML and contours are both listed");
        });

        RunParityCases(run);
        RunDrapeCases(run);

        return run.Report("import planner");
    }

    /// <summary>
    /// The satellite drape — Forma's last parity row, and the only artifact whose
    /// placement this host declines to take on the manifest's word alone.
    /// </summary>
    /// <remarks>
    /// The drape's declared extent lives in a sibling host's block, and the host-neutral field
    /// carrying the same numbers is undeclared by the published schema. So the planner corroborates
    /// the inferred extent against the image's own pixel grid, and these cases are mostly about the
    /// refusals — which is the half that would otherwise only exist inside Revit.
    /// </remarks>
    private static void RunDrapeCases(TestRun run)
    {
        run.Case("the DEM's bounds place the drape when the image's own grid backs them up", () =>
        {
            BundleImportPlan plan = PlanFor(
                DrapeManifest(MetricGeoreference, ImageryWithGsd, DemBounds),
                DrapeBundle);

            ImportStep? step = FindStep(plan, ImportStepKind.ImageryDrape);
            run.True(step is not null, "the drape is planned");

            DrapePlacement? placement = step?.Drape;
            run.True(placement is not null, "the step carries its placement");
            run.Within(placement?.LeftM ?? 0.0, -715.0, 0.001, "west edge in frame-local metres");
            run.Within(placement?.BottomM ?? 0.0, -710.0, 0.001, "south edge in frame-local metres");
            run.Within(placement?.RightM ?? 0.0, 715.0, 0.001, "east edge in frame-local metres");
            run.Within(placement?.TopM ?? 0.0, 710.0, 0.001, "north edge in frame-local metres");
            run.Equal(placement?.PixelSize.Width ?? 0, 4767, "the corroborated pixel grid rides along");
            run.False(
                placement?.ExtentFromDrapeBlock ?? true,
                "the placement records that this came from the DEM, not from a drape block");
        });

        run.Case("a drape block states its own extent, and is NOT second-guessed", () =>
        {
            // Deliberately a rectangle the pixel grid would REJECT if it were checked: 1000 m wide
            // against 4767 px at 0.3 m. A contract is applied verbatim (HPS-33); corroboration is
            // what an inference gets, and re-deriving the declared value is the habit this host is
            // built to avoid.
            BundleImportPlan plan = PlanFor(
                DrapeManifest(
                    MetricGeoreference,
                    """
                    "imagery": { "present": true, "gsd_m": 0.3, "drape": {
                      "extent": [471095.0, 4256550.0, 472095.0, 4257550.0], "extent_crs": "EPSG:32613" } }
                    """,
                    DemBounds),
                DrapeBundle);

            DrapePlacement? placement = FindStep(plan, ImportStepKind.ImageryDrape)?.Drape;
            run.True(placement is not null, "the drape is planned from its own block");
            run.Within(placement?.WidthM ?? 0.0, 1000.0, 0.001, "the drape block's extent won");
            run.True(placement?.ExtentFromDrapeBlock ?? false, "and the provenance says so");
        });

        run.Case("an inferred extent the image contradicts is refused, not draped", () =>
        {
            BundleImportPlan plan = PlanFor(
                DrapeManifest(
                    MetricGeoreference,
                    ImageryWithGsd,
                    """
                    "elevation": { "dem": { "crs": "EPSG:32613",
                      "bounds_target_crs": [470880.0, 4256340.0, 474310.0, 4257760.0] } }
                    """),
                DrapeBundle);

            SkippedImport? skip = FindSkip(plan, ImportStepKind.ImageryDrape);
            run.Equal(
                skip?.ReasonCode == SkipReasonCode.ExtentNotCorroborated,
                true,
                "a 3430 m extent over a 1430 m image is refused");
            run.Contains(skip?.Reason, "4767", "the refusal shows the curator both numbers");
        });

        run.Case("no ground resolution means nothing to corroborate against, so no drape", () =>
        {
            BundleImportPlan plan = PlanFor(
                DrapeManifest(MetricGeoreference, "\"imagery\": { \"present\": true }", DemBounds),
                DrapeBundle);

            run.Equal(
                FindSkip(plan, ImportStepKind.ImageryDrape)?.ReasonCode == SkipReasonCode.ExtentNotCorroborated,
                true,
                "an unknown GSD fails closed rather than being assumed");
        });

        run.Case("a drape this bundle publishes no readable extent for is refused", () =>
        {
            // Today's real bundles, if `elevation.dem.bounds_target_crs` ever stops being emitted:
            // the only remaining extent is `unreal.imagery_drape`, which this host may not read.
            BundleImportPlan plan = PlanFor(
                DrapeManifest(
                    MetricGeoreference,
                    ImageryWithGsd,
                    "\"unreal\": { \"imagery_drape\": { \"extent\": [470880.0, 4256340.0, 472310.0, 4257760.0] } }"),
                DrapeBundle);

            run.Equal(
                FindSkip(plan, ImportStepKind.ImageryDrape)?.ReasonCode == SkipReasonCode.ExtentNotCorroborated,
                true,
                "a sibling host's extent is not a fallback (HPS-36)");
        });

        run.Case("an inverted extent is refused rather than quietly flipped", () =>
        {
            BundleImportPlan plan = PlanFor(
                DrapeManifest(
                    MetricGeoreference,
                    ImageryWithGsd,
                    """
                    "elevation": { "dem": { "crs": "EPSG:32613",
                      "bounds_target_crs": [472310.0, 4257760.0, 470880.0, 4256340.0] } }
                    """),
                DrapeBundle);

            run.Equal(
                FindSkip(plan, ImportStepKind.ImageryDrape)?.ReasonCode == SkipReasonCode.ExtentNotCorroborated,
                true,
                "normalising it would drape the image mirrored and look plausible");
        });

        run.Case("a file that is not a readable PNG is refused before Revit ever sees it", () =>
        {
            BundleImportPlan plan = PlanFor(
                DrapeManifest(MetricGeoreference, ImageryWithGsd, DemBounds),
                DrapeBundle,
                _ => null);

            SkippedImport? skip = FindSkip(plan, ImportStepKind.ImageryDrape);
            run.Equal(
                skip?.ReasonCode == SkipReasonCode.ExtentNotCorroborated,
                true,
                "an unreadable image is a skip, not a texture Revit cannot decode");
            run.Contains(skip?.Reason, "Drape.png", "the refusal names the file");
        });

        run.Case("a bundle that says it has no imagery is not sent back to the vault", () =>
        {
            BundleImportPlan plan = PlanFor(
                DrapeManifest(MetricGeoreference, "\"imagery\": { \"present\": false }", DemBounds),
                ["Metadata/manifest.json"]);

            SkippedImport? skip = FindSkip(plan, ImportStepKind.ImageryDrape);
            run.Equal(
                skip?.ReasonCode == SkipReasonCode.ArtifactNotInManifest,
                true,
                "the producer's own absence is the reason");
            run.Contains(skip?.Reason, "Re-ordering will not change that", "and re-downloading is not advised");
        });

        run.Case("no pre-derived origin means no drape, never a guessed rectangle", () =>
        {
            BundleImportPlan plan = PlanFor(
                DrapeManifest(NoGeoreference, ImageryWithGsd, DemBounds),
                DrapeBundle);

            run.Equal(
                FindSkip(plan, ImportStepKind.ImageryDrape)?.ReasonCode == SkipReasonCode.NoSiteFrame,
                true,
                "the drape inherits the frame gate the other placed artifacts have");
        });

        run.Case("a foot-tier origin cannot place the DEM's metric extent", () =>
        {
            BundleImportPlan plan = PlanFor(
                DrapeManifest(FootGeoreference, ImageryWithGsd, DemBounds),
                DrapeBundle);

            run.Equal(
                FindSkip(plan, ImportStepKind.ImageryDrape)?.ReasonCode
                    == SkipReasonCode.CoordinateSystemNotSupported,
                true,
                "subtracting a UTM easting from a State-Plane one is ~2000 km of plausible-looking error");
        });

        run.Case("a pointer naming an entry the archive lacks is classified as such", () =>
        {
            BundleImportPlan plan = PlanFor(
                DrapeManifest(MetricGeoreference, ImageryWithGsd, DemBounds),
                ["Metadata/manifest.json"]);

            run.Equal(
                FindSkip(plan, ImportStepKind.ImageryDrape)?.ReasonCode == SkipReasonCode.EntryNotInArchive,
                true,
                "a missing file is not a missing pointer");
        });

        run.Case("the drape is planned LAST, after the terrain it textures", () =>
        {
            BundleImportPlan plan = PlanFor(
                $$"""
                {
                  "version": "1.0.0",
                  "layout": { "points_csv": "Surface/SurfacePoints.csv", "imagery_drape": "Imagery/Drape.png" },
                  {{MetricGeoreference}},
                  {{ImageryWithGsd}},
                  {{DemBounds}}
                }
                """,
                ["Metadata/manifest.json", "Surface/SurfacePoints.csv", "Imagery/Drape.png"]);

            run.True(plan.Steps.Count > 1, "both the terrain and the drape are planned");
            run.Equal(
                plan.Steps[^1].Kind == ImportStepKind.ImageryDrape,
                true,
                "the shim textures a toposolid that exists, and risks nothing queued behind it");
        });
    }

    /// <summary>
    /// The three Forma-parity layers: roads, site boundaries and vegetation. Each is
    /// placed against the bundle's own pre-derived origin, and each fails closed rather than being
    /// placed in a frame nobody checked.
    /// </summary>
    private static void RunParityCases(TestRun run)
    {
        run.Case("the parity layers are planned when the bundle carries them and states an origin", () =>
        {
            BundleImportPlan plan = PlanFor(ParityManifest(MetricGeoreference), ParityBundle);

            run.True(HasStep(plan, ImportStepKind.RoadCentrelines), "roads planned");
            run.True(HasStep(plan, ImportStepKind.SiteBoundaries), "site boundaries planned");
            run.True(HasStep(plan, ImportStepKind.Vegetation), "vegetation planned");

            run.Equal(
                FindStep(plan, ImportStepKind.RoadCentrelines)?.EntryName,
                "Vector/RoadSplines.geojson",
                "the road entry comes from the vector format table, not from convention");
            run.Equal(
                FindStep(plan, ImportStepKind.Vegetation)?.EntryName,
                "Landcover/TreePoints.csv",
                "the tree entry comes from the layout pointer");
            run.True(
                FindStep(plan, ImportStepKind.Vegetation)?.Frame is not null,
                "the step carries the frame it is to be placed in");
        });

        run.Case("a bundle carrying ONLY parity layers is still importable", () =>
        {
            // The predicate that decides this deliberately excludes SetSharedCoordinates, because
            // that step changes project settings and creates nothing. These three create elements,
            // so a bundle with roads and no terrain is an import, not a blocked one.
            BundleImportPlan plan = PlanFor(
                $$"""
                {
                  "version": "1.0.0",
                  "layout": { "tree_points": "Landcover/TreePoints.csv" },
                  {{MetricGeoreference}},
                  "landcover": { "tree_points": { "path": "Landcover/TreePoints.csv", "crs": "EPSG:32613" } }
                }
                """,
                ["README.md", "Landcover/TreePoints.csv"]);

            run.True(plan.CanImport, "vegetation alone is importable content");
            run.Equal(plan.BlockedReason, string.Empty, "and nothing is blocked");
        });

        run.Case("no pre-derived origin means the parity layers are skipped, never guessed into place", () =>
        {
            BundleImportPlan plan = PlanFor(ParityManifest(NoGeoreference), ParityBundle);

            foreach (ImportStepKind kind in ParityKinds)
            {
                run.Equal(
                    FindSkip(plan, kind)?.ReasonCode == SkipReasonCode.NoSiteFrame,
                    true,
                    $"{kind} skipped for want of a frame");
                run.False(HasStep(plan, kind), $"{kind} not planned");
            }

            run.Contains(
                FindSkip(plan, ImportStepKind.RoadCentrelines)?.Reason,
                "shared coordinates",
                "the skip points at the fix rather than at the internals");
        });

        run.Case("a foot-tier origin refuses BOTH the geographic layers and the metric UTM one", () =>
        {
            // The origin is State Plane feet; the GeoJSON layers are lon/lat and the tree CSV is
            // AOI-UTM metres whatever the tier. Neither can be brought into that frame by the one
            // forward projection HPS-45 permits, and subtracting a UTM easting from a State Plane
            // one yields a number that looks like a coordinate and is ~2000 km wrong.
            BundleImportPlan plan = PlanFor(ParityManifest(FootGeoreference), ParityBundle);

            foreach (ImportStepKind kind in ParityKinds)
            {
                run.Equal(
                    FindSkip(plan, kind)?.ReasonCode == SkipReasonCode.CoordinateSystemNotSupported,
                    true,
                    $"{kind} fails closed on the frame's CRS");
            }

            run.Contains(
                FindSkip(plan, ImportStepKind.Vegetation)?.Reason,
                "EPSG:2231",
                "the skip names the CRS it could not place into");
        });

        run.Case("a pointer naming an entry the archive lacks is classified as such", () =>
        {
            BundleImportPlan plan = PlanFor(ParityManifest(MetricGeoreference), ["README.md"]);

            foreach (ImportStepKind kind in ParityKinds)
            {
                run.Equal(
                    FindSkip(plan, kind)?.ReasonCode == SkipReasonCode.EntryNotInArchive,
                    true,
                    $"{kind} names the missing entry");
            }
        });

        run.Case("a bundle with no parity layers at all says so without inventing paths", () =>
        {
            BundleImportPlan plan = PlanFor($$"""{"version": "1.0.0", {{RevitLayout}}, {{MetricGeoreference}}}""", FullBundle);

            foreach (ImportStepKind kind in ParityKinds)
            {
                run.Equal(
                    FindSkip(plan, kind)?.ReasonCode == SkipReasonCode.ArtifactNotInManifest,
                    true,
                    $"{kind} absent from the manifest");
            }
        });

        run.Case("a road layer shipping no geojson is absent, not a gpkg this host cannot read", () =>
        {
            BundleImportPlan plan = PlanFor(
                $$"""
                {
                  "version": "1.0.0",
                  {{MetricGeoreference}},
                  "vector": {
                    "layers": [
                      { "name": "road_splines", "formats": [{ "format": "gpkg", "path": "Vector/RoadSplines.gpkg" }] }
                    ]
                  }
                }
                """,
                ["README.md", "Vector/RoadSplines.gpkg"]);

            run.Equal(
                FindSkip(plan, ImportStepKind.RoadCentrelines)?.ReasonCode == SkipReasonCode.ArtifactNotInManifest,
                true,
                "no geojson means no road layer");
        });
    }

    private static readonly ImportStepKind[] ParityKinds =
    [
        ImportStepKind.RoadCentrelines,
        ImportStepKind.SiteBoundaries,
        ImportStepKind.Vegetation,
    ];

    private static readonly string[] ParityBundle =
    [
        "README.md",
        "Vector/RoadSplines.geojson",
        "Vector/LandUse.geojson",
        "Landcover/TreePoints.csv",
    ];

    private const string DemBounds = """
        "elevation": { "dem": { "crs": "EPSG:32613",
          "bounds_target_crs": [470880.0, 4256340.0, 472310.0, 4257760.0] } }
        """;

    private const string ImageryWithGsd = "\"imagery\": { \"present\": true, \"gsd_m\": 0.3 }";

    private static readonly string[] DrapeBundle = ["Metadata/manifest.json", "Imagery/Drape.png"];

    /// <summary>
    /// A drape-only bundle: the layout pointer, a georeference, and whichever imagery/elevation
    /// blocks the case is about.
    /// </summary>
    private static string DrapeManifest(string georeference, string imagery, string elevation) =>
        $$"""
        {
          "version": "1.0.0",
          "layout": { "imagery_drape": "Imagery/Drape.png" },
          {{georeference}},
          {{imagery}},
          {{elevation}}
        }
        """;

    private const string MetricGeoreference = """
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
        }
        """;

    private const string FootGeoreference = """
        "hosts": {
          "revit": {
            "georeference": {
              "crs_projected": "EPSG:2231",
              "origin": {
                "lon": -105.32557885004304,
                "lat": 38.46130517000308,
                "projected": { "epsg": 2231, "easting": 1450131.2, "northing": 13171825.6, "linear_unit": "ftUS" }
              }
            }
          }
        }
        """;

    private const string NoGeoreference = "\"hosts\": { \"revit\": {} }";

    private static string ParityManifest(string georeference) =>
        $$"""
        {
          "version": "1.0.0",
          "layout": { "tree_points": "Landcover/TreePoints.csv" },
          {{georeference}},
          "landcover": { "tree_points": { "path": "Landcover/TreePoints.csv", "crs": "EPSG:32613" } },
          "vector": {
            "layers": [
              {
                "name": "road_splines",
                "formats": [{ "format": "geojson", "path": "Vector/RoadSplines.geojson", "sha256": "aa" }]
              },
              {
                "name": "land_use",
                "formats": [{ "format": "geojson", "path": "Vector/LandUse.geojson", "sha256": "bb" }]
              }
            ]
          }
        }
        """;

    /// <summary>
    /// The fixture bundle's real drape grid: 4767 × 4733 px at 0.3 m over a 1430 × 1420 m extent.
    /// </summary>
    /// <remarks>
    /// Real numbers rather than round ones on purpose. 4767 × 0.3 is 1430.1 against a stated 1430.0,
    /// so the corroboration is exercised against the third-of-a-pixel disagreement that is always
    /// there — a tolerance tuned to invented round numbers would pass here and fail on every real
    /// bundle.
    /// </remarks>
    private static readonly ImageSize DrapePixels = new(4767, 4733);

    private static BundleImportPlan PlanFor(string manifestJson, IReadOnlyList<string> entries)
        => PlanFor(manifestJson, entries, _ => DrapePixels);

    private static BundleImportPlan PlanFor(
        string manifestJson,
        IReadOnlyList<string> entries,
        Func<string, ImageSize?> probeImageSize)
        => BundleImportPlanner.Plan(BundleManifestReader.Parse(manifestJson), entries, probeImageSize);

    private static bool HasStep(BundleImportPlan plan, ImportStepKind kind) => FindStep(plan, kind) is not null;

    private static ImportStep? FindStep(BundleImportPlan plan, ImportStepKind kind)
    {
        foreach (ImportStep step in plan.Steps)
        {
            if (step.Kind == kind)
            {
                return step;
            }
        }

        return null;
    }

    private static SkippedImport? FindSkip(BundleImportPlan plan, ImportStepKind kind)
    {
        foreach (SkippedImport skip in plan.Skipped)
        {
            if (skip.Kind == kind)
            {
                return skip;
            }
        }

        return null;
    }
}
