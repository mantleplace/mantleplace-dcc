using System.Globalization;
using System.Text;
using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// The ground's surface: smooth shading, the toposolid inventory, and whether a subdivision's top
// face agrees with the ground it was cut from.
public sealed partial class TerrainProbeCommand
{
    /// <summary>
    /// The smooth-shading setting as it stands, and an inventory of every toposolid in the project.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Both of the questions this arm was built to ask are answered, so it no longer asks
    /// them.</b> It reports instead. Kept in that reduced form because the two facts below are the
    /// ones a confusing screenshot needs before anybody theorises about it, and both are free.
    /// </para>
    /// <para>
    /// <b>Answered 1 — scope.</b> <c>SetSmoothedSurface</c> and <c>IsSmoothedSurfaceEnabled</c> are
    /// <b>static</b> and take only a <c>Document</c>. The setting is document-wide; there is no
    /// per-toposolid state, so no subdivision ever needs the call in its own right. The compiler
    /// settled that in one build, at no cost.
    /// </para>
    /// <para>
    /// <b>Answered 2 — it moves where a real-world texture is measured from, and the drape is
    /// written for that.</b> Measured on a 1,419 × 1,413 m site by rendering the same view twice:
    /// under flat shading Revit measures the offset from the project origin, per face, in each
    /// face's own plane; under smooth shading from the element's bounding-box minimum corner,
    /// continuously. Autodesk documents that smoothing suppresses toposolid surface patterns and
    /// ignores paint and graphic overrides; moving a real-world-scaled bitmap's origin is an
    /// undocumented fourth effect. The importer therefore turns smoothing <b>on</b> — always, given
    /// terrain — and anchors every drape material to its own element's corner
    /// (<c>DrapeAnchor</c>, <c>TerrainSmoothing</c>). This arm is what would catch that changing in
    /// a future Revit.
    /// </para>
    /// <para>
    /// ⛔ <b>An earlier version of this remark said the two were incompatible and that the importer
    /// smoothed only undraped ground. That was true before the drape was anchored per element, and
    /// it survived here for two weeks after it stopped being true</b> — long enough to send a
    /// session hunting a call the importer had already been making. The runtime lines below were
    /// current the whole time; only this prose was not. Which is the argument for reading the log a
    /// probe writes before reading the remark above it.
    /// </para>
    /// <para>
    /// The inventory is the new half. A project that has been imported into more than once can hold
    /// more toposolids than a reader expects, and "how many terrains are actually here" is a
    /// question that has now been asked from a screenshot twice. A subdivision is itself a
    /// <c>Toposolid</c>, so the distinction is drawn the way the importer draws it — anything listed
    /// in another toposolid's <c>GetSubDivisionIds()</c> is a subdivision, and whatever is left is
    /// ground.
    /// </para>
    /// <para>
    /// ⛔ Nothing here opens a transaction at all now. The arm that did has served its purpose and is
    /// gone, which also removes the one place in this file that could have altered a project-wide
    /// display setting.
    /// </para>
    /// </remarks>
    private static void ProbeSmoothedSurface(Document document, StringBuilder report)
    {
        report.AppendLine("TOPOSOLID SMOOTH SHADING — read only, nothing is written");
        report.AppendLine("  (document-wide: both API members are static and take only a Document.)");
        report.AppendLine("  (it moves the origin of a real-world texture offset: flat shading measures");
        report.AppendLine("   from the project origin per face, smooth shading from the element's");
        report.AppendLine("   bounding-box corner. The importer turns it on and anchors the drape for it.)");

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
            // Would mean this Revit does not carry the 2025 pair, which is worth one line rather
            // than the loss of the inventory below.
            report.AppendLine(CultureInfo.InvariantCulture,
                $"  This Revit would not report the setting — {ex.GetType().Name}: {ex.Message}");
        }

        ReportToposolidInventory(document, report);
        report.AppendLine();
    }

    /// <summary>
    /// Every toposolid in the project, split into ground and subdivisions, with its footprint.
    /// </summary>
    /// <remarks>
    /// The footprint is what makes the list readable: a second toposolid the size of the site is a
    /// duplicate terrain from an earlier import and a real problem, while one a tenth of that size
    /// is a subdivision or a curator's own modelling and is not. Reporting only ids would leave that
    /// distinction to be guessed at from a screenshot, which is exactly what this is replacing.
    /// </remarks>
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
            // Said plainly, because it is the reading that changes what someone does next: two
            // full-size terrains stacked will z-fight and no amount of material work will fix it.
            report.AppendLine("      ⚠ more than one ground toposolid — check the footprints below "
                + "for a duplicate terrain left by an earlier import.");
        }

        foreach (Toposolid toposolid in all)
        {
            // Worded by FootprintExtent, which is also what the import's coextension report compares
            // against — the probe and the log must not print one site at two precisions.
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

            // The stamp, for ground only: it is what a re-import matches on, so "which of these two
            // grounds does the plugin think is its own" is answerable from the probe rather than by
            // running an import to find out. A subdivision's stamp is already covered above.
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

    /// <summary>
    /// Whether every site-boundary subdivision's surface actually lies on the ground it was cut
    /// from, measured vertically at its own tessellation vertices.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The question came from a screenshot: 23 subdivisions on one terrain, each drawing its own
    /// contour lines, and the lines do not line up with the continuous ground beneath them. Two
    /// explanations fit, and they call for opposite responses — either the surfaces agree and Revit
    /// simply generates contours per element, which is a limitation to document, or the surfaces
    /// genuinely differ, which is a defect. Nothing short of a number separates them.
    /// </para>
    /// <para>
    /// ⛔ Read-only and no transaction, like the arm above it. The arithmetic is
    /// <see cref="SurfaceAgreementCheck"/> in the pure core, where a headless test drives it; this
    /// method only gets Revit's tessellation out and converts feet to metres.
    /// </para>
    /// <para>
    /// Sampling is capped at <see cref="AgreementSampleCap"/> vertices per subdivision, strided
    /// evenly rather than taken from the front: taking the first N would sample one corner of each
    /// subdivision, which is exactly where a boundary artifact would hide a disagreement in the
    /// middle. The cap is a bound on how much of each subdivision is claimed to have been checked,
    /// which is why the report prints how many of its vertices were sampled — a verdict from part of
    /// a subdivision is a verdict about that part. See <see cref="AgreementSampleCap"/> for the
    /// number and why it is the number.
    /// </para>

    /// <para>
    /// The cap is <em>not</em> what keeps the run cheap, and an earlier version of this remark said
    /// it was — while the code beneath it asked for the finest tessellation Revit offers, which
    /// raises the ground's triangle count by an unmeasured factor, and then looped over every ground.
    /// <see cref="SurfaceAgreementCheck.Compare"/> indexes the reference triangles in plan, so the
    /// cost does not track that count. Cheapness is the core's problem; honesty about coverage is
    /// this method's.
    /// </para>
    /// <para>
    /// Every ground is measured against a noise floor of its own before its subdivisions are judged,
    /// because a deviation only means something once there is a number it had to beat. See
    /// <see cref="MeasureGroundNoiseFloor"/>.
    /// </para>
    /// </remarks>
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

        // ⛔ Every ground, not the first one found. A project imported into more than once can hold
        // several, the arm above this one warns about exactly that, and measuring one of them while
        // silently skipping the rest would report a clean bill for a project where the problem sits
        // under the terrain nobody looked at.
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

    /// <summary>
    /// One ground toposolid's subdivisions, each measured against it.
    /// </summary>
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

        // The floor before the verdicts it qualifies, and only once there is a verdict to qualify:
        // the control is a second tessellation of the whole ground, which is not worth asking Revit
        // for on a ground nothing is being measured against.
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

    /// <summary>
    /// How far one ground's own chords fall from its own surface — the floor every verdict about it
    /// is stated against.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same element, tessellated a second time at <see cref="ControlDetail"/> and measured
    /// against its own finest triangles. Both point sets come off one face, so the true deviation is
    /// zero by construction and what comes out is chord error and nothing else. Without it the
    /// report can only say a subdivision deviates by more than a number nobody computed, which is
    /// the sentence <see cref="SurfaceAgreementCheck"/> exists to stop being written.
    /// </para>
    /// <para>
    /// ⛔ Read-only, like everything in this arm: a second <c>Triangulate</c> on a face Revit has
    /// already given up once, opening no transaction and touching no element.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// How many of a subdivision's vertices the agreement arm measures. See
    /// <see cref="ProbeSubDivisionAgreement"/> for why it is capped and why the sample is strided.
    /// </summary>
    /// <remarks>
    /// ⛔ Large enough that every subdivision on the site this arm was written for is measured whole,
    /// because the cap's job is coverage rather than cost — <c>SurfaceAgreementCheck.Compare</c>
    /// indexes the reference triangles in plan, so a run does not track their count. A cap sized to
    /// keep a run cheap would buy nothing and cost the one thing a one-shot measurement cannot
    /// afford, which is a "they agree" verdict from a strided sample that walked past the
    /// disagreement.
    /// </remarks>
    private const int AgreementSampleCap = 10_000;

    /// <summary>
    /// Revit's finest tessellation — "0 is the lowest level of detail and 1 is the highest"
    /// (Revit 2025 API).
    /// </summary>
    private const double FinestDetail = 1.0;

    /// <summary>
    /// The level of detail the noise-floor control is tessellated at.
    /// </summary>
    /// <remarks>
    /// ⛔ Coarse, but not the coarsest. The level decides only <em>where</em> the floor is sampled,
    /// never how large it reads — both point sets lie on the same true face — so the single property
    /// it has to have is that its vertices are not simply the fine mesh's own, which interpolate
    /// exactly and would report a floor of zero from a control that measured no chord at all. 0.0
    /// risks collapsing the face to a handful of triangles whose corners are exactly that; 0.3
    /// spreads the samples off them while staying plainly coarser than the mesh under test.
    /// <see cref="TessellationNoiseFloor.Coincident"/> reports how many landed on a vertex anyway,
    /// so this is a choice the report does not ask a reader to trust.
    /// </remarks>
    private const double ControlDetail = 0.3;

    /// <summary>
    /// The largest upward-facing face of one element, or <c>null</c> with a reason.
    /// </summary>
    /// <remarks>
    /// Chosen by the largest upward normal rather than by index, because face order is not a
    /// documented property of anything. One copy rather than one per caller: this is an unverified
    /// Revit API shape (`revit/CLAUDE.md` ▸ "Revit API risk is real and not caught by the
    /// compiler"), and a second copy is a second thing to fix when it turns out to be wrong.
    /// </remarks>
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
            // Two different absences, and the distinction is the first thing a reader needs: no
            // solids at all is a geometry option or a view-detail problem, while solids whose faces
            // all point elsewhere is a real shape. "0 face(s), none of them upward-facing" reads as
            // a contradiction and sends the reader looking for the wrong thing.
            reason = faceCount == 0
                ? "its geometry contains no solid faces."
                : "none of its faces point upward.";
        }

        return top;
    }

    /// <summary>
    /// Every vertex and triangle of one toposolid's upward face, in metres.
    /// </summary>
    /// <remarks>
    /// The face comes from <see cref="FindTopFace"/>, shared with <see cref="PaintTopFace"/>.
    /// <paramref name="detail"/> is handed to <c>Face.Triangulate</c> ("0 is the lowest level of
    /// detail and 1 is the highest" — Revit 2025 API), and Revit's tessellation is the right thing
    /// to measure: the question is whether the surfaces Revit built agree, not whether the points
    /// they were built from did.
    /// </remarks>
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

            // ⛔ An explicit level, never the parameterless overload. Callers measuring one surface
            // against another pass FinestDetail, because each element is tessellated on its own: a
            // sample vertex from one is interpolated across the other's chords, and two different
            // triangulations of the same sloped shape disagree by the chord height between them with
            // no geometry differing at all. The finest tessellation makes that error as small as
            // this probe can make it and does not make it zero, which is why the arm also measures
            // how large it is — see MeasureGroundNoiseFloor, whose control deliberately asks for a coarser
            // level. Do not read this comment as a guarantee the core declines to give.
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
            // One element's geometry failing must not cost the reader the other twenty-two lines.
            reason = $"Revit threw {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }
}
