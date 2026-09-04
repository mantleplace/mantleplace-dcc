// UseWPF switches the SDK to the WindowsDesktop implicit-usings set, which drops System.IO.
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// Renders the imported terrain under every candidate shading and drape arrangement, so that
/// "smooth ground AND a correct photograph" is answered by images rather than by argument.
/// </summary>
/// <remarks>
/// <para>
/// Diagnostic scaffolding in the <see cref="TerrainProbeCommand"/> tradition, gated behind
/// <see cref="DirectoryVariable"/> so a curator clicking the probe button never runs it. Each
/// experiment is applied inside a transaction group that is rolled back once the view has been
/// exported, so nothing survives in the project; the PNGs beside the report are the whole output.
/// </para>
/// <para>
/// The question it exists for: Revit 2025's toposolid smooth shading removes the flat-triangle
/// faceting and breaks the real-world-scaled bitmap into four quadrants. Two of the arrangements
/// below attack the faceting from the other side — taking the directional light out of the view
/// (<c>SunlightIntensity</c>), or making the photograph self-illuminating — so that per-face
/// shading has nothing to show while the mapping is left exactly as it is. The rest establish the
/// baseline and try the cheap variations under smoothing.
/// </para>
/// </remarks>
internal static class DrapeRenderProbe
{
    /// <summary>Set to a directory to run this probe and write its renders there.</summary>
    internal const string DirectoryVariable = "MANTLEPLACE_PROBE_RENDER_DIR";

    private const int PixelSize = 1800;

    /// <summary>One arrangement of the terrain, named for the PNG it produces.</summary>
    /// <param name="Smoothed">The document-wide smooth-shading setting for this render, or null to leave it as found.</param>
    /// <param name="Style">The view's display style for this render.</param>
    /// <param name="Sunlight">A sunlight/shadow intensity override, or null to leave the view's.</param>
    /// <param name="Tweak">A change to the material or type, applied inside the transaction.</param>
    private sealed record Experiment(
        string Name,
        string Description,
        bool? Smoothed,
        DisplayStyle Style,
        int? Sunlight,
        Action<Scene>? Tweak);

    /// <summary>What every experiment is applied to.</summary>
    private sealed class Scene
    {
        internal required Document Document { get; init; }

        internal required Toposolid Terrain { get; init; }

        internal required ToposolidType Type { get; init; }

        internal required Material Drape { get; init; }

        internal required View3D Top { get; init; }

        internal required View3D Oblique { get; init; }

        internal required StringBuilder Report { get; init; }
    }

    internal static void Run(Document document, string outputDirectory, StringBuilder report)
    {
        report.AppendLine("DRAPE RENDER PROBE — every change below is rolled back; only the PNGs survive");
        report.AppendLine(CultureInfo.InvariantCulture, $"  output: {outputDirectory}");

        try
        {
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"  cannot create the output directory — {ex.Message}");
            return;
        }

        Toposolid? terrain = NewestGround(document, out List<Toposolid> others, report);
        if (terrain is null)
        {
            report.AppendLine("  no ground toposolid in this project, nothing to render.");
            report.AppendLine();
            return;
        }

        if (document.GetElement(terrain.GetTypeId()) is not ToposolidType type)
        {
            report.AppendLine("  the ground toposolid has no readable type, nothing to render.");
            report.AppendLine();
            return;
        }

        CompoundStructure? structure = type.GetCompoundStructure();
        ElementId drapeId = structure is { LayerCount: > 0 }
            ? structure.GetMaterialId(0)
            : ElementId.InvalidElementId;

        if (document.GetElement(drapeId) is not Material drape)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  the terrain's type \"{type.Name}\" carries no material on its top layer, so there is no drape to render.");
            report.AppendLine();
            return;
        }

        report.AppendLine(CultureInfo.InvariantCulture,
            $"  terrain {terrain.Id.Value} on type \"{type.Name}\", drape material {drape.Id.Value} \"{drape.Name}\"; "
            + $"{others.Count} other toposolid(s) hidden from the renders.");

        bool wasSmoothed = Toposolid.IsSmoothedSurfaceEnabled(document);
        report.AppendLine(CultureInfo.InvariantCulture, $"  smoothing as found: {wasSmoothed}");

        using TransactionGroup outer = new(document, "Mantle Place: drape render probe");
        outer.Start();

        try
        {
            Scene scene = BuildScene(document, terrain, type, drape, others, report);

            foreach (Experiment experiment in Selected())
            {
                RunOne(scene, experiment, outputDirectory);
            }
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException
                                       or ArgumentException
                                       or InvalidOperationException
                                       or IOException)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  RENDER PROBE ABORTED: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (outer.HasStarted())
            {
                outer.RollBack();
            }

            report.AppendLine(CultureInfo.InvariantCulture,
                $"  ROLLED BACK: smoothing reads {Toposolid.IsSmoothedSurfaceEnabled(document)} "
                + $"(expected {wasSmoothed}).");
        }

        report.AppendLine();
    }

    /// <summary>The arrangements, in the order they are rendered. Material and type edits come last.</summary>
    private static IEnumerable<Experiment> Experiments()
    {
        yield return new("00-asis-realistic", "as found: nothing changed, Realistic — what the last import actually left", null, DisplayStyle.Realistic, null, null);
        yield return new("01-off-realistic", "baseline: smoothing off, Realistic", false, DisplayStyle.Realistic, null, null);
        yield return new("02-on-realistic", "smoothing on, Realistic — the four-quadrant failure", true, DisplayStyle.Realistic, null, null);
        yield return new("03-off-textures", "smoothing off, Textures style", false, DisplayStyle.Textures, null, null);
        yield return new("04-on-textures", "smoothing on, Textures style", true, DisplayStyle.Textures, null, null);
        yield return new("05-off-flatcolors", "smoothing off, Consistent Colors", false, DisplayStyle.FlatColors, null, null);
        yield return new("06-off-shading", "smoothing off, Shaded", false, DisplayStyle.Shading, null, null);
        yield return new("07-off-realistic-sun0", "smoothing off, Realistic, sunlight and shadow intensity 0", false, DisplayStyle.Realistic, 0, null);
        yield return new("08-on-realistic-linktransforms", "smoothing on, Realistic, texture_LinkTextureTransforms = true", true, DisplayStyle.Realistic, null, LinkTextureTransforms);
        yield return new("09-off-realistic-selfillum100", "smoothing off, Realistic, drape self-illuminating at 100 cd/m2", false, DisplayStyle.Realistic, null, scene => SelfIlluminate(scene, 100.0));
        yield return new("10-off-realistic-selfillum1000", "smoothing off, Realistic, drape self-illuminating at 1000 cd/m2", false, DisplayStyle.Realistic, null, scene => SelfIlluminate(scene, 1000.0));
        yield return new("11-off-realistic-selfillum100-sun0", "smoothing off, Realistic, self-illuminating at 100 cd/m2 and sunlight 0", false, DisplayStyle.Realistic, 0, scene => SelfIlluminate(scene, 100.0));
        yield return new("12-on-realistic-selfillum100", "smoothing on, Realistic, self-illuminating at 100 cd/m2", true, DisplayStyle.Realistic, null, scene => SelfIlluminate(scene, 100.0));
        yield return new("13-on-realistic-finishlayer", "smoothing on, Realistic, top layer function Finish1 instead of Structure", true, DisplayStyle.Realistic, null, FinishLayer);
        yield return new("14-on-realistic-bboxoffsets", "smoothing on, Realistic, real-world offsets measured from each toposolid's bounding-box corner", true, DisplayStyle.Realistic, null, BoundingBoxRelativeOffsets);
        yield return new("15-off-realistic-bboxoffsets", "control: smoothing off with the same bounding-box-relative offsets", false, DisplayStyle.Realistic, null, BoundingBoxRelativeOffsets);
    }

    /// <summary>Set to a comma-separated list of name prefixes to render only those experiments.</summary>
    internal const string OnlyVariable = "MANTLEPLACE_PROBE_RENDER_ONLY";

    private static IEnumerable<Experiment> Selected()
    {
        string? only = Environment.GetEnvironmentVariable(OnlyVariable);
        if (string.IsNullOrWhiteSpace(only))
        {
            return Experiments();
        }

        string[] prefixes = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Experiments().Where(experiment => prefixes.Any(prefix => experiment.Name.StartsWith(prefix, StringComparison.Ordinal)));
    }

    private static void RunOne(Scene scene, Experiment experiment, string outputDirectory)
    {
        Document document = scene.Document;
        StringBuilder report = scene.Report;
        Stopwatch clock = Stopwatch.StartNew();

        report.AppendLine(CultureInfo.InvariantCulture, $"  [{experiment.Name}] {experiment.Description}");

        using TransactionGroup group = new(document, $"Mantle Place render probe: {experiment.Name}");
        group.Start();

        try
        {
            ImportFailureSwallower swallower = new(experiment.Name);
            using (Transaction transaction = new(document, experiment.Name))
            {
                transaction.Start();
                FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
                options.SetFailuresPreprocessor(swallower);
                options.SetForcedModalHandling(false);
                options.SetClearAfterRollback(true);
                transaction.SetFailureHandlingOptions(options);

                if (experiment.Smoothed is { } smoothed)
                {
                    Toposolid.SetSmoothedSurface(document, smoothed);
                }

                foreach (View3D view in new[] { scene.Top, scene.Oblique })
                {
                    view.DisplayStyle = experiment.Style;
                    if (experiment.Sunlight is { } sunlight)
                    {
                        view.SunlightIntensity = sunlight;
                        view.ShadowIntensity = sunlight;
                    }
                }

                experiment.Tweak?.Invoke(scene);

                // Inside the transaction: Regenerate is a modification as far as Revit is concerned,
                // and outside one it throws "Modification of the document is forbidden".
                document.Regenerate();

                TransactionStatus status = transaction.Commit();
                foreach (string line in swallower.Lines)
                {
                    report.AppendLine(CultureInfo.InvariantCulture, $"      {line}");
                }

                if (status != TransactionStatus.Committed)
                {
                    report.AppendLine(CultureInfo.InvariantCulture, $"      NOT RENDERED: the transaction ended {status}.");
                    return;
                }
            }

            report.AppendLine(CultureInfo.InvariantCulture,
                $"      applied in {clock.Elapsed.TotalSeconds:N1} s; smoothing reads {Toposolid.IsSmoothedSurfaceEnabled(document)}, "
                + $"top view style {scene.Top.DisplayStyle}, sunlight {scene.Top.SunlightIntensity}, shadow {scene.Top.ShadowIntensity}.");

            Export(scene, experiment.Name, outputDirectory);
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException
                                       or ArgumentException
                                       or InvalidOperationException
                                       or IOException)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"      THREW {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (group.HasStarted())
            {
                group.RollBack();
            }

            report.AppendLine(CultureInfo.InvariantCulture, $"      done in {clock.Elapsed.TotalSeconds:N1} s, rolled back.");
        }
    }

    private static void Export(Scene scene, string name, string outputDirectory)
    {
        string prefix = Path.Combine(outputDirectory, name);
        ImageExportOptions options = new()
        {
            ExportRange = ExportRange.SetOfViews,
            FilePath = prefix,
            HLRandWFViewsFileType = ImageFileType.PNG,
            ShadowViewsFileType = ImageFileType.PNG,
            ZoomType = ZoomFitType.FitToPage,
            PixelSize = PixelSize,
            FitDirection = FitDirectionType.Horizontal,
            ImageResolution = ImageResolution.DPI_150,
        };
        options.SetViewsAndSheets([scene.Top.Id, scene.Oblique.Id]);

        Stopwatch clock = Stopwatch.StartNew();
        scene.Document.ExportImage(options);

        string[] written = Directory.GetFiles(outputDirectory, name + "*.png");
        string names = string.Join(", ", written.Select(Path.GetFileName));
        scene.Report.AppendLine(CultureInfo.InvariantCulture,
            $"      exported {written.Length} file(s) in {clock.Elapsed.TotalSeconds:N1} s: {names}");
    }

    /// <summary>Two 3D views of the terrain alone: straight down, and oblique from the south-west.</summary>
    private static Scene BuildScene(
        Document document,
        Toposolid terrain,
        ToposolidType type,
        Material drape,
        List<Toposolid> others,
        StringBuilder report)
    {
        BoundingBoxXYZ box = terrain.get_BoundingBox(null)
            ?? throw new InvalidOperationException("the terrain has no bounding box");

        XYZ centre = (box.Min + box.Max) * 0.5;
        double span = Math.Max(box.Max.X - box.Min.X, box.Max.Y - box.Min.Y);

        ElementId viewTypeId = new FilteredElementCollector(document)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .First(candidate => candidate.ViewFamily == ViewFamily.ThreeDimensional)
            .Id;

        HashSet<ElementId> keep = [terrain.Id, .. terrain.GetSubDivisionIds()];
        List<ElementId> hide = [];
        foreach (Toposolid other in others)
        {
            if (!keep.Contains(other.Id))
            {
                hide.Add(other.Id);
            }
        }

        View3D top;
        View3D oblique;
        using (Transaction transaction = new(document, "Mantle Place render probe: views"))
        {
            transaction.Start();

            top = View3D.CreateIsometric(document, viewTypeId);
            top.Name = "Mantle Place render probe - top";
            top.SetOrientation(new ViewOrientation3D(centre + XYZ.BasisZ * span, XYZ.BasisY, -XYZ.BasisZ));

            oblique = View3D.CreateIsometric(document, viewTypeId);
            oblique.Name = "Mantle Place render probe - oblique";
            XYZ eye = centre + new XYZ(-span, -span, span * 0.7);
            XYZ forward = (centre - eye).Normalize();
            XYZ right = forward.CrossProduct(XYZ.BasisZ).Normalize();
            XYZ up = right.CrossProduct(forward).Normalize();
            oblique.SetOrientation(new ViewOrientation3D(eye, up, forward));

            foreach (View3D view in new[] { top, oblique })
            {
                view.DetailLevel = ViewDetailLevel.Fine;
                view.CropBoxActive = false;
                view.CropBoxVisible = false;
                HideEverythingButToposolids(document, view);

                List<ElementId> hideable = [.. hide.Where(id => document.GetElement(id)?.CanBeHidden(view) == true)];
                if (hideable.Count > 0)
                {
                    view.HideElements(hideable);
                }
            }

            transaction.Commit();
        }

        report.AppendLine(CultureInfo.InvariantCulture,
            $"  views {top.Id.Value} (top) and {oblique.Id.Value} (oblique); terrain spans {UnitUtils.ConvertFromInternalUnits(span, UnitTypeId.Meters):N0} m; "
            + $"{hide.Count} other toposolid(s) hidden.");

        return new Scene
        {
            Document = document,
            Terrain = terrain,
            Type = type,
            Drape = drape,
            Top = top,
            Oblique = oblique,
            Report = report,
        };
    }

    /// <summary>
    /// Hides every model and annotation category except toposolids, so the renders show ground and
    /// nothing else — the 39,739 tree solids in particular read as white speckle from above.
    /// </summary>
    private static void HideEverythingButToposolids(Document document, View view)
    {
        foreach (Category category in document.Settings.Categories)
        {
            if (category.BuiltInCategory == BuiltInCategory.OST_Toposolid)
            {
                continue;
            }

            if (category.CategoryType is not (CategoryType.Model or CategoryType.Annotation or CategoryType.AnalyticalModel))
            {
                continue;
            }

            try
            {
                if (view.CanCategoryBeHidden(category.Id))
                {
                    view.SetCategoryHidden(category.Id, true);
                }
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException)
            {
                // A category that will not hide is one more thing in the picture, not a failure.
            }
        }
    }

    /// <summary>
    /// The ground toposolid with the highest id — the newest, where a project has been imported into
    /// more than once and carries a stack of them — and every other toposolid in the project.
    /// </summary>
    private static Toposolid? NewestGround(Document document, out List<Toposolid> others, StringBuilder report)
    {
        List<Toposolid> all = [.. new FilteredElementCollector(document)
            .OfClass(typeof(Toposolid))
            .Cast<Toposolid>()];

        HashSet<ElementId> subdivisionIds = [];
        foreach (Toposolid toposolid in all)
        {
            foreach (ElementId id in toposolid.GetSubDivisionIds())
            {
                subdivisionIds.Add(id);
            }
        }

        List<Toposolid> grounds = [.. all.Where(toposolid => !subdivisionIds.Contains(toposolid.Id))];
        Toposolid? newest = grounds.OrderByDescending(ground => ground.Id.Value).FirstOrDefault();

        report.AppendLine(CultureInfo.InvariantCulture,
            $"  {all.Count} toposolid(s): {grounds.Count} ground, {subdivisionIds.Count} subdivision(s); "
            + $"rendering ground {newest?.Id.Value.ToString(CultureInfo.InvariantCulture) ?? "(none)"}.");

        others = newest is null ? all : [.. all.Where(toposolid => toposolid.Id != newest.Id)];
        return newest;
    }

    private static void LinkTextureTransforms(Scene scene)
    {
        EditDrape(scene, (editable, bitmap) =>
        {
            if (bitmap?.FindByName(UnifiedBitmap.TextureLinkTextureTransforms) is AssetPropertyBoolean link && !link.IsReadOnly)
            {
                link.Value = true;
                scene.Report.AppendLine(CultureInfo.InvariantCulture, $"      {UnifiedBitmap.TextureLinkTextureTransforms} = {link.Value}");
            }
            else
            {
                scene.Report.AppendLine("      texture_LinkTextureTransforms not writable on this asset.");
            }
        });
    }

    /// <summary>
    /// Makes the drape emit the photograph rather than reflect it: luminance on, and the same bitmap
    /// (copied, real-world placement included) connected to the self-illumination filter slot.
    /// </summary>
    private static void SelfIlluminate(Scene scene, double luminance)
    {
        EditDrape(scene, (editable, bitmap) =>
        {
            if (editable.FindByName(Generic.GenericSelfIllumLuminance) is AssetPropertyDouble level && !level.IsReadOnly)
            {
                level.Value = luminance;
                scene.Report.AppendLine(CultureInfo.InvariantCulture, $"      {Generic.GenericSelfIllumLuminance} = {level.Value}");
            }
            else
            {
                scene.Report.AppendLine("      generic_self_illum_luminance not writable on this asset.");
            }

            if (editable.FindByName(Generic.GenericSelfIllumFilterMap) is not { } filter)
            {
                scene.Report.AppendLine("      generic_self_illum_filter_map absent from this asset.");
                return;
            }

            if (bitmap is null)
            {
                scene.Report.AppendLine("      the diffuse slot carries no bitmap to copy.");
                return;
            }

            if (filter.GetSingleConnectedAsset() is null)
            {
                filter.AddCopyAsConnectedAsset(bitmap);
            }

            Asset? copy = filter.GetSingleConnectedAsset();
            string path = copy?.FindByName(UnifiedBitmap.UnifiedbitmapBitmap) is AssetPropertyString text ? text.Value : "(none)";
            string scale = copy?.FindByName(UnifiedBitmap.TextureRealWorldScaleX) is AssetPropertyDistance width
                ? width.Value.ToString("R", CultureInfo.InvariantCulture)
                : "(none)";
            scene.Report.AppendLine(CultureInfo.InvariantCulture,
                $"      self-illum filter map: bitmap {path}, texture_RealWorldScaleX {scale}");
        });
    }

    private static void EditDrape(Scene scene, Action<Asset, Asset?> edit)
    {
        using AppearanceAssetEditScope scope = new(scene.Document);
        Asset editable = scope.Start(scene.Drape.AppearanceAssetId);
        Asset? bitmap = editable.FindByName(Generic.GenericDiffuse)?.GetSingleConnectedAsset();
        edit(editable, bitmap);
        scope.Commit(true);
    }

    /// <summary>
    /// The measured hypothesis: under smooth shading Revit measures a real-world texture offset from
    /// the element's bounding-box minimum corner rather than from the project origin. So the ground
    /// gets its offsets shifted by its own corner, and every subdivision gets a duplicate of the
    /// drape material shifted by ITS corner, since one material cannot carry two offsets.
    /// </summary>
    private static void BoundingBoxRelativeOffsets(Scene scene)
    {
        Document document = scene.Document;

        // ⛔ Duplicate for the subdivisions BEFORE shifting the ground's own asset: a copy taken
        // afterwards inherits the ground's already-shifted offsets and lands half an image out.
        List<(Toposolid Subdivision, Material Copy)> copies = [];
        int index = 0;
        foreach (ElementId subdivisionId in scene.Terrain.GetSubDivisionIds())
        {
            index++;
            if (document.GetElement(subdivisionId) is not Toposolid subdivision)
            {
                continue;
            }

            if (document.GetElement(scene.Drape.AppearanceAssetId) is not AppearanceAssetElement asset)
            {
                scene.Report.AppendLine("      the drape material has no appearance asset to duplicate.");
                return;
            }

            string name = $"{scene.Drape.Name} probe subdivision {index}";
            Material copy = scene.Drape.Duplicate(name);
            copy.AppearanceAssetId = asset.Duplicate(name).Id;

            Parameter? parameter = subdivision.get_Parameter(BuiltInParameter.TOPOSOLID_SUBDIVIDE_MATERIAL);
            if (parameter is null || parameter.IsReadOnly || !parameter.Set(copy.Id))
            {
                scene.Report.AppendLine(CultureInfo.InvariantCulture,
                    $"      subdivision {subdivisionId.Value}: the Material parameter would not take the copy.");
                continue;
            }

            copies.Add((subdivision, copy));
        }

        ShiftOffsets(scene, scene.Drape, scene.Terrain, "ground");
        foreach ((Toposolid subdivision, Material copy) in copies)
        {
            ShiftOffsets(scene, copy, subdivision, $"subdivision {subdivision.Id.Value}");
        }
    }

    /// <summary>Subtracts the element's bounding-box minimum from the bitmap's real-world offsets.</summary>
    private static void ShiftOffsets(Scene scene, Material material, Element element, string label)
    {
        BoundingBoxXYZ? box = element.get_BoundingBox(null);
        if (box is null)
        {
            scene.Report.AppendLine(CultureInfo.InvariantCulture, $"      {label}: no bounding box, offsets left alone.");
            return;
        }

        double minXm = UnitUtils.ConvertFromInternalUnits(box.Min.X, UnitTypeId.Meters);
        double minYm = UnitUtils.ConvertFromInternalUnits(box.Min.Y, UnitTypeId.Meters);

        using AppearanceAssetEditScope scope = new(scene.Document);
        Asset editable = scope.Start(material.AppearanceAssetId);
        Asset? bitmap = editable.FindByName(Generic.GenericDiffuse)?.GetSingleConnectedAsset();
        if (bitmap is null)
        {
            scope.Cancel();
            scene.Report.AppendLine(CultureInfo.InvariantCulture, $"      {label}: no diffuse bitmap, offsets left alone.");
            return;
        }

        string x = Shift(bitmap, UnifiedBitmap.TextureRealWorldOffsetX, minXm);
        string y = Shift(bitmap, UnifiedBitmap.TextureRealWorldOffsetY, minYm);
        scope.Commit(true);

        scene.Report.AppendLine(CultureInfo.InvariantCulture,
            $"      {label}: bbox min ({minXm:0.0}, {minYm:0.0}) m; {x}; {y}");
    }

    private static string Shift(Asset bitmap, string propertyName, double byMetres)
    {
        if (bitmap.FindByName(propertyName) is not AssetPropertyDistance distance || distance.IsReadOnly)
        {
            return $"{propertyName} not writable";
        }

        ForgeTypeId unit = distance.GetUnitTypeId();
        bool known = unit is not null && UnitUtils.IsUnit(unit);
        double wasMetres = known ? UnitUtils.Convert(distance.Value, unit!, UnitTypeId.Meters) : UnitUtils.ConvertFromInternalUnits(distance.Value, UnitTypeId.Meters);
        double nowMetres = wasMetres - byMetres;
        distance.Value = known ? UnitUtils.Convert(nowMetres, UnitTypeId.Meters, unit!) : UnitUtils.ConvertToInternalUnits(nowMetres, UnitTypeId.Meters);
        double readBack = known ? UnitUtils.Convert(distance.Value, unit!, UnitTypeId.Meters) : UnitUtils.ConvertFromInternalUnits(distance.Value, UnitTypeId.Meters);
        return $"{propertyName} {wasMetres:0.0} -> {readBack:0.0} m";
    }

    /// <summary>
    /// The forum's lead: textures on a toposolid's STRUCTURE layer split by face. Moves the drape's
    /// thin top layer to Finish1 to see whether the smoothed renderer maps it differently.
    /// </summary>
    private static void FinishLayer(Scene scene)
    {
        CompoundStructure? structure = scene.Type.GetCompoundStructure();
        if (structure is not { LayerCount: > 0 })
        {
            scene.Report.AppendLine("      the type has no layers to reassign.");
            return;
        }

        structure.SetLayerFunction(0, MaterialFunctionAssignment.Finish1);
        scene.Type.SetCompoundStructure(structure);
        scene.Report.AppendLine(CultureInfo.InvariantCulture,
            $"      layer 0 function now {scene.Type.GetCompoundStructure()?.GetLayerFunction(0)}");
    }
}
