using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal sealed partial class RevitBundleImporter
{
    private void ImportToposurfaceFromPoints(ImportStep step)
    {
        if (TerrainToBuild(step) is not { } stamp)
        {
            return;
        }

        Stopwatch reading = Stopwatch.StartNew();
        string csvPath = _archive.Extract(step.EntryName, step.Lifetime, step.ExpectedSha256);
        string? parseError = SurfacePointsReader.TryParse(File.ReadAllText(csvPath), out IReadOnlyList<SurfacePoint> points);
        if (parseError is not null)
        {
            Say(parseError);
            return;
        }

        points = SurfacePointsSanitiser.Clean(points, step.Crop, out SurfaceCleanReport cleaned, step.CropUnavailable);
        if (cleaned.Explanation.Length > 0)
        {
            Say(cleaned.Explanation);
        }

        Trace($"  terrain: extracted, read and cleaned {points.Count:N0} points in {reading.Elapsed.TotalSeconds:0.0} s.");
        BuildTerrain(step.Kind, points, LinearUnits.MetresPerUnit(step.Units), step.EntryName, "points", stamp, step.ToposolidType);
    }

    private void ImportToposurfaceFromTin(ImportStep step)
    {
        if (TerrainToBuild(step) is not { } stamp)
        {
            return;
        }

        if (step.Frame is not { } frame)
        {
            Say("The terrain could not be placed: this bundle publishes no origin for its surface.");
            return;
        }

        Stopwatch reading = Stopwatch.StartNew();
        string dxfPath = _archive.Extract(step.EntryName, step.Lifetime, step.ExpectedSha256);

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

        IReadOnlyList<SurfacePoint>? local = SurfaceTinFrame.TryToLocalMetres(tin, frame, step.Units, out string? frameError);
        if (local is null)
        {
            Say(frameError ?? "The surface DXF could not be placed against this bundle's origin.");
            return;
        }

        IReadOnlyList<SurfacePoint> vertices = SurfaceTinSanitiser.Clean(tin, local, step.Crop, out SurfaceCleanReport cleaned, step.CropUnavailable);
        if (cleaned.Explanation.Length > 0)
        {
            Say(cleaned.Explanation);
        }

        Trace($"  terrain: extracted, read and cleaned {vertices.Count:N0} TIN vertices in {reading.Elapsed.TotalSeconds:0.0} s.");

        BuildTerrain(step.Kind, vertices, 1.0, step.EntryName, "TIN vertices", stamp, step.ToposolidType);
    }

    private string? TerrainToBuild(ImportStep step)
    {
        TerrainDecision decision = TerrainIdentity.Decide(
            ExistingGrounds(),
            _archive.Layout.Key.Stem,
            step.ExpectedSha256,
            step.HeightDatum?.Stated);

        if (decision.Explanation.Length > 0)
        {
            Say(decision.Explanation);
        }

        if (decision.Disposition == TerrainDisposition.Create)
        {
            return decision.Stamp;
        }

        _terrainId = new ElementId(decision.ExistingElementId);

        return null;
    }

    private void BuildTerrain(
        ImportStepKind kind,
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
                ToInternalFeet(point.X, metresPerUnit) + MetresToInternal(Anchoring.OffsetEastM),
                ToInternalFeet(point.Y, metresPerUnit) + MetresToInternal(Anchoring.OffsetNorthM),
                ToInternalFeet(point.Z, metresPerUnit)));
        }

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

        if (!TryBuildTerrain(kind, plan, chosenType, toposolidType, revitPoints, relief, stamp, announce: true))
        {
            TerrainBasePlan escalated = TerrainBasePlanner.Escalate(plan, relief);
            Say(escalated.Explanation);

            if (!TryBuildTerrain(kind, escalated, chosenType, toposolidType, revitPoints, relief, stamp, announce: false))
            {
                Say("The terrain could not be built on either base plane, so this project has no "
                    + "ground. The rest of the bundle was still imported.");
                return;
            }
        }

        Say($"Built the terrain from {points.Count:N0} {noun} ({entryName}).");
    }

    private bool TryBuildTerrain(
        ImportStepKind kind,
        TerrainBasePlan plan,
        CandidateToposolidType type,
        TerrainToposolidType toposolidType,
        IList<XYZ> revitPoints,
        TerrainRelief relief,
        string stamp,
        bool announce)
    {
        ImportFailureSwallower swallower = new("Building the terrain");
        using Transaction transaction = BeginTransaction("Mantle Place: terrain from points file", swallower);
        Stopwatch phase = Stopwatch.StartNew();
        double? imageryTypeSeconds = null;

        ElementId levelId = plan.Strategy == TerrainBaseStrategy.DedicatedLevel
            ? FindOrCreateTerrainLevel(plan.LevelElevation)
            : new ElementId(plan.LevelId);

        ElementId typeId = new(type.Id);
        string typeName = type.Name;
        string? declined = null;
        if (toposolidType == TerrainToposolidType.Imagery)
        {
            phase.Restart();
            if (ImageryToposolidType(typeId, out declined) is { } imagery)
            {
                typeId = imagery.Id;
                typeName = imagery.Name;
            }

            imageryTypeSeconds = phase.Elapsed.TotalSeconds;
        }

        if (announce)
        {
            Announce(SlowStepNotice.ForTerrain(kind, onImageryType: typeId != new ElementId(type.Id), relief.PointCount));
        }

        phase.Restart();
        Toposolid terrain = Toposolid.Create(_document, revitPoints, typeId, levelId);
        double createSeconds = phase.Elapsed.TotalSeconds;

        if (plan.HeightOffset != 0.0)
        {
            terrain.get_Parameter(BuiltInParameter.TOPOSOLID_HEIGHTABOVELEVEL_PARAM)?.Set(plan.HeightOffset);

            _document.Regenerate();
        }

        Parameter? comments = terrain.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        bool stamped = comments is not null && !comments.IsReadOnly && comments.Set(stamp);

        ElementId built = terrain.Id;

        string preparing = imageryTypeSeconds is { } prepared
            ? $"the imagery type took {prepared:0.0} s to prepare"
            : "no imagery type was prepared";
        Trace($"  terrain: {preparing}, Toposolid.Create {createSeconds:0.0} s for {revitPoints.Count:N0} points, "
            + $"the rest before the commit {phase.Elapsed.TotalSeconds - createSeconds:0.0} s.");

        if (!CommitAndReport(transaction, swallower))
        {
            return false;
        }

        if (!stamped)
        {
            Say("The terrain could not be stamped with this bundle's identity, so a re-import will "
                + "not recognise it and will build a second ground alongside it.");
        }

        _terrainId = built;
        _terrainVertexCount = relief.PointCount;

        if (declined is not null)
        {
            Say($"The terrain was built on the project's own type \"{type.Name}\" rather than the "
                + $"imagery type: {declined}. The satellite imagery step will try to retype it instead.");
        }

        Say(plan.Explanation
            + $" Type \"{typeName}\"; terrain spans {UnitUtils.ConvertFromInternalUnits(relief.MinZ, UnitTypeId.Meters):0.##}"
            + $" m to {UnitUtils.ConvertFromInternalUnits(relief.MaxZ, UnitTypeId.Meters):0.##} m.");

        return true;
    }

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

    private bool EnsureSmoothedSurface()
    {
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
            Trace($"  smoothing: this Revit would not report the setting - {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        if (enabled)
        {
            if (!_smoothingSettled)
            {
                Trace("  smoothing: already on for this project. Nothing written.");
            }

            _smoothingSettled = true;
            return true;
        }

        if (_smoothingSettled)
        {
            return false;
        }

        _smoothingSettled = true;

        Announce(SlowStepNotice.ForSmoothShading(SubDivisionsOnTerrain(), _terrainVertexCount));

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
            return false;
        }

        bool isEnabled;
        try
        {
            isEnabled = Toposolid.IsSmoothedSurfaceEnabled(_document);
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException
                                       or ArgumentException
                                       or InvalidOperationException)
        {
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

    private bool HasTerrain()
        => _document.GetElement(
            _terrainId != ElementId.InvalidElementId ? _terrainId : TerrainToposolidId()) is Toposolid;

    private int SubDivisionsOnTerrain()
        => _document.GetElement(_terrainId != ElementId.InvalidElementId ? _terrainId : TerrainToposolidId()) is Toposolid terrain
            ? terrain.GetSubDivisionIds().Count
            : 0;

    private List<CandidateLevel> CollectLevels()
        => [.. new FilteredElementCollector(_document)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .Select(level => new CandidateLevel(level.Id.Value, level.Name, level.ProjectElevation))];

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

    private void LinkCadSurface(ImportStep step)
    {
        string dxfPath = _archive.Extract(step.EntryName, step.Lifetime, step.ExpectedSha256);

        DWGImportOptions options = new()
        {
            Placement = ImportPlacement.Origin,
            ThisViewOnly = false,
            ColorMode = ImportColorMode.Preserved,

            Unit = ToImportUnit(step.Units),
        };

        ImportFailureSwallower swallower = new("Linking the surface DXF");
        using Transaction transaction = BeginTransaction("Mantle Place: link surface DXF", swallower);
        bool linked = _document.Link(dxfPath, options, null, out ElementId linkId);
        if (linked && linkId != ElementId.InvalidElementId)
        {
            MoveToAnchor(linkId, "surface DXF link");
        }

        if (!CommitAndReport(transaction, swallower))
        {
            return;
        }

        Say(linked && linkId != ElementId.InvalidElementId
            ? $"Linked the surface DXF ({step.EntryName}). Use Massing & Site ▸ Toposurface ▸ Create from "
              + "Import ▸ Select Import Instance to build a surface from it."
            : $"Revit declined to link the surface DXF ({step.EntryName}).");
    }

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
