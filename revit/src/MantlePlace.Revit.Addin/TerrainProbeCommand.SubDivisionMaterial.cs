using System.Globalization;
using System.Text;
using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// The subdivision-material question: what can carry the drape material, tried on a scratch
// subdivision and rolled back.
public sealed partial class TerrainProbeCommand
{
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
    /// an answered question is the scaffolding this command's deletion trigger exists to prevent.
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
}
