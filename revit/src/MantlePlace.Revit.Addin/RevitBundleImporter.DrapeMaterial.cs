using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// The imagery drape's material: the appearance asset, the UnifiedBitmap writes and their
// read-back.
internal sealed partial class RevitBundleImporter
{
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
        DrapeOffset anchor,
        out string? misplaced)
    {
        misplaced = null;

        if (DrapeMaterial(name) is not { } drapeMaterial)
        {
            return ElementId.InvalidElementId;
        }

        ElementId materialId = drapeMaterial.Id;

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
            // corner sits, measured from whichever origin the renderer uses (DrapeAnchor). Together
            // they are the whole placement.
            (string Name, double Metres, DistanceWrite Result)[] writes =
            [
                ("width", placement.WidthM,
                    SetDistance(bitmap, UnifiedBitmap.TextureRealWorldScaleX, placement.WidthM)),
                ("height", placement.HeightM,
                    SetDistance(bitmap, UnifiedBitmap.TextureRealWorldScaleY, placement.HeightM)),
                ("west edge", anchor.Xm,
                    SetDistance(bitmap, UnifiedBitmap.TextureRealWorldOffsetX, anchor.Xm)),
                ("south edge", anchor.Ym,
                    SetDistance(bitmap, UnifiedBitmap.TextureRealWorldOffsetY, anchor.Ym)),
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
    /// The material named <paramref name="name"/>, created or reused, with an appearance asset of its
    /// own — but no photograph yet. <c>null</c> when this Revit has no asset to start one from.
    /// </summary>
    /// <remarks>
    /// Split from <see cref="DrapeMaterialId"/> because the terrain step needs the material before the
    /// drape step can write it: the imagery type's top layer has to wear SOMETHING when the ground is
    /// created on it, and the photograph's offsets cannot be known until that ground exists to be
    /// measured (<see cref="DrapeAnchor"/>).
    /// </remarks>
    private Material? DrapeMaterial(string name)
    {
        Material? existing = new FilteredElementCollector(_document)
            .OfClass(typeof(Material))
            .Cast<Material>()
            .FirstOrDefault(material => string.Equals(material.Name, name, StringComparison.Ordinal));

        ElementId materialId = existing?.Id ?? Material.Create(_document, name);
        if (_document.GetElement(materialId) is not Material drapeMaterial)
        {
            return null;
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
                return null;
            }

            drapeMaterial.AppearanceAssetId = template.Duplicate(name).Id;
        }

        return drapeMaterial;
    }

    /// <summary>
    /// The toposolid type the terrain step builds a drape-bound ground on: a duplicate of
    /// <paramref name="projectTypeId"/> whose top layer wears this bundle's drape material.
    /// </summary>
    /// <returns><c>null</c>, with the reason in <paramref name="declined"/>, when it cannot be built.</returns>
    /// <remarks>
    /// ⛔ This is what keeps the drape step from retyping the terrain. A retype makes Revit rebuild the
    /// whole terrain's element relations on commit — 409 s on an 80,372-point toposolid, the largest
    /// single cost in the import — and it bought nothing a type chosen at creation could not. The
    /// photograph itself is still the drape step's to write; the material is created here bare so the
    /// layer has something to wear, and the drape step finds it by the same name.
    /// </remarks>
    private ToposolidType? ImageryToposolidType(ElementId projectTypeId, out string? declined)
    {
        declined = null;
        string name = DrapeLayering.ImageryName(_archive.Layout.Key.Stem);

        if (_document.GetElement(projectTypeId) is not ToposolidType projectType)
        {
            declined = "the project's type could not be read";
            return null;
        }

        if (DrapeMaterial(name) is not { } material)
        {
            declined = "this Revit build would not create an appearance asset for the photograph";
            return null;
        }

        ToposolidType? imagery = ImageryTypeFrom(projectType, name, material.Id, out string layering);
        if (imagery is null)
        {
            declined = layering;
        }

        return imagery;
    }

    /// <summary>
    /// The imagery type named <paramref name="name"/> — reused, or duplicated from
    /// <paramref name="source"/> — with <paramref name="materialId"/> as its top layer.
    /// </summary>
    /// <returns><c>null</c> when the type cannot be had or cannot be layered.</returns>
    /// <remarks>
    /// <b>Duplicated, never edited.</b> The source type belongs to the project, and texturing it in
    /// place would repaint every other toposolid in the model with this site's photograph.
    /// </remarks>
    private ToposolidType? ImageryTypeFrom(ToposolidType source, string name, ElementId materialId, out string layering)
    {
        ToposolidType? draped = new FilteredElementCollector(_document)
            .OfClass(typeof(ToposolidType))
            .Cast<ToposolidType>()
            .FirstOrDefault(type => string.Equals(type.Name, name, StringComparison.Ordinal));

        draped ??= source.Duplicate(name) as ToposolidType;

        if (draped is null)
        {
            layering = $"Revit would not duplicate the terrain's type \"{source.Name}\"";
            return null;
        }

        return TryLayerImagery(draped, materialId, out layering) ? draped : null;
    }

    /// <summary>
    /// Puts <paramref name="materialId"/> on a thin top layer of a DUPLICATE of the terrain's type,
    /// retypes the terrain onto it unless it already wears it, and drapes the subdivisions this
    /// import created.
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
        Func<Element, ElementId> materialFor,
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

        if (ImageryTypeFrom(current, typeName, materialId, out layering) is not { } draped)
        {
            return false;
        }

        try
        {
            // ⛔ Only when the terrain does not already wear it. The terrain step builds a drape-bound
            // ground on this type from the start (ImportStep.ToposolidType), so this is now the rare
            // path: ground an earlier build of this plugin laid and ADR 0004's reuse arm kept, or a
            // terrain step that could not prepare the type. Asking first is not a politeness — a
            // retype rebuilds every element relation the terrain has, 409 s on an 80,372-point
            // toposolid, and whether Revit short-circuits a same-type ChangeTypeId was never measured.
            if (terrain.GetTypeId() != draped.Id)
            {
                terrain.ChangeTypeId(draped.Id);
            }
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
                ElementId wanted = materialFor(subdivision);
                bool wrote = material.Set(wanted);

                // ⛔ Read back. The same four texture properties two methods down are read back for
                // the same reason, and that read-back is what caught the drape going in as feet and
                // tiling the photograph twelve times across the site. A Set returning true is a
                // claim about the call, not about what Revit stored.
                ElementId stored = subdivision
                    .get_Parameter(BuiltInParameter.TOPOSOLID_SUBDIVIDE_MATERIAL)
                    ?.AsElementId() ?? ElementId.InvalidElementId;

                if (wrote && stored == wanted)
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
}
