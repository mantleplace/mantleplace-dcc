using System.Globalization;
using Autodesk.Revit.DB;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal sealed partial class RevitBundleImporter
{
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

        string imagePath = _archive.Extract(step.EntryName, step.Lifetime, step.ExpectedSha256);
        string name = DrapeLayering.ImageryName(_archive.Layout.Key.Stem);

        bool wearsImageryType = _document.GetElement(terrain.GetTypeId()) is ToposolidType worn
            && string.Equals(worn.Name, name, StringComparison.Ordinal);

        bool smoothed = EnsureSmoothedSurface();
        DrapeOffset groundAnchor = AnchorFor(terrain, "the terrain", placement, smoothed);

        if (SlowStepNotice.For(step.Kind, _terrainVertexCount, wearsImageryType ? 0 : 1) is { } notice)
        {
            Announce(notice);
        }

        (int onInstances, int onTheirTypes, int toRetype) = CountDrapeRoutes(terrain, name, smoothed);
        if (SlowStepNotice.ForSubDivisionRetypes(toRetype, _terrainVertexCount) is { } retypes)
        {
            Announce(retypes);
        }

        Announce(SlowStepNotice.ForDrapeOnInstances(onInstances, _terrainVertexCount));

        if (SlowStepNotice.ForDrapeCommit(onTheirTypes, toRetype, wearsImageryType ? 0 : 1, _terrainVertexCount) is { } commit)
        {
            Announce(commit);
        }

        ImportFailureSwallower swallower = new("Applying the aerial photograph");
        using Transaction transaction = BeginTransaction("Mantle Place: satellite imagery", swallower);

        ElementId materialId = DrapeMaterialId(name, imagePath, placement, groundAnchor, out string? misplaced);
        if (materialId == ElementId.InvalidElementId)
        {
            transaction.RollBack();
            Say(
                $"Skipped the satellite imagery ({step.EntryName}): this Revit build would not create an "
                + (wearsImageryType
                    ? "appearance asset for it, so the terrain's imagery layer carries no photograph."
                    : "appearance asset for it, so the terrain was left with the material it had."));
            return;
        }

        Dictionary<string, ElementId> sharedByKeyword = new(StringComparer.Ordinal);
        if (!TryWearMaterial(
            terrain,
            materialId,
            name,
            subdivision => smoothed
                ? SubDivisionMaterialId(subdivision, name, imagePath, placement)
                : SharedMaterialId(subdivision, name, imagePath, placement, groundAnchor, materialId, sharedByKeyword),
            out int drapedSubDivisions,
            out int refusedSubDivisions,
            out string? refusalReason,
            out string layering))
        {
            transaction.RollBack();
            Say(
                $"Skipped the satellite imagery ({step.EntryName}): {layering}, so "
                + (wearsImageryType
                    ? "the terrain's imagery layer carries no photograph."
                    : "the terrain was left untouched rather than half-changed."));
            return;
        }

        if (!CommitAndReport(transaction, swallower))
        {
            return;
        }

        string summary =
            $"Draped the satellite imagery from {step.EntryName} over the terrain — {placement.PixelSize} pixels "
            + $"across {placement.WidthM:N0} × {placement.HeightM:N0} m, positioned from "
            + (placement.ExtentFromDrapeBlock
                ? "the bundle's own imagery extent"
                : "the DEM's bounds, corroborated against the image's pixel grid");

        summary += $"; {layering}";

        summary += SubDivisionDrape.Clause(
            drapedSubDivisions,
            refusedSubDivisions,
            refusalReason is null ? [] : [refusalReason]) ?? string.Empty;

        Say(summary + ".");

        if (misplaced is not null)
        {
            Say($"⚠ The aerial photograph is not pinned where it should be: {misplaced}. The imagery "
                + "will repeat or sit off the ground it belongs to. This is a plugin defect — please "
                + "report it with this log.");
        }

        Say(TerrainSmoothing.DrapeNotice(smoothed));
    }

    private DrapeOffset AnchorFor(Element element, string label, DrapePlacement placement, bool smoothed)
    {
        BoundingBoxXYZ? box = element.get_BoundingBox(null);
        if (box is null)
        {
            Trace($"  drape: {label} has no bounding box, so its offset is anchored to the origin.");
            return DrapeAnchor.For(placement, smoothShading: false, 0.0, 0.0);
        }

        double minXm = InternalToMetres(box.Min.X);
        double minYm = InternalToMetres(box.Min.Y);

        DrapePlacement inDocument = placement.Shifted(Anchoring.OffsetEastM, Anchoring.OffsetNorthM);
        DrapeOffset anchor = DrapeAnchor.For(inDocument, smoothed, minXm, minYm);
        Trace("  " + DrapeAnchor.Describe(label, inDocument, smoothed, minXm, minYm, anchor));
        return anchor;
    }

    private ElementId SubDivisionMaterialId(Element subdivision, string name, string imagePath, DrapePlacement placement)
    {
        string materialName = SubDivisionMaterialName(subdivision, name, smoothed: true);

        DrapeOffset anchor = AnchorFor(subdivision, $"subdivision {subdivision.Id.Value}", placement, smoothed: true);
        ElementId id = DrapeMaterialId(materialName, imagePath, placement, anchor, out string? misplaced);
        if (misplaced is not null)
        {
            Trace($"  drape: ⚠ subdivision {subdivision.Id.Value}'s photograph is not pinned where it should be: {misplaced}");
        }

        return id;
    }

    private ElementId SharedMaterialId(
        Element subdivision,
        string name,
        string imagePath,
        DrapePlacement placement,
        DrapeOffset groundAnchor,
        ElementId groundMaterialId,
        Dictionary<string, ElementId> sharedByKeyword)
    {
        if (GroundLayerWords.For(NamingStampOf(subdivision).Layer).MaterialWord is not { } keyword)
        {
            return groundMaterialId;
        }

        if (!sharedByKeyword.TryGetValue(keyword, out ElementId? id))
        {
            id = DrapeMaterialId(
                GroundMaterialNames.Shared(name, keyword), imagePath, placement, groundAnchor, out string? misplaced);
            if (misplaced is not null)
            {
                Trace($"  drape: ⚠ the \"{keyword}\" material's photograph is not pinned where it should be: {misplaced}");
            }

            sharedByKeyword[keyword] = id;
        }

        return id == ElementId.InvalidElementId ? groundMaterialId : id;
    }

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

    private (int OnInstances, int OnTheirTypes, int ToRetype) CountDrapeRoutes(Toposolid terrain, string imageryName, bool smoothed)
    {
        int onInstances = 0;
        int onTheirTypes = 0;
        int toRetype = 0;
        foreach (ElementId id in DrapeableSubDivisionIds(terrain))
        {
            if (_document.GetElement(id) is not Element subdivision)
            {
                continue;
            }

            SubDivisionMaterialRoute route = RouteFor(subdivision, out _, out ToposolidType? own);
            if (route == SubDivisionMaterialRoute.Instance)
            {
                onInstances++;
                continue;
            }

            if (route != SubDivisionMaterialRoute.Type)
            {
                continue;
            }

            if (SubDivisionMaterial.NeedsRetype(
                SubDivisionMaterialRoute.Type, own?.Name, SubDivisionMaterialName(subdivision, imageryName, smoothed)))
            {
                toRetype++;
            }
            else
            {
                onTheirTypes++;
            }
        }

        return (onInstances, onTheirTypes, toRetype);
    }

    private SubDivisionMaterialRoute RouteFor(Element subdivision, out Parameter? material, out ToposolidType? own)
    {
        material = subdivision.get_Parameter(BuiltInParameter.TOPOSOLID_SUBDIVIDE_MATERIAL);
        own = _document.GetElement(subdivision.GetTypeId()) as ToposolidType;
        return SubDivisionMaterial.Route(material is { IsReadOnly: false }, own is not null);
    }

    private string SubDivisionMaterialName(Element subdivision, string imageryName, bool smoothed)
    {
        GroundStamp stamp = NamingStampOf(subdivision);
        string? keyword = GroundLayerWords.For(stamp.Layer).MaterialWord;
        return smoothed
            ? GroundMaterialNames.PerSubDivision(imageryName, stamp.Layer, stamp.Token, keyword)
            : GroundMaterialNames.Shared(imageryName, keyword);
    }

    private GroundStamp NamingStampOf(Element subdivision)
        => SiteBoundaryIdentity.NamingStamp(
            subdivision.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString(),
            _stampsCutWith.GetValueOrDefault(subdivision.Id),
            subdivision.Id.Value,
            _archive.Layout.Key.Stem);

    private bool TryLayerImagery(ToposolidType draped, ElementId materialId, out string layering)
    {
        double minimum = CompoundStructure.GetMinimumLayerThickness();

        CompoundStructure structure = draped.GetCompoundStructure();
        if (structure is null || structure.LayerCount == 0)
        {
            layering = "the terrain's type has no compound structure this plugin could split";
            return false;
        }

        ElementId originalMaterialId = structure.GetMaterialId(0);
        MaterialFunctionAssignment function = structure.GetLayerFunction(0);

        DrapeLayerDecision decision = DrapeLayering.Decide(
            originalMaterialId == materialId,
            structure.GetLayerWidth(0),
            minimum);

        if (decision.Verdict == DrapeLayerVerdict.AlreadyLayered)
        {
            Trace($"  drape: the photograph is already the top layer of \"{draped.Name}\", so its "
                + $"structure was left alone ({DescribeLayers(structure, materialId)}).");
            layering = SidesClause(structure);
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
            Trace("  drape: ⚠ the imagery material is NOT on layer 0 alone — the photograph may reach "
                + "a vertical face. Please report this log.");
        }

        layering = SidesClause(written);
        return true;
    }

    private static string SidesClause(CompoundStructure structure)
        => $"the photograph is a {Mm(structure.GetLayerWidth(0))} layer on top of the terrain's "
            + $"own {Mm(structure.GetWidth() - structure.GetLayerWidth(0))}, so its sides keep the "
            + "material they had";

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

    private static string Mm(double internalFeet)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{DrapeLayering.MillimetresFromInternalFeet(internalFeet):0.###} mm");
}
