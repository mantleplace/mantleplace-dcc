// UseWPF switches the SDK to the WindowsDesktop implicit-usings set, which drops System.IO.
using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Visual;
using Autodesk.Revit.UI;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;
using Microsoft.Win32;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// "Probe terrain": measures what a project and a bundle would actually give
/// <c>Toposolid.Create</c>, tries every base-plane strategy — and every way of giving a site
/// subdivision the drape material — against it, and changes nothing.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Every transaction this opens is rolled back in a <c>finally</c>.</b> That is what makes it
/// safe to run in the curator's own project — which is the only place worth running it, because the
/// numbers that matter are that project's level elevations and toposolid types, not a fresh
/// template's.
/// </para>
/// <para>
/// It exists because two things cannot be settled by reading. Revit's API documents
/// <c>Toposolid.Create</c>'s points as building the <em>top face</em> but does not say what the
/// minimum-thickness check compares against, and <c>Toposolid.Create</c> takes no height offset, so
/// whether an offset written after it returns is seen by that check is unknowable until it is run.
/// Both answers change which arm of <see cref="TerrainBasePlanner"/> is the default rather than the
/// fallback. Guessing would mean shipping a plugin that adds a level to every curator's project for
/// no reason, or one that still cannot import a coastal site.
/// </para>
/// <para>
/// Same unattended contract as <see cref="ImportLocalBundleCommand"/>: set
/// <see cref="LocalBundleSource.PathVariable"/> and it skips the picker, writing to
/// <see cref="LocalBundleSource.ProbeLogPathFor"/>.
/// </para>
/// <para>
/// A third question arrived when the drape landed: every site-boundary subdivision refused it, and
/// the only record was a count. That refusal is now settled — see
/// <see cref="ProbeSubDivisionMaterial"/>, which asks what CAN carry the material instead, having
/// deleted the arms whose question is answered.
/// </para>
/// <para>
/// A fourth arrived when the drape worked and the terrain still read as a faceted mosaic. Vertex
/// placement was ruled out by observation — the mosaic survived the move to the TIN — leaving how
/// Revit shades the triangles rather than where they are. <see cref="ProbeSmoothedSurface"/> asks
/// the two things that decide the fix: whether Revit 2025's smooth-shading setting is per document
/// or per toposolid, and whether it disturbs the drape it would be turned on underneath.
/// </para>
/// <para>
/// <b>Deletion trigger:</b> this command is diagnostic scaffolding, not product. Delete this file
/// and its ribbon button once <em>all four</em> questions are settled by recorded probe runs — the
/// two base-plane ones above with <see cref="TerrainBasePlanner"/>'s default arm chosen on that
/// evidence, the subdivision material with its mechanism chosen, and the smooth-shading scope with
/// the importer walking the subdivisions or not on that evidence. A shipped diagnostics command
/// that outlived its question is UI debt, and extending its life without extending this trigger is
/// how it becomes permanent.
/// </para>
/// </remarks>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class TerrainProbeCommand : IExternalCommand
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
        report.AppendLine("Mantle Place terrain probe. Nothing in this project was changed.");
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

            // Layer 0's width, not the total — the number the drape actually splits.
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
            out SurfaceCleanReport cleaned);
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

        // (a) the status quo: whatever the collectors enumerate first, no offset. This is the
        // negative control. It is what failed, and the trial has to show it still fails, or the
        // diagnosis was wrong.
        CandidateLevel? firstLevel = levels.Count > 0 ? levels[0] : null;
        CandidateToposolidType firstType = types.Count > 0 ? types[0] : type2;
        if (firstLevel is { } control)
        {
            Trial(document, "(a) status quo: first type, first level, no offset",
                revitPoints, firstType.Id, control.Id, offset: 0.0, createLevelAt: null, report);
        }

        // (b) the chosen type and level with the height offset written before commit. If this
        // works, the plugin never has to add a level to anyone's project.
        Trial(document, "(b) chosen type + chosen level + height offset",
            revitPoints, type2.Id, chosenPlan.LevelId, chosenPlan.HeightOffset, createLevelAt: null, report);

        // (c) the same base plane, reached by a level created at it. The fallback, and the answer if
        // (b) turns out to be unreachable.
        Trial(document, "(c) chosen type + a level created at the base plane",
            revitPoints, type2.Id, levelId: 0, offset: 0.0, createLevelAt: chosenPlan.BasePlane, report);
    }

    /// <summary>
    /// Builds a toposolid one way, records what Revit said, and rolls it back.
    /// </summary>
    /// <remarks>
    /// The rollback is in a <c>finally</c> and the transaction is never committed on any path, so
    /// there is no arrangement of failures that leaves an element — or a level — behind.
    /// </remarks>
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
            // ⛔ Never committed, on any path. The probe's whole licence to run in a real project is
            // that it cannot change one.
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

    /// <summary>
    /// Dumps every real-world texture property this plugin writes, as Revit stored them.
    /// </summary>
    /// <remarks>
    /// ⛔ Reading these back is the only way to settle the drape's scale. The first successful import
    /// tiled the aerial photograph roughly ten times across the site while the plugin's own log said
    /// it had placed it across 1,425 × 1,419 m — so either the value did not land, or the unit it
    /// landed in is not the unit <see cref="RevitBundleImporter"/> assumed. Both are invisible from
    /// outside Revit, and a screenshot cannot tell them apart. The property's declared
    /// <c>GetUnitTypeId</c> is printed alongside the raw value for exactly that reason.
    /// </remarks>
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

        // The UnifiedBitmap hangs off the diffuse slot as a connected asset, so its scale properties
        // are one level down from anything a flat enumeration would reach.
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

    /// <summary>Both units on every number, because the answer depends on telling them apart.</summary>
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

    /// <summary>
    /// A site-boundary subdivision has no type, so what CAN give it the drape material?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The retype question is settled and is not asked here.</b> On order <c>eb00f56f</c>,
    /// 2026-08-25, all four subdivisions refused the drape with
    /// <c>InvalidOperationException: This Element cannot have type assigned.</c> — because
    /// <c>GetTypeId()</c> is <c>InvalidElementId</c> on every one of them. <c>ChangeTypeId</c> was
    /// never refusing that particular type; it refuses the operation. The same run ruled out the two
    /// rival explanations: retyping the host left every remembered id resolving, so ordering is
    /// irrelevant, and a single-layer type was refused identically, so the two-layer compound
    /// structure is not it either. Those arms are deleted rather than kept — a probe that still asks
    /// an answered question is the scaffolding this file's deletion trigger exists to prevent.
    /// </para>
    /// <para>
    /// What is printed below is the <em>evidence</em> for that finding, which is cheap and worth
    /// re-checking on any machine, followed by the two mechanisms that remain.
    /// </para>
    /// <para>
    /// ⚠ <c>IsValidType</c> is asked rather than inferred from a throw. A refusal Revit expresses as
    /// <c>false</c> leaves no message behind at all, and the diagnosis that took two sessions to
    /// reach was blind to exactly that case: it could only read exception text.
    /// </para>
    /// </remarks>
    private static void ProbeSubDivisionMaterial(Document document, StringBuilder report)
    {
        report.AppendLine("SUBDIVISION MATERIAL — everything below is rolled back");

        if (TerrainToposolid(document) is not { } terrain)
        {
            report.AppendLine("  This project has no toposolid that is not itself a subdivision.");
            report.AppendLine("  Open the project the import built — there is nothing to probe here.");
            report.AppendLine();
            return;
        }

        ElementId terrainTypeId = terrain.GetTypeId();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"  terrain id {terrain.Id.Value} on type id {terrainTypeId.Value} "
            + $"\"{document.GetElement(terrainTypeId)?.Name}\"");

        IList<ElementId> present = terrain.GetSubDivisionIds();
        report.AppendLine(CultureInfo.InvariantCulture,
            $"  GetSubDivisionIds() = {present.Count:N0}: {Ids(present)}");

        // The finding, restated as two numbers per subdivision. A type id of -1 IS the diagnosis.
        foreach (ElementId id in present)
        {
            if (document.GetElement(id) is not Element subdivision)
            {
                continue;
            }

            string stamp = subdivision.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()
                ?? string.Empty;
            report.AppendLine(CultureInfo.InvariantCulture,
                $"      {id.Value}: GetTypeId()={subdivision.GetTypeId().Value}  "
                + $"IsValidType(the terrain's type)={subdivision.IsValidType(terrainTypeId)}  "
                + $"comments={(stamp.Length == 0 ? "(none — a curator's, not this plugin's)" : stamp)}");
        }

        report.AppendLine();

        ImportFailureSwallower swallower = new("Subdivision material probe");
        Transaction transaction = new(document, "Mantle Place drape probe");

        try
        {
            transaction.Start();

            FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(swallower);
            options.SetForcedModalHandling(false);
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);

            List<ElementId> subdivisions = [.. present];
            if (subdivisions.Count == 0)
            {
                if (CreateProbeSubDivision(document, terrain) is not { } made)
                {
                    report.AppendLine("      no subdivisions here, and one could not be created to stand in.");
                    return;
                }

                subdivisions.Add(made);
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"      no subdivisions in this project; created probe subdivision {made.Value} to stand in.");
            }

            ProbeInstanceMaterial(document, subdivisions, report);
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException
                                       or InvalidOperationException
                                       or ArgumentException)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"      PROBE ABORTED: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // ⛔ Never committed, on any path — the same licence the strategy trials run under.
            transaction.RollBack();
            transaction.Dispose();
        }

        foreach (string line in swallower.Lines)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"      {line}");
        }

        if (swallower.SawError)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"      Revit verbatim: {swallower.FirstErrorText}");
        }

        report.AppendLine();
    }

    /// <summary>
    /// The two mechanisms left once <c>ChangeTypeId</c> is settled as impossible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every parameter the subdivision carries is dumped first, and that ordering is the point. The
    /// last diagnosis cost two sessions because the code kept a count where a message belonged;
    /// asserting "there is no other way to give this element a material" from the two calls someone
    /// happened to think of would be the same mistake one level up. The dump is what makes the claim
    /// checkable rather than merely confident.
    /// </para>
    /// <para>
    /// <c>TOPOSOLID_SUBDIVIDE_MATERIAL</c> is asked first because it is the shape an element with no
    /// type would have to use, and because it is a plain instance write — no geometry extraction, no
    /// face iteration, and nothing stored per-face that a toposolid regeneration is free to drop.
    /// <c>Paint</c> is the escalation the drape's own design already names, tried in the same
    /// breath so the two are compared on one run rather than two.
    /// </para>
    /// </remarks>
    private static void ProbeInstanceMaterial(
        Document document, IReadOnlyList<ElementId> subdivisions, StringBuilder report)
    {
        ElementId materialId = ProbeMaterialId(document);
        if (materialId == ElementId.InvalidElementId)
        {
            report.AppendLine("      no material to try, so neither mechanism can be asked.");
            return;
        }

        report.AppendLine(CultureInfo.InvariantCulture,
            $"      material under test: id {materialId.Value} "
            + $"\"{document.GetElement(materialId)?.Name}\"");

        if (document.GetElement(subdivisions[0]) is not Element first)
        {
            report.AppendLine("      the first subdivision does not resolve.");
            return;
        }

        report.AppendLine(CultureInfo.InvariantCulture,
            $"      EVERY parameter on subdivision {first.Id.Value} — the whole set, so "
            + $"\"no other mechanism exists\" is a claim about all of them:");

        foreach (Parameter parameter in first.Parameters)
        {
            string builtIn = parameter.Definition is InternalDefinition internalDefinition
                ? internalDefinition.BuiltInParameter.ToString()
                : "(shared or project)";

            string value = parameter.StorageType switch
            {
                StorageType.ElementId => $"ElementId {parameter.AsElementId().Value} "
                    + $"\"{document.GetElement(parameter.AsElementId())?.Name ?? string.Empty}\"",
                StorageType.String => $"\"{parameter.AsString() ?? string.Empty}\"",
                StorageType.None => "(none)",
                _ => parameter.AsValueString() ?? "(unset)",
            };

            report.AppendLine(CultureInfo.InvariantCulture,
                $"          {parameter.Definition?.Name}  [{builtIn}]  {parameter.StorageType}  "
                + $"readOnly={parameter.IsReadOnly}  = {value}");
        }

        report.AppendLine();
        report.AppendLine("      (1) TOPOSOLID_SUBDIVIDE_MATERIAL, set and read back");

        foreach (ElementId id in subdivisions)
        {
            if (document.GetElement(id) is not Element subdivision)
            {
                report.AppendLine(CultureInfo.InvariantCulture, $"          {id.Value}: does not resolve.");
                continue;
            }

            Parameter? material = subdivision.get_Parameter(BuiltInParameter.TOPOSOLID_SUBDIVIDE_MATERIAL);
            if (material is null)
            {
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"          {id.Value}: the parameter is not present on this element.");
                continue;
            }

            if (material.IsReadOnly)
            {
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"          {id.Value}: present but READ-ONLY (currently {material.AsElementId().Value}).");
                continue;
            }

            try
            {
                bool set = material.Set(materialId);

                // ⛔ Read back, always. A Set that returns true and stored something else is exactly
                // how the drape's texture distances went in as feet and tiled the photograph twelve
                // times across the site before anyone could see it.
                ElementId stored = subdivision.get_Parameter(BuiltInParameter.TOPOSOLID_SUBDIVIDE_MATERIAL)
                    ?.AsElementId() ?? ElementId.InvalidElementId;

                report.AppendLine(CultureInfo.InvariantCulture,
                    $"          {id.Value}: Set returned {set}; reads back {stored.Value} "
                    + $"({(stored == materialId ? "MATCHES — this is the mechanism" : "does NOT match")})");
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException
                                           or ArgumentException
                                           or InvalidOperationException)
            {
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"          {id.Value}: THREW {ex.GetType().Name}: {ex.Message}");
            }
        }

        report.AppendLine();
        report.AppendLine("      (2) Document.Paint on the top face — the documented escalation");
        PaintTopFace(document, first, materialId, report);
    }

    /// <summary>
    /// Paints the upward face of one subdivision, so the escalation is measured rather than assumed.
    /// </summary>
    /// <remarks>
    /// The face is chosen by the largest upward normal rather than by index, because face order is
    /// not a documented property of anything. If this is the mechanism that works, that search is the
    /// cost the drape's design already priced against it: <c>get_Geometry</c> plus face iteration,
    /// and a result stored per-face that a toposolid regeneration is free to drop.
    /// </remarks>
    private static void PaintTopFace(
        Document document, Element subdivision, ElementId materialId, StringBuilder report)
    {
        try
        {
            Options options = new() { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
            GeometryElement? geometry = subdivision.get_Geometry(options);
            if (geometry is null)
            {
                report.AppendLine("          no geometry — Paint has nothing to act on.");
                return;
            }

            Face? top = null;
            double bestArea = 0.0;
            int faceCount = 0;

            foreach (GeometryObject item in geometry)
            {
                if (item is not Solid { Faces.Size: > 0 } solid)
                {
                    continue;
                }

                foreach (Face face in solid.Faces)
                {
                    faceCount++;
                    BoundingBoxUV bounds = face.GetBoundingBox();
                    XYZ normal = face.ComputeNormal((bounds.Min + bounds.Max) * 0.5);
                    if (normal.Z > 0.5 && face.Area > bestArea)
                    {
                        bestArea = face.Area;
                        top = face;
                    }
                }
            }

            if (top is null)
            {
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"          {faceCount:N0} face(s), none of them upward-facing — no top face to paint.");
                return;
            }

            document.Paint(subdivision.Id, top, materialId);
            ElementId painted = document.GetPaintedMaterial(subdivision.Id, top);

            report.AppendLine(CultureInfo.InvariantCulture,
                $"          painted the largest of {faceCount:N0} face(s) (area {bestArea:0.##} ft²); "
                + $"IsPainted={document.IsPainted(subdivision.Id, top)}, "
                + $"GetPaintedMaterial={painted.Value} "
                + $"({(painted == materialId ? "MATCHES" : "does NOT match")})");
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException
                                       or ArgumentException
                                       or InvalidOperationException)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"          Paint THREW {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Is Revit's toposolid smooth shading off in this project, does it turn on, and does the drape
    /// survive it?
    /// </summary>
    /// <remarks>
    /// <para>
    /// The terrain renders as a faceted mosaic over a 0.30 m/px photograph. Building it from the
    /// surface DXF's TIN was expected to fix that and did not, which rules out vertex placement by
    /// observation rather than by argument: <c>Toposolid.Create</c> takes points and re-triangulates,
    /// so the mesh is triangles either way, and what is left is how Revit <em>shades</em> them.
    /// <c>Toposolid.SetSmoothedSurface</c> arrived in Revit 2025 named for exactly that distinction.
    /// </para>
    /// <para>
    /// <b>The scope question is already settled and is not asked here.</b> Both members are
    /// <b>static</b> and take only a <c>Document</c> — there is no element argument, so there is no
    /// per-toposolid setting for the subdivisions to need. That was answered by the compiler
    /// (<c>CS0176</c>) and by reflection over Revit 2025's <c>RevitAPI.dll</c>, at no cost at all,
    /// and an arm that still asked it would be the scaffolding this file's deletion trigger exists
    /// to prevent.
    /// </para>
    /// <para>
    /// What is left is what a signature cannot answer, and both halves are seconds here against
    /// 8–17 minutes per hypothesis anywhere else:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <b>Is it off, and does it turn on?</b> The issue's claim is that whatever Revit's default is,
    /// it is a default nobody chose. That is a claim about this document and it has never been read.
    /// Setting it and reading it back also proves the pair works in this Revit build before a
    /// ten-minute import bets on it.
    /// </item>
    /// <item>
    /// <b>Does it disturb the drape?</b> Autodesk's own note says surface patterns stop drawing and
    /// paint and graphic overrides are ignored while smoothing is on. The drape rides
    /// <c>TOPOSOLID_SUBDIVIDE_MATERIAL</c> — an instance material, not paint — so it should survive,
    /// and "should" is what this reads back instead of asserting. It settles one more thing in
    /// passing: if smoothing ignores paint, then <c>Document.Paint</c> is no longer the escalation
    /// <see cref="PaintTopFace"/> measured it to be, and that has to be known before anything ever
    /// falls back to it.
    /// </item>
    /// </list>
    /// <para>
    /// ⛔ The write is inside a transaction rolled back in a <c>finally</c>, so the curator's project
    /// keeps its own setting — which matters more here than anywhere else in this file, because this
    /// is the one setting that would change how every toposolid in their model looks.
    /// </para>
    /// </remarks>
    private static void ProbeSmoothedSurface(Document document, StringBuilder report)
    {
        report.AppendLine("TOPOSOLID SMOOTH SHADING — everything below is rolled back");
        report.AppendLine("  (scope is not in question: both API members are static and take only a Document,");
        report.AppendLine("   so the setting is document-wide and no subdivision needs it in its own right.)");

        bool before;
        try
        {
            before = Toposolid.IsSmoothedSurfaceEnabled(document);
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException
                                       or ArgumentException
                                       or InvalidOperationException)
        {
            // Would mean this Revit does not carry the 2025 pair, which answers the whole question
            // in one line rather than throwing away the rest of the probe.
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  This Revit would not report the setting — {ex.GetType().Name}: {ex.Message}");
            report.AppendLine();
            return;
        }

        // The whole point of the line: "off" is only interesting if nobody chose it, and this probe
        // is the first thing that has ever read it.
        string note = before
            ? "   <- something has already turned it on in this project"
            : "   <- the default, and a default nobody chose";
        report.AppendLine(CultureInfo.InvariantCulture,
            $"  BEFORE: IsSmoothedSurfaceEnabled = {before}{note}");

        Toposolid? terrain = TerrainToposolid(document);
        IList<ElementId> subdivisions = terrain?.GetSubDivisionIds() ?? [];
        report.AppendLine(CultureInfo.InvariantCulture,
            $"  terrain: {(terrain is null ? "none in this project" : $"id {terrain.Id.Value}")}; "
            + $"subdivisions: {subdivisions.Count:N0}");

        ImportFailureSwallower swallower = new("Smooth shading probe");
        Transaction transaction = new(document, "Mantle Place smooth shading probe");

        try
        {
            transaction.Start();

            FailureHandlingOptions options = transaction.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(swallower);
            options.SetForcedModalHandling(false);
            options.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(options);

            Toposolid.SetSmoothedSurface(document, true);

            // ⛔ Read back inside the transaction. A Set that returns without complaint and stores
            // nothing is the drape's texture-distance defect, and it is invisible without this line.
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  AFTER SetSmoothedSurface(document, true): reads {Toposolid.IsSmoothedSurfaceEnabled(document)}");

            // The drape, re-read under smoothing. Expected to hold, because the drape reaches a
            // subdivision through an instance material rather than through paint — and Autodesk's
            // note that paint is ignored under smoothing is exactly why "expected" is not measured.
            report.AppendLine("  DRAPE UNDER SMOOTHING — TOPOSOLID_SUBDIVIDE_MATERIAL re-read:");
            if (subdivisions.Count == 0)
            {
                report.AppendLine("      no subdivisions on this terrain to re-read.");
            }

            foreach (ElementId id in subdivisions)
            {
                if (document.GetElement(id) is not Element subdivision)
                {
                    report.AppendLine(CultureInfo.InvariantCulture, $"      {id.Value}: does not resolve.");
                    continue;
                }

                ElementId stored = subdivision.get_Parameter(BuiltInParameter.TOPOSOLID_SUBDIVIDE_MATERIAL)
                    ?.AsElementId() ?? ElementId.InvalidElementId;

                report.AppendLine(CultureInfo.InvariantCulture,
                    $"      {id.Value}: material {stored.Value} \"{document.GetElement(stored)?.Name ?? string.Empty}\"");
            }

            if (terrain is not null)
            {
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"  terrain type under smoothing: \"{document.GetElement(terrain.GetTypeId())?.Name}\"");
            }
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException
                                       or ArgumentException
                                       or InvalidOperationException)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  THREW {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // ⛔ Never committed, on any path — the ribbon setting included, which is the one thing
            // in this file a curator would notice across their whole project.
            transaction.RollBack();
            transaction.Dispose();
        }

        report.AppendLine(CultureInfo.InvariantCulture,
            $"  ROLLED BACK: reads {SmoothedQuietly(document)} again (expected {before}).");

        foreach (string line in swallower.Lines)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"      {line}");
        }

        if (swallower.SawError)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"      Revit verbatim: {swallower.FirstErrorText}");
        }

        report.AppendLine();
    }

    /// <summary>
    /// The smoothing setting, with an unreadable one reported as text rather than thrown.
    /// </summary>
    /// <remarks>
    /// Used for the post-rollback line, where the probe is proving it left nothing behind. A throw
    /// there would replace that proof with a stack trace, which is the one outcome that would make
    /// this command unsafe to run in a curator's project.
    /// </remarks>
    private static string SmoothedQuietly(Document document)
    {
        try
        {
            return Toposolid.IsSmoothedSurfaceEnabled(document).ToString();
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException
                                       or ArgumentException
                                       or InvalidOperationException)
        {
            return $"unreadable ({ex.GetType().Name})";
        }
    }

    /// <summary>
    /// A material to hang on the probe's subdivisions — the import's own if this project has one, so
    /// the mechanisms are asked about the real thing wherever that is possible.
    /// </summary>
    private static ElementId ProbeMaterialId(Document document)
    {
        Material? existing = new FilteredElementCollector(document)
            .OfClass(typeof(Material))
            .Cast<Material>()
            .FirstOrDefault(material =>
                material.Name.StartsWith("Mantle Place Site Imagery", StringComparison.Ordinal));

        if (existing is not null)
        {
            return existing.Id;
        }

        try
        {
            return Material.Create(document, "Mantle Place drape probe material");
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException)
        {
            return ElementId.InvalidElementId;
        }
    }

    /// <summary>
    /// A stand-in subdivision, for a project that carries a terrain but none of the land-use rings.
    /// </summary>
    /// <remarks>
    /// A square an eighth of the terrain's width, at its centre, so it lands well inside the host and
    /// what is measured is the material write rather than a projection that missed.
    /// </remarks>
    private static ElementId? CreateProbeSubDivision(Document document, Toposolid terrain)
    {
        BoundingBoxXYZ? box = terrain.get_BoundingBox(null);
        if (box is null)
        {
            return null;
        }

        XYZ centre = (box.Min + box.Max) * 0.5;
        double half = Math.Min(box.Max.X - box.Min.X, box.Max.Y - box.Min.Y) / 8.0;
        if (half <= document.Application.ShortCurveTolerance)
        {
            return null;
        }

        XYZ[] corners =
        [
            new(centre.X - half, centre.Y - half, 0.0),
            new(centre.X + half, centre.Y - half, 0.0),
            new(centre.X + half, centre.Y + half, 0.0),
            new(centre.X - half, centre.Y + half, 0.0),
        ];

        List<Curve> edges = [];
        for (int index = 0; index < corners.Length; index++)
        {
            edges.Add(Line.CreateBound(corners[index], corners[(index + 1) % corners.Length]));
        }

        try
        {
            return terrain.CreateSubDivision(document, [CurveLoop.Create(edges)]).Id;
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The first toposolid that is a TERRAIN, by the same exclusion the importer uses: a subdivision
    /// is itself a <see cref="Toposolid"/>, so a bare first-of-class collector hands back a patch.
    /// </summary>
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
            // Best effort, exactly as the import log is: an unwritable path must not turn a probe
            // that DID measure everything into a failure.
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
