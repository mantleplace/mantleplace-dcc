using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using Autodesk.Revit.UI;
using MantlePlace.Client;
using MantlePlace.Core;
using Microsoft.Win32;

namespace MantlePlace.Revit.Addin;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed partial class TerrainProbeCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        ArgumentNullException.ThrowIfNull(commandData);

        Document document = commandData.Application.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("no active document");

        string? unattended = LocalBundleSource.Unattended(
            Environment.GetEnvironmentVariable(LocalBundleSource.PathVariable));

        string zipPath;
        if (unattended is not null)
        {
            zipPath = unattended;
        }
        else
        {
            OpenFileDialog picker = new()
            {
                Title = "Choose a Mantle Place bundle to probe",
                Filter = "Mantle Place bundle (*.zip)|*.zip",
                CheckFileExists = true,
            };

            if (picker.ShowDialog() != true)
            {
                return Result.Cancelled;
            }

            zipPath = picker.FileName;
        }

        if (!File.Exists(zipPath))
        {
            message = $"No bundle zip at \"{zipPath}\".";
            return Result.Failed;
        }

        StringBuilder report = new();
        try
        {
            Probe(document, zipPath, report);

            if (Environment.GetEnvironmentVariable(DrapeRenderProbe.DirectoryVariable) is { Length: > 0 } renderDirectory)
            {
                DrapeRenderProbe.Run(document, renderDirectory, report);
            }
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException
                                       or InvalidOperationException
                                       or IOException)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"PROBE ABORTED: {ex.GetType().Name}: {ex.Message}");
        }

        Publish(zipPath, unattended is not null, report.ToString());
        return Result.Succeeded;
    }

    private static void Probe(Document document, string zipPath, StringBuilder report)
    {
        report.AppendLine(CultureInfo.InvariantCulture,
            $"Mantle Place {PluginVersion.Current} terrain probe. Nothing in this project was changed.");
        report.AppendLine(CultureInfo.InvariantCulture, $"Document: {document.Title}");
        report.AppendLine();

        double minimumLayer = CompoundStructure.GetMinimumLayerThickness();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"CompoundStructure.GetMinimumLayerThickness() = {Ft(minimumLayer)}");
        report.AppendLine();

        report.AppendLine("LEVELS");
        List<CandidateLevel> levels = [];
        foreach (Level level in new FilteredElementCollector(document).OfClass(typeof(Level)).Cast<Level>())
        {
            levels.Add(new CandidateLevel(level.Id.Value, level.Name, level.ProjectElevation));
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  id {level.Id.Value}  \"{level.Name}\"  Elevation={Ft(level.Elevation)}  "
                + $"ProjectElevation={Ft(level.ProjectElevation)}");
        }

        report.AppendLine(CultureInfo.InvariantCulture,
            $"  first-of-collector (what the plugin used to take): {FirstOf<Level>(document)}");
        report.AppendLine();

        report.AppendLine("TOPOSOLID TYPES");
        List<CandidateToposolidType> types = [];
        foreach (ToposolidType type in new FilteredElementCollector(document)
            .OfClass(typeof(ToposolidType))
            .Cast<ToposolidType>())
        {
            CompoundStructure? structure = type.GetCompoundStructure();
            double thickness = structure is { LayerCount: > 0 }
                ? structure.GetWidth()
                : type.get_Parameter(BuiltInParameter.TOPOSOLID_TYPE_DEFAULT_THICKNESS_PARAM)?.AsDouble() ?? 0.0;

            double topLayer = structure is { LayerCount: > 0 } ? structure.GetLayerWidth(0) : thickness;

            bool structural = RevitBundleImporter.HasStructuralLayer(structure);
            types.Add(new CandidateToposolidType(
                type.Id.Value, type.Name, thickness, topLayer, structure?.LayerCount ?? 0, structural));

            report.AppendLine(CultureInfo.InvariantCulture,
                $"  id {type.Id.Value}  \"{type.Name}\"  layers={structure?.LayerCount ?? 0}  "
                + $"total={Ft(thickness)}  topLayer={Ft(topLayer)}  "
                + $"defaultThickness={Ft(type.get_Parameter(BuiltInParameter.TOPOSOLID_TYPE_DEFAULT_THICKNESS_PARAM)?.AsDouble() ?? 0.0)}  "
                + $"facesLocation={type.get_Parameter(BuiltInParameter.TOPOSOLID_FACES_LOCATION)?.AsInteger().ToString(CultureInfo.InvariantCulture) ?? "n/a"}  "
                + $"structural={structural}  "
                + $"drapeSplittable={DrapeLayering.Split(topLayer, minimumLayer).Ok}");

            if (structure is { LayerCount: > 0 })
            {
                for (int layer = 0; layer < structure.LayerCount; layer++)
                {
                    report.AppendLine(CultureInfo.InvariantCulture,
                        $"      layer {layer}: width={Ft(structure.GetLayerWidth(layer))} "
                        + $"function={structure.GetLayerFunction(layer)}");
                }
            }

            DescribeContours(type, report);
        }

        report.AppendLine(CultureInfo.InvariantCulture,
            $"  first-of-collector (what the plugin used to take): {FirstOf<ToposolidType>(document)}");
        report.AppendLine();

        DescribeDrapeMaterials(document, report);

        ProbeSubDivisionMaterial(document, report);

        ProbeSmoothedSurface(document, report);

        ProbeSubDivisionAgreement(document, report);

        using LocalBundleArchive archive = LocalBundleArchive.Open(zipPath);
        if (archive.Manifest is not { } manifest)
        {
            report.AppendLine("That zip has no Metadata/manifest.json, so there are no points to probe.");
            return;
        }

        BundleImportPlan plan = BundleImportPlanner.Plan(manifest, archive.EntryNames, archive.ProbeImageSize);
        ImportStep? pointsStep = plan.Steps.FirstOrDefault(
            step => step.Kind == ImportStepKind.ToposurfaceFromPointsFile);

        if (pointsStep is null)
        {
            report.AppendLine("This bundle plans no toposurface-from-points step, so there is nothing to build.");
            return;
        }

        string csvPath = archive.Extract(
            pointsStep.EntryName,
            ImportStepKinds.LifetimeOf(pointsStep.Kind),
            pointsStep.ExpectedSha256);

        if (SurfacePointsReader.TryParse(File.ReadAllText(csvPath), out IReadOnlyList<SurfacePoint> raw) is { } parseError)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"Points file unreadable: {parseError}");
            return;
        }

        ReportPoints(raw, pointsStep, report);

        IReadOnlyList<SurfacePoint> points = SurfacePointsSanitiser.Clean(
            raw,
            pointsStep.Crop,
            out SurfaceCleanReport cleaned,
            pointsStep.CropUnavailable);
        report.AppendLine(CultureInfo.InvariantCulture,
            $"  after cleaning: {cleaned.Kept:N0} kept, {cleaned.DroppedOutsideAoi:N0} outside the AOI, "
            + $"{cleaned.DroppedFilledEdge:N0} on a filled edge line");
        report.AppendLine(CultureInfo.InvariantCulture, $"  {cleaned.Explanation}");
        report.AppendLine();

        double metresPerUnit = LinearUnits.MetresPerUnit(pointsStep.Units);
        List<XYZ> revitPoints = [.. points.Select(point => new XYZ(
            ToFeet(point.X, metresPerUnit),
            ToFeet(point.Y, metresPerUnit),
            ToFeet(point.Z, metresPerUnit)))];

        TerrainRelief relief = new(
            revitPoints.Min(point => point.Z),
            revitPoints.Max(point => point.Z),
            revitPoints.Count);

        CandidateToposolidType? chosen = ToposolidTypeChoice.Best(types, minimumLayer);
        TerrainBasePlan chosenPlan = TerrainBasePlanner.Decide(
            levels,
            relief,
            chosen?.TotalThickness ?? 0.0,
            minimumLayer);

        report.AppendLine("WHAT THE NEW CODE WOULD CHOOSE");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"  type: {(chosen is { } picked ? $"id {picked.Id} \"{picked.Name}\" total={Ft(picked.TotalThickness)}" : "none")}");
        report.AppendLine(CultureInfo.InvariantCulture, $"  strategy: {chosenPlan.Strategy}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"  level: id {chosenPlan.LevelId} \"{chosenPlan.LevelName}\" at {Ft(chosenPlan.LevelElevation)}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"  height offset: {Ft(chosenPlan.HeightOffset)}   clearance: {Ft(chosenPlan.RequiredClearance)}   "
            + $"base plane: {Ft(chosenPlan.BasePlane)}");
        report.AppendLine(CultureInfo.InvariantCulture, $"  {chosenPlan.Explanation}");
        report.AppendLine();

        if (chosen is not { } type2)
        {
            report.AppendLine("No usable toposolid type, so no strategy can be tried.");
            return;
        }

        report.AppendLine("STRATEGY TRIALS — every one of these is rolled back");

        CandidateLevel? firstLevel = levels.Count > 0 ? levels[0] : null;
        CandidateToposolidType firstType = types.Count > 0 ? types[0] : type2;
        if (firstLevel is { } control)
        {
            Trial(document, "(a) status quo: first type, first level, no offset",
                revitPoints, firstType.Id, control.Id, offset: 0.0, createLevelAt: null, report);
        }

        Trial(document, "(b) chosen type + chosen level + height offset",
            revitPoints, type2.Id, chosenPlan.LevelId, chosenPlan.HeightOffset, createLevelAt: null, report);

        Trial(document, "(c) chosen type + a level created at the base plane",
            revitPoints, type2.Id, levelId: 0, offset: 0.0, createLevelAt: chosenPlan.BasePlane, report);
    }

    private static void Trial(
        Document document,
        string label,
        IList<XYZ> points,
        long typeId,
        long levelId,
        double offset,
        double? createLevelAt,
        StringBuilder report)
    {
        ImportFailureSwallower swallower = new(label);
        Transaction transaction = new(document, "Mantle Place terrain probe");

        try
        {
            transaction.Start();

            FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(swallower);
            options.SetForcedModalHandling(false);
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);

            ElementId level = createLevelAt is { } elevation
                ? Level.Create(document, elevation).Id
                : new ElementId(levelId);

            Toposolid built = Toposolid.Create(document, points, new ElementId(typeId), level);

            if (offset != 0.0)
            {
                built.get_Parameter(BuiltInParameter.TOPOSOLID_HEIGHTABOVELEVEL_PARAM)?.Set(offset);
                document.Regenerate();
            }
            else
            {
                document.Regenerate();
            }

            report.AppendLine(CultureInfo.InvariantCulture,
                $"  {label}: Create returned id {built.Id.Value}; "
                + $"bottom={Ft(built.get_Parameter(BuiltInParameter.TOPOSOLID_ELEVATION_AT_BOTTOM)?.AsDouble() ?? double.NaN)} "
                + $"top={Ft(built.get_Parameter(BuiltInParameter.TOPOSOLID_ELEVATION_AT_TOP)?.AsDouble() ?? double.NaN)}");
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException
                                       or InvalidOperationException
                                       or ArgumentException)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  {label}: THREW {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            transaction.RollBack();
            transaction.Dispose();
        }

        foreach (string line in swallower.Lines)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"      {line}");
        }

        if (swallower.SawError)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"      Revit verbatim: {swallower.FirstErrorText}");
            report.AppendLine(CultureInfo.InvariantCulture,
                $"      classified as too-thin: {swallower.SawTooThin}");
        }
    }

    private static void ReportPoints(IReadOnlyList<SurfacePoint> raw, ImportStep step, StringBuilder report)
    {
        double minZ = raw.Min(point => point.Z);
        double maxZ = raw.Max(point => point.Z);
        int belowZero = raw.Count(point => point.Z < 0.0);

        report.AppendLine("POINTS");
        report.AppendLine(CultureInfo.InvariantCulture, $"  entry: {step.EntryName}   units: {step.Units}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"  count={raw.Count:N0}  minZ={minZ:0.###}  maxZ={maxZ:0.###}  below zero={belowZero:N0}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"  in internal feet: minZ={ToFeetMetric(minZ):0.###}  maxZ={ToFeetMetric(maxZ):0.###}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"  grid: {(SurfaceGrid.Detect(raw) is { } shape ? $"{shape.ColumnCount} x {shape.RowCount} at {shape.Spacing:0.###}" : "not a regular grid")}");
        report.AppendLine(CultureInfo.InvariantCulture,
            $"  crop window: {(step.Crop is { } crop ? $"west={crop.WestM:0.##} south={crop.SouthM:0.##} east={crop.EastM:0.##} north={crop.NorthM:0.##}" : "none published")}");
    }

    private static void DescribeDrapeMaterials(Document document, StringBuilder report)
    {
        report.AppendLine("DRAPE MATERIALS (appearance assets carrying a UnifiedBitmap)");

        foreach (Material material in new FilteredElementCollector(document)
            .OfClass(typeof(Material))
            .Cast<Material>()
            .Where(material => material.Name.StartsWith("Mantle Place", StringComparison.Ordinal)))
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  material id {material.Id.Value} \"{material.Name}\"");

            if (document.GetElement(material.AppearanceAssetId) is not AppearanceAssetElement element)
            {
                report.AppendLine("      no appearance asset");
                continue;
            }

            Asset asset = element.GetRenderingAsset();
            for (int i = 0; i < asset.Size; i++)
            {
                DescribeAssetProperty(asset[i], "      ", report);
            }
        }

        report.AppendLine();
    }

    private static void DescribeAssetProperty(AssetProperty property, string indent, StringBuilder report)
    {
        string value = property switch
        {
            AssetPropertyDistance distance => string.Create(
                CultureInfo.InvariantCulture,
                $"{distance.Value} (unit {distance.GetUnitTypeId()?.TypeId ?? "none"})"),
            AssetPropertyDouble number => number.Value.ToString("0.######", CultureInfo.InvariantCulture),
            AssetPropertyString text => text.Value,
            AssetPropertyBoolean flag => flag.Value.ToString(),
            AssetPropertyInteger integer => integer.Value.ToString(CultureInfo.InvariantCulture),
            _ => property.Type.ToString(),
        };

        report.AppendLine(CultureInfo.InvariantCulture, $"{indent}{property.Name} = {value}");

        for (int i = 0; i < property.NumberOfConnectedProperties; i++)
        {
            if (property.GetConnectedProperty(i) is Asset connected)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"{indent}  -> connected asset:");
                for (int j = 0; j < connected.Size; j++)
                {
                    DescribeAssetProperty(connected[j], indent + "    ", report);
                }
            }
        }
    }

    private static void DescribeContours(ToposolidType type, StringBuilder report)
    {
        try
        {
            ContourSetting setting = type.GetContourSetting();
            IList<ContourSettingItem> items = setting.GetContourSettingItems();
            report.AppendLine(CultureInfo.InvariantCulture, $"      contour items: {items.Count}");
            foreach (ContourSettingItem item in items)
            {
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"        {item.Type} start={Ft(item.Start)} stop={Ft(item.Stop)} "
                    + $"step={Ft(item.Step)} enabled={setting.IsItemEnabled(item)}");
            }
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException
                                       or InvalidOperationException)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"      contour items: unreadable ({ex.GetType().Name})");
        }
    }

    private static string FirstOf<T>(Document document)
        where T : Element
    {
        using FilteredElementCollector collector = new(document);
        ElementId id = collector.OfClass(typeof(T)).FirstElementId();
        return id == ElementId.InvalidElementId
            ? "none"
            : $"id {id.Value} \"{document.GetElement(id)?.Name}\"";
    }

    private static string Ft(double internalFeet)
        => double.IsNaN(internalFeet)
            ? "n/a"
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{internalFeet:0.####} ft ({UnitUtils.ConvertFromInternalUnits(internalFeet, UnitTypeId.Meters):0.####} m)");

    private static double ToFeet(double value, double metresPerUnit)
        => UnitUtils.ConvertToInternalUnits(value * metresPerUnit, UnitTypeId.Meters);

    private static double ToFeetMetric(double metres)
        => UnitUtils.ConvertToInternalUnits(metres, UnitTypeId.Meters);

    private static Toposolid? TerrainToposolid(Document document)
    {
        List<Toposolid> toposolids = [.. new FilteredElementCollector(document)
            .OfClass(typeof(Toposolid))
            .Cast<Toposolid>()];

        HashSet<ElementId> subdivisionIds = [];
        foreach (Toposolid toposolid in toposolids)
        {
            foreach (ElementId id in toposolid.GetSubDivisionIds())
            {
                subdivisionIds.Add(id);
            }
        }

        return toposolids.FirstOrDefault(toposolid => !subdivisionIds.Contains(toposolid.Id));
    }

    private static string Ids(IEnumerable<ElementId> ids)
    {
        string joined = string.Join(", ", ids.Select(id => id.Value.ToString(CultureInfo.InvariantCulture)));
        return joined.Length == 0 ? "(none)" : joined;
    }

    private static void Publish(string zipPath, bool unattended, string body)
    {
        string path = LocalBundleSource.ProbeLogPathFor(zipPath);
        try
        {
            File.WriteAllText(path, body);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        if (unattended)
        {
            return;
        }

        TaskDialog dialog = new("Mantle Place")
        {
            MainInstruction = "Terrain probe finished. Nothing was changed.",
            MainContent = $"Measurements written to:{Environment.NewLine}{path}",
        };
        dialog.Show();
    }
}
