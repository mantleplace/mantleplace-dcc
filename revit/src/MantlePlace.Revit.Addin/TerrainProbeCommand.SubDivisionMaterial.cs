using System.Globalization;
using System.Text;
using Autodesk.Revit.DB;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

public sealed partial class TerrainProbeCommand
{
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

    private static void PaintTopFace(
        Document document, Element subdivision, ElementId materialId, StringBuilder report)
    {
        try
        {
            Face? top = FindTopFace(
                subdivision, computeReferences: true, out int faceCount, out double bestArea, out string? reason);

            if (top is null)
            {
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"          {faceCount:N0} face(s) — no top face to paint: {reason}");
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

    private static ElementId ProbeMaterialId(Document document)
    {
        Material? existing = new FilteredElementCollector(document)
            .OfClass(typeof(Material))
            .Cast<Material>()
            .FirstOrDefault(material =>
                material.Name.StartsWith(DrapeLayering.ImageryNamePrefix, StringComparison.Ordinal));

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
}
