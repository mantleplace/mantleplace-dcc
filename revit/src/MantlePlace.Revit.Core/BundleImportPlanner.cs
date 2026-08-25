using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>
/// Turns a manifest plus a bundle's entry list into a <see cref="BundleImportPlan"/>. Pure.
/// </summary>
public static class BundleImportPlanner
{
    /// <summary>
    /// How far a corroborated extent may disagree with the image's own grid, in pixels per axis.
    /// </summary>
    /// <remarks>
    /// Two pixels is comfortably above the rounding that is always present — a drape whose grid is
    /// derived by dividing an extent by a GSD lands within a fraction of a pixel — and nowhere near
    /// the disagreement it exists to catch. The failure this guards is a drape cut to the imagery's
    /// own footprint rather than to the DEM's, which differs by hundreds of metres, not centimetres.
    /// A tolerance tight enough to trip on rounding would be worse than none: it would be switched
    /// off the first time it fired.
    /// </remarks>
    internal const double ExtentTolerancePixels = 2.0;

    /// <summary>
    /// Plans an import.
    /// </summary>
    /// <param name="manifest">A manifest already parsed by <see cref="BundleManifestReader"/>.</param>
    /// <param name="entryNames">Every entry name in the bundle archive.</param>
    /// <param name="probeImageSize">
    /// Reads an archive entry's pixel dimensions from its header, by the entry name this planner
    /// resolved — <c>null</c> when the entry is not a readable image.
    /// </param>
    /// <remarks>
    /// <paramref name="probeImageSize"/> is REQUIRED, with no forgiving overload, for the reason
    /// <c>LocalBundleArchive.Extract</c>'s digest argument is: the last optional integrity input this
    /// codebase had was parsed and then consumed by nothing at all, because an overload let callers
    /// not mention it. A defaulted probe would let a future call site plan a drape whose
    /// extent nothing corroborated, and it would plan it silently.
    /// </remarks>
    public static BundleImportPlan Plan(
        BundleManifest manifest,
        IEnumerable<string> entryNames,
        Func<string, ImageSize?> probeImageSize)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(entryNames);
        ArgumentNullException.ThrowIfNull(probeImageSize);

        if (!manifest.IsValid)
        {
            return new BundleImportPlan { CanImport = false, BlockedReason = manifest.Error };
        }

        BundleEntryIndex entries = new(entryNames);
        List<ImportStep> steps = [];
        List<SkippedImport> skipped = [];
        List<string> notImported = [];

        PlanToposurface(manifest, entries, steps, skipped);
        PlanSiteIfc(manifest, entries, steps, skipped);
        PlanSharedCoordinates(manifest, steps, skipped);
        PlanSiteContext(manifest, entries, steps, skipped);

        // Last, and last for three reasons. The drape needs the terrain step to have run before it —
        // it textures the toposolid that step built; it also retypes the site-boundary subdivisions
        // this import created, which must exist before they can be draped; and it is the one
        // kind whose Revit API surface has never executed anywhere, so if any step is going to fail
        // it should be the one with nothing queued behind it. Execute runs a transaction per step and
        // does not catch, so the order of this list is also the order of what survives.
        PlanImageryDrape(manifest, entries, steps, skipped, probeImageSize);

        NoteAvailableButNotImported(manifest, entries, notImported);

        // Every kind but SetSharedCoordinates changes the document, so any one of them is an import.
        // That step is excluded because it changes project settings and builds nothing — a bundle
        // whose only planned step was the survey point would report "imported" over an empty model.
        // The parity layers are on the creating side of that line: a bundle carrying roads and no
        // terrain still has something to put in the document. So is the drape, which builds no
        // geometry but does build a material and retype the terrain it is applied to.
        bool canImport = steps.Exists(step => step.Kind != ImportStepKind.SetSharedCoordinates);

        return new BundleImportPlan
        {
            CanImport = canImport,
            Steps = steps,
            Skipped = skipped,
            AvailableButNotImported = notImported,
            BlockedReason = canImport
                ? string.Empty
                : "This bundle carries nothing this plugin can import into Revit. "
                  + DescribeAbsence(manifest),
        };
    }

    /// <summary>
    /// Three tiers, and only ever one of them: the surface DXF's TIN vertices, then the points file,
    /// then the same DXF as a linked CAD instance the user converts by hand. Running two would build
    /// the same surface twice.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>The TIN is preferred over the points file, and it is not a fidelity-for-time trade.</b>
    /// Both describe the same ground, cut by the same emitter, and on the bundle this was measured
    /// against the TIN is the <em>cheaper</em> of the two: 75,203 vertices against the grid's 80,940,
    /// and 73,537 against 80,372 once both are cleaned. What differs is where the vertices are. The
    /// points file is a perfectly regular lattice — 285 × 284 at exactly 5.000 m, its distinct X and
    /// Y counts multiplying to the point count exactly — so every cell's four corners are cocircular,
    /// Delaunay triangulation is degenerate, and the triangulator picks slivers and fans arbitrarily.
    /// That is what makes the terrain read as faceted no matter how well the imagery is draped. TIN
    /// vertices are adaptive, dense on slopes and sparse on flats, and therefore in general position.
    /// </para>
    /// <para>
    /// The points file keeps its place as tier 2 rather than being retired: it is <c>local_enu</c>
    /// already, so it needs no origin, and it is the only topo path a bundle with no publishable
    /// georeference has. The DXF is <c>absolute_projected</c> — eastings and northings around
    /// 500 000 m, where Revit's precision warnings start — so tier 1 needs a
    /// <see cref="SiteFrame"/> to subtract the published origin, and falls through when there is
    /// none rather than dropping the site 500 km from the project origin.
    /// </para>
    /// <para>
    /// ⛔ Tier 3 is the same file as tier 1 under a different kind, and that is deliberate rather
    /// than a duplicate: a DXF this plugin cannot parse into a TIN can still be linked for the user
    /// to convert, and the two kinds have different extraction lifetimes because Revit keeps a path
    /// to a link and no path to a toposolid.
    /// </para>
    /// </remarks>
    private static void PlanToposurface(
        BundleManifest manifest,
        BundleEntryIndex entries,
        List<ImportStep> steps,
        List<SkippedImport> skipped)
    {
        // The crop rides on the step, so the shim never has to work out what the area of interest was
        // — and so the case that matters, a bundle whose frame cannot project one, is a null a
        // headless test can assert rather than a branch inside Revit (HPS-02).
        SiteFrame? frame = SiteFrame.For(manifest);
        SurfaceCropWindow? crop = SurfaceCrop.For(manifest, frame);

        if (TryPlanTin(manifest, entries, crop, frame, out ImportStep? tinStep, out SkippedImport? tinSkip))
        {
            steps.Add(tinStep!);
            return;
        }

        if (TryPlanArtifact(
                manifest.ToposurfacePoints,
                manifest,
                entries,
                ImportStepKind.ToposurfaceFromPointsFile,
                manifest.Readiness.ToposurfacePoints,
                "toposurface points file",
                out ImportStep? pointsStep,
                out SkippedImport? pointsSkip,
                out bool pointsUnitUnreadable,
                crop))
        {
            skipped.Add(tinSkip!);
            steps.Add(pointsStep!);
            return;
        }

        // An unreadable unit is a statement about the BUNDLE, not about one file. Falling back to
        // the surface DXF here would turn the fail-closed check into a detour: the DXF carries the
        // same terrain, cut by the same emitter, at the same suspect scale. Stop instead.
        if (pointsUnitUnreadable)
        {
            skipped.Add(tinSkip!);
            skipped.Add(pointsSkip!);
            skipped.Add(new SkippedImport
            {
                Kind = ImportStepKind.ToposurfaceFromSurfaceDxf,
                ReasonCode = SkipReasonCode.FallbackSuppressed,
                Reason = "Not used as a fallback: this bundle declares a unit this plugin cannot read, so "
                    + "every surface in it is suspect, not just the points file.",
            });
            return;
        }

        if (TryPlanArtifact(
                manifest.SurfaceDxf,
                manifest,
                entries,
                ImportStepKind.ToposurfaceFromSurfaceDxf,
                manifest.Readiness.SurfaceDxf,
                "surface DXF",
                out ImportStep? dxfStep,
                out SkippedImport? dxfSkip,
                out _))
        {
            skipped.Add(tinSkip!);
            skipped.Add(new SkippedImport
            {
                Kind = ImportStepKind.ToposurfaceFromPointsFile,
                ReasonCode = SkipReasonCode.SupersededByFallback,
                Reason = pointsSkip!.Reason + " Falling back to the surface DXF.",
            });
            steps.Add(dxfStep!);
            return;
        }

        skipped.Add(tinSkip!);
        skipped.Add(pointsSkip!);
        skipped.Add(dxfSkip!);
    }

    /// <summary>
    /// Tier 1: the toposolid built from the surface DXF's TIN vertices, when the bundle publishes
    /// everything needed to place them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two gates on top of the usual artifact checks, both about the frame. The file is
    /// <c>absolute_projected</c>, so it needs an origin to be measured against and that origin's CRS
    /// has to be the one the coordinates are in. Neither is derived: the frame token and the CRS are
    /// both published, and <see cref="SiteFrame"/> only ever subtracts (<c>HPS-33</c>).
    /// </para>
    /// <para>
    /// ⛔ An unreadable unit here falls through to the points file rather than suppressing it. That
    /// is not a hole in the rule two tiers below — that rule refuses to fall back from a suspect file
    /// onto <em>the same terrain from the same emitter at the same suspect scale</em>. The points
    /// file is a different deliverable carrying its own unit declaration, and if that one is also
    /// unreadable the rule fires there, where it always did.
    /// </para>
    /// </remarks>
    private static bool TryPlanTin(
        BundleManifest manifest,
        BundleEntryIndex entries,
        SurfaceCropWindow? crop,
        SiteFrame? frame,
        out ImportStep? step,
        out SkippedImport? skipped)
    {
        const ImportStepKind kind = ImportStepKind.ToposurfaceFromSurfaceTin;
        const string label = "surface TIN";

        step = null;

        if (!TryPlanArtifact(
                manifest.SurfaceDxf,
                manifest,
                entries,
                kind,
                manifest.Readiness.SurfaceDxf,
                label,
                out ImportStep? candidate,
                out skipped,
                out _,
                crop,
                frame))
        {
            return false;
        }

        if (frame is null)
        {
            skipped = new SkippedImport
            {
                Kind = kind,
                ReasonCode = SkipReasonCode.NoSiteFrame,
                Reason = "The surface TIN is measured in real-world coordinates and this bundle "
                    + "publishes no origin to place it against, so the terrain was built from the "
                    + "points file instead.",
            };
            return false;
        }

        string? declared = manifest.SurfaceDxf?.HorizontalFrame;
        if (!string.Equals(declared, BundleManifestReader.ProjectedFrame, StringComparison.Ordinal))
        {
            skipped = new SkippedImport
            {
                Kind = kind,
                ReasonCode = SkipReasonCode.CoordinateSystemNotSupported,
                Reason = declared is null
                    ? "This bundle does not say what frame its surface DXF is measured in, so its "
                        + "vertices were not used as the terrain; the points file was."
                    : $"The surface DXF is in \"{declared}\", which this plugin does not know how to "
                        + "place, so the terrain was built from the points file instead.",
            };
            return false;
        }

        if (!frame.CanPlaceProjected(frame.Epsg))
        {
            skipped = new SkippedImport
            {
                Kind = kind,
                ReasonCode = SkipReasonCode.CoordinateSystemNotSupported,
                Reason = "This bundle publishes no projected coordinate system for its origin, so "
                    + "the surface DXF's absolute coordinates could not be reduced to the site; the "
                    + "terrain was built from the points file instead.",
            };
            return false;
        }

        step = candidate;
        skipped = null;
        return true;
    }

    private static void PlanSiteIfc(
        BundleManifest manifest,
        BundleEntryIndex entries,
        List<ImportStep> steps,
        List<SkippedImport> skipped)
    {
        if (TryPlanArtifact(
                manifest.SiteIfc,
                manifest,
                entries,
                ImportStepKind.LinkSiteIfc,
                manifest.Readiness.IfcSite,
                "IFC site model",
                out ImportStep? step,
                out SkippedImport? skip,
                out _))
        {
            steps.Add(step!);
            return;
        }

        skipped.Add(skip!);
    }

    /// <summary>
    /// Shared coordinates are published only from values the manifest pre-derived. This host does
    /// not compute a survey point, and it does not read one out of another host's block to get one
    /// (HPS-33) — but it DOES read its own, so a v19 bundle on any tier is placed
    /// from <c>revit.georeference.origin.projected</c> and only a bundle that publishes no origin
    /// at all yields the named skip.
    /// </summary>
    private static void PlanSharedCoordinates(
        BundleManifest manifest,
        List<ImportStep> steps,
        List<SkippedImport> skipped)
    {
        if (manifest.SurveyPoint is { IsUsable: true } origin)
        {
            steps.Add(new ImportStep
            {
                Kind = ImportStepKind.SetSharedCoordinates,
                SurveyPoint = new SurveyPointPlacement
                {
                    Origin = origin,

                    // An unstated rotation is an axis-aligned grid. That is not HPS-20's "unknown
                    // is not zero" being waived — it is the only tier that omits the field
                    // (`delivery.local_origin`, which has no rotation to state), and the block that
                    // does state one says zero by construction: the emitters reproject per-vertex,
                    // so meridian convergence is already absorbed into the coordinates. A published
                    // non-zero value is still applied verbatim rather than assumed away.
                    AngleRadians = (manifest.Georeference.GridRotationDeg ?? 0.0) * Math.PI / 180.0,
                },
            });
            return;
        }

        skipped.Add(new SkippedImport
        {
            Kind = ImportStepKind.SetSharedCoordinates,
            ReasonCode = SkipReasonCode.NoSurveyPoint,
            Reason = "This bundle carries no pre-derived survey point for Revit, so the model is placed in "
                + "the project's own frame and shared coordinates are left untouched. Set them by hand if you "
                + "need real-world positioning.",
        });
    }

    /// <summary>
    /// The three Forma-parity layers: road centrelines, site boundaries and vegetation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// All three carry coordinates that are absolute — lon/lat for the vector layers, AOI-UTM for the
    /// tree points — where every artifact this host imported before them was either already local
    /// (the toposurface points) or placed by Revit's own link machinery (the DXF, the IFC). So they
    /// share a gate the older kinds do not have: without the manifest's pre-derived origin there is
    /// no frame to place them in, and this host does not work one out for itself (<c>HPS-33</c>).
    /// </para>
    /// <para>
    /// They are planned independently of the terrain. A road centreline carries its own draped Z, so
    /// it is correct with or without a toposolid under it, and coupling the two would mean a bundle
    /// whose points file was superseded silently lost its roads as well.
    /// </para>
    /// </remarks>
    private static void PlanSiteContext(
        BundleManifest manifest,
        BundleEntryIndex entries,
        List<ImportStep> steps,
        List<SkippedImport> skipped)
    {
        SiteFrame? frame = SiteFrame.For(manifest);

        PlanPlacedArtifact(
            manifest.RoadSplines,
            frame,
            entries,
            ImportStepKind.RoadCentrelines,
            "road centrelines",
            steps,
            skipped);

        PlanPlacedArtifact(
            manifest.LandUse,
            frame,
            entries,
            ImportStepKind.SiteBoundaries,
            "site boundaries",
            steps,
            skipped);

        PlanPlacedArtifact(
            manifest.TreePoints,
            frame,
            entries,
            ImportStepKind.Vegetation,
            "trees",
            steps,
            skipped);
    }

    /// <summary>
    /// Resolves one artifact whose coordinates have to be brought into the bundle's frame, or
    /// explains why they cannot be.
    /// </summary>
    /// <remarks>
    /// The CRS check is the point of this method. A bundle cut on a State-Plane foot tier publishes
    /// its origin in that CRS, while the tree points stay AOI-UTM and the GeoJSON stays lon/lat —
    /// so the frame genuinely cannot place them, and subtracting one CRS's easting from another's
    /// yields a number that looks like a coordinate and is ~2000 km wrong. Failing closed here is
    /// the same rule <c>HPS-35</c> applies to an unreadable unit one level up.
    /// </remarks>
    private static void PlanPlacedArtifact(
        BundleArtifact? artifact,
        SiteFrame? frame,
        BundleEntryIndex entries,
        ImportStepKind kind,
        string label,
        List<ImportStep> steps,
        List<SkippedImport> skipped)
    {
        if (artifact is null)
        {
            skipped.Add(new SkippedImport
            {
                Kind = kind,
                ReasonCode = SkipReasonCode.ArtifactNotInManifest,
                Reason = $"No {label} in this bundle. Open your vault at mantle.place/vault, add the Revit "
                    + "deliverables to this order, then re-download.",
            });
            return;
        }

        string? entry = entries.Resolve(artifact.Path);
        if (entry is null)
        {
            skipped.Add(new SkippedImport
            {
                Kind = kind,
                ReasonCode = SkipReasonCode.EntryNotInArchive,
                Reason = $"The manifest points the {label} at \"{artifact.Path}\", but no such entry is in this "
                    + "bundle. Re-download it from your vault at mantle.place/vault.",
            });
            return;
        }

        if (frame is null)
        {
            skipped.Add(new SkippedImport
            {
                Kind = kind,
                ReasonCode = SkipReasonCode.NoSiteFrame,
                Reason = $"The {label} are positioned in real-world coordinates, and this bundle publishes no "
                    + "origin to place them against, so they were left out rather than dropped at the project "
                    + "origin. The terrain still imports; set shared coordinates by hand if you need them.",
            });
            return;
        }

        bool geographic = string.Equals(
            artifact.HorizontalFrame,
            BundleManifestReader.GeographicFrame,
            StringComparison.Ordinal);

        bool placeable = geographic
            ? frame.CanPlaceGeographic
            : frame.CanPlaceProjected(GeoProjection.TryParseEpsg(artifact.HorizontalFrame) ?? 0);

        if (!placeable)
        {
            skipped.Add(new SkippedImport
            {
                Kind = kind,
                ReasonCode = SkipReasonCode.CoordinateSystemNotSupported,
                Reason = $"The {label} are in \"{artifact.HorizontalFrame}\", which this plugin cannot place "
                    + $"into this bundle's coordinate system (EPSG:{frame.Epsg}). Importing them anyway would "
                    + "put them a long way from the site, so they were left out.",
            });
            return;
        }

        steps.Add(new ImportStep
        {
            Kind = kind,
            EntryName = entry,
            ExpectedSha256 = artifact.Sha256,
            Frame = frame,
        });
    }

    /// <summary>
    /// The satellite drape — Forma's last parity row, and the only artifact this host stretches over
    /// ground rather than placing point by point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It reuses the whole placement path: the extent goes through <see cref="SiteFrame"/>, so a
    /// bundle with no origin yields <see cref="SkipReasonCode.NoSiteFrame"/> and a foot-tier bundle
    /// whose DEM stays metric UTM yields <see cref="SkipReasonCode.CoordinateSystemNotSupported"/>,
    /// both without a line of arithmetic written for this row.
    /// </para>
    /// <para>
    /// What is new is where the extent comes from. The drape's declared extent is in
    /// <c>unreal.imagery_drape</c>, which this host may not read; the host-neutral
    /// <c>elevation.dem.bounds_target_crs</c> carries the same four numbers but is not declared by
    /// the published schema at all. So the fallback is used only when the image's own pixel grid
    /// agrees with it, and the day <c>imagery.drape</c> lands the fallback and this corroboration
    /// both become dead code.
    /// </para>
    /// </remarks>
    private static void PlanImageryDrape(
        BundleManifest manifest,
        BundleEntryIndex entries,
        List<ImportStep> steps,
        List<SkippedImport> skipped,
        Func<string, ImageSize?> probeImageSize)
    {
        void Skip(SkipReasonCode code, string reason) => skipped.Add(new SkippedImport
        {
            Kind = ImportStepKind.ImageryDrape,
            ReasonCode = code,
            Reason = reason,
        });

        // The producer's own "there is no imagery" beats anything this host could infer from a
        // missing pointer, and it is the one absence a re-download cannot fix.
        if (manifest.ImageryAbsentByDeclaration)
        {
            Skip(
                SkipReasonCode.ArtifactNotInManifest,
                "This bundle states that it carries no satellite imagery, so the terrain is imported "
                + "untextured. Re-ordering will not change that — the imagery was unavailable for this "
                + "site when the bundle was cut.");
            return;
        }

        if (manifest.ImageryDrape is not { } drape)
        {
            Skip(
                SkipReasonCode.ArtifactNotInManifest,
                "No satellite imagery drape in this bundle. Open your vault at mantle.place/vault, add "
                + "the Revit deliverables to this order, then re-download.");
            return;
        }

        if (entries.Resolve(drape.Path) is not { } entry)
        {
            Skip(
                SkipReasonCode.EntryNotInArchive,
                $"The manifest points the satellite imagery at \"{drape.Path}\", but no such entry is in "
                + "this bundle. Re-download it from your vault at mantle.place/vault.");
            return;
        }

        if (SiteFrame.For(manifest) is not { } frame)
        {
            Skip(
                SkipReasonCode.NoSiteFrame,
                "The satellite imagery covers a real-world rectangle, and this bundle publishes no origin "
                + "to place that rectangle against, so the terrain was left untextured rather than draped "
                + "against a guess. It still imports; set shared coordinates by hand if you need them.");
            return;
        }

        bool fromDrapeBlock = manifest.ImageryDrapeExtent is { IsUsable: true };
        if ((fromDrapeBlock ? manifest.ImageryDrapeExtent : manifest.DemBounds) is not { IsUsable: true } extent)
        {
            Skip(
                SkipReasonCode.ExtentNotCorroborated,
                "This bundle publishes no ground extent for the satellite imagery that this plugin is "
                + "allowed to read, so there is no way to know which ground the image covers and the "
                + "terrain was left untextured.");
            return;
        }

        if (!frame.CanPlaceProjected(extent.Epsg))
        {
            Skip(
                SkipReasonCode.CoordinateSystemNotSupported,
                $"The satellite imagery covers a rectangle in EPSG:{extent.Epsg}, which this plugin cannot "
                + $"place into this bundle's coordinate system (EPSG:{frame.Epsg}). Draping it anyway would "
                + "stretch the image over the wrong ground, so the terrain was left untextured.");
            return;
        }

        // Both paths, not just the corroborated one: an extent this host trusts is still no use if
        // the file it belongs to is not an image Revit can decode.
        if (probeImageSize(entry) is not { IsUsable: true } pixels)
        {
            Skip(
                SkipReasonCode.ExtentNotCorroborated,
                $"The satellite imagery at \"{drape.Path}\" is not a readable PNG, so neither Revit could "
                + "texture with it nor this plugin could check which ground it covers. Re-download the "
                + "bundle from your vault at mantle.place/vault.");
            return;
        }

        if (!frame.TryToLocalMetres(extent.Left, extent.Bottom, out double leftM, out double bottomM)
            || !frame.TryToLocalMetres(extent.Right, extent.Top, out double rightM, out double topM))
        {
            Skip(
                SkipReasonCode.NoSiteFrame,
                "This bundle's origin is incomplete, so the satellite imagery's rectangle could not be "
                + "placed and the terrain was left untextured.");
            return;
        }

        if (!fromDrapeBlock && Uncorroborated(manifest, extent, pixels, rightM - leftM, topM - bottomM) is { } complaint)
        {
            Skip(SkipReasonCode.ExtentNotCorroborated, complaint);
            return;
        }

        steps.Add(new ImportStep
        {
            Kind = ImportStepKind.ImageryDrape,
            EntryName = entry,
            ExpectedSha256 = drape.Sha256,
            Frame = frame,
            Drape = new DrapePlacement
            {
                LeftM = leftM,
                BottomM = bottomM,
                RightM = rightM,
                TopM = topM,
                PixelSize = pixels,
                ExtentFromDrapeBlock = fromDrapeBlock,
            },
        });
    }

    /// <summary>
    /// Why an inferred extent cannot be trusted, or <c>null</c> when the image's own grid backs it up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The check is <c>pixels × gsd ≈ metres</c> on both axes, and it is run ONLY on the inferred
    /// extent. A drape block that states its own extent is a contract, and re-deriving a contract to
    /// see whether this host agrees with it is the habit <c>HPS-33</c> exists to stop.
    /// </para>
    /// <para>
    /// Be honest about what this proves: it corroborates the extent's SIZE, not its POSITION. It
    /// catches the divergence that is actually plausible here — a drape cut to the imagery footprint
    /// instead of the DEM grid, which changes the size — and it would not catch an extent of the
    /// right size in the wrong place. It is the strongest host-neutral check available from the
    /// declared fields, which is a different claim from proof.
    /// </para>
    /// </remarks>
    private static string? Uncorroborated(
        BundleManifest manifest,
        GroundExtent extent,
        ImageSize pixels,
        double widthM,
        double heightM)
    {
        if (manifest.ImageryGsdM is not { } gsd || gsd <= 0.0)
        {
            return "This bundle does not say what ground resolution its satellite imagery was cut at, so "
                + "there is no way to check that the extent it was given is the extent it actually covers. "
                + "The terrain was left untextured rather than draped against an unverified rectangle.";
        }

        double tolerance = ExtentTolerancePixels * gsd;
        double expectedWidth = pixels.Width * gsd;
        double expectedHeight = pixels.Height * gsd;

        if (Math.Abs(expectedWidth - widthM) <= tolerance && Math.Abs(expectedHeight - heightM) <= tolerance)
        {
            return null;
        }

        return "The ground extent this bundle publishes for its satellite imagery does not match the image "
            + $"itself: {pixels} pixels at {Metres(gsd)} m covers {Metres(expectedWidth)} × "
            + $"{Metres(expectedHeight)} m, but the extent spans {Metres(widthM)} × {Metres(heightM)} m. "
            + "Draping it would stretch the image over the wrong ground, so the terrain was left untextured.";
    }

    private static string Metres(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static void NoteAvailableButNotImported(
        BundleManifest manifest,
        BundleEntryIndex entries,
        List<string> notImported)
    {
        if (manifest.LandXml is not null && entries.Resolve(manifest.LandXml.Path) is not null)
        {
            notImported.Add(
                $"{manifest.LandXml.Path} — a LandXML TIN surface; the Civil 3D path (Insert ▸ LandXML), not imported here.");
        }

        if (manifest.ContoursDxf is not null && entries.Resolve(manifest.ContoursDxf.Path) is not null)
        {
            notImported.Add(
                $"{manifest.ContoursDxf.Path} — 2-D contour linework; link it with Insert ▸ Link CAD if you want it alongside.");
        }
    }

    /// <summary>
    /// Resolves one artifact into a step, or explains its absence. Fails closed on units: an
    /// artifact whose declared unit this host does not understand is skipped rather than imported
    /// at a guessed scale, because a silently-wrong scale is a site that looks imported and is
    /// 3.28× the wrong size (HPS-35).
    /// </summary>
    private static bool TryPlanArtifact(
        BundleArtifact? artifact,
        BundleManifest manifest,
        BundleEntryIndex entries,
        ImportStepKind kind,
        ReadinessPath readiness,
        string label,
        out ImportStep? step,
        out SkippedImport? skipped,
        out bool unitUnreadable,
        SurfaceCropWindow? crop = null,
        SiteFrame? frame = null)
    {
        step = null;
        skipped = null;
        unitUnreadable = false;

        if (artifact is null)
        {
            skipped = new SkippedImport
            {
                Kind = kind,
                ReasonCode = SkipReasonCode.ArtifactNotInManifest,
                Reason = DescribeMissingArtifact(label, readiness),
            };
            return false;
        }

        string? entry = entries.Resolve(artifact.Path);
        if (entry is null)
        {
            skipped = new SkippedImport
            {
                Kind = kind,
                ReasonCode = SkipReasonCode.EntryNotInArchive,
                Reason = $"The manifest points the {label} at \"{artifact.Path}\", but no such entry is in this "
                    + "bundle. Re-download it from your vault at mantle.place/vault.",
            };
            return false;
        }

        if (!TryResolveUnits(artifact, manifest, out LinearUnit units))
        {
            unitUnreadable = true;
            skipped = new SkippedImport
            {
                Kind = kind,
                ReasonCode = SkipReasonCode.UnitNotUnderstood,
                Reason = $"The {label} declares units \"{artifact.Units}\", which this plugin does not "
                    + "understand. Importing it could place the site at the wrong scale, so it was left out.",
            };
            return false;
        }

        step = new ImportStep
        {
            Kind = kind,
            EntryName = entry,
            Units = units,
            ExpectedSha256 = artifact.Sha256,
            Crop = crop,
            Frame = frame,
        };
        return true;
    }

    /// <summary>
    /// Per-artifact <c>units</c> wins; the <c>delivery</c> block is the fallback; a bundle that
    /// states neither is metric, which is what every pre-<c>delivery</c> bundle was.
    /// </summary>
    private static bool TryResolveUnits(BundleArtifact artifact, BundleManifest manifest, out LinearUnit units)
    {
        switch (artifact.Units)
        {
            case null:
                units = manifest.Delivery.LinearUnit == LinearUnit.Unspecified
                    ? LinearUnit.Metre
                    : manifest.Delivery.LinearUnit;
                return true;
            case "m":
                units = LinearUnit.Metre;
                return true;
            case "ftUS":
                units = LinearUnit.UsSurveyFoot;
                return true;
            case "ft":
                units = LinearUnit.InternationalFoot;
                return true;
            default:
                units = LinearUnit.Unspecified;
                return false;
        }
    }

    /// <summary>
    /// Explains an absent artifact, preferring the manifest's own reason where it stated one.
    /// </summary>
    /// <remarks>
    /// The reason is TRANSLATED, not interpolated. <c>hosts.&lt;hostId&gt;.readiness</c> reasons are an open
    /// vocabulary until v19 and include <c>emit_threw:&lt;stage&gt;</c>, so splicing the raw token
    /// into this sentence showed curators internal stage identifiers (<see cref="ReadinessReasons"/>).
    /// </remarks>
    private static string DescribeMissingArtifact(string label, ReadinessPath readiness)
    {
        if (readiness.Declared && !readiness.Present
            && ReadinessReasons.ClauseFor(readiness.Reason) is { } clause)
        {
            return $"No {label} in this bundle: {clause}.";
        }

        return $"No {label} in this bundle. Open your vault at mantle.place/vault, add the Revit "
            + "deliverables to this order, then re-download.";
    }

    private static string DescribeAbsence(BundleManifest manifest)
    {
        if (manifest.Readiness.Declared)
        {
            return "The manifest's Revit readiness block explains why — see the skipped items.";
        }

        return "It was built before the Revit deliverables were selectable, or they were not selected. "
            + "Re-generate it from your vault at mantle.place/vault with the Revit formats chosen.";
    }
}
