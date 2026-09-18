// UseWPF switches the SDK to the WindowsDesktop implicit-usings set, which drops System.IO.
using System.IO;
using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// The terrain steps: the toposolid from the points file or the TIN, the linked surface DXF, and
// the smooth-shading decision that goes with the ground.
internal sealed partial class RevitBundleImporter
{
    /// <summary>
    /// The Revit-2024-and-later equivalent of "Toposurface ▸ Create from Import ▸ Specify Points
    /// File": read the X,Y,Z rows and build a Toposolid from them.
    /// </summary>
    private void ImportToposurfaceFromPoints(ImportStep step)
    {
        if (TerrainToBuild(step) is not { } stamp)
        {
            return;
        }

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

        BuildTerrain(points, LinearUnits.MetresPerUnit(step.Units), step.EntryName, "points", stamp, step.ToposolidType);
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
        if (TerrainToBuild(step) is not { } stamp)
        {
            return;
        }

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
        BuildTerrain(vertices, 1.0, step.EntryName, "TIN vertices", stamp, step.ToposolidType);
    }

    /// <summary>
    /// The terrain step's re-import guard: the stamp a new toposolid must carry, or <c>null</c> when
    /// this bundle's ground is already in the project and nothing is to be built.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ Before the artifact is extracted or parsed, not after. Every other repeatable step in this
    /// import already asks "is it already here" — the IFC link, the terrain base level, the drape's
    /// duplicated type, the site-boundary subdivisions — and the terrain was the one that never did,
    /// so a second import laid a whole second ground on the first and the boundary guard above it
    /// silently rebuilt its entire set against the new one. Guarding here also means a re-import
    /// never pays to unzip and triangulate a surface it is not going to use.
    /// </para>
    /// <para>
    /// The three arms are <see cref="TerrainIdentity.Decide"/>'s and the reasoning lives there. What
    /// the shim adds is the two facts only a document can answer — which toposolids are ground, and
    /// what each one's Comments says — and the assignment below.
    /// </para>
    /// </remarks>
    private string? TerrainToBuild(ImportStep step)
    {
        List<ExistingTerrain> grounds = [];
        foreach (Toposolid ground in GroundToposolids())
        {
            grounds.Add(new ExistingTerrain(
                ground.Id.Value,
                ground.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()));
        }

        TerrainDecision decision = TerrainIdentity.Decide(
            grounds,
            _archive.Layout.Key.Stem,
            step.ExpectedSha256);

        if (decision.Explanation.Length > 0)
        {
            Say(decision.Explanation);
        }

        if (decision.Disposition == TerrainDisposition.Create)
        {
            return decision.Stamp;
        }

        // Reuse and refusal alike leave this bundle's existing ground as the terrain every later step
        // works on. Without this the boundary and drape steps fall back to "the first toposolid that
        // is not a subdivision", which in a project holding a curator's own ground as well is a
        // coin toss — and the right answer is already known here.
        _terrainId = new ElementId(decision.ExistingElementId);

        // _terrainVertexCount is deliberately left null: this run did not read a points file, so it
        // has no count, and SlowStepNotice says so rather than inventing one.
        return null;
    }

    /// <summary>
    /// Everything the two toposolid paths share: choose a type, convert into Revit's internal feet,
    /// decide a base plane, and build — including the escalation retry.
    /// </summary>
    /// <param name="toposolidType">
    /// The planner's answer to which type the ground is built on — see
    /// <see cref="ImportStep.ToposolidType"/>. The project type is still chosen either way: it is
    /// what the imagery type is duplicated from, and its thickness is what the base plane clears.
    /// </param>
    private void BuildTerrain(
        IReadOnlyList<SurfacePoint> points,
        double metresPerUnit,
        string entryName,
        string noun,
        string stamp,
        TerrainToposolidType toposolidType)
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

        if (!TryBuildTerrain(plan, chosenType, toposolidType, revitPoints, relief, stamp))
        {
            // ⛔ The retry is not defensive coding. Toposolid.Create takes no offset argument, so the
            // height offset can only be written after the element exists — and whether Revit
            // evaluates its minimum-thickness check before or after that write is the one thing that
            // could not be established without running it. If it is before, the offset arm is
            // unreachable and only a level at the right elevation works. So the second arm is tried
            // rather than assumed away, and the log records which one Revit accepted.
            TerrainBasePlan escalated = TerrainBasePlanner.Escalate(plan, relief);
            Say(escalated.Explanation);

            if (!TryBuildTerrain(escalated, chosenType, toposolidType, revitPoints, relief, stamp))
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
    /// <remarks>
    /// The imagery type is prepared inside this attempt's transaction, not before it, so a refused
    /// attempt rolls the duplicate back with the terrain and the retry finds the document as the
    /// first attempt did.
    /// </remarks>
    private bool TryBuildTerrain(
        TerrainBasePlan plan,
        CandidateToposolidType type,
        TerrainToposolidType toposolidType,
        IList<XYZ> revitPoints,
        TerrainRelief relief,
        string stamp)
    {
        ImportFailureSwallower swallower = new("Building the terrain");
        using Transaction transaction = BeginTransaction("Mantle Place: terrain from points file", swallower);

        ElementId levelId = plan.Strategy == TerrainBaseStrategy.DedicatedLevel
            ? FindOrCreateTerrainLevel(plan.LevelElevation)
            : new ElementId(plan.LevelId);

        ElementId typeId = new(type.Id);
        string typeName = type.Name;
        string? declined = null;
        if (toposolidType == TerrainToposolidType.Imagery)
        {
            if (ImageryToposolidType(typeId, out declined) is { } imagery)
            {
                typeId = imagery.Id;
                typeName = imagery.Name;
            }
        }

        Toposolid terrain = Toposolid.Create(_document, revitPoints, typeId, levelId);

        if (plan.HeightOffset != 0.0)
        {
            terrain.get_Parameter(BuiltInParameter.TOPOSOLID_HEIGHTABOVELEVEL_PARAM)?.Set(plan.HeightOffset);

            // The shape does not settle against the new base plane until the document regenerates,
            // and a too-thin refusal that would have fired at commit fires here instead — inside the
            // transaction, where the swallower can turn it into a rollback rather than a dialog.
            _document.Regenerate();
        }

        // The terrain's identity for the NEXT import. A ground it could not stamp is still kept —
        // the terrain is real and the rest of the bundle drapes onto it — it just cannot be
        // recognised later, and that is said in the log rather than hidden. Same contract as the
        // subdivisions' Comments, and the same failure mode if it is skipped: a second import that
        // builds a second ground.
        Parameter? comments = terrain.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        bool stamped = comments is not null && !comments.IsReadOnly && comments.Set(stamp);

        // Captured BEFORE the commit: a rolled-back element cannot be asked for its id.
        ElementId built = terrain.Id;

        if (!CommitAndReport(transaction, swallower))
        {
            return false;
        }

        if (!stamped)
        {
            Say("The terrain could not be stamped with this bundle's identity, so a re-import will "
                + "not recognise it and will build a second ground alongside it.");
        }

        // Remembered, not re-found: the site-boundary step drapes its rings onto THIS toposolid, and
        // a collector would happily return one the user had already modelled.
        _terrainId = built;
        _terrainVertexCount = relief.PointCount;

        if (declined is not null)
        {
            // Not a failure of the terrain, and not yet of the drape: the drape step still tries its
            // own path, which retypes this ground after the fact — correct, and slow on a large one.
            Say($"The terrain was built on the project's own type \"{type.Name}\" rather than the "
                + $"imagery type: {declined}. The satellite imagery step will retype it instead.");
        }

        Say(plan.Explanation
            + $" Type \"{typeName}\"; terrain spans {UnitUtils.ConvertFromInternalUnits(relief.MinZ, UnitTypeId.Meters):0.##}"
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

    /// <summary>
    /// Turns on Revit's toposolid smooth shading for a project with ground, once, and reports
    /// whether it is on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>Smoothing is a display setting, and it is the terrain's entire appearance.</b>
    /// <c>Toposolid.Create</c> takes an <c>IList&lt;XYZ&gt;</c> and re-triangulates, so a toposolid is
    /// a triangulated mesh no matter where its vertices came from. What decides whether a curator
    /// sees ground or a mosaic is how Revit maps and shades those triangles. Moving the vertex
    /// source to the surface DXF's TIN did not change that and could not have — the mosaic outlived
    /// it — which is what
    /// <see href="https://help.autodesk.com/cloudhelp/2025/ENU/Revit-WhatsNew/files/GUID-50FB6EAF-5308-487B-9BF0-A59C36126B96.htm">Revit
    /// 2025's Toposolid Enhancements</see> added the setting for.
    /// </para>
    /// <para>
    /// ⛔ <b>The photograph is placed for whichever renderer this leaves in charge.</b> The two
    /// measure a real-world texture offset from different origins (<see cref="DrapeAnchor"/>), so
    /// this runs BEFORE the drape's material is written and its answer is what the offsets are
    /// computed from. An earlier version of this plugin turned smoothing on after the drape, saw
    /// the photograph arrive as four quarters at a cross, and concluded the two were incompatible;
    /// they are not — the drape was simply written for the other origin.
    /// </para>
    /// <para>
    /// ⛔ <b>The plugin never turns the setting off.</b> It is document-wide and reaches every
    /// toposolid the curator owns, so reversing it silently would be the same trespass as setting
    /// it silently. It is document-wide by signature, not by inference:
    /// <c>SetSmoothedSurface(Document, bool)</c> and <c>IsSmoothedSurfaceEnabled(Document)</c> are
    /// <b>static</b> and take no element, so there is nothing here to walk.
    /// </para>
    /// </remarks>
    /// <returns>Whether smooth shading is on now — read back, never assumed.</returns>
    private bool EnsureSmoothedSurface()
    {
        // No ground, nothing to shade. A bundle whose terrain step was skipped or refused must not
        // have a project-wide display setting changed on its behalf.
        if (!HasTerrain())
        {
            Trace("  smoothing: this import left no terrain, so the setting was not touched.");
            return false;
        }

        bool enabled;
        try
        {
            enabled = Toposolid.IsSmoothedSurfaceEnabled(_document);
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException
                                       or ArgumentException
                                       or InvalidOperationException)
        {
            // Read before written, so a Revit without the 2025 pair leaves the project exactly as it
            // found it rather than half-set.
            Trace($"  smoothing: this Revit would not report the setting - {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        if (enabled)
        {
            // An earlier import, the curator, or this import's own first call. Either way it is
            // already what this wants, and a redundant write to a project-wide setting is not worth
            // an undo entry.
            if (!_smoothingSettled)
            {
                Trace("  smoothing: already on for this project. Nothing written.");
            }

            _smoothingSettled = true;
            return true;
        }

        if (_smoothingSettled)
        {
            // Asked once, refused once, said once. Asking again would only repeat the sentence.
            return false;
        }

        _smoothingSettled = true;

        ImportFailureSwallower swallower = new("Smoothing the terrain surface");
        using Transaction transaction = BeginTransaction("Mantle Place: terrain smooth shading", swallower);

        try
        {
            Toposolid.SetSmoothedSurface(_document, true);
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException
                                       or ArgumentException
                                       or InvalidOperationException)
        {
            transaction.RollBack();

            if (TerrainSmoothing.Notice(wasEnabled: false, isEnabled: false, ex.Message) is { } refused)
            {
                Say(refused);
            }

            return false;
        }

        if (!CommitAndReport(transaction, swallower))
        {
            // Nothing more to say: the commit was refused, the swallower has already said in Revit's
            // own words why, and the shading is simply unchanged. The defect sentence below would
            // report a value that did not hold, when what actually happened is a rollback.
            return false;
        }

        // ⛔ Read back AFTER the commit. The drape's texture distances went in as feet, returned
        // success, and tiled the photograph twelve times across the site; the only reason anybody
        // found out is that somebody read the value back. A bool is no different.
        bool isEnabled;
        try
        {
            isEnabled = Toposolid.IsSmoothedSurfaceEnabled(_document);
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException
                                       or ArgumentException
                                       or InvalidOperationException)
        {
            // Unreadable after a committed write is not "probably fine". Reported as not held, which
            // is the conservative half and the one a curator can act on.
            Trace($"  smoothing: unreadable after the commit - {ex.GetType().Name}: {ex.Message}");
            isEnabled = false;
        }

        if (TerrainSmoothing.Notice(wasEnabled: false, isEnabled, null) is { } notice)
        {
            Say(notice);
        }

        Trace($"  smoothing: turned on, reads back {isEnabled} (document-wide; no per-element state).");
        return isEnabled;
    }

    /// <summary>Whether this project has ground for the smoothing decision to be about.</summary>
    private bool HasTerrain()
        => _document.GetElement(
            _terrainId != ElementId.InvalidElementId ? _terrainId : TerrainToposolidId()) is Toposolid;

    /// <summary>Every level in the project, as the pure planner needs to see it.</summary>
    private List<CandidateLevel> CollectLevels()
        => [.. new FilteredElementCollector(_document)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .Select(level => new CandidateLevel(level.Id.Value, level.Name, level.ProjectElevation))];

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

    /// <summary>
    /// The toposolid type the terrain is built from, or <c>null</c> when the project has none usable.
    /// </summary>
    /// <remarks>
    /// The choice itself is <see cref="ToposolidTypeChoice"/>'s. What lives here is reading a total
    /// thickness out of Revit: the compound structure when there is one, the type's Default Thickness
    /// parameter when there is not.
    /// </remarks>
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

    /// <summary>Maps the manifest's unit to Revit's import vocabulary.</summary>
    private static ImportUnit ToImportUnit(LinearUnit unit) => unit switch
    {
        LinearUnit.Metre => ImportUnit.Meter,
        LinearUnit.UsSurveyFoot => ImportUnit.USSurveyFoot,
        LinearUnit.InternationalFoot => ImportUnit.Foot,
        _ => ImportUnit.Default,
    };

    private static double ToInternalFeet(double value, double metresPerUnit)
        => UnitUtils.ConvertToInternalUnits(value * metresPerUnit, UnitTypeId.Meters);
}
