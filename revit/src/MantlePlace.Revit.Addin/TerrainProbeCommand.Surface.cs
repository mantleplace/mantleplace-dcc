using System.Globalization;
using System.Text;
using Autodesk.Revit.DB;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

public sealed partial class TerrainProbeCommand
{
    private static void ProbeSmoothedSurface(Document document, StringBuilder report)
    {
        report.AppendLine("TOPOSOLID SMOOTH SHADING — read only, nothing is written");
        report.AppendLine("  (document-wide: both API members are static and take only a Document.)");
        report.AppendLine("  (it moves the origin of a real-world texture offset: flat shading measures");
        report.AppendLine("   from the project origin per face, smooth shading from the element's");
        report.AppendLine("   bounding-box corner. The plugin turns it on and anchors the drape for it.)");

        try
        {
            bool enabled = Toposolid.IsSmoothedSurfaceEnabled(document);
            string reading = enabled
                ? "ON  <- a drape anchored to the element's corner renders correctly; one anchored to the origin shows four quarters at a cross"
                : "OFF <- flat shading maps per face, so a drape anchored to the element's corner (what this plugin writes) shatters into unrelated ground; one anchored to the origin is positioned correctly at the large scale and still reads as a mosaic";
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  IsSmoothedSurfaceEnabled = {reading}");
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException
                                       or ArgumentException
                                       or InvalidOperationException)
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  This Revit would not report the setting — {ex.GetType().Name}: {ex.Message}");
        }

        ReportToposolidInventory(document, report);
        report.AppendLine();
    }

    private static void ReportToposolidInventory(Document document, StringBuilder report)
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

        int ground = all.Count(toposolid => !subdivisionIds.Contains(toposolid.Id));
        report.AppendLine(CultureInfo.InvariantCulture,
            $"  TOPOSOLIDS: {all.Count:N0} total — {ground:N0} ground, {subdivisionIds.Count:N0} subdivision(s)");

        if (ground > 1)
        {
            report.AppendLine("      ⚠ more than one ground toposolid — check the footprints below "
                + "for a duplicate terrain left by an earlier import.");
        }

        foreach (Toposolid toposolid in all)
        {
            BoundingBoxXYZ? box = toposolid.get_BoundingBox(null);
            string footprint = box is null
                ? "no bounding box"
                : new FootprintExtent(
                    UnitUtils.ConvertFromInternalUnits(box.Min.X, UnitTypeId.Meters),
                    UnitUtils.ConvertFromInternalUnits(box.Min.Y, UnitTypeId.Meters),
                    UnitUtils.ConvertFromInternalUnits(box.Max.X, UnitTypeId.Meters),
                    UnitUtils.ConvertFromInternalUnits(box.Max.Y, UnitTypeId.Meters)).Describe();

            bool isSubdivision = subdivisionIds.Contains(toposolid.Id);
            string? comments = toposolid
                .get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();

            string identity = isSubdivision
                ? string.Empty
                : TerrainIdentity.IsStamp(comments)
                    ? $"  stamp \"{comments}\""
                    : "  UNSTAMPED (a re-import treats it as not its own)";

            report.AppendLine(CultureInfo.InvariantCulture,
                $"      {toposolid.Id.Value}: {(isSubdivision ? "subdivision" : "GROUND     ")}  "
                + $"{footprint}  type \"{document.GetElement(toposolid.GetTypeId())?.Name ?? "(none)"}\""
                + $"{identity}");
        }
    }

    private static void ProbeSubDivisionAgreement(Document document, StringBuilder report)
    {
        report.AppendLine("SUBDIVISION SURFACE AGREEMENT — read only, nothing is written");
        report.AppendLine("  (does each subdivision lie ON the ground toposolid, or is it a different surface?");
        report.AppendLine("   measured vertically at the subdivision's own tessellation vertices.)");

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
        if (grounds.Count == 0)
        {
            report.AppendLine("  No ground toposolid in this project, so there is nothing to measure against.");
            report.AppendLine();
            return;
        }

        foreach (Toposolid ground in grounds)
        {
            MeasureAgainstGround(document, ground, report);
        }

        report.AppendLine();
    }

    private static void MeasureAgainstGround(Document document, Toposolid ground, StringBuilder report)
    {
        if (!TryTopSurface(ground, FinestDetail, out List<SurfacePoint> groundVertices,
                out List<SurfaceTriangle> groundTriangles, out string? groundReason))
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  ground {ground.Id.Value} gave no top surface to measure against — {groundReason}");
            return;
        }

        List<ElementId> children = [.. ground.GetSubDivisionIds()];
        report.AppendLine(CultureInfo.InvariantCulture,
            $"  reference: ground {ground.Id.Value} — {groundVertices.Count:N0} vertices, "
            + $"{groundTriangles.Count:N0} triangles, {children.Count:N0} subdivision(s).");

        if (children.Count == 0)
        {
            report.AppendLine("      no subdivisions on this ground, so there is nothing to compare to it.");
            return;
        }

        TessellationNoiseFloor floor =
            MeasureGroundNoiseFloor(ground, groundVertices, groundTriangles, report);
        report.AppendLine(CultureInfo.InvariantCulture,
            $"      {SurfaceAgreementCheck.DescribeNoiseFloor(floor)}");

        foreach (ElementId id in children)
        {
            if (document.GetElement(id) is not Toposolid subdivision)
            {
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"      {id.Value}: not a toposolid any more — skipped.");
                continue;
            }

            if (!TryTopSurface(subdivision, FinestDetail, out List<SurfacePoint> vertices, out _,
                    out string? reason))
            {
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"      {id.Value}: no top surface — {reason}");
                continue;
            }

            IReadOnlyList<SurfacePoint> samples = SurfaceAgreementCheck.Sample(vertices, AgreementSampleCap);
            SurfaceAgreement agreement =
                SurfaceAgreementCheck.Compare(groundVertices, groundTriangles, samples);

            report.AppendLine(CultureInfo.InvariantCulture,
                $"      {SurfaceAgreementCheck.Describe(id.Value.ToString(CultureInfo.InvariantCulture), agreement, floor)} "
                + $"[{samples.Count:N0} of {vertices.Count:N0} vertices sampled]");
        }
    }

    private static TessellationNoiseFloor MeasureGroundNoiseFloor(
        Toposolid ground,
        IReadOnlyList<SurfacePoint> groundVertices,
        IReadOnlyList<SurfaceTriangle> groundTriangles,
        StringBuilder report)
    {
        if (!TryTopSurface(ground, ControlDetail, out List<SurfacePoint> control, out _, out string? reason))
        {
            report.AppendLine(CultureInfo.InvariantCulture,
                $"      the control tessellation of ground {ground.Id.Value} gave no surface — {reason}");
            return TessellationNoiseFloor.Unmeasured;
        }

        return SurfaceAgreementCheck.MeasureNoiseFloor(
            groundVertices, groundTriangles, SurfaceAgreementCheck.Sample(control, AgreementSampleCap));
    }

    private const int AgreementSampleCap = 10_000;

    private const double FinestDetail = 1.0;

    private const double ControlDetail = 0.3;

    private static Face? FindTopFace(
        Element element,
        bool computeReferences,
        out int faceCount,
        out double topArea,
        out string? reason)
    {
        faceCount = 0;
        topArea = 0.0;
        reason = null;

        Options options = new()
        {
            ComputeReferences = computeReferences,
            DetailLevel = ViewDetailLevel.Fine,
        };

        GeometryElement? geometry = element.get_Geometry(options);
        if (geometry is null)
        {
            reason = "it has no geometry.";
            return null;
        }

        Face? top = null;
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
                if (normal.Z > 0.5 && face.Area > topArea)
                {
                    topArea = face.Area;
                    top = face;
                }
            }
        }

        if (top is null)
        {
            reason = faceCount == 0
                ? "its geometry contains no solid faces."
                : "none of its faces point upward.";
        }

        return top;
    }

    private static bool TryTopSurface(
        Element element,
        double detail,
        out List<SurfacePoint> vertices,
        out List<SurfaceTriangle> triangles,
        out string? reason)
    {
        vertices = [];
        triangles = [];
        reason = null;

        try
        {
            Face? top = FindTopFace(element, computeReferences: false, out _, out _, out reason);
            if (top is null)
            {
                return false;
            }

            Mesh mesh = top.Triangulate(detail);
            for (int i = 0; i < mesh.Vertices.Count; i++)
            {
                XYZ point = mesh.Vertices[i];
                vertices.Add(new SurfacePoint(
                    UnitUtils.ConvertFromInternalUnits(point.X, UnitTypeId.Meters),
                    UnitUtils.ConvertFromInternalUnits(point.Y, UnitTypeId.Meters),
                    UnitUtils.ConvertFromInternalUnits(point.Z, UnitTypeId.Meters)));
            }

            for (int i = 0; i < mesh.NumTriangles; i++)
            {
                MeshTriangle triangle = mesh.get_Triangle(i);
                triangles.Add(new SurfaceTriangle(
                    (int)triangle.get_Index(0),
                    (int)triangle.get_Index(1),
                    (int)triangle.get_Index(2)));
            }

            if (vertices.Count == 0)
            {
                reason = "its top face tessellated to nothing.";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException
                                       or ArgumentException
                                       or InvalidOperationException)
        {
            reason = $"Revit threw {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }
}
