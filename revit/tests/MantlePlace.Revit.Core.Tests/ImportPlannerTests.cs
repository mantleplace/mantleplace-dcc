using System.Globalization;

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

    /// <summary>
    /// A <c>hosts.revit</c> block complete enough for the TIN tier to win: an origin to reduce the
    /// DXF's absolute coordinates against, and the DXF declaring the frame it is measured in.
    /// </summary>
    /// <remarks>
    /// The origin is the real one from order <c>f93bc782</c> — Mantle Place's own first-party test
    /// site, not a customer's, which is what licenses committing it — so the numbers here and the
    /// ones pinned in <see cref="SurfaceTinTests"/> describe the same site.
    /// </remarks>
    private const string TinHostBlock = """
        "hosts": {
          "revit": {
            "georeference": {
              "crs_projected": "EPSG:32610",
              "origin": {
                "lon": -122.47853042317121,
                "lat": 37.83126164839943,
                "projected": { "epsg": 32610, "easting": 545888.5, "northing": 4187221.5, "linear_unit": "m" }
              }
            },
            "toposurface_points": {
              "path": "Surface/SurfacePoints.csv",
              "horizontal_frame": "local_enu",
              "units": "m",
              "sha256": "3f00000000000000000000000000000000000000000000000000000000000000"
            },
            "surface_dxf": {
              "path": "Surface/Surface.dxf",
              "surf_type": "TIN-3DFACE",
              "horizontal_frame": "absolute_projected",
              "units": "m",
              "sha256": "7c00000000000000000000000000000000000000000000000000000000000000"
            }
          }
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
            run.True(HasStep(plan, ImportStepKind.ContextBuildings), "context buildings copied from the site model");
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
                FindStep(plan, ImportStepKind.ContextBuildings)?.ExpectedSha256,
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

        RunTinTierCases(run);
        RunParityCases(run);
        RunDrapeCases(run);
        RunAttributionCases(run);
        RunSiteModelCases(run);
        RunSiteLocationCases(run);

        return run.Report("import planner");
    }

    /// <summary>
    /// The site model is copied, not linked: its buildings are a checklist row that starts checked,
    /// and the link is a row that starts unchecked, both planned and checked like every other step.
    /// </summary>
    private static void RunSiteModelCases(TestRun run)
    {
        ImportLayerChoice byDefault = ImportLayerChoice.Only(Enum.GetValues<ImportLayer>().Where(ImportLayers.OnByDefault));

        run.Case("by default the site model's buildings are copied, and the site model is not linked", () =>
        {
            BundleImportPlan plan = PlanFor($$"""{"version": "1.0.0", {{RevitLayout}}}""", FullBundle, byDefault);

            ImportStep? copy = FindStep(plan, ImportStepKind.ContextBuildings);
            run.Equal(copy?.EntryName, "Site/Site.ifc", "the buildings come from the site model");
            run.False(
                HasStep(plan, ImportStepKind.LinkSiteIfc),
                "a link as well would show every building twice, one copy selectable and one not");
            run.True(
                FindSkip(plan, ImportStepKind.LinkSiteIfc)?.ReasonCode == SkipReasonCode.LeftOutByChoice,
                "the log says the link was left out, and why");
        });

        run.Case("the link is offered, unchecked, and bound to the site model's digest", () =>
        {
            BundleImportPlan plan = PlanFor(
                $$"""
                {
                  "version": "1.0.0",
                  {{RevitLayout}},
                  "hosts": {
                    "revit": {
                      "ifc_site": {
                        "path": "Site/Site.ifc", "units": "m",
                        "sha256": "e1cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
                      }
                    }
                  }
                }
                """,
                FullBundle);

            ImportChecklist checklist = ImportChecklist.For(plan);
            run.True(checklist.IsChecked(ImportLayer.ContextBuildings), "the copies start checked");
            run.True(checklist.Layers.Contains(ImportLayer.SiteModel), "the link is a row");
            run.False(checklist.IsChecked(ImportLayer.SiteModel), "and it starts unchecked");
            run.Equal(
                FindStep(plan, ImportStepKind.LinkSiteIfc)?.ExpectedSha256,
                "e1cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                "opting in cannot skip the integrity check: the whole plan is verified");
        });

        run.Case("a bundle whose only artifact is the site model can still import", () =>
        {
            BundleImportPlan plan = PlanFor($$"""{"version": "1.0.0", {{RevitLayout}}}""", ["Site/Site.ifc"], byDefault);

            run.True(plan.CanImport, "the buildings are something to put in the document");
            run.True(HasStep(plan, ImportStepKind.ContextBuildings), "and they are planned");
        });

        run.Case("no site model is one skip, carrying the manifest's own reason", () =>
        {
            BundleImportPlan plan = PlanFor(
                """
                {
                  "version": "1.0.0",
                  "hosts": {
                    "revit": {
                      "readiness": { "ifc_site": { "present": false, "reason": "ifc_site_not_produced" } }
                    }
                  }
                }
                """,
                ["README.md"]);

            SkippedImport? skip = FindSkip(plan, ImportStepKind.ContextBuildings);
            run.True(skip?.ReasonCode == SkipReasonCode.ArtifactNotInManifest, "classified");
            run.Contains(skip?.Reason, "did not produce it for this order", "the manifest's reason, translated");
            run.True(FindSkip(plan, ImportStepKind.LinkSiteIfc) is null, "one artifact missing is one line, not two");
        });
    }

    /// <summary>
    /// The attribution view and the provenance record: written on every import, and never the thing
    /// that makes a bundle count as importable.
    /// </summary>
    private static void RunAttributionCases(TestRun run)
    {
        run.Case("every import writes attribution and provenance, carried on the step", () =>
        {
            BundleImportPlan plan = PlanFor(
                $$"""
                {
                  "version": "1.0.0",
                  "job_id": "job-7",
                  "order_id": "order-3",
                  {{RevitLayout}},
                  "attribution": { "sources": [ { "provider_id": "naip", "attribution_text": "NAIP" } ] }
                }
                """,
                FullBundle);
            ImportStep? step = FindStep(plan, ImportStepKind.AttributionAndProvenance);

            run.True(step?.Provenance is not null, "the record rides on the step, not on the shim (HPS-02)");
            run.Equal(step?.Provenance?.OrderId, "order-3", "the order");
            run.Equal(step?.Provenance?.JobId, "job-7", "the build");
            run.Equal(step?.Provenance?.Sources.Count ?? 0, 1, "the sources");
        });

        run.Case("a bundle with no attribution block still records where the project came from", () =>
        {
            BundleImportPlan plan = PlanFor($$"""{"version": "1.0.0", {{RevitLayout}}}""", FullBundle);

            run.True(HasStep(plan, ImportStepKind.AttributionAndProvenance), "the order and build are worth recording alone");
        });

        run.Case("attribution alone is not an import", () =>
        {
            BundleImportPlan plan = PlanFor(
                """{"version": "1.0.0", "attribution": { "sources": [ { "provider_id": "naip" } ] }}""",
                ["Metadata/manifest.json"]);

            run.False(plan.CanImport, "a drafting view over an empty model is not 'imported'");
        });

        run.Case("attribution is written before the drape, which stays last", () =>
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

            List<ImportStepKind> kinds = [.. plan.Steps.Select(step => step.Kind)];
            int attribution = kinds.IndexOf(ImportStepKind.AttributionAndProvenance);

            // Not "second to last": the site context view sits between it and the drape.
            run.True(attribution > kinds.IndexOf(ImportStepKind.ToposurfaceFromPointsFile), "after the layer it credits");
            run.True(attribution >= 0 && attribution < kinds.Count - 1, "before the drape");
            run.Equal(plan.Steps[^1].Kind.ToString(), "ImageryDrape", "the drape still last");
        });
    }


    /// <summary>
    /// Tier 1 — the surface DXF's TIN vertices — and the four ways it stands aside for the points
    /// file rather than placing a site half a world from the project origin.
    /// </summary>
    private static void RunTinTierCases(TestRun run)
    {
        run.Case("the surface TIN is the preferred toposurface path", () =>
        {
            BundleImportPlan plan = PlanFor($$"""{"version": "1.0.0", {{RevitLayout}}, {{TinHostBlock}}}""", FullBundle);

            run.True(plan.CanImport, "can import");
            run.True(HasStep(plan, ImportStepKind.ToposurfaceFromSurfaceTin), "the TIN step is planned");
            run.False(
                HasStep(plan, ImportStepKind.ToposurfaceFromPointsFile),
                "the points file is not also imported — that would build the same terrain twice");
            run.False(
                HasStep(plan, ImportStepKind.ToposurfaceFromSurfaceDxf),
                "and the DXF is not ALSO linked as CAD — it is the same file under the other kind");
        });

        run.Case("the TIN step carries the frame it will be reduced against, and its own hash", () =>
        {
            BundleImportPlan plan = PlanFor($$"""{"version": "1.0.0", {{RevitLayout}}, {{TinHostBlock}}}""", FullBundle);
            ImportStep? step = FindStep(plan, ImportStepKind.ToposurfaceFromSurfaceTin);

            run.True(step?.Frame is not null, "the origin rides on the step, not on the shim (HPS-02)");
            run.Equal(step?.Frame?.Epsg ?? 0, 32610, "and it is the bundle's own projected CRS");
            run.Contains(step?.ExpectedSha256, "7c00", "the DXF's own hash gates the extraction (HPS-34)");
            run.Equal(step?.EntryName, "Surface/Surface.dxf", "reading the TIN out of the DXF");
        });

        run.Case("a bundle that does not say what frame its DXF is in still gets its terrain", () =>
        {
            // ⛔ Not a refusal to import — a fall-through. Guessing the frame is how a site lands
            // 500 km away looking like a successful import.
            string withoutFrame = TinHostBlock.Replace(
                "\"horizontal_frame\": \"absolute_projected\",",
                string.Empty,
                StringComparison.Ordinal);
            BundleImportPlan plan = PlanFor($$"""{"version": "1.0.0", {{RevitLayout}}, {{withoutFrame}}}""", FullBundle);

            run.True(HasStep(plan, ImportStepKind.ToposurfaceFromPointsFile), "the points file is used");
            SkippedImport? skip = FindSkip(plan, ImportStepKind.ToposurfaceFromSurfaceTin);
            run.Equal(
                skip?.ReasonCode.ToString(),
                SkipReasonCode.CoordinateSystemNotSupported.ToString(),
                "and the TIN's absence is on the record");
            run.Contains(skip?.Reason, "does not say what frame", "with the reason a curator can act on");
        });

        run.Case("a frame this plugin cannot read is refused rather than approximated", () =>
        {
            string otherFrame = TinHostBlock.Replace(
                "\"horizontal_frame\": \"absolute_projected\",",
                "\"horizontal_frame\": \"state_plane_grid\",",
                StringComparison.Ordinal);
            BundleImportPlan plan = PlanFor($$"""{"version": "1.0.0", {{RevitLayout}}, {{otherFrame}}}""", FullBundle);

            run.True(HasStep(plan, ImportStepKind.ToposurfaceFromPointsFile), "the points file is used");
            run.Contains(
                FindSkip(plan, ImportStepKind.ToposurfaceFromSurfaceTin)?.Reason,
                "state_plane_grid",
                "and the skip quotes what the bundle actually said");
        });

        run.Case("a bundle publishing no origin falls back to the points file", () =>
        {
            // The points file is local_enu and needs no origin, which is exactly why it stays as
            // tier 2 rather than being retired.
            BundleImportPlan plan = PlanFor($$"""{"version": "1.0.0", {{RevitLayout}}}""", FullBundle);

            run.True(HasStep(plan, ImportStepKind.ToposurfaceFromPointsFile), "the points file is used");
            SkippedImport? skip = FindSkip(plan, ImportStepKind.ToposurfaceFromSurfaceTin);
            run.Equal(
                skip?.ReasonCode.ToString(),
                SkipReasonCode.NoSiteFrame.ToString(),
                "the DXF is declared, but there is no origin to reduce its coordinates against");
            run.Contains(skip?.Reason, "publishes no origin", "and it says so in those words");
        });

        run.Case("a DXF the archive does not carry falls back to the points file", () =>
        {
            string[] withoutDxf = Array.FindAll(
                FullBundle,
                entry => !entry.EndsWith("Surface.dxf", StringComparison.Ordinal));
            BundleImportPlan plan = PlanFor($$"""{"version": "1.0.0", {{RevitLayout}}, {{TinHostBlock}}}""", withoutDxf);

            run.True(HasStep(plan, ImportStepKind.ToposurfaceFromPointsFile), "the points file is used");
            run.Equal(
                FindSkip(plan, ImportStepKind.ToposurfaceFromSurfaceTin)?.ReasonCode.ToString(),
                SkipReasonCode.EntryNotInArchive.ToString(),
                "and the TIN says the entry is missing");
        });
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

        // A terrain-plus-drape bundle whose drape can be switched off by what the manifest says about
        // the imagery, so the two cases below differ in the drape and nothing else.
        static string TerrainAndDrape(string imagery) => $$"""
            {
              "version": "1.0.0",
              "layout": { "points_csv": "Surface/SurfacePoints.csv", "imagery_drape": "Imagery/Drape.png" },
              {{MetricGeoreference}},
              {{imagery}},
              {{DemBounds}}
            }
            """;

        string[] terrainAndDrapeBundle = ["Metadata/manifest.json", "Surface/SurfacePoints.csv", "Imagery/Drape.png"];

        run.Case("a planned drape builds the terrain on the imagery type, so the drape never retypes it", () =>
        {
            BundleImportPlan plan = PlanFor(TerrainAndDrape(ImageryWithGsd), terrainAndDrapeBundle);

            run.True(HasStep(plan, ImportStepKind.ImageryDrape), "the drape is planned");
            run.True(
                FindStep(plan, ImportStepKind.ToposurfaceFromPointsFile)?.ToposolidType == TerrainToposolidType.Imagery,
                "retyping an 80,372-point toposolid after the fact cost 409 s");
        });

        run.Case("with no drape planned the terrain is built on the project's own type", () =>
        {
            BundleImportPlan plan = PlanFor(
                TerrainAndDrape("\"imagery\": { \"present\": false }"),
                ["Metadata/manifest.json", "Surface/SurfacePoints.csv"]);

            run.False(HasStep(plan, ImportStepKind.ImageryDrape), "no drape is planned");
            run.True(
                FindStep(plan, ImportStepKind.ToposurfaceFromPointsFile)?.ToposolidType == TerrainToposolidType.Project,
                "an imagery type with no photograph to carry would be a blank layer on the ground");
        });

        run.Case("the TIN path takes the imagery type too, not only the points file", () =>
        {
            // The preferred topo path, with a drape placed around the same origin the TIN is reduced
            // against: 1,430 × 1,420 m either side of it, which is what the probe's pixel grid covers.
            string layout = RevitLayout.Replace(
                "\"contours\": \"Surface/Contours.dxf\"",
                "\"contours\": \"Surface/Contours.dxf\", \"imagery_drape\": \"Imagery/Drape.png\"",
                StringComparison.Ordinal);
            const string tinDemBounds = """
                "elevation": { "dem": { "crs": "EPSG:32610",
                  "bounds_target_crs": [545173.5, 4186511.5, 546603.5, 4187931.5] } }
                """;

            BundleImportPlan plan = PlanFor(
                $$"""{"version": "1.0.0", {{layout}}, {{TinHostBlock}}, {{ImageryWithGsd}}, {{tinDemBounds}}}""",
                [.. FullBundle, "Imagery/Drape.png"]);

            run.True(HasStep(plan, ImportStepKind.ImageryDrape), "the drape is planned");
            run.True(
                FindStep(plan, ImportStepKind.ToposurfaceFromSurfaceTin)?.ToposolidType == TerrainToposolidType.Imagery,
                "whichever tier builds the terrain, it is built on the type the drape will want");
        });

        run.Case("a drape the planner refused leaves the terrain on the project's own type", () =>
        {
            // The pointer is there and the image is not readable: the drape is skipped, not planned,
            // and the decision follows the plan rather than the manifest's intent.
            BundleImportPlan plan = PlanFor(TerrainAndDrape(ImageryWithGsd), terrainAndDrapeBundle, _ => null);

            run.False(HasStep(plan, ImportStepKind.ImageryDrape), "the drape is skipped");
            run.True(
                FindStep(plan, ImportStepKind.ToposurfaceFromPointsFile)?.ToposolidType == TerrainToposolidType.Project,
                "only a drape that will run earns the imagery type");
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
            run.True(HasStep(plan, ImportStepKind.LandCover), "land cover planned");
            run.True(HasStep(plan, ImportStepKind.Water), "water planned");
            run.True(HasStep(plan, ImportStepKind.RoadPolygons), "road surfaces planned");
            run.True(HasStep(plan, ImportStepKind.Vegetation), "vegetation planned");

            run.Equal(
                FindStep(plan, ImportStepKind.RoadCentrelines)?.EntryName,
                "Vector/RoadSplines.geojson",
                "the road entry comes from the vector format table, not from convention");
            run.Equal(
                FindStep(plan, ImportStepKind.LandCover)?.EntryName,
                "Vector/LandCover.geojson",
                "the land-cover entry comes from its own layer, not from the land use");
            run.Equal(
                FindStep(plan, ImportStepKind.Water)?.EntryName,
                "Vector/Water.geojson",
                "the water entry comes from its own layer");
            run.Equal(
                FindStep(plan, ImportStepKind.RoadPolygons)?.EntryName,
                "Vector/RoadPolygons.geojson",
                "the road surfaces come from road_polygons, never from the centrelines beside them");
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

        run.Case("roads with no road surfaces is a derived layer nobody produced, not an area without roads", () =>
        {
            // road_polygons is derived from road and is best-effort (spec/format.md §6.3). Saying
            // "no roads in this bundle" here would be a statement about the area, and a wrong one —
            // and telling the curator to re-download would send them after something nobody has.
            BundleImportPlan plan = PlanFor(
                ParityManifest(MetricGeoreference).Replace("road_polygons", "road_surfaces_someday", StringComparison.Ordinal),
                ParityBundle);

            SkippedImport? skip = FindSkip(plan, ImportStepKind.RoadPolygons);
            run.True(skip?.ReasonCode == SkipReasonCode.DerivedLayerNotPublished, "the skip is about the derivation");
            run.Contains(skip?.Reason, "carries roads but no road surfaces", "it says the roads are there");
            run.Contains(skip?.Reason, "says nothing about the roads in this area", "and refuses to report the area as roadless");
            run.False(HasStep(plan, ImportStepKind.RoadPolygons), "nothing is cut");
            run.True(HasStep(plan, ImportStepKind.RoadCentrelines), "the centrelines still import");
        });

        run.Case("no road layer either is the ordinary absence", () =>
        {
            // Both layers gone: this AOI really may have no roads, so the ordinary "not in this
            // bundle" reason stands rather than a sentence about a derivation that was never owed.
            BundleImportPlan plan = PlanFor(
                ParityManifest(MetricGeoreference)
                    .Replace("\"name\": \"road_polygons\"", "\"name\": \"road_surfaces_someday\"", StringComparison.Ordinal)
                    .Replace("\"name\": \"road\"", "\"name\": \"rail_someday\"", StringComparison.Ordinal),
                ParityBundle);

            run.True(
                FindSkip(plan, ImportStepKind.RoadPolygons)?.ReasonCode == SkipReasonCode.ArtifactNotInManifest,
                "no road layer to contradict, so the plain absence is the true one");
        });

        run.Case("land cover without land use is its own layer, not a renamed one", () =>
        {
            // Two different Overture layers. A layer with no features in the area is left out of
            // vector.layers entirely, so a bundle with land cover and no land use is ordinary.
            BundleImportPlan plan = PlanFor(
                $$"""
                {
                  "version": "1.0.0",
                  {{MetricGeoreference}},
                  "vector": {
                    "layers": [
                      { "name": "land_cover", "formats": [{ "format": "geojson", "path": "Vector/LandCover.geojson" }] }
                    ]
                  }
                }
                """,
                ["README.md", "Vector/LandCover.geojson"]);

            run.True(HasStep(plan, ImportStepKind.LandCover), "the land cover is planned");
            run.Equal(
                FindSkip(plan, ImportStepKind.SiteBoundaries)?.ReasonCode == SkipReasonCode.ArtifactNotInManifest,
                true,
                "and the land use is absent, not read from the land-cover file");
            run.True(plan.CanImport, "land cover alone is importable content");
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

    /// <summary>
    /// The project's place on Earth beyond the survey point, and the view and filter that let a
    /// curator find what an import made.
    /// </summary>
    private static void RunSiteLocationCases(TestRun run)
    {
        run.Case("the site location is the own block's lat and lon, verbatim (HPS-33)", () =>
        {
            BundleImportPlan plan = PlanFor(ParityManifest(MetricGeoreference), ParityBundle);

            SiteLocationPlacement? location = FindStep(plan, ImportStepKind.SetSiteLocation)?.SiteLocation;
            run.True(location is not null, "the site location is planned");
            run.Within(location?.LatitudeDeg ?? 0.0, 38.46130517000308, 1e-12, "latitude verbatim");
            run.Within(location?.LongitudeDeg ?? 0.0, -105.32557885004304, 1e-12, "longitude verbatim, west negative");
            run.Within(
                location?.LatitudeRadians ?? 0.0,
                38.46130517000308 * Math.PI / 180.0,
                1e-15,
                "in radians, which is what SiteLocation takes");
            run.Within(
                location?.LongitudeRadians ?? 0.0,
                -105.32557885004304 * Math.PI / 180.0,
                1e-15,
                "in radians, sign kept");
        });

        run.Case("it follows the survey point, so the two place-on-Earth steps sit together", () =>
        {
            BundleImportPlan plan = PlanFor(ParityManifest(MetricGeoreference), ParityBundle);
            List<ImportStepKind> kinds = [.. plan.Steps.Select(step => step.Kind)];

            run.Equal(
                kinds.IndexOf(ImportStepKind.SetSiteLocation),
                kinds.IndexOf(ImportStepKind.SetSharedCoordinates) + 1,
                "directly after the shared coordinates");
        });

        run.Case("no lat/lon in the own block is a named skip, and delivery's pair is not borrowed", () =>
        {
            // delivery.local_origin carries a lon/lat too, and the survey point falls back to it. The
            // site location does not: the issue that asked for it names the own block, and a sun
            // placed from a value this host was not told to read is a sun nobody can audit.
            BundleImportPlan plan = PlanFor(
                $$"""
                {
                  "version": "1.0.0",
                  {{RevitLayout}},
                  "delivery": {
                    "tier": "local_ft", "linear_unit": "ft",
                    "local_origin": {
                      "lon": -105.6462, "lat": 36.2725, "utm_epsg": 32613,
                      "easting_m": 441959.5, "northing_m": 4014372.5
                    }
                  }
                }
                """,
                FullBundle);

            run.False(HasStep(plan, ImportStepKind.SetSiteLocation), "no site location invented");
            run.True(
                FindSkip(plan, ImportStepKind.SetSiteLocation)?.ReasonCode == SkipReasonCode.NoGeographicOrigin,
                "skipped for want of a published lat/lon");
            run.Contains(
                FindSkip(plan, ImportStepKind.SetSiteLocation)?.Reason,
                "sun",
                "the curator is told what is left wrong");
        });

        run.Case("half a lat/lon pair is no pair", () =>
        {
            BundleImportPlan plan = PlanFor(
                ParityManifest(MetricGeoreference.Replace(
                    "\"lon\": -105.32557885004304,",
                    string.Empty,
                    StringComparison.Ordinal)),
                ParityBundle);

            run.True(
                FindSkip(plan, ImportStepKind.SetSiteLocation)?.ReasonCode == SkipReasonCode.NoGeographicOrigin,
                "a latitude alone places no sun");
        });

        run.Case("a lat or lon off the globe is refused, not wrapped", () =>
        {
            // Revit throws on a latitude past a pole and silently wraps a longitude past the
            // antimeridian. Wrapping would put the sun over some other site with no word said.
            foreach ((string field, string bad) in (ReadOnlySpan<(string, string)>)
                [("\"lat\": 38.46130517000308", "\"lat\": 91.0"), ("\"lon\": -105.32557885004304", "\"lon\": 254.67")])
            {
                BundleImportPlan plan = PlanFor(
                    ParityManifest(MetricGeoreference.Replace(field, bad, StringComparison.Ordinal)),
                    ParityBundle);

                run.False(HasStep(plan, ImportStepKind.SetSiteLocation), $"{bad} is not applied");
                run.True(
                    FindSkip(plan, ImportStepKind.SetSiteLocation)?.ReasonCode
                        == SkipReasonCode.GeographicOriginOutOfRange,
                    $"{bad} is named as out of range");
            }
        });

        run.Case("a published time zone is written as published, after the coordinates", () =>
        {
            SiteLocationPlacement? location = FindStep(
                PlanFor(WithTimeZone(-7.0, "America/Denver", "true"), ParityBundle),
                ImportStepKind.SetSiteLocation)?.SiteLocation;

            run.True(location?.TimeZone is not null, "the zone travels with the site location");
            run.Within(location?.TimeZoneToWrite(9.0) ?? 0.0, -7.0, 0.0, "the published zone, not the project's");
            run.Contains(location?.LogLine(9.0), "UTC-7 (America/Denver), as published", "the log names the zone set");
            run.Contains(location?.LogLine(9.0), "observes daylight saving", "and what Revit will not let the add-in set");
        });

        run.Case("with no published time zone the project keeps its own, and the log says so", () =>
        {
            SiteLocationPlacement? location = FindStep(
                PlanFor(ParityManifest(MetricGeoreference), ParityBundle),
                ImportStepKind.SetSiteLocation)?.SiteLocation;

            run.True(location?.TimeZone is null, "a 1.0.x bundle publishes none");
            run.Within(location?.TimeZoneToWrite(-5.0) ?? 0.0, -5.0, 0.0, "the zone read before the coordinates moved it");
            run.Contains(location?.LogLine(-5.0), "publishes no time zone, so the project keeps its own, UTC-5", "said plainly");
        });

        run.Case("fractional zones are written as fractions, and named in hours and minutes", () =>
        {
            foreach ((double hours, string named) in (ReadOnlySpan<(double, string)>)
                [(5.5, "UTC+5:30"), (5.75, "UTC+5:45"), (-3.5, "UTC-3:30"), (-9.5, "UTC-9:30"), (0.0, "UTC+0")])
            {
                SiteLocationPlacement? location = FindStep(
                    PlanFor(WithTimeZone(hours, "Zone/Under/Test", "false"), ParityBundle),
                    ImportStepKind.SetSiteLocation)?.SiteLocation;

                run.Within(location?.TimeZoneToWrite(1.0) ?? double.NaN, hours, 0.0, $"{hours} verbatim");
                run.Contains(location?.LogLine(1.0), named, $"{hours} reads as {named}");
            }
        });

        run.Case("a zone east of +12 wraps a day back to fit Revit, and the log says what that costs", () =>
        {
            // Revit's SiteLocation.TimeZone takes -12 to +12. Wrapping keeps the clock and moves the
            // date a day; clamping would keep the date and put every hour of a sun study one out.
            foreach ((double published, double written) in (ReadOnlySpan<(double, double)>)
                [(13.0, -11.0), (14.0, -10.0), (12.75, -11.25), (12.0, 12.0), (-12.0, -12.0)])
            {
                SiteLocationPlacement? location = FindStep(
                    PlanFor(WithTimeZone(published, "Pacific/Test", "false"), ParityBundle),
                    ImportStepKind.SetSiteLocation)?.SiteLocation;

                run.Within(location?.TimeZoneToWrite(0.0) ?? double.NaN, written, 0.0, $"{published} is written as {written}");
                run.Equal(location?.TimeZone?.IsWrapped ?? false, published != written, $"{published} wrapped only past the edge");
            }

            string? report = FindStep(
                PlanFor(WithTimeZone(13.0, "Pacific/Tongatapu", "false"), ParityBundle),
                ImportStepKind.SetSiteLocation)?.SiteLocation?.LogLine(0.0);
            run.Contains(report, "Pacific/Tongatapu is UTC+13", "the published zone is named");
            run.Contains(report, "so the site's time zone is UTC-11", "and the one written");
            run.Contains(report, "a calendar day later", "and what moved");
        });

        run.Case("an offset of a day or more is not a zone, so the project keeps its own", () =>
        {
            foreach (double hours in (double[])[24.0, -24.0, 30.0])
            {
                SiteLocationPlacement? location = FindStep(
                    PlanFor(WithTimeZone(hours, "Broken/Zone", "false"), ParityBundle),
                    ImportStepKind.SetSiteLocation)?.SiteLocation;

                run.Within(location?.TimeZoneToWrite(-6.0) ?? double.NaN, -6.0, 0.0, $"{hours} is not written");
                run.Contains(location?.LogLine(-6.0), "which is not a time zone", $"{hours} is named as unusable");
            }
        });

        run.Case("an unstated observes_dst says nothing about daylight saving", () =>
        {
            SiteLocationPlacement? location = FindStep(
                PlanFor(WithTimeZone(1.0, "Europe/Paris", null), ParityBundle),
                ImportStepKind.SetSiteLocation)?.SiteLocation;

            run.False(location?.LogLine(0.0)?.Contains("daylight", StringComparison.Ordinal) ?? true, "no claim either way");
        });

        run.Case("the context view comes after every step that stamps an element, and before the drape", () =>
        {
            BundleImportPlan plan = PlanFor(
                $$"""
                {
                  "version": "1.0.0",
                  "layout": { "points_csv": "Surface/SurfacePoints.csv", "imagery_drape": "Imagery/Drape.png",
                              "tree_points": "Landcover/TreePoints.csv" },
                  {{MetricGeoreference}},
                  "landcover": { "tree_points": { "path": "Landcover/TreePoints.csv", "crs": "EPSG:32613" } },
                  {{ImageryWithGsd}},
                  {{DemBounds}}
                }
                """,
                ["Metadata/manifest.json", "Surface/SurfacePoints.csv", "Imagery/Drape.png", "Landcover/TreePoints.csv"]);
            List<ImportStepKind> kinds = [.. plan.Steps.Select(step => step.Kind)];
            int view = kinds.IndexOf(ImportStepKind.SiteContextView);

            run.True(view >= 0, "the context view is planned");
            run.True(view > kinds.IndexOf(ImportStepKind.ToposurfaceFromPointsFile), "after the terrain");
            run.True(view > kinds.IndexOf(ImportStepKind.Vegetation), "after the trees");
            run.Equal(view, kinds.Count - 2, "second to last");
            run.True(kinds[^1] == ImportStepKind.ImageryDrape, "the drape stays last");
            run.Equal(kinds.Count(kind => kind == ImportStepKind.SiteContextView), 1, "once");
        });

        run.Case("with no drape, the context view is the last step", () =>
        {
            BundleImportPlan plan = PlanFor(ParityManifest(MetricGeoreference), ParityBundle);

            run.True(plan.Steps[^1].Kind == ImportStepKind.SiteContextView, "last");
        });

        run.Case("placing the project and naming a view is not an import", () =>
        {
            // The survey point, the site location and the context view change project settings and
            // add an empty view. A bundle whose plan held only those would report "imported" over a
            // model with nothing in it — and a view and filter for nothing to be found.
            BundleImportPlan plan = PlanFor(
                $$"""{"version": "1.0.0", {{MetricGeoreference}}}""",
                ["Metadata/manifest.json"]);

            run.False(plan.CanImport, "nothing to import");
            run.False(HasStep(plan, ImportStepKind.SiteContextView), "and no view for nothing");
        });

        run.Case("only the settings and credits kinds are not content", () =>
        {
            foreach (ImportStepKind kind in Enum.GetValues<ImportStepKind>())
            {
                bool settings = kind is ImportStepKind.SetSharedCoordinates
                    or ImportStepKind.SetSiteLocation
                    or ImportStepKind.AttributionAndProvenance
                    or ImportStepKind.SiteContextView;
                run.Equal(ImportStepKinds.ImportsContent(kind), !settings, $"{kind}");
            }
        });
    }

    private static readonly ImportStepKind[] ParityKinds =
    [
        ImportStepKind.RoadCentrelines,
        ImportStepKind.SiteBoundaries,
        ImportStepKind.LandCover,
        ImportStepKind.Water,
        ImportStepKind.RoadPolygons,
        ImportStepKind.Vegetation,
    ];

    private static readonly string[] ParityBundle =
    [
        "README.md",
        "Vector/RoadSplines.geojson",
        "Vector/LandUse.geojson",
        "Vector/LandCover.geojson",
        "Vector/Water.geojson",
        "Vector/RoadPolygons.geojson",
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

    /// <summary>
    /// <see cref="ParityManifest"/> at MPB 1.1.0, with a <c>location.time_zone</c> block;
    /// <paramref name="observesDst"/> is the JSON literal, or <c>null</c> to leave the field out.
    /// </summary>
    private static string WithTimeZone(double hours, string iana, string? observesDst) =>
        ParityManifest(MetricGeoreference).Replace(
            "\"version\": \"1.0.0\",",
            string.Format(
                CultureInfo.InvariantCulture,
                "\"version\": \"1.1.0\", \"location\": {{ \"time_zone\": {{ \"iana\": \"{0}\", \"utc_offset_standard_h\": {1}{2}, \"tzdata_version\": \"2025b\" }} }},",
                iana,
                hours,
                observesDst is null ? string.Empty : ", \"observes_dst\": " + observesDst),
            StringComparison.Ordinal);

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
              },
              {
                "name": "land_cover",
                "formats": [{ "format": "geojson", "path": "Vector/LandCover.geojson", "sha256": "cc" }]
              },
              {
                "name": "water",
                "formats": [{ "format": "geojson", "path": "Vector/Water.geojson", "sha256": "dd" }]
              },
              {
                "name": "road",
                "formats": [{ "format": "geojson", "path": "Vector/Road.geojson", "sha256": "ee" }]
              },
              {
                "name": "road_polygons",
                "formats": [{ "format": "geojson", "path": "Vector/RoadPolygons.geojson", "sha256": "ff" }]
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

    private static BundleImportPlan PlanFor(string manifestJson, IReadOnlyList<string> entries, ImportLayerChoice choice)
        => BundleImportPlanner.Plan(BundleManifestReader.Parse(manifestJson), entries, _ => DrapePixels, choice);

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
