using System.Globalization;
using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// The imagery-drape step: which elements wear the photograph, where it is anchored, and the
// layered toposolid type that carries it.
internal sealed partial class RevitBundleImporter
{
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
    /// <b>The type is duplicated, never edited.</b> The project's own toposolid type belongs to the
    /// project — texturing it in place would repaint every other toposolid in the model with this
    /// site's aerial photograph. The duplicate is named from the bundle's cache key
    /// (<see cref="DrapeLayering.ImageryName"/>), so importing the same order twice reuses one type
    /// rather than growing a new one each time.
    /// </para>
    /// <para>
    /// ⛔ <b>On a terrain this run built, the type is already on it.</b> The planner decides the
    /// terrain's type before the terrain exists (<see cref="ImportStep.ToposolidType"/>), and the
    /// terrain step creates the ground on the imagery type with a bare drape material, so what is
    /// left here is writing the photograph into that material. The retype below is kept for ground
    /// not on that type: an earlier import's terrain kept by ADR 0004's reuse arm, which may predate
    /// the change, or this run's terrain when the terrain step could not prepare the type. It is the
    /// expensive half: 409 s on an 80,372-point toposolid.
    /// </para>
    /// <para>
    /// ⚠️ <b>None of this is reachable by CI</b> (no Revit on a hosted runner).
    /// <c>AppearanceAssetEditScope</c>, the <c>UnifiedBitmap</c> schema and
    /// <c>ToposolidType.Duplicate</c> — which the terrain step now calls too — all compile, which
    /// says nothing about what they do; only a real import does.
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
        string name = DrapeLayering.ImageryName(_archive.Layout.Key.Stem);

        // Whether the terrain step already built this ground on the imagery type — which decides
        // whether this step retypes at all, and therefore whether it is slow.
        bool wearsImageryType = _document.GetElement(terrain.GetTypeId()) is ToposolidType worn
            && string.Equals(worn.Name, name, StringComparison.Ordinal);

        // ⛔ BEFORE the material is written, because the offsets depend on the answer. Under smooth
        // shading Revit measures a real-world texture offset from the element's bounding-box corner;
        // under flat shading from the project origin (DrapeAnchor). The photograph has to be placed
        // for the renderer that will draw it, so the setting is decided first and read back.
        bool smoothed = EnsureSmoothedSurface();
        DrapeOffset groundAnchor = AnchorFor(terrain, "the terrain", placement, smoothed);

        // The host retype is the dominant cost, so the work count is 1 rather than the subdivision
        // count — the drape is slow on a terrain with no subdivisions at all. A terrain already on the
        // imagery type has no retype coming, and announcing a seven-minute freeze there would teach a
        // curator to ignore the line.
        if (SlowStepNotice.For(step.Kind, _terrainVertexCount, wearsImageryType ? 0 : 1) is { } notice)
        {
            Say(notice);
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

        // One material carries one offset, and under smooth shading every subdivision's offset is
        // its own — so each gets its own material, anchored to its own corner. Under flat shading
        // the origin is shared and so is the material, save that a renderer keyword needs a name of
        // its own: one extra material per keyword, anchored as the ground is.
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

        // The photograph is placed for one renderer, and the curator owns the switch between the
        // two. Said here, after the drape, so the sentence follows the thing it is about.
        Say(TerrainSmoothing.DrapeNotice(smoothed));
    }

    /// <summary>
    /// The <c>texture_RealWorldOffset</c> pair for one element, from its bounding box and the
    /// smooth-shading state — decided in <see cref="DrapeAnchor"/>, read here.
    /// </summary>
    /// <remarks>
    /// An element with no bounding box gets the origin-anchored offset and a trace line saying so;
    /// under smooth shading that photograph will be misplaced on it, and the log records why rather
    /// than the plugin guessing a corner.
    /// </remarks>
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
        DrapeOffset anchor = DrapeAnchor.For(placement, smoothed, minXm, minYm);
        Trace("  " + DrapeAnchor.Describe(label, placement, smoothed, minXm, minYm, anchor));
        return anchor;
    }

    /// <summary>
    /// A subdivision's own drape material: the ground's image and scale, anchored to the
    /// subdivision's corner, named by the subdivision's stamp and its renderer keyword so a
    /// re-import reuses it (<see cref="GroundMaterialNames.PerSubDivision"/>).
    /// </summary>
    private ElementId SubDivisionMaterialId(Element subdivision, string name, string imagePath, DrapePlacement placement)
    {
        // A subdivision this run cut and could not stamp is named by its id, under the land-use
        // spelling this plugin has always used for it.
        GroundStamp stamp = SiteBoundaryIdentity.Parse(
            subdivision.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString(),
            _archive.Layout.Key.Stem)
            ?? new GroundStamp(GroundLayer.LandUse, subdivision.Id.Value.ToString(CultureInfo.InvariantCulture));

        string materialName = GroundMaterialNames.PerSubDivision(
            name,
            stamp.Layer,
            stamp.Token,
            _subDivisionKeywords.GetValueOrDefault(subdivision.Id));

        DrapeOffset anchor = AnchorFor(subdivision, $"subdivision {subdivision.Id.Value}", placement, smoothed: true);
        ElementId id = DrapeMaterialId(materialName, imagePath, placement, anchor, out string? misplaced);
        if (misplaced is not null)
        {
            Trace($"  drape: ⚠ subdivision {subdivision.Id.Value}'s photograph is not pinned where it should be: {misplaced}");
        }

        return id;
    }

    /// <summary>
    /// Under flat shading, the material a subdivision shares: the ground's own when its subtype names
    /// no renderer keyword, and otherwise one per keyword, made once per step
    /// (<see cref="GroundMaterialNames.Shared"/>).
    /// </summary>
    /// <remarks>
    /// Anchored to the project origin like the ground, which is what flat shading measures every
    /// offset from, so the photograph lines up across the subdivision's edge. A keyword material this
    /// Revit will not make falls back to the ground's: the photograph matters more than the grass.
    /// </remarks>
    private ElementId SharedMaterialId(
        Element subdivision,
        string name,
        string imagePath,
        DrapePlacement placement,
        DrapeOffset groundAnchor,
        ElementId groundMaterialId,
        Dictionary<string, ElementId> sharedByKeyword)
    {
        if (_subDivisionKeywords.GetValueOrDefault(subdivision.Id) is not { } keyword)
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
            // front of us rather than from which import happens to be running — the terrain step
            // earlier in this run, or an earlier import.
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

        layering = SidesClause(written);
        return true;
    }

    /// <summary>
    /// What a layered imagery type does for the terrain's vertical faces, as a curator reads it.
    /// </summary>
    /// <remarks>
    /// One sentence whether this run split the layer or found it split: the terrain step now does
    /// the split on a first import, so the drape step's summary would otherwise describe the
    /// mechanism only on the path that has become rare.
    /// </remarks>
    private static string SidesClause(CompoundStructure structure)
        => $"the photograph is a {Mm(structure.GetLayerWidth(0))} layer on top of the terrain's "
            + $"own {Mm(structure.GetWidth() - structure.GetLayerWidth(0))}, so its sides keep the "
            + "material they had";

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
}
