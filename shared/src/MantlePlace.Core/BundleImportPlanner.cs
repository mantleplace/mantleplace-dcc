using System.Globalization;

namespace MantlePlace.Core;

public static class BundleImportPlanner
{
    public static IReadOnlyList<string> ThreeFiles { get; } =
    [
        "Elevation/Terrain.dxf",
        "Elevation/Contours.dxf",
        "Elevation/Slope.gpkg",
        "Site/Site.ifc",
        "Basemap/Roads.gpkg",
        "Basemap/Pavement.gpkg",
        "Basemap/Water.gpkg",
        "Basemap/Landuse.gpkg",
        "Landcover/Landcover.gpkg",
        "Landcover/Trees.csv",
        "Imagery/Imagery.png",
        "Records/Floodplain.gpkg",
    ];

    public static IReadOnlyList<ThreeLeftOutItem> ThreeLeftOut { get; } =
    [
        new(
            ImportStepKind.ToposurfaceFromPointsFile,
            "The toposurface points file",
            "MPB 3.0 ships no points file, and the plugin never converts a georeferenced file; the terrain is built from Elevation/Terrain.dxf."),
        new(
            null,
            "Cropping the terrain and contours to the area of interest",
            "MPB 3.0 states the area of interest only in longitude and latitude, and no reader projects; the terrain is built from the whole surface."),
        new(
            null,
            "The project's time zone",
            "MPB 3.0 names the time zone without the UTC offset Revit needs, and the plugin does not work one out; the site location is still set."),
        new(
            null,
            "The flood map's zones, panels and effective dates on the hazard plan's key",
            "MPB 3.0 states none of them; the flood zones are still drawn from Records/Floodplain.gpkg."),
        new(
            null,
            "The steep-ground threshold on the hazard plan's key",
            "MPB 3.0 publishes no build threshold; the steep ground is still drawn from Elevation/Slope.gpkg."),
    ];

    private static string ThreeNoCrop => ThreeLeftOut[1].Why;

    internal const double ExtentTolerancePixels = 2.0;

    public static BundleImportPlan Plan(
        BundleManifest manifest,
        IEnumerable<string> entryNames,
        Func<string, ImageSize?> probeImageSize,
        ImportLayerChoice? choice = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(entryNames);
        ArgumentNullException.ThrowIfNull(probeImageSize);
        choice ??= ImportLayerChoice.All;

        if (!manifest.IsValid)
        {
            return new BundleImportPlan { CanImport = false, BlockedReason = manifest.Error };
        }

        BundleManifest? three = null;
        if (RevitThreeView.IsThree(manifest))
        {
            three = manifest;
            manifest = RevitThreeView.From(manifest);
        }

        BundleEntryIndex entries = new(entryNames);
        List<ImportStep> steps = [];
        List<SkippedImport> skipped = [];
        List<string> notImported = [];

        List<ImportStep> drapeSteps = [];
        List<SkippedImport> drapeSkipped = [];
        PlanImageryDrape(manifest, entries, drapeSteps, drapeSkipped, probeImageSize, choice.LevelOf(ImportLayer.ImageryDrape));
        bool drapePlanned = drapeSteps.Count > 0 && choice.Includes(ImportLayer.ImageryDrape);

        PlanToposurface(
            manifest,
            entries,
            steps,
            skipped,
            drapePlanned ? TerrainToposolidType.Imagery : TerrainToposolidType.Project,
            choice.LevelOf(ImportLayer.Terrain));
        PlanPublishedContours(manifest, entries, steps, skipped, choice.LevelOf(ImportLayer.PublishedContours));
        PlanSiteIfc(manifest, entries, steps, skipped, choice);
        PlanSharedCoordinates(manifest, steps, skipped);
        PlanSiteLocation(manifest, steps, skipped);

        FootprintExtent? crop = drapeSteps.FirstOrDefault()?.Drape is { } placed
            ? new FootprintExtent(placed.LeftM, placed.BottomM, placed.RightM, placed.TopM)
            : null;
        PlanLandPlan(manifest, entries, steps, skipped, LandLayer.LandUse, crop, choice.LevelOf(ImportLayer.LandUsePlan));
        PlanLandPlan(manifest, entries, steps, skipped, LandLayer.LandCover, crop, choice.LevelOf(ImportLayer.LandCoverPlan));
        PlanHazard(manifest, entries, steps, skipped, HazardLayer.FloodZones, crop, choice.LevelOf(ImportLayer.FloodZones));
        PlanHazard(manifest, entries, steps, skipped, HazardLayer.SteepGround, crop, choice.LevelOf(ImportLayer.SteepGround));

        steps.Add(new ImportStep { Kind = ImportStepKind.SiteContextView });

        PlanSiteContext(manifest, entries, steps, skipped, drapePlanned, choice);

        steps.Add(new ImportStep
        {
            Kind = ImportStepKind.AttributionAndProvenance,
            Provenance = ProjectProvenance.From(manifest),
        });

        steps.AddRange(drapeSteps);
        skipped.AddRange(drapeSkipped);

        if (three is not null)
        {
            ExplainThreeAbsences(three, steps, skipped);
        }

        LeaveOut(choice, steps, skipped);
        NoteAvailableButNotImported(manifest, entries, notImported);

        bool canImport = steps.Exists(step => ImportStepKinds.ImportsContent(step.Kind));
        if (!canImport)
        {
            steps.RemoveAll(step => step.Kind == ImportStepKind.SiteContextView);

            if (three is not null)
            {
                skipped.Add(new SkippedImport
                {
                    Kind = ImportStepKind.SiteContextView,
                    ReasonCode = SkipReasonCode.DeclaredAbsent,
                    Reason = "No site context view: nothing in this import would be shown in it.",
                });
            }
        }

        foreach (ImportStep step in steps)
        {
            step.Odbl = OdblContent.RoleOf(manifest.FileEntries, step);

            if (three is not null && step.EntryName.Length > 0)
            {
                step.Cost = SlowStepNotice.CostOfCatalogCount(step.Kind, three.Contents?.Find(step.EntryName)?.Count);
            }
        }

        return new BundleImportPlan
        {
            CanImport = canImport,
            Steps = steps,
            Skipped = skipped,
            AvailableButNotImported = notImported,
            BlockedReason = canImport
                ? string.Empty
                : skipped.Exists(skip => skip.ReasonCode == SkipReasonCode.LeftOutByChoice)
                    ? "Everything this bundle carries was left out of the import, so there is nothing to import."
                    : "This bundle carries nothing this plugin can import into Revit. "
                      + DescribeAbsence(manifest),
        };
    }

    private static void ExplainThreeAbsences(BundleManifest three, List<ImportStep> steps, List<SkippedImport> skipped)
    {
        foreach (ThreeLeftOutItem item in ThreeLeftOut)
        {
            if (item.Kind is not { } notInFormat)
            {
                continue;
            }

            SkippedImport replacement = new() { Kind = notInFormat, ReasonCode = SkipReasonCode.NotInFormat, Reason = item.Why };
            int at = skipped.FindIndex(skip => skip.Kind == notInFormat);
            skipped.RemoveAll(skip => skip.Kind == notInFormat);
            skipped.Insert(at < 0 ? skipped.Count : at, replacement);
        }

        foreach ((ImportStepKind kind, string path) in RevitThreeView.FileOf)
        {
            SkippedImport absent = Absent(kind, path);
            int at = skipped.FindIndex(skip => skip.Kind == kind);
            if (at >= 0)
            {
                if (skipped[at].ReasonCode is SkipReasonCode.ArtifactNotInManifest or SkipReasonCode.DeclaredAbsent)
                {
                    skipped[at] = absent;
                }

                continue;
            }

            if (kind != ImportStepKind.ToposurfaceFromSurfaceDxf && !steps.Exists(step => step.Kind == kind))
            {
                skipped.Add(absent);
            }
        }

        SkippedImport Absent(ImportStepKind kind, string path) =>
            RevitThreeView.NotHeld(three, path) is { } notHeld
                ? new SkippedImport
                {
                    Kind = kind,
                    ReasonCode = SkipReasonCode.NotHeld,
                    Reason = $"This bundle does not hold {path}: {notHeld.Reason}",
                }
                : new SkippedImport
                {
                    Kind = kind,
                    ReasonCode = SkipReasonCode.ArtifactNotInManifest,
                    Reason = $"This bundle's catalog names no {path}, and lists no reason it does not hold one. "
                        + "Re-download it from your vault at mantle.place/vault.",
                };
    }

    private static void LeaveOut(ImportLayerChoice choice, List<ImportStep> steps, List<SkippedImport> skipped)
    {
        foreach (ImportLayer layer in Enum.GetValues<ImportLayer>())
        {
            if (choice.Includes(layer) || steps.Find(step => ImportLayers.Of(step.Kind) == layer) is not { } planned)
            {
                continue;
            }

            steps.RemoveAll(step => ImportLayers.Of(step.Kind) == layer);
            skipped.RemoveAll(skip => ImportLayers.Of(skip.Kind) == layer);
            skipped.Add(new SkippedImport
            {
                Kind = planned.Kind,
                ReasonCode = SkipReasonCode.LeftOutByChoice,
                Reason = $"Left out of this import by choice: {WindowLabels.LayerName(layer)}.",
            });
        }
    }

    private static void PlanToposurface(
        BundleManifest manifest,
        BundleEntryIndex entries,
        List<ImportStep> steps,
        List<SkippedImport> skipped,
        TerrainToposolidType toposolidType,
        FidelityLevel level)
    {
        SiteFrame? frame = SiteFrame.For(manifest);
        SurfaceCropWindow? crop = SurfaceCrop.For(manifest, frame);

        if (TryPlanTin(manifest, entries, crop, frame, toposolidType, level, out ImportStep? tinStep, out SkippedImport? tinSkip))
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
                crop,
                toposolidType: toposolidType,
                level: level))
        {
            skipped.Add(tinSkip!);
            steps.Add(pointsStep!);
            return;
        }

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
                out _,
                level: level))
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

    private static bool TryPlanTin(
        BundleManifest manifest,
        BundleEntryIndex entries,
        SurfaceCropWindow? crop,
        SiteFrame? frame,
        TerrainToposolidType toposolidType,
        FidelityLevel level,
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
                frame,
                toposolidType,
                level))
        {
            return false;
        }

        bool three = manifest.SurfaceDxf?.Family is not null;
        string instead = three
            ? "so the terrain's DXF was linked instead."
            : "so the terrain was built from the points file instead.";

        if (frame is null)
        {
            skipped = new SkippedImport
            {
                Kind = kind,
                ReasonCode = SkipReasonCode.NoSiteFrame,
                Reason = "The surface TIN is measured in real-world coordinates and this bundle "
                    + "publishes no origin to place it against, " + instead,
            };
            return false;
        }

        if (three && !frame.IsInOriginUnit(candidate!.Units))
        {
            skipped = new SkippedImport
            {
                Kind = kind,
                ReasonCode = SkipReasonCode.CoordinateSystemNotSupported,
                Reason = $"The surface TIN is in \"{manifest.SurfaceDxf!.Units}\", but this bundle states its site "
                    + $"origin in \"{LinearUnits.ToManifestToken(frame.Origin.LinearUnit)}\", and this plugin "
                    + "converts neither, " + instead,
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
        List<SkippedImport> skipped,
        ImportLayerChoice choice)
    {
        if (!TryPlanArtifact(
                manifest.SiteIfc,
                manifest,
                entries,
                ImportStepKind.ContextBuildings,
                manifest.Readiness.IfcSite,
                "IFC site model",
                out ImportStep? copy,
                out SkippedImport? skip,
                out _,
                level: choice.LevelOf(ImportLayer.ContextBuildings),
                savesLinkCompanion: choice.Includes(ImportLayer.SiteModel)))
        {
            skipped.Add(skip!);
            return;
        }

        steps.Add(copy!);
        steps.Add(new ImportStep
        {
            Kind = ImportStepKind.LinkSiteIfc,
            EntryName = copy!.EntryName,
            Units = copy.Units,
            ExpectedSha256 = copy.ExpectedSha256,
            HeightDatum = HeightDatums.For(ImportStepKind.LinkSiteIfc, manifest.SiteIfc, manifest),

            Levels = copy.Levels,
            Level = copy.Level,
        });
    }

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

    private static void PlanSiteLocation(
        BundleManifest manifest,
        List<ImportStep> steps,
        List<SkippedImport> skipped)
    {
        if (manifest.Georeference.Origin is not { Lat: { } lat, Lon: { } lon })
        {
            skipped.Add(new SkippedImport
            {
                Kind = ImportStepKind.SetSiteLocation,
                ReasonCode = SkipReasonCode.NoGeographicOrigin,
                Reason = "This bundle publishes no latitude and longitude for Revit, so the project's site "
                    + "location was left as it was and the sun in every view and render is placed for "
                    + "that location, not this site. Set it under Manage ▸ Location if you need the sun.",
            });
            return;
        }

        if (!double.IsFinite(lat) || !double.IsFinite(lon)
            || lat < -90.0 || lat > 90.0 || lon < -180.0 || lon > 180.0)
        {
            skipped.Add(new SkippedImport
            {
                Kind = ImportStepKind.SetSiteLocation,
                ReasonCode = SkipReasonCode.GeographicOriginOutOfRange,
                Reason = string.Format(
                    CultureInfo.InvariantCulture,
                    "This bundle publishes a latitude of {0} and a longitude of {1}, which is not a place "
                    + "on Earth, so the project's site location was left as it was.",
                    lat,
                    lon),
            });
            return;
        }

        steps.Add(new ImportStep
        {
            Kind = ImportStepKind.SetSiteLocation,
            SiteLocation = new SiteLocationPlacement
            {
                LatitudeDeg = lat,
                LongitudeDeg = lon,
                TimeZone = manifest.TimeZone is { } zone ? new SiteTimeZone { Published = zone } : null,
            },
        });
    }

    private static void PlanSiteContext(
        BundleManifest manifest,
        BundleEntryIndex entries,
        List<ImportStep> steps,
        List<SkippedImport> skipped,
        bool drapePlanned,
        ImportLayerChoice choice)
    {
        SiteFrame? frame = SiteFrame.For(manifest);

        PlanPlacedArtifact(
            manifest,
            manifest.RoadSplines,
            frame,
            entries,
            ImportStepKind.RoadCentrelines,
            SiteVectorLayers.RoadSplines.Label,
            steps,
            skipped,
            VectorAbsence(manifest, ImportStepKind.RoadCentrelines),
            level: choice.LevelOf(ImportLayer.RoadCentrelines));

        PlanPlacedArtifact(
            manifest,
            manifest.Water,
            frame,
            entries,
            ImportStepKind.Water,
            SiteVectorLayers.Water.Label,
            steps,
            skipped,
            VectorAbsence(manifest, ImportStepKind.Water),
            drapePlanned: drapePlanned,
            level: choice.LevelOf(ImportLayer.WaterSubdivisions));

        PlanRoadPolygons(manifest, frame, entries, steps, skipped, drapePlanned, choice.LevelOf(ImportLayer.RoadSubdivisions));

        PlanPlacedArtifact(
            manifest,
            manifest.TreePoints,
            frame,
            entries,
            ImportStepKind.Vegetation,
            "trees",
            steps,
            skipped,
            level: choice.LevelOf(ImportLayer.Planting));
    }

    private static void PlanRoadPolygons(
        BundleManifest manifest,
        SiteFrame? frame,
        BundleEntryIndex entries,
        List<ImportStep> steps,
        List<SkippedImport> skipped,
        bool drapePlanned,
        FidelityLevel level)
    {
        if (manifest.RoadPolygons is null && manifest.HasRoadLayer)
        {
            skipped.Add(new SkippedImport
            {
                Kind = ImportStepKind.RoadPolygons,
                ReasonCode = SkipReasonCode.DerivedLayerNotPublished,
                Reason = "This bundle carries roads but no road surfaces, so none were cut into the "
                    + "terrain. The surfaces are derived from the centrelines when they can be, and "
                    + "this order's were not — which says nothing about the roads in this area. The "
                    + "road centrelines still import.",
            });
            return;
        }

        PlanPlacedArtifact(
            manifest,
            manifest.RoadPolygons,
            frame,
            entries,
            ImportStepKind.RoadPolygons,
            SiteVectorLayers.RoadPolygons.Label,
            steps,
            skipped,
            VectorAbsence(manifest, ImportStepKind.RoadPolygons),
            drapePlanned: drapePlanned,
            level: level);
    }

    private static void PlanLandPlan(
        BundleManifest manifest,
        BundleEntryIndex entries,
        List<ImportStep> steps,
        List<SkippedImport> skipped,
        LandLayer layer,
        FootprintExtent? crop,
        FidelityLevel level)
    {
        ImportStepKind kind = layer == LandLayer.LandUse ? ImportStepKind.LandUse : ImportStepKind.LandCover;
        PlanPlacedArtifact(
            manifest,
            layer == LandLayer.LandUse ? manifest.LandUse : manifest.LandCover,
            SiteFrame.For(manifest),
            entries,
            kind,
            SiteVectorLayers.For(kind).Label,
            steps,
            skipped,
            VectorAbsence(manifest, kind),
            landPlan: new LandPlanFacts { Build = LandPlan.BuildToken(BuildIdentity(manifest)), Crop = crop },
            level: level);
    }

    private static string BuildIdentity(BundleManifest manifest) =>
        string.IsNullOrWhiteSpace(manifest.JobId) ? manifest.OrderId : manifest.JobId;

    private static void PlanHazard(
        BundleManifest manifest,
        BundleEntryIndex entries,
        List<ImportStep> steps,
        List<SkippedImport> skipped,
        HazardLayer layer,
        FootprintExtent? crop,
        FidelityLevel level)
    {
        bool flood = layer == HazardLayer.FloodZones;
        ImportStepKind kind = flood ? ImportStepKind.FloodZones : ImportStepKind.SteepGround;
        string label = SiteVectorLayers.For(kind).Label;
        BundleArtifact? artifact = flood ? manifest.FloodZones : manifest.SteepGround;

        if (artifact is null)
        {
            ReadinessPath readiness = flood ? manifest.Readiness.FloodZones : manifest.Readiness.SteepGround;
            bool carriesGeoPackage = flood ? manifest.FloodMap is not null : manifest.HasSteepGroundBlock;
            (SkipReasonCode code, string reason) = HazardAbsence(label, readiness, carriesGeoPackage);
            skipped.Add(new SkippedImport { Kind = kind, ReasonCode = code, Reason = reason });
            return;
        }

        PlanPlacedArtifact(
            manifest,
            artifact,
            SiteFrame.For(manifest),
            entries,
            kind,
            label,
            steps,
            skipped,
            hazard: new HazardPlanFacts
            {
                Build = HazardPlan.BuildToken(BuildIdentity(manifest)),
                Crop = crop,
                FloodMap = flood ? manifest.FloodMap : null,
                Threshold = flood ? null : manifest.SteepGroundThreshold,
            },
            level: level);
    }

    private static (SkipReasonCode Code, string Reason) HazardAbsence(string label, ReadinessPath readiness, bool carriesGeoPackage)
    {
        if (readiness.Declared)
        {
            string clause = ReadinessReasons.ClauseFor(readiness.Reason)
                ?? "the bundle does not say why";
            SkipReasonCode code = readiness.Reason.Trim() is "outside_coverage" or "no_features_in_aoi"
                ? SkipReasonCode.DeclaredAbsent
                : SkipReasonCode.ArtifactNotInManifest;
            return (code, $"No {label} in this bundle: {clause}. An absent layer says nothing about the "
                + "site itself.");
        }

        return carriesGeoPackage
            ? (SkipReasonCode.PredatesHostCopy,
                $"This bundle carries its {label} only as a GeoPackage, from before they were published for "
                + "Revit, so none were drawn. Download the bundle again from your vault at mantle.place/vault "
                + "to get them.")
            : (SkipReasonCode.DeclaredAbsent, $"No {label} in this bundle.");
    }

    private static void PlanPlacedArtifact(
        BundleManifest manifest,
        BundleArtifact? artifact,
        SiteFrame? frame,
        BundleEntryIndex entries,
        ImportStepKind kind,
        string label,
        List<ImportStep> steps,
        List<SkippedImport> skipped,
        string? absence = null,
        SurfaceCropWindow? crop = null,
        LinearUnit verticalUnits = LinearUnit.Unspecified,
        HazardPlanFacts? hazard = null,
        bool drapePlanned = false,
        LandPlanFacts? landPlan = null,
        FidelityLevel level = FidelityLevel.Max)
    {
        if (artifact is null)
        {
            skipped.Add(new SkippedImport
            {
                Kind = kind,
                ReasonCode = absence is null ? SkipReasonCode.ArtifactNotInManifest : SkipReasonCode.DeclaredAbsent,
                Reason = absence ?? $"No {label} in this bundle. Open your vault at mantle.place/vault, add the Revit "
                    + "deliverables to this order, then re-download.",
            });
            return;
        }

        FidelityLevels? published = artifact.Levels;
        LevelApplication leveled = FidelityCuts.Apply(artifact, kind, level, label);
        if (leveled.File is not { } file)
        {
            skipped.Add(new SkippedImport { Kind = kind, ReasonCode = SkipReasonCode.LevelUnavailable, Reason = leveled.Refusal! });
            return;
        }

        IReadOnlyList<string> levelFiles = FidelityCuts.LevelFiles(artifact);
        artifact = file;

        string? entry = entries.Resolve(artifact.Path);
        if (entry is null)
        {
            if (artifact.IsGeoPackage && artifact.GeoJsonFallback is { } sibling)
            {
                PlanPlacedArtifact(
                    manifest, sibling, frame, entries, kind, label, steps, skipped, absence, crop, verticalUnits,
                    hazard, drapePlanned, landPlan, level);
                return;
            }

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

        LinearUnit units = LinearUnit.Unspecified;
        LayerFrame? layer = null;
        (SkipReasonCode Code, string Reason)? refusal;

        ImportStep? geoJsonFallback = null;
        if (artifact.Family is { } family)
        {
            refusal = family == FileFamily.HostReady ? null : ThreeUnitRefusal(artifact, frame, label, out units);
            if (family == FileFamily.HostReady)
            {
                units = LinearUnit.Metre;
                layer = new LayerFrame(LayerCoordinates.LocalOffsets, LinearUnit.Metre);
            }
            else
            {
                layer = new LayerFrame(LayerCoordinates.AbsoluteProjected, units);
            }
        }
        else if (artifact.IsGeoPackage)
        {
            if (frame.Origin.LinearUnit == LinearUnit.Unspecified)
            {
                if (artifact.GeoJsonFallback is { } sibling)
                {
                    PlanPlacedArtifact(
                        manifest, sibling, frame, entries, kind, label, steps, skipped, absence, crop, verticalUnits,
                        hazard, drapePlanned, landPlan, level);
                    return;
                }

                skipped.Add(new SkippedImport
                {
                    Kind = kind,
                    ReasonCode = SkipReasonCode.UnitNotUnderstood,
                    Reason = $"The {label} are in the shared vector file, which is placed in the linear unit of this "
                        + "bundle's origin, and the origin states no linear unit this plugin can read. Importing them "
                        + "could place them at the wrong scale, so they were left out.",
                });
                return;
            }

            refusal = null;
            layer = CarrierLayer(frame);
            units = layer.Value.Unit;
            geoJsonFallback = PlanGeoJsonFallback(
                manifest, artifact, frame, entries, kind, label, absence, crop, verticalUnits, hazard,
                drapePlanned, landPlan, level);
        }
        else if (artifact.FromOwnBlock)
        {
            refusal = OwnFrameRefusal(artifact, manifest, frame, label, out LayerFrame own);
            layer = own;
            units = own.Unit;
        }
        else if (string.Equals(artifact.HorizontalFrame, BundleManifestReader.GeographicFrame, StringComparison.Ordinal))
        {
            refusal = frame.CanPlaceGeographic ? null : CrsRefusal(artifact, frame, label);
            layer = LayerFrame.Geographic;
        }
        else
        {
            refusal = frame.CanPlaceProjected(GeoProjection.TryParseEpsg(artifact.HorizontalFrame) ?? 0)
                ? UnplaceableUnits(artifact, manifest, frame, label, out units)
                : CrsRefusal(artifact, frame, label);
        }

        if (refusal is { } refused)
        {
            skipped.Add(new SkippedImport { Kind = kind, ReasonCode = refused.Code, Reason = refused.Reason });
            return;
        }

        steps.Add(new ImportStep
        {
            Kind = kind,
            EntryName = entry,
            Units = units,
            Layer = layer,
            ExpectedSha256 = artifact.Sha256,
            Frame = frame,
            Crop = crop,
            CropUnavailable = CropUnavailable(kind, crop, manifest),
            VerticalUnits = verticalUnits,
            DrapePlanned = drapePlanned,

            FoliageTypeVocabulary = artifact.FoliageTypeVocabulary,
            Hazard = hazard,
            LandPlan = landPlan,
            HeightDatum = HeightDatums.For(kind, artifact, manifest),
            HeightBaseM = artifact.HeightBaseM,
            Levels = published,
            Level = leveled.Applied,
            LevelFiles = levelFiles,
            Table = artifact.Table,
            SoleTable = artifact.SoleTable,
            GeoJsonFallback = geoJsonFallback,
        });
    }

    private static LayerFrame CarrierLayer(SiteFrame frame)
        => new(LayerCoordinates.AbsoluteProjected, frame.Origin.LinearUnit);

    private static ImportStep? PlanGeoJsonFallback(
        BundleManifest manifest,
        BundleArtifact carrier,
        SiteFrame frame,
        BundleEntryIndex entries,
        ImportStepKind kind,
        string label,
        string? absence,
        SurfaceCropWindow? crop,
        LinearUnit verticalUnits,
        HazardPlanFacts? hazard,
        bool drapePlanned,
        LandPlanFacts? landPlan,
        FidelityLevel level)
    {
        if (carrier.GeoJsonFallback is not { } sibling)
        {
            return null;
        }

        List<ImportStep> alternative = [];
        PlanPlacedArtifact(
            manifest, sibling, frame, entries, kind, label, alternative, [], absence, crop, verticalUnits,
            hazard, drapePlanned, landPlan, level);
        return alternative.FirstOrDefault();
    }

    private static void PlanImageryDrape(
        BundleManifest manifest,
        BundleEntryIndex entries,
        List<ImportStep> steps,
        List<SkippedImport> skipped,
        Func<string, ImageSize?> probeImageSize,
        FidelityLevel level)
    {
        void Skip(SkipReasonCode code, string reason) => skipped.Add(new SkippedImport
        {
            Kind = ImportStepKind.ImageryDrape,
            ReasonCode = code,
            Reason = reason,
        });

        if (manifest.ImageryDrape?.Family is not null)
        {
            PlanThreeDrape(manifest, manifest.ImageryDrape, entries, steps, Skip, probeImageSize);
            return;
        }

        if (manifest.ImageryAbsentByDeclaration)
        {
            Skip(
                SkipReasonCode.DeclaredAbsent,
                "This bundle states that it carries no satellite imagery, so the terrain is imported "
                + "untextured. Re-ordering will not change that — the imagery was unavailable for this "
                + "site when the bundle was cut.");
            return;
        }

        bool own = manifest.RevitDrape is not null;
        if ((manifest.RevitDrape ?? manifest.ImageryDrape) is not { } entryDrape)
        {
            Skip(
                SkipReasonCode.ArtifactNotInManifest,
                "No satellite imagery drape in this bundle. Open your vault at mantle.place/vault, add "
                + "the Revit deliverables to this order, then re-download.");
            return;
        }

        LevelApplication leveled = FidelityCuts.Apply(entryDrape, ImportStepKind.ImageryDrape, level, "satellite imagery");
        if (leveled.File is not { } drape)
        {
            Skip(SkipReasonCode.LevelUnavailable, leveled.Refusal!);
            return;
        }

        bool pointer = leveled.Applied?.Kind == FidelityLevelKind.Pointer;
        GroundExtent? pointerExtent = pointer ? entryDrape.Levels!.Resolve(level).Extent : null;

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

        bool fromDrapeBlock = own || pointer || manifest.ImageryDrapeExtent is { IsUsable: true };
        GroundExtent? declared = pointer ? pointerExtent : own ? manifest.RevitDrapeExtent : manifest.ImageryDrapeExtent;
        if ((fromDrapeBlock ? declared : manifest.DemBounds) is not { IsUsable: true } extent)
        {
            Skip(
                SkipReasonCode.ExtentNotCorroborated,
                "This bundle publishes no ground extent for the satellite imagery that this plugin is "
                + "allowed to read, so there is no way to know which ground the image covers and the "
                + "terrain was left untextured.");
            return;
        }

        LinearUnit extentUnit = LinearUnit.Unspecified;
        if (own && !TryReadUnitToken(drape.Units ?? string.Empty, out extentUnit))
        {
            Skip(
                SkipReasonCode.UnitNotUnderstood,
                $"The satellite imagery's extent is in \"{drape.Units}\", which this plugin does not "
                + "understand, so the terrain was left untextured rather than draped at a guessed scale.");
            return;
        }

        if (!frame.CanPlaceProjected(extent.Epsg) || (own && !frame.IsInOriginUnit(extentUnit)))
        {
            Skip(
                SkipReasonCode.CoordinateSystemNotSupported,
                $"The satellite imagery covers a rectangle in EPSG:{extent.Epsg}"
                + (own ? $", in \"{drape.Units}\"" : string.Empty)
                + $", which this plugin cannot place into this project's coordinate system (EPSG:{frame.Epsg}). "
                + "Draping it anyway would stretch the image over the wrong ground, so the terrain was left "
                + "untextured.");
            return;
        }

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
            Levels = entryDrape.Levels,
            Level = leveled.Applied,
        });
    }

    private static void PlanThreeDrape(
        BundleManifest manifest,
        BundleArtifact drape,
        BundleEntryIndex entries,
        List<ImportStep> steps,
        Action<SkipReasonCode, string> skip,
        Func<string, ImageSize?> probeImageSize)
    {
        if (entries.Resolve(drape.Path) is not { } entry)
        {
            skip(
                SkipReasonCode.EntryNotInArchive,
                $"The catalog names the satellite imagery \"{drape.Path}\", but no such entry is in this bundle. "
                + "Re-download it from your vault at mantle.place/vault.");
            return;
        }

        if (SiteFrame.For(manifest) is not { } frame)
        {
            skip(
                SkipReasonCode.NoSiteFrame,
                "This bundle states its site origin in a coordinate system this plugin cannot place, so the "
                + "terrain was left untextured rather than draped against a guess.");
            return;
        }

        if (drape.Raster is not { Rectangle: { } rectangle } raster
            || !string.Equals(raster.Frame, RevitThreeView.LocalRasterFrame, StringComparison.Ordinal)
            || !(rectangle.Right > rectangle.Left) || !(rectangle.Top > rectangle.Bottom))
        {
            skip(
                SkipReasonCode.ExtentNotCorroborated,
                $"This bundle's catalog states no rectangle in the local site frame for \"{drape.Path}\", so "
                + "there is no way to know which ground the image covers and the terrain was left untextured.");
            return;
        }

        if (probeImageSize(entry) is not { IsUsable: true } pixels)
        {
            skip(
                SkipReasonCode.ExtentNotCorroborated,
                $"The satellite imagery at \"{drape.Path}\" is not a readable PNG, so neither Revit could "
                + "texture with it nor this plugin could check which ground it covers. Re-download the "
                + "bundle from your vault at mantle.place/vault.");
            return;
        }

        if (pixels.Width != raster.Width || pixels.Height != raster.Height)
        {
            skip(
                SkipReasonCode.ExtentNotCorroborated,
                $"This bundle's catalog states \"{drape.Path}\" is {raster.Width} × {raster.Height} pixels, but "
                + $"the image is {pixels}, so its rectangle cannot be shown to be this image's. Draping it "
                + "could stretch the image over the wrong ground, so the terrain was left untextured.");
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
                LeftM = rectangle.Left,
                BottomM = rectangle.Bottom,
                RightM = rectangle.Right,
                TopM = rectangle.Top,
                PixelSize = pixels,
                ExtentFromDrapeBlock = true,
            },
        });
    }

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

        if (manifest.RevitContours is null
            && manifest.ContoursDxf is not null
            && entries.Resolve(manifest.ContoursDxf.Path) is not null)
        {
            notImported.Add(
                $"{manifest.ContoursDxf.Path} — 2-D contour linework; link it with Insert ▸ Link CAD if you want it "
                + "alongside. " + WhyContoursAreNotDrawn(manifest.Readiness.Contours));
        }
    }

    private static string WhyContoursAreNotDrawn(ReadinessPath readiness)
    {
        const string Predates = "This bundle predates Revit's own contour support, so they are not drawn here.";

        if (readiness is not { Declared: true, Present: false })
        {
            return Predates;
        }

        if (readiness.Reason == NotProducedReason)
        {
            return "This bundle gives Revit no pointer to draw them from: it predates Revit's own contour "
                + "support, or has no site origin to place them against.";
        }

        return ReadinessReasons.ClauseFor(readiness.Reason) is { } clause
            ? $"Revit does not draw them from this bundle: {clause}."
            : Predates;
    }

    private const string NotProducedReason = "not_produced";

    private static void PlanPublishedContours(
        BundleManifest manifest,
        BundleEntryIndex entries,
        List<ImportStep> steps,
        List<SkippedImport> skipped,
        FidelityLevel level)
    {
        if (manifest.RevitContours is not { } contours)
        {
            return;
        }

        if (ContourUnitRefusal(contours, manifest, out LinearUnit verticalUnits) is { } refused)
        {
            skipped.Add(new SkippedImport
            {
                Kind = ImportStepKind.PublishedContours,
                ReasonCode = refused.Code,
                Reason = refused.Reason,
            });
            return;
        }

        SiteFrame? frame = SiteFrame.For(manifest);
        PlanPlacedArtifact(
            manifest,
            contours,
            frame,
            entries,
            ImportStepKind.PublishedContours,
            "published contours",
            steps,
            skipped,
            crop: SurfaceCrop.For(manifest, frame),
            verticalUnits: verticalUnits,
            level: level);
    }

    private static (SkipReasonCode Code, string Reason)? ContourUnitRefusal(
        BundleArtifact contours,
        BundleManifest manifest,
        out LinearUnit verticalUnits)
    {
        const string LeftOut = " Drawing them anyway could put them at the wrong height or scale, so they were left out.";

        if (!TryReadUnitToken(contours.VerticalUnits ?? string.Empty, out verticalUnits)
            || verticalUnits == LinearUnit.Unspecified)
        {
            return (SkipReasonCode.UnitNotUnderstood,
                $"The published contours declare their heights in \"{contours.VerticalUnits}\", which this "
                + "plugin does not understand." + LeftOut);
        }

        if (contours.HorizontalUnits is { } horizontal
            && !string.Equals(horizontal, contours.Units, StringComparison.Ordinal))
        {
            return (SkipReasonCode.CoordinateSystemNotSupported,
                $"The published contours state their X/Y in \"{horizontal}\" and their file in "
                + $"\"{contours.Units}\"." + LeftOut);
        }

        if (manifest.FileFrame is { } frame && frame.VerticalUnit is null)
        {
            return (SkipReasonCode.CoordinateSystemNotSupported,
                "This bundle declares the Revit files' frame without a height unit this plugin knows, so the "
                + "published contours' heights cannot be shown to be in it." + LeftOut);
        }

        if (manifest.FileFrame?.VerticalUnit is { } declared && declared != verticalUnits)
        {
            return (SkipReasonCode.CoordinateSystemNotSupported,
                $"The published contours state their heights in \"{contours.VerticalUnits}\", but this bundle "
                + $"declares the Revit files' heights in \"{LinearUnits.ToManifestToken(declared)}\"." + LeftOut);
        }

        return null;
    }

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
        SiteFrame? frame = null,
        TerrainToposolidType toposolidType = TerrainToposolidType.Project,
        FidelityLevel level = FidelityLevel.Max,
        bool savesLinkCompanion = false)
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

        LevelApplication leveled = FidelityCuts.Apply(artifact, kind, level, label);
        if (leveled.File is null)
        {
            skipped = new SkippedImport { Kind = kind, ReasonCode = SkipReasonCode.LevelUnavailable, Reason = leveled.Refusal! };
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
            CropUnavailable = CropUnavailable(kind, crop, manifest),
            Frame = frame,
            ToposolidType = toposolidType,
            HeightDatum = HeightDatums.For(kind, artifact, manifest),

            VerticalUnits = artifact.Family is not null
                && TryReadUnitToken(artifact.VerticalUnits ?? string.Empty, out LinearUnit vertical)
                    ? vertical
                    : LinearUnit.Unspecified,
            Levels = artifact.Levels,
            Level = leveled.Applied,
            SavesLinkCompanion = savesLinkCompanion,
        };
        return true;
    }

    private static string? CropUnavailable(ImportStepKind kind, SurfaceCropWindow? crop, BundleManifest manifest)
        => crop is null
            && (kind is ImportStepKind.ToposurfaceFromPointsFile
                or ImportStepKind.ToposurfaceFromSurfaceTin
                or ImportStepKind.PublishedContours)
            ? RevitThreeView.IsThree(manifest) ? ThreeNoCrop.TrimEnd('.') : SurfaceCrop.Unavailable(manifest, SiteFrame.For(manifest))
            : null;

    private static (SkipReasonCode Code, string Reason)? ThreeUnitRefusal(
        BundleArtifact artifact,
        SiteFrame frame,
        string label,
        out LinearUnit units)
    {
        if (!TryReadUnitToken(artifact.Units ?? string.Empty, out units))
        {
            return (SkipReasonCode.UnitNotUnderstood,
                $"The {label} are in \"{artifact.Units}\", which this plugin does not understand. Importing them "
                + "could place them at the wrong scale, so they were left out.");
        }

        return frame.IsInOriginUnit(units)
            ? null
            : (SkipReasonCode.CoordinateSystemNotSupported,
                $"The {label} are absolute coordinates in \"{artifact.Units}\", but this bundle states its site "
                + $"origin in \"{LinearUnits.ToManifestToken(frame.Origin.LinearUnit)}\". Importing them anyway "
                + "would put them in the wrong place, and this plugin converts neither, so they were left out.");
    }

    private static (SkipReasonCode Code, string Reason) CrsRefusal(BundleArtifact artifact, SiteFrame frame, string label)
        => (SkipReasonCode.CoordinateSystemNotSupported,
            $"The {label} are in \"{artifact.HorizontalFrame}\", which this plugin cannot place into this "
            + $"project's coordinate system (EPSG:{frame.Epsg}). Importing them anyway would put them a long "
            + "way from the site, so they were left out.");

    private static (SkipReasonCode Code, string Reason)? OwnFrameRefusal(
        BundleArtifact artifact,
        BundleManifest manifest,
        SiteFrame frame,
        string label,
        out LayerFrame layer)
    {
        layer = default;

        if (!TryReadUnitToken(artifact.Units ?? string.Empty, out LinearUnit units))
        {
            return (SkipReasonCode.UnitNotUnderstood,
                $"The {label} declare units \"{artifact.Units}\", which this plugin does not understand. "
                + "Importing them could place them at the wrong scale, so they were left out.");
        }

        LayerCoordinates? coordinates = artifact.HorizontalFrame switch
        {
            BundleManifestReader.ProjectedFrame => LayerCoordinates.AbsoluteProjected,
            BundleManifestReader.LocalFrame => LayerCoordinates.LocalOffsets,
            _ => null,
        };

        const string LeftOut = " Importing them anyway would put them in the wrong place, so they were left out.";

        if (coordinates is not { } kind)
        {
            return (SkipReasonCode.CoordinateSystemNotSupported,
                $"The {label} are in a \"{artifact.HorizontalFrame}\" frame, which this plugin does not know." + LeftOut);
        }

        if (artifact.VerticalReference is { } vertical
            && !string.Equals(vertical, BundleManifestReader.AbsoluteHeight, StringComparison.Ordinal))
        {
            return (SkipReasonCode.CoordinateSystemNotSupported,
                $"The {label} carry heights referenced as \"{vertical}\", which this plugin does not know." + LeftOut);
        }

        if (manifest.FileFrame is not { } declared)
        {
            return (SkipReasonCode.CoordinateSystemNotSupported,
                $"This bundle declares no frame for the Revit files, so the {label} cannot be shown to be in "
                + "this project's." + LeftOut);
        }

        if (!frame.Holds(declared))
        {
            return (SkipReasonCode.CoordinateSystemNotSupported,
                $"This bundle declares the Revit files' frame as one this project's origin (EPSG:{frame.Epsg}) "
                + $"is not, so the {label} cannot be shown to be in it." + LeftOut);
        }

        if (declared.HorizontalUnit != units)
        {
            string declaredUnit = declared.HorizontalUnit is { } unit ? LinearUnits.ToManifestToken(unit) : "no unit this plugin knows";
            return (SkipReasonCode.CoordinateSystemNotSupported,
                $"The {label} are in \"{artifact.Units}\", but this bundle declares the Revit files in "
                + $"\"{declaredUnit}\"." + LeftOut);
        }

        if (kind == LayerCoordinates.AbsoluteProjected && !frame.IsInOriginUnit(units))
        {
            return (SkipReasonCode.CoordinateSystemNotSupported,
                $"The {label} are absolute coordinates in \"{artifact.Units}\", but this project's origin is "
                + $"in \"{LinearUnits.ToManifestToken(frame.Origin.LinearUnit)}\"." + LeftOut);
        }

        layer = new LayerFrame(kind, units);
        return null;
    }

    private static string? VectorAbsence(BundleManifest manifest, ImportStepKind kind)
    {
        SiteVectorLayer layer = SiteVectorLayers.For(kind);
        if (manifest.VectorsFromOwnBlock)
        {
            return $"No {layer.Label} in this bundle: there are none in this area.";
        }

        if (manifest.SharedLayersWithNothingToPlace.Contains(layer.Name))
        {
            string geometry = layer.DrawnFrom == SiteGeometryKinds.Lines ? "lines" : "areas";
            return $"No {layer.Label} in this bundle: the layer they come from is split by geometry and "
                + $"carries no {geometry}, which is what they are drawn from.";
        }

        return null;
    }

    private static (SkipReasonCode Code, string Reason)? UnplaceableUnits(
        BundleArtifact artifact,
        BundleManifest manifest,
        SiteFrame frame,
        string label,
        out LinearUnit units)
    {
        if (!TryResolveUnits(artifact, manifest, out units))
        {
            return (SkipReasonCode.UnitNotUnderstood,
                $"The {label} declare units \"{artifact.Units}\", which this plugin does not understand. "
                + "Importing them could place them at the wrong height, so they were left out.");
        }

        if (artifact.HorizontalUnits is not { } stated)
        {
            return null;
        }

        if (!TryReadUnitToken(stated, out LinearUnit plan))
        {
            return (SkipReasonCode.UnitNotUnderstood,
                $"The {label} declare plan units \"{stated}\", which this plugin does not understand. "
                + "Importing them could place them at the wrong scale, so they were left out.");
        }

        LinearUnit origin = frame.Origin.LinearUnit == LinearUnit.Unspecified ? LinearUnit.Metre : frame.Origin.LinearUnit;
        return plan == origin
            ? null
            : (SkipReasonCode.CoordinateSystemNotSupported,
                $"The {label} are in \"{stated}\", but this project's origin is in "
                + $"\"{LinearUnits.ToManifestToken(origin)}\". A file that disagrees with the origin it is "
                + "placed against is not in this host's frame, so they were left out.");
    }

    private static bool TryResolveUnits(BundleArtifact artifact, BundleManifest manifest, out LinearUnit units)
    {
        if (artifact.Units is null)
        {
            units = manifest.Delivery.LinearUnit == LinearUnit.Unspecified
                ? LinearUnit.Metre
                : manifest.Delivery.LinearUnit;
            return true;
        }

        return TryReadUnitToken(artifact.Units, out units);
    }

    private static bool TryReadUnitToken(string token, out LinearUnit units)
    {
        switch (token)
        {
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
        if (RevitThreeView.IsThree(manifest))
        {
            return "The skipped items name each file it does not hold, and why.";
        }

        if (manifest.Readiness.Declared)
        {
            return "The manifest's Revit readiness block explains why — see the skipped items.";
        }

        return "It was built before the Revit deliverables were selectable, or they were not selected. "
            + "Re-generate it from your vault at mantle.place/vault with the Revit formats chosen.";
    }
}
