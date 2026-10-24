using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using MantlePlace.Revit.Addin;
using MantlePlace.Revit.Core;

namespace MantlePlace.Scratch.Timing;

// The live verification a layers run does after its import when MANTLEPLACE_TIMING_VERIFY is 1 (-Verify): it MEASURES
// and writes what it measured to the run folder; verify_report.py judges it. Compiled only from a source that has
// PrepareForHandover (Build-TimingHarness.ps1 decides), so a harness for an older commit still builds.
//
//   placement.csv       the bundle's own surface points (hosts.revit.toposurface_points, which a TIN import never
//                       reads) against the ground toposolid, cast straight down at each point's plan position
//   link-buildings.csv  every element of each linked model that carries an IFC GlobalId: its plan centre and its
//                       base, in the host's shared coordinates, in metres, and the ground under that centre
//   verify.json         the frame self-check, what the import stamped by class, each ODbL container by class,
//                       each link's transform, the site context view's override on each container, and the handover:
//                       what it planned, what Delete took (filed and not), what is left, and the credit
//   site-context.png    the site context view, for the eyes-on check of the orange override
internal static class Verify
{
    private const double FeetPerMetre = 1.0 / 0.3048;
    private const int CheckPoints = 64;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    internal static string Run(Document document, string bundle, string dir, Action<string> say)
    {
        Dictionary<string, object?> record = [];
        Transform toInternal = document.ActiveProjectLocation.GetTotalTransform();
        Transform toShared = toInternal.Inverse;

        using JsonDocument manifest = ReadManifest(bundle);
        JsonElement revit = manifest.RootElement.GetProperty("hosts").GetProperty("revit");
        JsonElement origin = revit.GetProperty("georeference").GetProperty("origin").GetProperty("projected");
        double east = origin.GetProperty("easting").GetDouble();
        double north = origin.GetProperty("northing").GetDouble();
        record["manifest_version"] = manifest.RootElement.GetProperty("version").GetString();
        record["origin_projected"] = new { east, north, unit = origin.GetProperty("linear_unit").GetString() };

        // Self-check of the frame: the internal origin, in shared coordinates, is the survey point the import set.
        XYZ internalOrigin = toShared.OfPoint(XYZ.Zero);
        record["internal_origin_in_shared_m"] = Metres(internalOrigin);

        List<ElementId> grounds = GroundToposolids(document);
        record["ground_toposolids"] = grounds.Count;
        View3D? probeView = grounds.Count == 0 ? null : ProbeView(document);
        ReferenceIntersector? caster = probeView is null ? null : new ReferenceIntersector(grounds, FindReferenceTarget.Face, probeView);

        record["placement"] = Placement(bundle, revit, east, north, toInternal, toShared, caster, dir);
        say("placement measured");
        record["stamped_by_class"] = ByClass(document, OdblContainers.StampedElementIds(document));
        record["links"] = Links(document, toShared, caster, dir);
        say("links measured");

        PrepareForHandover.HandoverPlan plan = PrepareForHandover.Plan(document);
        record["containers"] = plan.Containers.Select(container => new
        {
            name = container.Filter.Name,
            identity = container.Key.Identity,
            group = container.Key.Group,
            live = container.Live.Count,
            by_class = ByClass(document, container.Live),
        }).ToList();
        record["site_context_overrides"] = Overrides(document, plan.Containers);
        record["site_context_png"] = ExportSiteContext(document, dir, say);
        record["handover"] = Handover(document, plan, say);

        File.WriteAllText(Path.Combine(dir, "verify.json"), JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }));
        return "ok";
    }

    private static JsonDocument ReadManifest(string bundle)
    {
        using ZipArchive zip = ZipFile.OpenRead(bundle);
        ZipArchiveEntry entry = zip.GetEntry("Metadata/manifest.json") ?? throw new InvalidDataException("the bundle has no Metadata/manifest.json");
        using Stream stream = entry.Open();
        return JsonDocument.Parse(stream);
    }

    private static double[] Metres(XYZ point) => [point.X / FeetPerMetre, point.Y / FeetPerMetre, point.Z / FeetPerMetre];

    private static List<ElementId> GroundToposolids(Document document)
    {
        List<Toposolid> all = [.. new FilteredElementCollector(document).OfClass(typeof(Toposolid)).Cast<Toposolid>()];
        HashSet<ElementId> subdivisions = [.. all.SelectMany(toposolid => toposolid.GetSubDivisionIds())];
        return [.. all.Where(toposolid => !subdivisions.Contains(toposolid.Id)).Select(toposolid => toposolid.Id)];
    }

    /// <summary>A fresh isometric 3D view to cast in: no section box, no hidden category, nothing the import set.</summary>
    private static View3D? ProbeView(Document document)
    {
        ViewFamilyType? type = new FilteredElementCollector(document)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(candidate => candidate.ViewFamily == ViewFamily.ThreeDimensional);
        if (type is null)
        {
            return null;
        }

        using Transaction transaction = new(document, "Mantle Place verify: probe view");
        transaction.Start();
        View3D view = View3D.CreateIsometric(document, type.Id);
        view.Name = "Mantle Place Verify Probe";
        transaction.Commit();
        return view;
    }

    /// <summary>The ground's height, in shared metres, straight down through the shared plan point; null on a miss.</summary>
    private static double? GroundAt(ReferenceIntersector? caster, Transform toInternal, Transform toShared, double sharedEastFt, double sharedNorthFt)
    {
        if (caster is null)
        {
            return null;
        }

        XYZ start = toInternal.OfPoint(new XYZ(sharedEastFt, sharedNorthFt, 20000 * FeetPerMetre));
        ReferenceWithContext? hit = caster.FindNearest(start, toInternal.BasisZ.Negate());
        return hit is null ? null : toShared.OfPoint(hit.GetReference().GlobalPoint).Z / FeetPerMetre;
    }

    private static object Placement(string bundle, JsonElement revit, double east, double north, Transform toInternal, Transform toShared, ReferenceIntersector? caster, string dir)
    {
        if (!revit.TryGetProperty("toposurface_points", out JsonElement points) || points.ValueKind != JsonValueKind.Object)
        {
            return new { skipped = "the bundle publishes no hosts.revit.toposurface_points" };
        }

        string path = points.GetProperty("path").GetString()!;
        string frame = points.TryGetProperty("horizontal_frame", out JsonElement f) ? f.GetString() ?? "" : "";
        string units = points.TryGetProperty("units", out JsonElement u) ? u.GetString() ?? "" : "";
        if (units != "m" || (frame != "local_enu" && frame != "absolute_projected"))
        {
            return new { skipped = $"the points are '{frame}' in '{units}'; this check reads local_enu or absolute_projected in m" };
        }

        List<double[]> all = [];
        using (ZipArchive zip = ZipFile.OpenRead(bundle))
        using (StreamReader reader = new((zip.GetEntry(path) ?? throw new InvalidDataException($"no entry {path}")).Open()))
        {
            while (reader.ReadLine() is { } line)
            {
                string[] parts = line.Split(',');
                if (parts.Length >= 3
                    && double.TryParse(parts[0], NumberStyles.Float, Invariant, out double x)
                    && double.TryParse(parts[1], NumberStyles.Float, Invariant, out double y)
                    && double.TryParse(parts[2], NumberStyles.Float, Invariant, out double z))
                {
                    all.Add([x, y, z]);
                }
            }
        }

        StringBuilder csv = new("index,east_m,north_m,expected_z_m,measured_z_m\n");
        int step = Math.Max(1, all.Count / CheckPoints);
        for (int i = step / 2; i < all.Count; i += step)
        {
            double e = frame == "local_enu" ? east + all[i][0] : all[i][0];
            double n = frame == "local_enu" ? north + all[i][1] : all[i][1];
            double? measured = GroundAt(caster, toInternal, toShared, e * FeetPerMetre, n * FeetPerMetre);
            csv.Append(Invariant, $"{i},{e:F3},{n:F3},{all[i][2]:F3},{(measured is { } m ? m.ToString("F3", Invariant) : "")}\n");
        }

        File.WriteAllText(Path.Combine(dir, "placement.csv"), csv.ToString());
        return new { path, frame, units, points_in_file = all.Count, file = "placement.csv" };
    }

    private static Dictionary<string, int> ByClass(Document document, IEnumerable<ElementId> ids)
    {
        Dictionary<string, int> counts = [];
        foreach (ElementId id in ids)
        {
            string key = Describe(document.GetElement(id));
            counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        return counts;
    }

    private static string Describe(Element? element)
        => element is null ? "(gone)" : $"{element.GetType().Name}/{element.Category?.Name ?? "-"}{(element.ViewSpecific ? "/view-specific" : "")}";

    private static List<object> Links(Document document, Transform toShared, ReferenceIntersector? caster, string dir)
    {
        List<object> links = [];
        StringBuilder csv = new("link,guid,centre_east_m,centre_north_m,base_z_m,top_z_m,ground_z_m\n");
        Transform toInternal = toShared.Inverse;
        foreach (RevitLinkInstance instance in new FilteredElementCollector(document).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {
            Transform total = instance.GetTotalTransform();
            Document? linked = instance.GetLinkDocument();
            int buildings = 0;
            if (linked is not null)
            {
                foreach (Element element in new FilteredElementCollector(linked).WhereElementIsNotElementType())
                {
                    string? guid = element.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString();
                    if (string.IsNullOrEmpty(guid) || element.Category is null || element.get_BoundingBox(null) is not { } box)
                    {
                        continue;
                    }

                    XYZ min = toShared.OfPoint(total.OfPoint(box.Min));
                    XYZ max = toShared.OfPoint(total.OfPoint(box.Max));
                    double cx = (min.X + max.X) / 2;
                    double cy = (min.Y + max.Y) / 2;
                    double? ground = GroundAt(caster, toInternal, toShared, cx, cy);
                    csv.Append(Invariant, $"{instance.Name.Replace(',', ' ')},{guid},{cx / FeetPerMetre:F3},{cy / FeetPerMetre:F3},{Math.Min(min.Z, max.Z) / FeetPerMetre:F3},{Math.Max(min.Z, max.Z) / FeetPerMetre:F3},{(ground is { } g ? g.ToString("F3", Invariant) : "")}\n");
                    buildings++;
                }
            }

            ProjectPosition? linkPosition = linked?.ActiveProjectLocation.GetProjectPosition(XYZ.Zero);
            links.Add(new
            {
                name = instance.Name,
                loaded = linked is not null,
                transform_origin_ft = new[] { total.Origin.X, total.Origin.Y, total.Origin.Z },
                transform_basis_x = new[] { total.BasisX.X, total.BasisX.Y, total.BasisX.Z },
                transform_is_identity = total.IsIdentity,
                link_project_position_m = linkPosition is null ? null : new[] { linkPosition.EastWest / FeetPerMetre, linkPosition.NorthSouth / FeetPerMetre, linkPosition.Elevation / FeetPerMetre },
                link_angle_rad = linkPosition?.Angle,
                elements_with_ifc_guid = buildings,
            });
        }

        File.WriteAllText(Path.Combine(dir, "link-buildings.csv"), csv.ToString());
        return links;
    }

    private static List<object> Overrides(Document document, IReadOnlyList<PrepareForHandover.Container> containers)
    {
        List<object> found = [];
        View3D? view = new FilteredElementCollector(document)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .FirstOrDefault(candidate => !candidate.IsTemplate && candidate.Name == SiteContext.ViewName);
        if (view is null)
        {
            return found;
        }

        foreach (PrepareForHandover.Container container in containers.Where(container => view.IsFilterApplied(container.Filter.Id)))
        {
            OverrideGraphicSettings overrides = view.GetFilterOverrides(container.Filter.Id);
            Color color = overrides.ProjectionLineColor;
            found.Add(new
            {
                filter = container.Filter.Name,
                line_color = color.IsValid ? $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}" : "(none)",
                line_weight = overrides.ProjectionLineWeight,
                view_display_style = view.DisplayStyle.ToString(),
            });
        }

        return found;
    }

    private static string ExportSiteContext(Document document, string dir, Action<string> say)
    {
        View3D? view = new FilteredElementCollector(document)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .FirstOrDefault(candidate => !candidate.IsTemplate && candidate.Name == SiteContext.ViewName);
        if (view is null)
        {
            return "(no site context view)";
        }

        try
        {
            ImageExportOptions options = new()
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = Path.Combine(dir, "site-context"),
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                PixelSize = 2400,
                FitDirection = FitDirectionType.Horizontal,
                ZoomType = ZoomFitType.FitToPage,
            };
            options.SetViewsAndSheets([view.Id]);
            document.ExportImage(options);
            return string.Join(", ", Directory.GetFiles(dir, "site-context*.png").Select(Path.GetFileName));
        }
        catch (Exception ex)
        {
            say("site context export failed: " + ex.Message);
            return "(export failed: " + ex.Message + ")";
        }
    }

    private static object Handover(Document document, PrepareForHandover.HandoverPlan plan, Action<string> say)
    {
        // What every id is before anything is deleted, so what Delete took can be named afterwards.
        Dictionary<ElementId, string> before = [];
        foreach (Element element in new FilteredElementCollector(document).WhereElementIsNotElementType())
        {
            before[element.Id] = Describe(element);
        }

        foreach (Element element in new FilteredElementCollector(document).WhereElementIsElementType())
        {
            before[element.Id] = Describe(element);
        }

        HashSet<ElementId> filed = [.. plan.Removing.SelectMany(container => container.Live)];
        PrepareForHandover.HandoverResult result = PrepareForHandover.Run(document, plan);
        say($"handover: committed={result.Committed}, deleted {result.Deleted.Count}, filed {filed.Count}");

        Dictionary<string, int> alongside = [];
        foreach (ElementId id in result.Deleted.Where(id => !filed.Contains(id)))
        {
            string key = before.TryGetValue(id, out string? what) ? what : "(not in the snapshot)";
            alongside[key] = alongside.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        List<string> attribution = [];
        foreach (ViewDrafting view in new FilteredElementCollector(document).OfClass(typeof(ViewDrafting)).Cast<ViewDrafting>().Where(view => view.Name == AttributionView.ViewName))
        {
            attribution.AddRange(new FilteredElementCollector(document, view.Id).OfClass(typeof(TextNote)).Cast<TextNote>().Select(note => note.Text));
        }

        return new
        {
            counts = new { plan.Counts },
            planned_removals = plan.Removing.Select(container => container.Filter.Name).ToList(),
            kept = plan.Containers.Except(plan.Removing).Select(container => container.Filter.Name).ToList(),
            committed = result.Committed,
            failure = result.Failure,
            deleted_total = result.Deleted.Count,
            filed_total = filed.Count,
            filed_deleted = result.Deleted.Count(filed.Contains),
            deleted_alongside_by_class = alongside,
            containers_left = OdblContainers.All(document).Select(found => found.Filter.Name).ToList(),
            filed_left = filed.Count(id => document.GetElement(id) is not null),
            credit_line = result.CreditLine,
            attribution_notes = attribution,
        };
    }
}
