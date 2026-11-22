using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal sealed partial class RevitBundleImporter
{
    private static readonly string[] Tiling =
    [
        UnifiedBitmap.TextureScaleLock,
        UnifiedBitmap.TextureOffsetLock,
        UnifiedBitmap.TextureURepeat,
        UnifiedBitmap.TextureVRepeat,
        UnifiedBitmap.TextureLinkTextureTransforms,
        UnifiedBitmap.TextureWAngle,
    ];

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

            foreach (string untouched in Tiling)
            {
                Trace("  drape, as found: " + Describe(bitmap, untouched));
            }

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

            if (template is not null)
            {
                drapeMaterial.AppearanceAssetId = template.Duplicate(name).Id;
            }
            else if (LibraryGenericAsset() is { } library)
            {
                drapeMaterial.AppearanceAssetId = AppearanceAssetElement.Create(_document, name, library).Id;
            }
            else
            {
                return null;
            }
        }

        return drapeMaterial;
    }

    private Asset? LibraryGenericAsset()
    {
        return _document.Application.GetAssets(AssetType.Appearance)
            .FirstOrDefault(asset => string.Equals(asset.Name, "Generic", StringComparison.Ordinal)
                && asset.FindByName(Generic.GenericDiffuse) is not null);
    }

    private ToposolidType? ImageryToposolidType(ElementId projectTypeId, out string? declined)
    {
        declined = null;
        string name = DrapeLayering.ImageryName(_archive.Layout.Key.Stem);

        if (_document.GetElement(projectTypeId) is not ToposolidType projectType)
        {
            declined = "the project's type could not be read";
            return null;
        }

        using SubTransaction preparation = new(_document);
        preparation.Start();

        try
        {
            if (DrapeMaterial(name) is not { } material)
            {
                declined = "this Revit build would not create an appearance asset for the photograph";
            }
            else if (ImageryTypeFrom(projectType, name, material.Id, out string layering) is { } imagery)
            {
                preparation.Commit();
                return imagery;
            }
            else
            {
                declined = layering;
            }
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
        {
            declined = $"Revit refused to prepare it — {ex.Message}";
        }

        preparation.RollBack();
        return null;
    }

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
            if (terrain.GetTypeId() != draped.Id)
            {
                terrain.ChangeTypeId(draped.Id);
            }
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
        {
            layering = $"Revit refused to put the drape type on the terrain — {ex.Message}";
            return false;
        }

        List<ElementId> drapeable = DrapeableSubDivisionIds(terrain);
        int handled = 0;
        foreach (ElementId subdivisionId in drapeable)
        {
            Count(handled++, drapeable.Count);
            if (_document.GetElement(subdivisionId) is not Element subdivision)
            {
                refusedSubDivisions++;
                Trace($"  drape: subdivision {subdivisionId.Value} is no longer in the document.");
                refusals.Add("a subdivision vanished mid-import");
                continue;
            }

            SubDivisionMaterialRoute route = RouteFor(subdivision, out Parameter? material, out ToposolidType? own);

            try
            {
                string? refusal = route switch
                {
                    SubDivisionMaterialRoute.Instance => WearOnInstance(subdivision, material!, materialFor(subdivision)),
                    SubDivisionMaterialRoute.Type => WearThroughType(subdivision, own!, materialFor(subdivision)),
                    _ => "a subdivision had neither a writable Material parameter nor a toposolid type",
                };

                if (refusal is null)
                {
                    drapedSubDivisions++;
                    continue;
                }

                refusedSubDivisions++;
                Trace($"  drape: subdivision {subdivisionId.Value} was not draped — {refusal}.");
                refusals.Add(refusal);
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
            {
                refusedSubDivisions++;
                Trace($"  drape: Revit refused the material on subdivision {subdivisionId.Value} — "
                    + $"{ex.GetType().Name}: {ex.Message}");
                refusals.Add(ex.Message);
            }
        }

        Count(drapeable.Count, drapeable.Count);
        refusalReason = refusals.Count == 0 ? null : string.Join(" / ", refusals);
        return true;
    }

    private static string? WearOnInstance(Element subdivision, Parameter material, ElementId wanted)
    {
        bool wrote = material.Set(wanted);

        ElementId stored = subdivision
            .get_Parameter(BuiltInParameter.TOPOSOLID_SUBDIVIDE_MATERIAL)
            ?.AsElementId() ?? ElementId.InvalidElementId;

        return wrote && stored == wanted
            ? null
            : $"the material did not hold on a subdivision (Set returned {wrote}, reads back {stored.Value})";
    }

    private string? WearThroughType(Element subdivision, ToposolidType own, ElementId wanted)
    {
        if (_document.GetElement(wanted) is not Material drape)
        {
            return "this Revit would not create the subdivision's drape material";
        }

        if (MoveOntoOwnType(subdivision, own, drape, out string layering) is not { } type)
        {
            return layering;
        }

        bool onType = subdivision.GetTypeId() == type.Id;
        bool wearsIt = type.GetCompoundStructure() is { LayerCount: > 0 } structure && structure.GetMaterialId(0) == wanted;
        return onType && wearsIt
            ? null
            : $"the drape type did not hold on a subdivision (on the type: {onType}, its top layer wears the photograph: {wearsIt})";
    }

    private bool TypeForDrapeAtCut(Toposolid subdivision, ToposolidType typeAsCut, bool smoothed)
    {
        SubTransaction? typing = null;
        try
        {
            string materialName = SubDivisionMaterialName(
                subdivision,
                DrapeLayering.ImageryName(_archive.Layout.Key.Stem),
                smoothed);

            typing = new SubTransaction(_document);
            typing.Start();

            if (DrapeMaterial(materialName) is not { } material)
            {
                Trace($"  cut: subdivision {subdivision.Id.Value} left for the drape to type — this Revit "
                    + "would not create an appearance asset for its material.");
            }
            else if (MoveOntoOwnType(subdivision, typeAsCut, material, out string layering) is null)
            {
                Trace($"  cut: subdivision {subdivision.Id.Value} left for the drape to type — {layering}.");
            }
            else
            {
                typing.Commit();
                return true;
            }
        }
        catch (Exception ex) when (IsRevitRefusal(ex))
        {
            Trace($"  cut: subdivision {subdivision.Id.Value} left for the drape to type — Revit refused "
                + $"its type: {ex.Message}");
        }
        finally
        {
            RollBackIfOpen(typing);
        }

        return false;
    }

    private void RollBackIfOpen(SubTransaction? typing)
    {
        if (typing is null)
        {
            return;
        }

        try
        {
            if (typing.GetStatus() == TransactionStatus.Started)
            {
                typing.RollBack();
            }
        }
        catch (Exception ex) when (IsRevitRefusal(ex))
        {
            Trace($"  cut: a drape type's rollback was refused — {ex.Message}");
        }
        finally
        {
            typing.Dispose();
        }
    }

    private SubDivisionMaterialRoute RouteAtCut(Element subdivision, out ToposolidType? own)
    {
        try
        {
            return RouteFor(subdivision, out _, out own);
        }
        catch (Exception ex) when (IsRevitRefusal(ex))
        {
            Trace($"  cut: subdivision {subdivision.Id.Value} would not say what it accepts — {ex.Message}");
            own = null;
            return SubDivisionMaterialRoute.Refused;
        }
    }

    private static bool IsRevitRefusal(Exception ex)
        => ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException or InvalidOperationException;

    private ToposolidType? MoveOntoOwnType(Element subdivision, ToposolidType currentType, Material drape, out string layering)
    {
        if (SubDivisionTypeFor(currentType, drape, out layering) is not { } type)
        {
            return null;
        }

        if (subdivision.GetTypeId() != type.Id)
        {
            subdivision.ChangeTypeId(type.Id);
        }

        return type;
    }

    private ToposolidType? SubDivisionTypeFor(ToposolidType own, Material drape, out string layering)
    {
        string name = SubDivisionMaterial.TypeName(drape.Name);
        if (!SubDivisionMaterial.IsLayeredImageryType(own.Name))
        {
            return ImageryTypeFrom(own, name, drape.Id, out layering);
        }

        ToposolidType? type = new FilteredElementCollector(_document)
            .OfClass(typeof(ToposolidType))
            .Cast<ToposolidType>()
            .FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal))
            ?? own.Duplicate(name) as ToposolidType;

        if (type?.GetCompoundStructure() is not { LayerCount: > 0 } structure)
        {
            layering = $"Revit would not duplicate the subdivision's type \"{own.Name}\"";
            return null;
        }

        if (structure.GetMaterialId(0) != drape.Id)
        {
            structure.SetMaterialId(0, drape.Id);
            type.SetCompoundStructure(structure);
        }

        layering = SidesClause(type.GetCompoundStructure());
        return type;
    }

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

        double stored = property.Value;
        double storedMetres = known
            ? UnitUtils.Convert(stored, unit!, UnitTypeId.Meters)
            : InternalToMetres(stored);

        return new DistanceWrite(
            storedMetres,
            $"{propertyName}: {metres:R} m written as {converted:R} in {unitName}, "
                + $"reads back {stored:R} = {storedMetres:R} m.");
    }

    private readonly record struct DistanceWrite(double StoredMetres, string Report)
    {
        internal static DistanceWrite Refused(string report) => new(double.NaN, report);

        internal bool Holds(double metres) => Math.Abs(StoredMetres - metres) <= 0.05;
    }

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
