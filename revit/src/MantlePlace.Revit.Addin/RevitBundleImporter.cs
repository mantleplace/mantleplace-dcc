// UseWPF switches the SDK to the WindowsDesktop implicit-usings set, which drops System.IO.
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// Executes a <see cref="BundleImportPlan"/> against a Revit document.
/// </summary>
/// <remarks>
/// <para>
/// Every "should we" question was already answered by <see cref="BundleImportPlanner"/>. This type
/// only answers "how", in Revit's vocabulary: transactions, element creation, unit conversion into
/// Revit's internal decimal feet. Keeping it decision-free is what makes the policy testable
/// without a licence (HPS-02).
/// </para>
/// <para>
/// Two decisions used to leak in here and no longer do. The three-point minimum
/// <c>Toposolid.Create</c> imposes fired AFTER the planner had already reported <c>CanImport</c>,
/// and now lives in <see cref="SurfacePointsReader.MinimumPoints"/> where the headless suite can
/// reach it. The Transient/Retained choice was a per-handler literal, and now comes from
/// <see cref="ImportStepKinds.LifetimeOf"/> — so getting a new step kind's lifetime wrong is a
/// failing test rather than a Revit link that breaks on someone else's machine.
/// </para>
/// </remarks>
internal sealed class RevitBundleImporter(
    Autodesk.Revit.ApplicationServices.Application application,
    Document document,
    LocalBundleArchive archive,
    Action<string>? trace = null)
{
    private readonly Autodesk.Revit.ApplicationServices.Application _application = application;
    private readonly Document _document = document;
    private readonly LocalBundleArchive _archive = archive;
    private readonly List<string> _log = [];

    /// <summary>
    /// Where a line goes the MOMENT it is produced, when the caller offered somewhere to put it.
    /// </summary>
    /// <remarks>
    /// ⛔ The summary is returned to the caller and written once, after <see cref="Execute"/> has
    /// returned. A step that never returns therefore leaves no evidence at all — and the
    /// site-boundary step's commit has been observed spending over four minutes inside
    /// <c>updateElementRelations</c> on a large toposolid, which is exactly the shape of run this
    /// import needs a record of. Anything written here is diagnostic and does not appear in the
    /// curator's dialog.
    /// </remarks>
    private readonly Action<string>? _trace = trace;

    /// <summary>The toposolid this import built, so the boundary step drapes onto the right one.</summary>
    private ElementId _terrainId = ElementId.InvalidElementId;

    /// <summary>
    /// How many points that toposolid was built from, for the two steps that have to warn about it.
    /// </summary>
    /// <remarks>
    /// Remembered on the way past rather than asked for later, for the same reason as
    /// <see cref="_terrainId"/> — and because the number is only knowable from the points file this
    /// run read. Null when this run did not build the terrain, which is a re-import onto ground an
    /// earlier one laid; <see cref="SlowStepNotice"/> says so rather than inventing a count.
    /// </remarks>
    private int? _terrainVertexCount;

    /// <summary>
    /// The subdivisions this import created, as a supplement to finding them by stamp.
    /// </summary>
    /// <remarks>
    /// It is no longer the drape's only source — see <c>DrapeableSubDivisionIds</c>. This list is
    /// empty on a re-import, because boundaries that already exist are not created again, and a drape
    /// driven by it alone therefore touched nothing while reporting success. What it still covers is
    /// the subdivision this import created but could not stamp: it is not findable by stamp, and
    /// without this list it would not be draped on the very import that made it.
    /// </remarks>
    private readonly List<ElementId> _createdSubDivisionIds = [];

    /// <summary>Trunk height as a fraction of total height — the rest is crown.</summary>
    private const double TrunkHeightFraction = 0.35;

    /// <summary>Trunk radius as a fraction of crown radius.</summary>
    private const double TrunkRadiusFraction = 0.12;

    /// <summary>Crown radius at the apex, as a fraction of its widest — never zero.</summary>
    private const double CrownApexFraction = 0.05;

    /// <summary>
    /// The UnifiedBitmap properties that decide whether a real-world-scaled image is a drape or a
    /// tile, and that this plugin has never written. Logged, not set — see <see cref="Describe"/>.
    /// </summary>
    private static readonly string[] Tiling =
    [
        UnifiedBitmap.TextureScaleLock,
        UnifiedBitmap.TextureOffsetLock,
        UnifiedBitmap.TextureURepeat,
        UnifiedBitmap.TextureVRepeat,
        UnifiedBitmap.TextureLinkTextureTransforms,
        UnifiedBitmap.TextureWAngle,
    ];

    internal IReadOnlyList<string> Log => _log;

    /// <summary>Adds a line to the summary, and streams it out as it happens.</summary>
    private void Say(string line)
    {
        _log.Add(line);
        Trace(line);
    }

    /// <summary>The same, for a batch the swallower already worded.</summary>
    private void SayAll(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            Say(line);
        }
    }

    /// <summary>
    /// Diagnostics only — the streamed log, never the curator's dialog. Best-effort by contract:
    /// a sink that throws must not take the import down with it.
    /// </summary>
    private void Trace(string line)
    {
        try
        {
            _trace?.Invoke(line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Runs every step in the plan. One transaction per step, so a late failure keeps the earlier work.</summary>
    internal void Execute(BundleImportPlan plan)
    {
        // ⛔ Revit's own IFC importer opens a transaction named "Import" inside
        // RevitLinkType.CreateFromIFC, and a per-transaction preprocessor cannot reach it — which is
        // how "Can't keep elements joined" and the IFC4 warning surfaced as a modal in the middle of
        // an otherwise silent import. This is the only hook that sees a transaction we did not open.
        // It is session-wide, so it is attached for the length of the import and detached in the
        // finally: leaving it on would put this policy between the curator and their own edits.
        ImportFailureSwallower session = new("The import");
        _application.FailuresProcessing += session.OnFailuresProcessing;

        try
        {
            ExecuteSteps(plan);
        }
        finally
        {
            _application.FailuresProcessing -= session.OnFailuresProcessing;
            SayAll(session.Lines);
        }
    }

    private void ExecuteSteps(BundleImportPlan plan)
    {
        foreach (ImportStep step in plan.Steps)
        {
            // A start marker and a duration, because a step is the unit a curator waits on and the
            // unit a slow one has to be attributed to. The marker is traced rather than said: it is
            // only useful in the streamed file, where it is the last line standing if a step hangs.
            Trace($"[{step.Kind}] started.");
            Stopwatch clock = Stopwatch.StartNew();

            // ⛔ One step's exception used to abandon every step after it. The only catch was at the
            // top of the command, so a re-import that tripped over the IFC link — step two of eight
            // — reported a single sentence and silently never attempted the terrain, the
            // boundaries, the vegetation or the drape. The steps already take a transaction each
            // precisely so a late failure keeps the earlier work; that promise is only half kept if
            // the LATER work is what disappears instead.
            //
            // InvalidOperationException is deliberately NOT caught here: the default arm throws it
            // for a step kind this build cannot dispatch, and that one must still stop the import
            // rather than quietly produce a model missing whatever the new kind was for.
            try
            {
                switch (step.Kind)
                {
                    case ImportStepKind.ToposurfaceFromPointsFile:
                        ImportToposurfaceFromPoints(step);
                        break;
                    case ImportStepKind.ToposurfaceFromSurfaceTin:
                        ImportToposurfaceFromTin(step);
                        break;
                    case ImportStepKind.ToposurfaceFromSurfaceDxf:
                        LinkCadSurface(step);
                        break;
                    case ImportStepKind.LinkSiteIfc:
                        LinkSiteIfc(step);
                        break;
                    case ImportStepKind.SetSharedCoordinates:
                        SetSharedCoordinates(step);
                        break;
                    case ImportStepKind.RoadCentrelines:
                        ImportRoadCentrelines(step);
                        break;
                    case ImportStepKind.SiteBoundaries:
                        ImportSiteBoundaries(step);
                        break;
                    case ImportStepKind.Vegetation:
                        ImportVegetation(step);
                        break;
                    case ImportStepKind.ImageryDrape:
                        ApplyImageryDrape(step);
                        break;
                    default:
                        // Fail, do not log-and-continue. A step kind added to the pure core and
                        // never dispatched here would otherwise import silently-incomplete: the plan
                        // says the bundle is fully handled, the model is missing whatever the new
                        // kind was for, and the summary reads like a success.
                        throw new InvalidOperationException(
                            $"This build of the plugin does not know how to execute the import step "
                            + $"'{step.Kind}', so the import was stopped rather than left half-done. "
                            + "Update the Mantle Place add-in.");
                }
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or IOException)
            {
                Say($"The \"{step.Kind}\" step failed and was skipped — {ex.Message} Everything after "
                    + "it was still attempted.");
            }

            clock.Stop();
            Say($"({step.Kind} took {clock.Elapsed.TotalSeconds:N1} s.)");
        }
    }

    /// <summary>
    /// The Revit-2024-and-later equivalent of "Toposurface ▸ Create from Import ▸ Specify Points
    /// File": read the X,Y,Z rows and build a Toposolid from them.
    /// </summary>
    private void ImportToposurfaceFromPoints(ImportStep step)
    {
        string csvPath = _archive.Extract(step.EntryName, ImportStepKinds.LifetimeOf(step.Kind), step.ExpectedSha256);
        string? parseError = SurfacePointsReader.TryParse(File.ReadAllText(csvPath), out IReadOnlyList<SurfacePoint> points);
        if (parseError is not null)
        {
            Say(parseError);
            return;
        }

        // Guard the producer's nodata fill before Revit ever sees it. What this removes and why is
        // SurfacePointsSanitiser's; the underlying defect is filed against the platform.
        points = SurfacePointsSanitiser.Clean(points, step.Crop, out SurfaceCleanReport cleaned);
        if (cleaned.Explanation.Length > 0)
        {
            Say(cleaned.Explanation);
        }

        BuildTerrain(points, LinearUnits.MetresPerUnit(step.Units), step.EntryName, "points");
    }

    /// <summary>
    /// The toposolid built from the surface DXF's own TIN vertices — the preferred topo path.
    /// </summary>
    /// <remarks>
    /// It differs from <see cref="ImportToposurfaceFromPoints"/> in where the points come from and
    /// nowhere else: same type choice, same base-plane planning, same escalation retry, same
    /// <c>Toposolid.Create</c>. The TIN's triangulation is not passed on because there is nowhere to
    /// pass it — the API takes points — so what this buys is the vertex placement.
    /// </remarks>
    private void ImportToposurfaceFromTin(ImportStep step)
    {
        if (step.Frame is not { } frame)
        {
            // The planner does not emit this step without a frame. Stated rather than assumed,
            // because the failure without it is a site placed 500 km from the project origin.
            Say("The terrain could not be placed: this bundle publishes no origin for its surface.");
            return;
        }

        string dxfPath = _archive.Extract(step.EntryName, ImportStepKinds.LifetimeOf(step.Kind), step.ExpectedSha256);

        SurfaceTin? tin;
        string? parseError;
        using (StreamReader reader = new(dxfPath))
        {
            parseError = SurfaceTinReader.TryParse(reader, out tin);
        }

        if (parseError is not null || tin is null)
        {
            Say(parseError ?? "The surface DXF could not be read.");
            return;
        }

        // Absolute eastings and northings become local metres by subtracting the published origin,
        // and nothing else happens to them (HPS-33).
        IReadOnlyList<SurfacePoint>? local = SurfaceTinFrame.TryToLocalMetres(tin, frame, step.Units, out string? frameError);
        if (local is null)
        {
            Say(frameError ?? "The surface DXF could not be placed against this bundle's origin.");
            return;
        }

        // Guard the producer's nodata fill before Revit ever sees it. What this removes and why is
        // SurfaceTinSanitiser's; the underlying defect is filed against the platform.
        IReadOnlyList<SurfacePoint> vertices = SurfaceTinSanitiser.Clean(tin, local, step.Crop, out SurfaceCleanReport cleaned);
        if (cleaned.Explanation.Length > 0)
        {
            Say(cleaned.Explanation);
        }

        // 1.0, not step.Units: SurfaceTinFrame consumed the artifact's unit when it subtracted the
        // origin, exactly as TreePointsReader does, so these coordinates are already metres.
        BuildTerrain(vertices, 1.0, step.EntryName, "TIN vertices");
    }

    /// <summary>
    /// Everything the two toposolid paths share: choose a type, convert into Revit's internal feet,
    /// decide a base plane, and build — including the escalation retry.
    /// </summary>
    private void BuildTerrain(
        IReadOnlyList<SurfacePoint> points,
        double metresPerUnit,
        string entryName,
        string noun)
    {
        if (ChooseToposolidType() is not { } chosenType)
        {
            Say(
                "This project has no toposolid type, so the terrain could not be created. "
                + "Start from an architectural template and try again.");
            return;
        }

        List<XYZ> revitPoints = new(points.Count);
        foreach (SurfacePoint point in points)
        {
            revitPoints.Add(new XYZ(
                ToInternalFeet(point.X, metresPerUnit),
                ToInternalFeet(point.Y, metresPerUnit),
                ToInternalFeet(point.Z, metresPerUnit)));
        }

        // Relief is read off the points in Revit's own internal feet, because that is the unit the
        // level elevations and the type thickness are already in — TerrainBasePlanner never converts.
        TerrainRelief relief = new(
            revitPoints.Min(point => point.Z),
            revitPoints.Max(point => point.Z),
            revitPoints.Count);

        TerrainBasePlan plan = TerrainBasePlanner.Decide(
            CollectLevels(),
            relief,
            chosenType.TotalThickness,
            CompoundStructure.GetMinimumLayerThickness());

        if (plan.Strategy == TerrainBaseStrategy.NoLevelAvailable)
        {
            Say(plan.Explanation + " Start from an architectural template and try again.");
            return;
        }

        if (!TryBuildTerrain(plan, chosenType, revitPoints, relief))
        {
            // ⛔ The retry is not defensive coding. Toposolid.Create takes no offset argument, so the
            // height offset can only be written after the element exists — and whether Revit
            // evaluates its minimum-thickness check before or after that write is the one thing that
            // could not be established without running it. If it is before, the offset arm is
            // unreachable and only a level at the right elevation works. So the second arm is tried
            // rather than assumed away, and the log records which one Revit accepted.
            TerrainBasePlan escalated = TerrainBasePlanner.Escalate(plan, relief);
            Say(escalated.Explanation);

            if (!TryBuildTerrain(escalated, chosenType, revitPoints, relief))
            {
                Say("The terrain could not be built on either base plane, so this project has no "
                    + "ground. The rest of the bundle was still imported.");
                return;
            }
        }

        Say($"Built the terrain from {points.Count:N0} {noun} ({entryName}).");
    }

    /// <summary>
    /// One attempt at the toposolid, on the base plane <paramref name="plan"/> describes.
    /// </summary>
    /// <returns><c>false</c> when Revit refused it and rolled the transaction back.</returns>
    private bool TryBuildTerrain(
        TerrainBasePlan plan,
        CandidateToposolidType type,
        IList<XYZ> revitPoints,
        TerrainRelief relief)
    {
        ImportFailureSwallower swallower = new("Building the terrain");
        using Transaction transaction = BeginTransaction("Mantle Place: terrain from points file", swallower);

        ElementId levelId = plan.Strategy == TerrainBaseStrategy.DedicatedLevel
            ? FindOrCreateTerrainLevel(plan.LevelElevation)
            : new ElementId(plan.LevelId);

        Toposolid terrain = Toposolid.Create(_document, revitPoints, new ElementId(type.Id), levelId);

        if (plan.HeightOffset != 0.0)
        {
            terrain.get_Parameter(BuiltInParameter.TOPOSOLID_HEIGHTABOVELEVEL_PARAM)?.Set(plan.HeightOffset);

            // The shape does not settle against the new base plane until the document regenerates,
            // and a too-thin refusal that would have fired at commit fires here instead — inside the
            // transaction, where the swallower can turn it into a rollback rather than a dialog.
            _document.Regenerate();
        }

        // Captured BEFORE the commit: a rolled-back element cannot be asked for its id.
        ElementId built = terrain.Id;

        if (!CommitAndReport(transaction, swallower))
        {
            return false;
        }

        // Remembered, not re-found: the site-boundary step drapes its rings onto THIS toposolid, and
        // a collector would happily return one the user had already modelled.
        _terrainId = built;
        _terrainVertexCount = relief.PointCount;
        Say(plan.Explanation
            + $" Type \"{type.Name}\"; terrain spans {UnitUtils.ConvertFromInternalUnits(relief.MinZ, UnitTypeId.Meters):0.##}"
            + $" m to {UnitUtils.ConvertFromInternalUnits(relief.MaxZ, UnitTypeId.Meters):0.##} m.");
        return true;
    }

    /// <summary>
    /// The level a dedicated-base terrain sits on, reused by name across imports.
    /// </summary>
    /// <remarks>
    /// Found by name rather than created every time, the same way <c>TryWearMaterial</c> reuses its
    /// duplicated type: a curator who re-imports an area should not accumulate a level per run.
    /// </remarks>
    private ElementId FindOrCreateTerrainLevel(double elevation)
    {
        Level? existing = new FilteredElementCollector(_document)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .FirstOrDefault(level => string.Equals(
                level.Name,
                TerrainBasePlanner.DedicatedLevelName,
                StringComparison.Ordinal));

        if (existing is not null)
        {
            return existing.Id;
        }

        Level created = Level.Create(_document, elevation);
        created.Name = TerrainBasePlanner.DedicatedLevelName;
        return created.Id;
    }

    /// <summary>Every level in the project, as the pure planner needs to see it.</summary>
    private List<CandidateLevel> CollectLevels()
        => [.. new FilteredElementCollector(_document)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .Select(level => new CandidateLevel(level.Id.Value, level.Name, level.ProjectElevation))];

    /// <summary>
    /// The toposolid type the terrain is built from, or <c>null</c> when the project has none usable.
    /// </summary>
    /// <remarks>
    /// The choice itself is <see cref="ToposolidTypeChoice"/>'s. What lives here is reading a total
    /// thickness out of Revit: the compound structure when there is one, the type's Default Thickness
    /// parameter when there is not.
    /// </remarks>
    /// <summary>
    /// Whether any layer of <paramref name="structure"/> is a <c>Structure</c> — the difference
    /// between a ground type and a paving type.
    /// </summary>
    /// <remarks>
    /// Why this and not a thickness or a name: see <see cref="ToposolidTypeChoice"/>. The short
    /// version is that thickness alone picked a 150 mm wood-plank path, and on a re-import it picked
    /// this plugin's own imagery-drape type.
    /// </remarks>
    internal static bool HasStructuralLayer(CompoundStructure? structure)
    {
        if (structure is not { LayerCount: > 0 })
        {
            return false;
        }

        for (int layer = 0; layer < structure.LayerCount; layer++)
        {
            if (structure.GetLayerFunction(layer) == MaterialFunctionAssignment.Structure)
            {
                return true;
            }
        }

        return false;
    }

    private CandidateToposolidType? ChooseToposolidType()
    {
        List<CandidateToposolidType> candidates = [];
        foreach (ToposolidType type in new FilteredElementCollector(_document)
            .OfClass(typeof(ToposolidType))
            .Cast<ToposolidType>())
        {
            CompoundStructure? structure = type.GetCompoundStructure();
            double thickness = structure is { LayerCount: > 0 }
                ? structure.GetWidth()
                : type.get_Parameter(BuiltInParameter.TOPOSOLID_TYPE_DEFAULT_THICKNESS_PARAM)?.AsDouble() ?? 0.0;

            // Layer 0's own width, because that is the number the drape splits. Reading the total
            // here and splitting layer 0 there is how the chooser came to prefer types the drape
            // then refused — see the ⛔ paragraph on ToposolidTypeChoice.
            double topLayer = structure is { LayerCount: > 0 } ? structure.GetLayerWidth(0) : thickness;

            candidates.Add(new CandidateToposolidType(
                type.Id.Value,
                type.Name,
                thickness,
                topLayer,
                structure?.LayerCount ?? 0,
                HasStructuralLayer(structure)));
        }

        return ToposolidTypeChoice.Best(candidates, CompoundStructure.GetMinimumLayerThickness());
    }

    /// <summary>
    /// Road centrelines as DirectShape linework, one element per feature — Forma's "Roads" row.
    /// </summary>
    /// <remarks>
    /// Forma's own conversion writes Model Lines. This writes DirectShape curves instead, and the
    /// reason is the Z: the ETL drapes each centreline over the terrain, so a road is a genuinely
    /// non-planar 3-D polyline, while a Revit <c>ModelCurve</c> must lie in a <c>SketchPlane</c>.
    /// Matching Forma literally would mean one SketchPlane element per straight segment — tens of
    /// thousands of them for forty-five roads — or flattening the roads to one elevation and losing
    /// the drape that makes them useful. A DirectShape holds the whole polyline as one element in
    /// the Roads category, which reads the same in a 3-D view and schedules better.
    /// </remarks>
    private void ImportRoadCentrelines(ImportStep step)
    {
        if (ReadVectorLayer(step, SiteGeometryKinds.Lines, "road centrelines") is not { } features)
        {
            return;
        }

        ElementId category = DirectShapeCategory(BuiltInCategory.OST_Roads);
        int created = 0;

        ImportFailureSwallower swallower = new("Importing the road centrelines");
        using Transaction transaction = BeginTransaction("Mantle Place: road centrelines", swallower);

        foreach (SiteFeature feature in features)
        {
            List<GeometryObject> curves = [];
            for (int index = 1; index < feature.Vertices.Count; index++)
            {
                if (TryVertex(feature.Vertices[index - 1], out XYZ start)
                    && TryVertex(feature.Vertices[index], out XYZ end)
                    && start.DistanceTo(end) > _document.Application.ShortCurveTolerance)
                {
                    curves.Add(Line.CreateBound(start, end));
                }
            }

            if (curves.Count > 0 && TryCreateDirectShape(category, curves, FeatureName(feature, "Road")))
            {
                created++;
            }
        }

        if (!CommitAndReport(transaction, swallower))
        {
            // The swallower already said why. Reporting the work below as done would be a lie:
            // the rollback took all of it.
            return;
        }

        Say($"Imported {created:N0} road centreline(s) from {step.EntryName}.");
    }

    /// <summary>
    /// Property boundaries as toposolid subdivisions — Forma's "Site limits" row, by the mechanism
    /// Forma itself offers alongside Model Lines.
    /// </summary>
    /// <remarks>
    /// A subdivision rather than a model line because the land-use polygons are 2-D: the GeoJSON
    /// carries no third ordinate, so there is no honest elevation to draw them at. A subdivision is
    /// projected onto the toposolid and follows the relief, which is both what Forma produces and
    /// the only reading that does not need an elevation nobody published. With no toposolid in the
    /// document there is nothing to project onto, and that is said rather than worked around.
    /// </remarks>
    private void ImportSiteBoundaries(ImportStep step)
    {
        if (ReadVectorLayer(step, SiteGeometryKinds.Areas, "site boundaries") is not { } features)
        {
            return;
        }

        ElementId terrainId = _terrainId != ElementId.InvalidElementId ? _terrainId : TerrainToposolidId();
        if (_document.GetElement(terrainId) is not Toposolid terrain)
        {
            Say(
                $"Skipped the site boundaries ({step.EntryName}): they are draped onto the terrain as toposolid "
                + "subdivisions, and this project has no toposolid. Import the terrain first, then re-run.");
            return;
        }

        // Which features are already on the terrain is decided in the pure core from the stamps the
        // existing subdivisions carry, so a re-import creates nothing twice.
        IReadOnlyList<NewSiteBoundary> newBoundaries = SiteBoundaryIdentity.NewFeatures(
            ExistingBoundaryStamps(terrain),
            [.. features.Select(feature => (string?)feature.Name)],
            _archive.Layout.Key.Stem);
        int alreadyPresent = features.Count - newBoundaries.Count;

        int created = 0;
        int declined = 0;
        int unstamped = 0;

        // ⛔ Before the transaction, because the whole cost is inside its commit and nothing can be
        // written while that runs. This line is the only warning there will ever be.
        if (SlowStepNotice.For(step.Kind, _terrainVertexCount, newBoundaries.Count) is { } notice)
        {
            Say(notice);
        }

        ImportFailureSwallower swallower = new("Importing the site boundaries");
        using Transaction transaction = BeginTransaction("Mantle Place: site boundaries", swallower);

        foreach (NewSiteBoundary boundary in newBoundaries)
        {
            SiteFeature feature = features[boundary.Ordinal - 1];
            List<Curve> edges = [];
            for (int index = 0; index < feature.Vertices.Count; index++)
            {
                SiteVertex from = feature.Vertices[index];
                SiteVertex to = feature.Vertices[(index + 1) % feature.Vertices.Count];

                // Flat by construction: a subdivision profile is projected onto the toposolid, so the
                // loop's own elevation is irrelevant and zero keeps it well inside Revit's tolerance.
                XYZ start = new(MetresToInternal(from.EastM), MetresToInternal(from.NorthM), 0.0);
                XYZ end = new(MetresToInternal(to.EastM), MetresToInternal(to.NorthM), 0.0);
                if (start.DistanceTo(end) > _document.Application.ShortCurveTolerance)
                {
                    edges.Add(Line.CreateBound(start, end));
                }
            }

            if (edges.Count < 3)
            {
                continue;
            }

            try
            {
                Toposolid subdivision = terrain.CreateSubDivision(_document, [CurveLoop.Create(edges)]);
                created++;

                // Remembered for the drape, which prefers the stamp below but cannot use it for a
                // subdivision that fails to take one.
                _createdSubDivisionIds.Add(subdivision.Id);

                // The plugin's first parameter write. Comments is the subdivision's identity for the
                // NEXT import — a subdivision it could not stamp is kept (the boundary is real), it
                // just cannot be recognised later, and that is said in the log rather than hidden.
                Parameter? comments = subdivision.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                if (comments is null || comments.IsReadOnly || !comments.Set(boundary.Stamp))
                {
                    unstamped++;
                }
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
            {
                // A ring that self-intersects, or falls outside the terrain, is one boundary lost —
                // not a reason to abandon the other two.
                declined++;
            }
        }

        if (!CommitAndReport(transaction, swallower))
        {
            // The swallower already said why. Reporting the work below as done would be a lie:
            // the rollback took all of it.
            return;
        }

        string summary = $"Imported {created:N0} site boundary subdivision(s) from {step.EntryName}";
        if (alreadyPresent > 0)
        {
            summary += $"; {alreadyPresent:N0} from an earlier import of this bundle were already present and left alone";
        }

        if (declined > 0)
        {
            summary += $"; Revit declined {declined:N0} that did not lie cleanly on the terrain";
        }

        if (unstamped > 0)
        {
            summary += $"; {unstamped:N0} could not be stamped and will not be recognised by a re-import";
        }

        Say(summary + ".");
    }

    /// <summary>
    /// The stamps this plugin wrote onto the terrain's existing subdivisions — anything else in the
    /// Comments parameter, including nothing at all, is a curator's and is not an identity here.
    /// </summary>
    private HashSet<string> ExistingBoundaryStamps(Toposolid terrain)
    {
        HashSet<string> stamps = new(StringComparer.Ordinal);
        foreach (ElementId id in terrain.GetSubDivisionIds())
        {
            if (_document.GetElement(id) is Toposolid subdivision
                && subdivision.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS) is { } comments
                && comments.AsString() is { Length: > 0 } stamp)
            {
                stamps.Add(stamp);
            }
        }

        return stamps;
    }

    /// <summary>
    /// Trees as Generic Model DirectShapes, dimensioned from the CSV — Forma's "Vegetation" row.
    /// </summary>
    /// <remarks>
    /// The tree-points file carries <c>height_m</c> and <c>crown_radius_m</c> per tree, so these are
    /// real proxy geometry — a trunk and a tapered crown at the published size — rather than the
    /// markers a points-only layer would justify. Anything Revit refuses to build is counted and
    /// reported; one bad row must not cost the curator the other forty-three.
    /// </remarks>
    private void ImportVegetation(ImportStep step)
    {
        if (step.Frame is not { } frame)
        {
            return;
        }

        string csvPath = _archive.Extract(step.EntryName, ImportStepKinds.LifetimeOf(step.Kind), step.ExpectedSha256);
        string? parseError = TreePointsReader.TryParse(File.ReadAllText(csvPath), frame, out IReadOnlyList<SiteTree> trees);
        if (parseError is not null)
        {
            Say(parseError);
            return;
        }

        ElementId category = DirectShapeCategory(BuiltInCategory.OST_Planting);
        int created = 0;

        ImportFailureSwallower swallower = new("Importing the vegetation");
        using Transaction transaction = BeginTransaction("Mantle Place: vegetation", swallower);

        foreach (SiteTree tree in trees)
        {
            if (BuildTreeGeometry(tree) is { Count: > 0 } geometry
                && TryCreateDirectShape(category, geometry, "Tree"))
            {
                created++;
            }
        }

        if (!CommitAndReport(transaction, swallower))
        {
            // The swallower already said why. Reporting the work below as done would be a lie:
            // the rollback took all of it.
            return;
        }

        Say($"Imported {created:N0} tree(s) of {trees.Count:N0} from {step.EntryName}.");
    }

    /// <summary>
    /// A trunk and a tapered crown, both at the published dimensions.
    /// </summary>
    /// <remarks>
    /// Two extrusion-family primitives rather than one revolve: a blend between two circles is a
    /// truncated cone whose behaviour is unambiguous, where a revolved silhouette depends on which
    /// of the frame's axes Revit reads as the axis of revolution. The crown's top radius is a small
    /// fraction of its base rather than zero, because a degenerate loop is not a curve loop.
    /// </remarks>
    private static List<GeometryObject>? BuildTreeGeometry(SiteTree tree)
    {
        double height = MetresToInternal(tree.HeightM);
        double crownRadius = MetresToInternal(tree.CrownRadiusM);
        if (height <= 0.0 || crownRadius <= 0.0)
        {
            return null;
        }

        double east = MetresToInternal(tree.EastM);
        double north = MetresToInternal(tree.NorthM);
        double ground = MetresToInternal(tree.GroundElevationM);

        double trunkHeight = height * TrunkHeightFraction;
        double trunkRadius = crownRadius * TrunkRadiusFraction;

        try
        {
            Solid trunk = GeometryCreationUtilities.CreateExtrusionGeometry(
                [Circle(new XYZ(east, north, ground), trunkRadius)],
                XYZ.BasisZ,
                trunkHeight);

            Solid crown = GeometryCreationUtilities.CreateBlendGeometry(
                Circle(new XYZ(east, north, ground + trunkHeight), crownRadius),
                Circle(new XYZ(east, north, ground + height), crownRadius * CrownApexFraction),
                null);

            return [trunk, crown];
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
        {
            return null;
        }
    }

    /// <summary>A horizontal circle as two half-arcs — the shape Revit's curve loops want.</summary>
    private static CurveLoop Circle(XYZ centre, double radius) => CurveLoop.Create(
    [
        Arc.Create(centre, radius, 0.0, Math.PI, XYZ.BasisX, XYZ.BasisY),
        Arc.Create(centre, radius, Math.PI, 2.0 * Math.PI, XYZ.BasisX, XYZ.BasisY),
    ]);

    /// <summary>
    /// Extracts and parses one GeoJSON layer, or logs why it could not be read.
    /// </summary>
    /// <returns><c>null</c> when there is nothing to build.</returns>
    private IReadOnlyList<SiteFeature>? ReadVectorLayer(ImportStep step, SiteGeometryKinds accept, string label)
    {
        if (step.Frame is not { } frame)
        {
            return null;
        }

        string path = _archive.Extract(step.EntryName, ImportStepKinds.LifetimeOf(step.Kind), step.ExpectedSha256);
        string? parseError = SiteVectorReader.TryParse(
            File.ReadAllText(path),
            frame,
            accept,
            label,
            out IReadOnlyList<SiteFeature> features);

        if (parseError is not null)
        {
            Say(parseError);
            return null;
        }

        if (features.Count == 0)
        {
            Say($"The {label} layer ({step.EntryName}) carries nothing this plugin could place.");
            return null;
        }

        return features;
    }

    /// <summary>
    /// Creates one DirectShape, or reports nothing and returns false.
    /// </summary>
    /// <remarks>
    /// Per-element rather than per-layer, because Revit rejects individual shapes — a self-touching
    /// polyline, a crown whose radius rounds to nothing — and losing one road must not abort the
    /// transaction that holds the other forty-four.
    /// </remarks>
    private bool TryCreateDirectShape(ElementId category, IList<GeometryObject> geometry, string name)
    {
        try
        {
            DirectShape shape = DirectShape.CreateElement(_document, category);
            shape.SetShape(geometry);
            shape.Name = name;
            return true;
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
        {
            return false;
        }
    }

    /// <summary>
    /// The preferred category, or Generic Model where this Revit does not allow a DirectShape in it.
    /// </summary>
    /// <remarks>
    /// Generic Model is the fallback because it is the category Forma itself uses for context
    /// geometry, and because a DirectShape that cannot be created at all is worse than one filed a
    /// category away from where a curator would look for it.
    /// </remarks>
    private ElementId DirectShapeCategory(BuiltInCategory preferred)
    {
        ElementId category = new(preferred);
        return DirectShape.IsValidCategoryId(category, _document)
            ? category
            : new ElementId(BuiltInCategory.OST_GenericModel);
    }

    /// <summary>A vertex as a Revit point, or false when the layer published no elevation for it.</summary>
    /// <remarks>
    /// Z is ABSOLUTE orthometric height, the same reading the toposurface points take, so a vertex
    /// with no Z has no elevation this host may invent — dropping it is the <c>HPS-20</c> reading and
    /// zero would put the road two kilometres below the site.
    /// </remarks>
    private static bool TryVertex(SiteVertex vertex, out XYZ point)
    {
        point = XYZ.Zero;
        if (vertex.ElevationM is not { } elevation)
        {
            return false;
        }

        point = new XYZ(MetresToInternal(vertex.EastM), MetresToInternal(vertex.NorthM), MetresToInternal(elevation));
        return true;
    }

    private static string FeatureName(SiteFeature feature, string fallback)
        => feature.Name.Length > 0 ? feature.Name : fallback;

    private static double MetresToInternal(double metres)
        => UnitUtils.ConvertToInternalUnits(metres, UnitTypeId.Meters);

    private static double InternalToMetres(double internalUnits)
        => UnitUtils.ConvertFromInternalUnits(internalUnits, UnitTypeId.Meters);

    /// <summary>
    /// Drapes the satellite imagery over the terrain as a real-world-scaled diffuse texture —
    /// Forma's last parity row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rectangle is not computed here. <see cref="DrapePlacement"/> arrives with all four edges
    /// already in frame-local metres and already corroborated against the image's own pixel grid, so
    /// what is left is Revit's vocabulary: an appearance asset carrying a <c>UnifiedBitmap</c>, a
    /// material pointing at it, and a toposolid type wearing that material.
    /// </para>
    /// <para>
    /// <b>The type is duplicated, never edited.</b> The terrain was created against whichever
    /// <c>ToposolidType</c> the project had first, and that type belongs to the project — texturing
    /// it in place would repaint every other toposolid in the model with this site's aerial
    /// photograph. The duplicate is named from the bundle's cache key, so importing the same order
    /// twice reuses one type rather than growing a new one each time.
    /// </para>
    /// <para>
    /// ⚠️ <b>None of this is reachable by CI</b> (no Revit on a hosted runner), and none of it has
    /// executed anywhere before this change. <c>AppearanceAssetEditScope</c>, the <c>UnifiedBitmap</c>
    /// schema and <c>ToposolidType.Duplicate</c> all compile, which says nothing about what they do.
    /// The one behaviour worth naming: whether a duplicated type's compound structure accepts the
    /// two-layer split for a toposolid as it does for a floor — <see cref="TryWearMaterial"/>
    /// refuses rather than half-applies if it does not.
    /// </para>
    /// </remarks>
    private void ApplyImageryDrape(ImportStep step)
    {
        if (step.Drape is not { } placement)
        {
            return;
        }

        ElementId terrainId = _terrainId != ElementId.InvalidElementId ? _terrainId : TerrainToposolidId();
        if (_document.GetElement(terrainId) is not Toposolid terrain)
        {
            Say(
                $"Skipped the satellite imagery ({step.EntryName}): it is draped onto the terrain as a "
                + "material texture, and this project has no toposolid. Import the terrain first, then re-run.");
            return;
        }

        // Retained, not scratch: the appearance asset stores this PATH and re-reads it every time the
        // project is opened (ImportStepKinds.LifetimeOf).
        string imagePath = _archive.Extract(step.EntryName, ImportStepKinds.LifetimeOf(step.Kind), step.ExpectedSha256);
        string name = $"Mantle Place Site Imagery {_archive.Layout.Key.Stem}";

        // The host retype is always paid and is the dominant cost, so the work count is 1 rather
        // than the subdivision count — the drape is slow on a terrain with no subdivisions at all.
        if (SlowStepNotice.For(step.Kind, _terrainVertexCount, 1) is { } notice)
        {
            Say(notice);
        }

        ImportFailureSwallower swallower = new("Applying the aerial photograph");
        using Transaction transaction = BeginTransaction("Mantle Place: satellite imagery", swallower);

        ElementId materialId = DrapeMaterialId(name, imagePath, placement, out string? misplaced);
        if (materialId == ElementId.InvalidElementId)
        {
            transaction.RollBack();
            Say(
                $"Skipped the satellite imagery ({step.EntryName}): this Revit build would not create an "
                + "appearance asset for it, so the terrain was left with the material it had.");
            return;
        }

        if (!TryWearMaterial(
            terrain,
            materialId,
            name,
            out int drapedSubDivisions,
            out int refusedSubDivisions,
            out string? refusalReason,
            out string layering))
        {
            transaction.RollBack();
            Say(
                $"Skipped the satellite imagery ({step.EntryName}): {layering}, so the terrain was left "
                + "untouched rather than half-changed.");
            return;
        }

        if (!CommitAndReport(transaction, swallower))
        {
            // The swallower already said why. Reporting the work below as done would be a lie:
            // the rollback took all of it.
            return;
        }

        string summary =
            $"Draped the satellite imagery from {step.EntryName} over the terrain — {placement.PixelSize} pixels "
            + $"across {placement.WidthM:N0} × {placement.HeightM:N0} m, positioned from "
            + (placement.ExtentFromDrapeBlock
                ? "the bundle's own imagery extent"
                : "the DEM's bounds, corroborated against the image's pixel grid");

        // The surfaces the photograph lands on, said in the summary rather than left to a rendered
        // view. A single-layer type wears its material on every face, so this clause is the whole
        // answer to "is the aerial photograph smeared down the terrain's sides".
        summary += $"; {layering}";

        // Composed in the pure core, where a test asserts the sentence. It used to be assembled
        // here from a bare count, and a count was the entire record of the defect that made this
        // step's subdivisions invisible for two sessions.
        summary += SubDivisionDrape.Clause(
            drapedSubDivisions,
            refusedSubDivisions,
            refusalReason is null ? [] : [refusalReason]) ?? string.Empty;

        Say(summary + ".");

        // ⛔ Said out loud, in the summary, not buried in the diagnostics. The last time this went
        // wrong the plugin reported the placement it INTENDED and the photograph tiled twelve times
        // across the site; the summary above is still that same statement of intent, and this is the
        // only line that has read the document back to check it.
        if (misplaced is not null)
        {
            Say($"⚠ The aerial photograph is not pinned where it should be: {misplaced}. The imagery "
                + "will repeat or sit off the ground it belongs to. This is a plugin defect — please "
                + "report it with this log.");
        }
    }

    /// <summary>
    /// The material carrying the drape, created or reused by name.
    /// </summary>
    /// <remarks>
    /// The bitmap hangs off the generic schema's diffuse slot rather than replacing the material's
    /// colour, because that is the slot Revit renders AND shades a realistic view from. The four
    /// real-world properties are what make it a drape rather than a tile: the image is pinned to a
    /// rectangle of ground, so it stays put when the terrain is edited underneath it.
    /// </remarks>
    private ElementId DrapeMaterialId(
        string name,
        string imagePath,
        DrapePlacement placement,
        out string? misplaced)
    {
        misplaced = null;

        Material? existing = new FilteredElementCollector(_document)
            .OfClass(typeof(Material))
            .Cast<Material>()
            .FirstOrDefault(material => string.Equals(material.Name, name, StringComparison.Ordinal));

        ElementId materialId = existing?.Id ?? Material.Create(_document, name);
        if (_document.GetElement(materialId) is not Material drapeMaterial)
        {
            return ElementId.InvalidElementId;
        }

        if (drapeMaterial.AppearanceAssetId == ElementId.InvalidElementId)
        {
            AppearanceAssetElement? template = AppearanceAssetElement.GetAppearanceAssetElementByName(_document, "Generic")
                ?? new FilteredElementCollector(_document)
                    .OfClass(typeof(AppearanceAssetElement))
                    .Cast<AppearanceAssetElement>()
                    .FirstOrDefault();

            if (template is null)
            {
                return ElementId.InvalidElementId;
            }

            drapeMaterial.AppearanceAssetId = template.Duplicate(name).Id;
        }

        using (AppearanceAssetEditScope scope = new(_document))
        {
            Asset editable = scope.Start(drapeMaterial.AppearanceAssetId);

            if (editable.FindByName(Generic.GenericDiffuse) is not { } diffuse)
            {
                scope.Cancel();
                return ElementId.InvalidElementId;
            }

            if (diffuse.GetSingleConnectedAsset() is null)
            {
                diffuse.AddConnectedAsset("UnifiedBitmap");
            }

            if (diffuse.GetSingleConnectedAsset() is not { } bitmap)
            {
                scope.Cancel();
                return ElementId.InvalidElementId;
            }

            Trace("  drape: " + SetString(bitmap, UnifiedBitmap.UnifiedbitmapBitmap, imagePath));

            // ⚠ Read either side of the writes. texture_ScaleLock is True by default, and if it
            // locked Y to X through the API then writing one and then the other would not mean what
            // these four calls look like they mean. Measured: it does not — X and Y land as
            // distinct values — but the log says so rather than the reader having to trust it.
            foreach (string untouched in Tiling)
            {
                Trace("  drape, as found: " + Describe(bitmap, untouched));
            }

            // Real-world scale is the ground the image spans; the offset is where its lower-left
            // corner sits in the project's own frame. Together they are the whole placement.
            (string Name, double Metres, DistanceWrite Result)[] writes =
            [
                ("width", placement.WidthM,
                    SetDistance(bitmap, UnifiedBitmap.TextureRealWorldScaleX, placement.WidthM)),
                ("height", placement.HeightM,
                    SetDistance(bitmap, UnifiedBitmap.TextureRealWorldScaleY, placement.HeightM)),
                ("west edge", placement.LeftM,
                    SetDistance(bitmap, UnifiedBitmap.TextureRealWorldOffsetX, placement.LeftM)),
                ("south edge", placement.BottomM,
                    SetDistance(bitmap, UnifiedBitmap.TextureRealWorldOffsetY, placement.BottomM)),
            ];

            foreach ((_, _, DistanceWrite result) in writes)
            {
                Trace("  drape: " + result.Report);
            }

            foreach (string untouched in Tiling)
            {
                Trace("  drape, after the writes: " + Describe(bitmap, untouched));
            }

            string[] wrong =
            [
                .. writes
                    .Where(write => !write.Result.Holds(write.Metres))
                    .Select(write => double.IsNaN(write.Result.StoredMetres)
                        ? $"{write.Name} was not written at all"
                        : $"{write.Name} reads back as {write.Result.StoredMetres:N1} m, not {write.Metres:N1} m"),
            ];

            misplaced = wrong.Length == 0 ? null : string.Join("; ", wrong);

            scope.Commit(true);
        }

        return materialId;
    }

    /// <summary>
    /// Puts <paramref name="materialId"/> on a thin top layer of a DUPLICATE of the terrain's type,
    /// then retypes the terrain — and the subdivisions this import created — onto it.
    /// </summary>
    /// <returns>
    /// <c>false</c> when the type carries no layer to split, or when splitting it would leave a
    /// degenerate structure. Refusal is asymmetric on purpose: the parent failing means no drape
    /// happened and the caller rolls the whole transaction back, while a subdivision failing is one
    /// un-draped land-use patch — counted, not fatal, the same call <see cref="ImportSiteBoundaries"/>
    /// makes for a ring Revit declines.
    /// </returns>
    /// <param name="layering">
    /// What became of the drape type's layer stack, as a clause a curator can read. Set on every
    /// path, refusals included: it is the only account of the mechanism that keeps the photograph off
    /// the vertical faces, and until it existed the log said nothing about layering at all.
    /// </param>
    private bool TryWearMaterial(
        Toposolid terrain,
        ElementId materialId,
        string typeName,
        out int drapedSubDivisions,
        out int refusedSubDivisions,
        out string? refusalReason,
        out string layering)
    {
        drapedSubDivisions = 0;
        refusedSubDivisions = 0;
        refusalReason = null;
        layering = "the terrain has no type this plugin could read";
        HashSet<string> refusals = [];

        if (_document.GetElement(terrain.GetTypeId()) is not ToposolidType current)
        {
            return false;
        }

        ToposolidType? draped = new FilteredElementCollector(_document)
            .OfClass(typeof(ToposolidType))
            .Cast<ToposolidType>()
            .FirstOrDefault(type => string.Equals(type.Name, typeName, StringComparison.Ordinal));

        draped ??= current.Duplicate(typeName) as ToposolidType;

        if (draped is null)
        {
            layering = $"Revit would not duplicate the terrain's type \"{current.Name}\"";
            return false;
        }

        if (!TryLayerImagery(draped, materialId, out layering))
        {
            return false;
        }

        try
        {
            terrain.ChangeTypeId(draped.Id);
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
        {
            // Overwrite what TryLayerImagery just reported: the type is correct, and the terrain
            // still refused to wear it. Leaving the success clause here would have the log announce
            // a layer stack on a terrain that never took it.
            layering = $"Revit refused to put the drape type on the terrain — {ex.Message}";
            return false;
        }

        // ⛔ NOT ChangeTypeId. A toposolid subdivision is a TYPELESS element: GetTypeId() is
        // InvalidElementId, Element.IsValidType is false for every candidate singly and in bulk, and
        // ChangeTypeId throws "This Element cannot have type assigned" — it refuses the operation,
        // not the type. Measured on order eb00f56f, 2026-08-25, four of four; the same run ruled out
        // both rival explanations, retyping the host having left every subdivision id resolving and
        // a single-layer type having been refused identically.
        //
        // So the material goes on the INSTANCE, which is the shape an element with no type has to
        // use. It is also the cheaper of the two mechanisms that work: Paint needs get_Geometry plus
        // a search across 2,725 faces for the upward one, and stores its result per-face where a
        // toposolid regeneration is free to drop it. This is one parameter write that survives.
        foreach (ElementId subdivisionId in DrapeableSubDivisionIds(terrain))
        {
            if (_document.GetElement(subdivisionId) is not Element subdivision)
            {
                refusedSubDivisions++;
                Trace($"  drape: subdivision {subdivisionId.Value} is no longer in the document.");
                refusals.Add("a subdivision vanished mid-import");
                continue;
            }

            if (subdivision.get_Parameter(BuiltInParameter.TOPOSOLID_SUBDIVIDE_MATERIAL) is not { IsReadOnly: false } material)
            {
                refusedSubDivisions++;
                Trace($"  drape: subdivision {subdivisionId.Value} has no writable Material parameter.");
                refusals.Add("a subdivision had no writable Material parameter");
                continue;
            }

            try
            {
                bool wrote = material.Set(materialId);

                // ⛔ Read back. The same four texture properties two methods down are read back for
                // the same reason, and that read-back is what caught the drape going in as feet and
                // tiling the photograph twelve times across the site. A Set returning true is a
                // claim about the call, not about what Revit stored.
                ElementId stored = subdivision
                    .get_Parameter(BuiltInParameter.TOPOSOLID_SUBDIVIDE_MATERIAL)
                    ?.AsElementId() ?? ElementId.InvalidElementId;

                if (wrote && stored == materialId)
                {
                    drapedSubDivisions++;
                    continue;
                }

                refusedSubDivisions++;
                Trace($"  drape: subdivision {subdivisionId.Value} did not keep the material — "
                    + $"Set returned {wrote}, reads back {stored.Value}.");
                refusals.Add("the material did not hold on a subdivision");
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
            {
                refusedSubDivisions++;
                Trace($"  drape: Revit refused the material on subdivision {subdivisionId.Value} — "
                    + $"{ex.GetType().Name}: {ex.Message}");
                refusals.Add(ex.Message);
            }
        }

        refusalReason = refusals.Count == 0 ? null : string.Join(" / ", refusals);
        return true;
    }

    /// <summary>
    /// The subdivisions this drape may touch: the ones this plugin created for THIS bundle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Found by the stamp written into Comments, not only by the ids remembered from this session.
    /// That distinction is a bug fix in its own right: <see cref="SiteBoundaryIdentity.NewFeatures"/>
    /// suppresses boundaries that already exist, so on a RE-import the remembered list is empty — and
    /// the drape step used to walk it, touch nothing, and still report success. A model left with
    /// un-draped patches could not be repaired by importing again, which is the one remedy a curator
    /// would reach for.
    /// </para>
    /// <para>
    /// The remembered ids are still unioned in, for the subdivisions this import could not stamp.
    /// Those are already reported as "will not be recognised by a re-import"; without the union they
    /// would silently not be draped either, on the very import that made them.
    /// </para>
    /// <para>
    /// ⛔ A curator's own subdivision carries no stamp, and another order's carries a different stem,
    /// so neither is ever in this set. It is the same line this plugin draws when it declines to
    /// edit the project's own toposolid type: touch what this import owns, and nothing else.
    /// </para>
    /// </remarks>
    private List<ElementId> DrapeableSubDivisionIds(Toposolid terrain)
    {
        List<ElementId> ids = [];
        HashSet<ElementId> seen = [];

        foreach (ElementId id in terrain.GetSubDivisionIds())
        {
            if (_document.GetElement(id) is Element subdivision
                && SiteBoundaryIdentity.IsStampFor(
                    subdivision.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString(),
                    _archive.Layout.Key.Stem)
                && seen.Add(id))
            {
                ids.Add(id);
            }
        }

        foreach (ElementId id in _createdSubDivisionIds)
        {
            if (seen.Add(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>
    /// Splits the drape type's top layer into a thin imagery layer over the original material — the
    /// mechanism that keeps the photograph off the vertical faces, since a single-layer structure
    /// wears its material on every face.
    /// </summary>
    /// <returns><c>false</c> when there is no layer to split or the split refuses.</returns>
    /// <remarks>
    /// <para>
    /// ⛔ <b>The re-run guard is the structure, not the caller.</b> This used to run only on the
    /// import that duplicated the type, on the reasoning that a type found by name must already carry
    /// its imagery layer. That kept the guarantee it was written for — never a second imagery layer —
    /// and bought it with an assumption nobody ever checked. A SINGLE-layer
    /// <c>Mantle Place Site Imagery</c> type, left behind by a build predating the layering or by a
    /// curator editing the structure, was reused verbatim on every later import: the photograph on
    /// every vertical face, permanently, with no re-import able to repair it. Asking
    /// <see cref="DrapeLayering.Decide"/> about layer 0's material costs one read, keeps the same
    /// guarantee, and cannot go stale.
    /// </para>
    /// <para>
    /// <b>And the write is read back.</b> <c>SetCompoundStructure</c> was the last unverified write in
    /// this step — the drape's four texture properties are read back, and that read-back is what
    /// caught the placement being out by a factor of twelve. The stack Revit actually stored is what
    /// goes in the log, so "the photograph is off the sides" stops being a claim about code and
    /// becomes a number somebody can check.
    /// </para>
    /// </remarks>
    private bool TryLayerImagery(ToposolidType draped, ElementId materialId, out string layering)
    {
        double minimum = CompoundStructure.GetMinimumLayerThickness();

        CompoundStructure structure = draped.GetCompoundStructure();
        if (structure is null || structure.LayerCount == 0)
        {
            layering = "the terrain's type has no compound structure this plugin could split";
            return false;
        }

        // Captured before anything overwrites them: the lower layer inherits the original type's
        // material verbatim, and both new layers keep the original layer's function — a
        // finish-like assignment for the sliver would be invented semantics on a structure Revit
        // never renders differently for it, so the simple answer is the honest one.
        ElementId originalMaterialId = structure.GetMaterialId(0);
        MaterialFunctionAssignment function = structure.GetLayerFunction(0);

        // Layer 0's width, not the total: a multi-layer original keeps every layer below untouched,
        // and the imagery sliver comes out of the top layer alone. For the single-layer case they
        // are the same number, so this is also the total-preserving arithmetic. The minimum is
        // a STATIC on CompoundStructure in Revit 2025 — one host-wide floor in internal feet, not a
        // per-structure question (the compiler corrected the instance-call assumption here).
        DrapeLayerDecision decision = DrapeLayering.Decide(
            originalMaterialId == materialId,
            structure.GetLayerWidth(0),
            minimum);

        if (decision.Verdict == DrapeLayerVerdict.AlreadyLayered)
        {
            // Write nothing. This is the anti-stacking guarantee, now derived from the structure in
            // front of us rather than from which import happens to be running.
            layering = $"the photograph was already the top layer of \"{draped.Name}\" from an "
                + $"earlier import, so its structure was left alone ({DescribeLayers(structure, materialId)})";
            Trace($"  drape: {layering}.");
            return true;
        }

        if (decision.Verdict == DrapeLayerVerdict.Refuse)
        {
            layering = $"its top layer is {Mm(structure.GetLayerWidth(0))}, which cannot spare a "
                + $"{Mm(minimum)} imagery layer and still leave twice that beneath";
            Trace($"  drape: refused to layer \"{draped.Name}\" — {layering}.");
            return false;
        }

        List<CompoundStructureLayer> layers =
        [
            new CompoundStructureLayer(decision.ImageryThickness, function, materialId),
            new CompoundStructureLayer(decision.LowerThickness, function, originalMaterialId),
            .. structure.GetLayers().Skip(1),
        ];

        structure.SetLayers(layers);
        draped.SetCompoundStructure(structure);

        // Read back what Revit stored, not what we asked for.
        CompoundStructure written = draped.GetCompoundStructure();
        if (written is null || written.LayerCount == 0)
        {
            layering = "Revit stored no compound structure for the drape type";
            Trace($"  drape: ⚠ {layering}.");
            return false;
        }

        Trace($"  drape: \"{draped.Name}\" layers = {DescribeLayers(written, materialId)}");

        List<bool> wearsImagery = [];
        for (int layer = 0; layer < written.LayerCount; layer++)
        {
            wearsImagery.Add(written.GetMaterialId(layer) == materialId);
        }

        if (!DrapeLayering.ImageryIsTopAndOnly(wearsImagery))
        {
            // Reported, not gated: nobody has watched this API behave, and turning an unobserved
            // read into a new refusal path is how a working drape gets declined for a reason nobody
            // can diagnose. The read-back's job is to make the next run's log say so.
            Trace("  drape: ⚠ the imagery material is NOT on layer 0 alone — the photograph may reach "
                + "a vertical face. Please report this log.");
        }

        layering = $"the photograph is a {Mm(written.GetLayerWidth(0))} layer on top of the terrain's "
            + $"own {Mm(written.GetWidth() - written.GetLayerWidth(0))}, so its sides keep the "
            + "material they had";
        return true;
    }

    /// <summary>A compound structure's layers as one log line, with the imagery layer called out.</summary>
    private string DescribeLayers(CompoundStructure structure, ElementId materialId)
    {
        List<DrapeLayerLine> lines = [];
        for (int layer = 0; layer < structure.LayerCount; layer++)
        {
            ElementId material = structure.GetMaterialId(layer);
            lines.Add(new DrapeLayerLine(
                structure.GetLayerFunction(layer).ToString(),
                structure.GetLayerWidth(layer),
                _document.GetElement(material) is Material named
                    ? named.Name
                    : (material == materialId ? "the drape material" : string.Empty)));
        }

        return DrapeLayering.Describe(lines);
    }

    /// <summary>An internal-feet thickness as the millimetres a curator judges it in.</summary>
    private static string Mm(double internalFeet)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{DrapeLayering.MillimetresFromInternalFeet(internalFeet):0.###} mm");

    /// <summary>
    /// Sets one appearance-asset text property, and says what it did.
    /// </summary>
    /// <remarks>
    /// ⛔ These two setters used to return <c>void</c> and skip in silence. The first live import
    /// tiled the aerial photograph roughly ten times across the site while the summary line said it
    /// had been placed across 1,425 × 1,419 m — and nothing in the log could tell "the value never
    /// landed" apart from "it landed in a unit this shim guessed wrong". Reading the stored value
    /// back out of a saved document is the terrain probe's job; saying what was ATTEMPTED is this
    /// one's, and neither answer is enough on its own.
    /// </remarks>
    private static string SetString(Asset asset, string propertyName, string value)
    {
        if (asset.FindByName(propertyName) is not { } found)
        {
            return $"{propertyName}: absent from this asset, nothing written.";
        }

        if (found is not AssetPropertyString property)
        {
            return $"{propertyName}: is a {found.Type}, not text — nothing written.";
        }

        if (property.IsReadOnly)
        {
            return $"{propertyName}: read-only, nothing written.";
        }

        property.Value = value;
        return $"{propertyName} = {value}";
    }

    /// <summary>
    /// Sets one real-world texture distance, converted into whatever unit the property declares,
    /// and says what it did.
    /// </summary>
    /// <remarks>
    /// It asks rather than assumes. Every other length this shim hands Revit goes through
    /// <c>ConvertToInternalUnits</c> and lands in decimal feet, and appearance-asset distances are
    /// the one place that is not obviously right — so the property's own
    /// <c>GetUnitTypeId</c> is consulted instead of a guess being written down and flagged for a
    /// human to check later. A property that declares no measurable unit falls back to internal
    /// units rather than writing a number in an unknown scale.
    /// <para>
    /// The value is read back immediately after the write, inside the same edit scope. Revit will
    /// refuse or clamp a distance outside a property's own range, and a clamped value reads
    /// downstream exactly like a unit mistake — so the two are separated here rather than argued
    /// about later.
    /// </para>
    /// </remarks>
    private static DistanceWrite SetDistance(Asset asset, string propertyName, double metres)
    {
        if (asset.FindByName(propertyName) is not { } found)
        {
            return DistanceWrite.Refused($"{propertyName}: absent from this asset, nothing written.");
        }

        if (found is not AssetPropertyDistance property)
        {
            return DistanceWrite.Refused($"{propertyName}: is a {found.Type}, not a distance — nothing written.");
        }

        if (property.IsReadOnly)
        {
            return DistanceWrite.Refused($"{propertyName}: read-only, nothing written.");
        }

        // ⛔ This guard used to call UnitUtils.IsMeasurableSpec, which answers a question about a
        // SPEC (autodesk.spec.aec:length). What GetUnitTypeId hands back is a UNIT
        // (autodesk.unit.unit:inches), and a unit is never a measurable spec — so the predicate was
        // false every single time, the conversion below was dead code, and every real-world texture
        // distance went into Revit as decimal feet. The property is in inches. Feet read as inches
        // is a factor of twelve, and twelve is exactly how many times the aerial photograph tiled
        // across a 1,425 m site. IsUnit is the question that was meant.
        ForgeTypeId unit = property.GetUnitTypeId();
        bool known = unit is not null && UnitUtils.IsUnit(unit);
        double converted = known
            ? UnitUtils.Convert(metres, UnitTypeId.Meters, unit)
            : MetresToInternal(metres);
        string unitName = known ? unit!.TypeId : "internal feet (no unit declared)";

        if (!property.IsValidValue(converted))
        {
            return DistanceWrite.Refused(
                $"{propertyName}: Revit rejects {converted:R} in {unitName} as out of range, so "
                + $"{metres:R} m was not written; it still reads {property.Value:R}.");
        }

        property.Value = converted;

        // Read back and convert back, in the same edit scope. The shim is the one assembly CI never
        // builds, so a mistake here is caught by review or by nothing — unless the code checks its
        // own arithmetic, which costs two lines and is the whole difference between this defect
        // shipping and this defect being a log line.
        double stored = property.Value;
        double storedMetres = known
            ? UnitUtils.Convert(stored, unit!, UnitTypeId.Meters)
            : InternalToMetres(stored);

        return new DistanceWrite(
            storedMetres,
            $"{propertyName}: {metres:R} m written as {converted:R} in {unitName}, "
                + $"reads back {stored:R} = {storedMetres:R} m.");
    }

    /// <summary>
    /// What one distance write actually achieved, measured in the units the caller asked in.
    /// </summary>
    /// <param name="StoredMetres">
    /// What the property reads back as, converted to metres — <c>NaN</c> when nothing was written.
    /// </param>
    /// <param name="Report">One line for the log, whichever way it went.</param>
    private readonly record struct DistanceWrite(double StoredMetres, string Report)
    {
        internal static DistanceWrite Refused(string report) => new(double.NaN, report);

        /// <summary>
        /// Whether the property now holds the ground distance it was asked for. Five centimetres
        /// over spans of a kilometre and a half: loose enough for a unit's own rounding, tight
        /// enough that no wrong unit survives it — the closest wrong answer available is a factor
        /// of twelve.
        /// </summary>
        internal bool Holds(double metres) => Math.Abs(StoredMetres - metres) <= 0.05;
    }

    /// <summary>
    /// Reads one appearance-asset property WITHOUT writing it, for the log.
    /// </summary>
    /// <remarks>
    /// The tiling properties Revit maintains beside the four real-world ones — the scale and offset
    /// locks, the U/V repeat flags, the rotation — are not written by this plugin, so what they hold
    /// is whatever Revit's own default is. That is either irrelevant or the entire explanation, and
    /// the only way to know is to state them beside the values that were written.
    /// </remarks>
    private static string Describe(Asset asset, string propertyName)
        => asset.FindByName(propertyName) switch
        {
            null => $"{propertyName}: absent.",
            AssetPropertyBoolean flag => $"{propertyName} = {flag.Value} (not written by this plugin).",
            AssetPropertyDistance distance =>
                $"{propertyName} = {distance.Value:R} in "
                + $"{distance.GetUnitTypeId()?.TypeId ?? "no declared unit"} (not written by this plugin).",
            AssetPropertyDouble number => $"{propertyName} = {number.Value:R} (not written by this plugin).",
            AssetPropertyInteger number => $"{propertyName} = {number.Value} (not written by this plugin).",
            { } other => $"{propertyName}: a {other.Type}, not read.",
        };

    /// <summary>
    /// Links the surface DXF, as a retained file so the link keeps resolving after this import.
    /// </summary>
    private void LinkCadSurface(ImportStep step)
    {
        string dxfPath = _archive.Extract(step.EntryName, ImportStepKinds.LifetimeOf(step.Kind), step.ExpectedSha256);

        DWGImportOptions options = new()
        {
            Placement = ImportPlacement.Origin,
            ThisViewOnly = false,
            ColorMode = ImportColorMode.Preserved,

            // The ETL writes $INSUNITS into the file, and when it is present Revit reads it and
            // ignores this. Setting it anyway covers the case where it is absent — without it Revit
            // imports "at the original scale regardless of the unit", i.e. metres read as feet.
            Unit = ToImportUnit(step.Units),
        };

        ImportFailureSwallower swallower = new("Linking the surface DXF");
        using Transaction transaction = BeginTransaction("Mantle Place: link surface DXF", swallower);
        bool linked = _document.Link(dxfPath, options, null, out ElementId linkId);
        if (!CommitAndReport(transaction, swallower))
        {
            // The swallower already said why. Reporting the work below as done would be a lie:
            // the rollback took all of it.
            return;
        }

        Say(linked && linkId != ElementId.InvalidElementId
            ? $"Linked the surface DXF ({step.EntryName}). Use Massing & Site ▸ Toposurface ▸ Create from "
              + "Import ▸ Select Import Instance to build a surface from it."
            : $"Revit declined to link the surface DXF ({step.EntryName}).");
    }

    /// <summary>
    /// Links the IFC site model as a coordinated reference — two steps, not one: convert the IFC to
    /// a companion <c>.rvt</c>, then link that.
    /// </summary>
    private void LinkSiteIfc(ImportStep step)
    {
        // Both the IFC and the companion .rvt are referenced by the link for the life of the
        // project, so the kind's lifetime carries the .rvt written next to it too.
        string ifcPath = _archive.Extract(step.EntryName, ImportStepKinds.LifetimeOf(step.Kind), step.ExpectedSha256);
        string companionRvt = Path.ChangeExtension(ifcPath, ".rvt");

        // CreateFromIFC does NOT convert; it links an ALREADY-CONVERTED Revit file and throws
        // FileArgumentNotFoundException when that file is missing. OpenIFCDocument is the
        // conversion step, and it must run outside any transaction on the host document.
        if (!File.Exists(companionRvt))
        {
            Document? converted = null;
            try
            {
                converted = _application.OpenIFCDocument(ifcPath);
                converted.SaveAs(companionRvt);
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or IOException)
            {
                Say($"Could not convert the IFC site model for linking ({step.EntryName}): {ex.Message}");
                return;
            }
            finally
            {
                converted?.Close(false);
            }
        }

        // ⛔ A re-import used to die here, and take the whole rest of the import with it:
        // CreateFromIFC throws when the document already carries a link at that path, and the
        // curator cannot fix it by selecting the site and pressing Delete — a RevitLinkType is not
        // in any view, it lives in Manage Links. Every other repeatable step in this import already
        // recognises its own earlier work (the boundary stamps, the drape's type-by-name); this one
        // simply had not been run twice yet. Reusing the existing link is also the honest answer:
        // the file on disk is retained for the life of the project, so the link that is already
        // pointing at it is the same link this step would create.
        if (ExistingSiteLink(ifcPath, companionRvt) is { } alreadyLinked)
        {
            Say($"The IFC site model ({step.EntryName}) is already linked into this project, so it "
                + "was left as it is. Remove it under Manage ▸ Manage Links if you want it rebuilt.");
            EnsureLinkInstance(alreadyLinked);
            return;
        }

        ImportFailureSwallower swallower = new("Linking the IFC site");
        using Transaction transaction = BeginTransaction("Mantle Place: link IFC site", swallower);
        LinkLoadResult result = RevitLinkType.CreateFromIFC(
            _document,
            ifcPath,
            companionRvt,
            false,
            new RevitLinkOptions(false));

        if (result.ElementId != ElementId.InvalidElementId)
        {
            RevitLinkInstance.Create(_document, result.ElementId);
        }

        if (!CommitAndReport(transaction, swallower))
        {
            // The swallower already said why. Reporting the work below as done would be a lie:
            // the rollback took all of it.
            return;
        }

        Say(result.ElementId != ElementId.InvalidElementId
            ? $"Linked the IFC site model ({step.EntryName})."
            : $"Revit declined to link the IFC site model ({step.EntryName}).");
    }

    /// <summary>
    /// The <see cref="RevitLinkType"/> already pointing at one of these paths, or <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Both paths are checked because <c>CreateFromIFC</c> takes two and Revit records the one it
    /// prefers: the IFC that was converted, or the <c>.rvt</c> it was converted into. Which of the
    /// two lands in the external file reference is not worth depending on.
    /// </remarks>
    private ElementId? ExistingSiteLink(string ifcPath, string companionRvt)
    {
        foreach (RevitLinkType link in new FilteredElementCollector(_document)
            .OfClass(typeof(RevitLinkType))
            .Cast<RevitLinkType>())
        {
            if (!link.IsExternalFileReference())
            {
                continue;
            }

            string linked;
            try
            {
                linked = ModelPathUtils.ConvertModelPathToUserVisiblePath(
                    link.GetExternalFileReference().GetAbsolutePath());
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                // A link whose path Revit will not resolve is not one this step can match against,
                // and it is certainly not a reason to abandon the search.
                continue;
            }

            if (string.Equals(linked, ifcPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(linked, companionRvt, StringComparison.OrdinalIgnoreCase))
            {
                return link.Id;
            }
        }

        return null;
    }

    /// <summary>
    /// Puts one instance of <paramref name="linkTypeId"/> in the document if none is there.
    /// </summary>
    /// <remarks>
    /// The type can outlive its instances — deleting the site from a view deletes the instance and
    /// leaves the type in Manage Links, which is exactly the state the curator who hit this was in.
    /// Reusing the type without restoring an instance would report a link nobody can see.
    /// </remarks>
    private void EnsureLinkInstance(ElementId linkTypeId)
    {
        bool placed = new FilteredElementCollector(_document)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>()
            .Any(instance => instance.GetTypeId() == linkTypeId);

        if (placed)
        {
            return;
        }

        ImportFailureSwallower swallower = new("Placing the existing IFC site link");
        using Transaction transaction = BeginTransaction("Mantle Place: place IFC site link", swallower);
        RevitLinkInstance.Create(_document, linkTypeId);

        if (CommitAndReport(transaction, swallower))
        {
            Say("Put the existing IFC site link back into the model — the link type was still in "
                + "Manage Links, but nothing in the project was showing it.");
        }
    }

    /// <summary>Maps the manifest's unit to Revit's import vocabulary.</summary>
    private static ImportUnit ToImportUnit(LinearUnit unit) => unit switch
    {
        LinearUnit.Metre => ImportUnit.Meter,
        LinearUnit.UsSurveyFoot => ImportUnit.USSurveyFoot,
        LinearUnit.InternationalFoot => ImportUnit.Foot,
        _ => ImportUnit.Default,
    };

    /// <summary>
    /// Publishes the pre-derived survey point. The values are applied verbatim — this method does
    /// no projection and no arithmetic beyond the unit conversion Revit's API requires (HPS-33).
    /// </summary>
    /// <remarks>
    /// The elevation and the angle used to be literal zeros here. Both are now
    /// <see cref="SurveyPointPlacement"/>'s, decided in the pure core where the headless suite can
    /// assert them; and the plan coordinates go in under their OWN unit rather than as assumed
    /// metres, because <c>revit.georeference.origin.projected</c> publishes a State-Plane foot
    /// origin on the foot tiers.
    /// </remarks>
    private void SetSharedCoordinates(ImportStep step)
    {
        if (step.SurveyPoint is not { Origin.IsUsable: true } placement)
        {
            return;
        }

        GeoOrigin origin = placement.Origin;
        ForgeTypeId originUnit = ToUnitTypeId(origin.LinearUnit);

        ImportFailureSwallower swallower = new("Setting the shared coordinates");
        using Transaction transaction = BeginTransaction("Mantle Place: shared coordinates", swallower);
        ProjectPosition position = new(
            UnitUtils.ConvertToInternalUnits(origin.Easting!.Value, originUnit),
            UnitUtils.ConvertToInternalUnits(origin.Northing!.Value, originUnit),
            UnitUtils.ConvertToInternalUnits(placement.ElevationM, UnitTypeId.Meters),
            placement.AngleRadians);
        _document.ActiveProjectLocation.SetProjectPosition(XYZ.Zero, position);
        if (!CommitAndReport(transaction, swallower))
        {
            // The swallower already said why. Reporting the work below as done would be a lie:
            // the rollback took all of it.
            return;
        }

        Say($"Set shared coordinates from the manifest (EPSG:{origin.Epsg}).");
    }

    /// <summary>
    /// Maps the manifest's unit to Revit's measurement vocabulary. <see cref="LinearUnit.Unspecified"/>
    /// is metric, the reading every other unit site in this plugin takes for an unstated unit.
    /// </summary>
    private static ForgeTypeId ToUnitTypeId(LinearUnit unit) => unit switch
    {
        LinearUnit.UsSurveyFoot => UnitTypeId.UsSurveyFeet,
        LinearUnit.InternationalFoot => UnitTypeId.Feet,
        _ => UnitTypeId.Meters,
    };

    private static double ToInternalFeet(double value, double metresPerUnit)
        => UnitUtils.ConvertToInternalUnits(value * metresPerUnit, UnitTypeId.Meters);

    /// <summary>
    /// Opens a transaction that will not stop for a dialog.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ Every transaction this importer opens goes through here, not just the terrain one. Any of
    /// them can post a warning — the site-boundary step posts one per overlapping ring, the drape's
    /// <c>ChangeTypeId</c> can post a slope warning — and a run driven by
    /// <c>MANTLEPLACE_BUNDLE_ZIP</c> has nobody to dismiss it. Uniformity is the only way to
    /// guarantee that.
    /// </para>
    /// <para>
    /// <c>SetClearAfterRollback(true)</c> matters on the retry path: without it a rolled-back
    /// transaction leaves its failures posted, and the next attempt starts in a document Revit
    /// already considers to be in failure mode.
    /// </para>
    /// </remarks>
    private Transaction BeginTransaction(string name, ImportFailureSwallower swallower)
    {
        Transaction transaction = new(_document, name);
        transaction.Start();

        FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(swallower);
        options.SetForcedModalHandling(false);
        options.SetClearAfterRollback(true);
        transaction.SetFailureHandlingOptions(options);

        return transaction;
    }

    /// <summary>
    /// Commits, appends whatever the swallower absorbed to the log, and says whether it stood.
    /// </summary>
    /// <remarks>
    /// ⛔ The return value of <c>Transaction.Commit</c> used to be discarded at every one of these
    /// sites. That is how the first real import ended up reading <c>terrain.Id</c> off an element
    /// Revit had just rolled back: the step reported success, remembered a dead <c>ElementId</c>, and
    /// the drape and boundary steps then chased it.
    /// </remarks>
    private bool CommitAndReport(Transaction transaction, ImportFailureSwallower swallower)
    {
        // Timed separately from the step that owns it. Revit does its element-relation bookkeeping
        // at commit, not at the API call, so "the step took N seconds" and "the commit took N
        // seconds" point at completely different levers — and only the second one was ever the
        // problem for the site-boundary subdivisions.
        Stopwatch clock = Stopwatch.StartNew();
        TransactionStatus status = transaction.Commit();
        clock.Stop();
        Trace($"[{transaction.GetName()}] commit took {clock.Elapsed.TotalSeconds:N1} s ({status}).");

        SayAll(swallower.Lines);

        if (status == TransactionStatus.Committed)
        {
            return true;
        }

        if (!swallower.SawError)
        {
            // Rolled back with nothing posted. Rare, and worth saying so rather than reporting a
            // silent success.
            Say($"Revit did not accept \"{transaction.GetName()}\" ({status}). Nothing from that "
                + "step was left in the project.");
        }

        return false;
    }

    private ElementId FirstElementIdOf<T>()
        where T : Element
    {
        using FilteredElementCollector collector = new(_document);
        return collector.OfClass(typeof(T)).FirstElementId();
    }

    /// <summary>
    /// The first toposolid that is a TERRAIN — a subdivision is itself a <see cref="Toposolid"/>, so
    /// a bare first-of-class collector can hand back a site-limit patch instead of the ground it
    /// sits on. Anything listed in another toposolid's <c>GetSubDivisionIds()</c> is excluded.
    /// </summary>
    private ElementId TerrainToposolidId()
    {
        List<Toposolid> toposolids = new FilteredElementCollector(_document)
            .OfClass(typeof(Toposolid))
            .Cast<Toposolid>()
            .ToList();

        HashSet<ElementId> subdivisionIds = [];
        foreach (Toposolid toposolid in toposolids)
        {
            foreach (ElementId id in toposolid.GetSubDivisionIds())
            {
                subdivisionIds.Add(id);
            }
        }

        return toposolids.FirstOrDefault(toposolid => !subdivisionIds.Contains(toposolid.Id))?.Id
            ?? ElementId.InvalidElementId;
    }
}
