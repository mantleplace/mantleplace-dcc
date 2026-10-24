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
//                       reads) against the ground toposolid's own triangulated top at each point's plan position
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
        Ground? ground = grounds.Count == 0 ? null : Ground.Of(document, grounds, toShared);
        record["ground_top_triangles"] = ground?.Count ?? 0;

        record["placement"] = Placement(bundle, revit, east, north, ground, dir);
        say("placement measured");
        record["stamped_by_class"] = ByClass(document, OdblContainers.StampedElementIds(document));
        record["links"] = Links(document, toShared, ground, dir);
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

    private static object Placement(string bundle, JsonElement revit, double east, double north, Ground? ground, string dir)
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
            double? measured = ground?.At(e * FeetPerMetre, n * FeetPerMetre) / FeetPerMetre;
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

    private static List<object> Links(Document document, Transform toShared, Ground? ground, string dir)
    {
        List<object> links = [];
        StringBuilder csv = new("link,guid,centre_east_m,centre_north_m,base_z_m,top_z_m,ground_z_m\n");
        foreach (RevitLinkInstance instance in new FilteredElementCollector(document).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {
            Transform total = instance.GetTotalTransform();
            Document? linked = instance.GetLinkDocument();
            int buildings = 0;
            if (linked is not null)
            {
                // The site model's buildings come in as Generic Models; its levels, grids and the like carry a
                // GlobalId too but a box about the origin.
                foreach (Element element in new FilteredElementCollector(linked).OfCategory(BuiltInCategory.OST_GenericModel).WhereElementIsNotElementType())
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
                    double? under = ground?.At(cx, cy) / FeetPerMetre;
                    csv.Append(Invariant, $"{instance.Name.Replace(',', ' ')},{guid},{cx / FeetPerMetre:F3},{cy / FeetPerMetre:F3},{Math.Min(min.Z, max.Z) / FeetPerMetre:F3},{Math.Max(min.Z, max.Z) / FeetPerMetre:F3},{(under is { } g ? g.ToString("F3", Invariant) : "")}\n");
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
        // Names now: a removed container is deleted with its content, and a deleted element has no name to read.
        List<string> removals = [.. plan.Removing.Select(container => container.Filter.Name)];
        List<string> kept = [.. plan.Containers.Except(plan.Removing).Select(container => container.Filter.Name)];
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
            planned_removals = removals,
            kept,
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

    /// <summary>
    /// The upward-facing triangles of the ground toposolids, in shared feet, and the height of the ground at a
    /// shared plan point. A ray cast is not used: a ReferenceIntersector over a toposolid returned only its flat
    /// bottom face, in every attempt, so the ground's top is read from its own triangulated faces instead.
    /// </summary>
    private sealed class Ground
    {
        private const double CellFt = 40.0;
        private readonly List<(XYZ A, XYZ B, XYZ C)> _triangles = [];
        private readonly Dictionary<(int, int), List<int>> _cells = [];

        internal int Count => _triangles.Count;

        internal static Ground Of(Document document, IEnumerable<ElementId> ids, Transform toShared)
        {
            Ground ground = new();
            Options options = new() { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false };
            foreach (ElementId id in ids)
            {
                if (document.GetElement(id)?.get_Geometry(options) is { } geometry)
                {
                    ground.Add(geometry, toShared);
                }
            }

            return ground;
        }

        private void Add(GeometryElement geometry, Transform toShared)
        {
            foreach (GeometryObject part in geometry)
            {
                switch (part)
                {
                    case Solid solid:
                        foreach (Face face in solid.Faces)
                        {
                            Add(face.Triangulate(), toShared);
                        }

                        break;
                    case Mesh mesh:
                        Add(mesh, toShared);
                        break;
                    case GeometryInstance instance:
                        Add(instance.GetInstanceGeometry(), toShared);
                        break;
                }
            }
        }

        private void Add(Mesh mesh, Transform toShared)
        {
            for (int i = 0; i < mesh.NumTriangles; i++)
            {
                MeshTriangle triangle = mesh.get_Triangle(i);
                XYZ a = toShared.OfPoint(triangle.get_Vertex(0));
                XYZ b = toShared.OfPoint(triangle.get_Vertex(1));
                XYZ c = toShared.OfPoint(triangle.get_Vertex(2));
                double normalZ = ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));
                if (Math.Abs(normalZ) < 1e-9 || Math.Max(a.Z, Math.Max(b.Z, c.Z)) - Math.Min(a.Z, Math.Min(b.Z, c.Z)) > 1e6)
                {
                    continue;
                }

                int index = _triangles.Count;
                _triangles.Add((a, b, c));
                for (int x = Cell(Math.Min(a.X, Math.Min(b.X, c.X))); x <= Cell(Math.Max(a.X, Math.Max(b.X, c.X))); x++)
                {
                    for (int y = Cell(Math.Min(a.Y, Math.Min(b.Y, c.Y))); y <= Cell(Math.Max(a.Y, Math.Max(b.Y, c.Y))); y++)
                    {
                        if (!_cells.TryGetValue((x, y), out List<int>? list))
                        {
                            _cells[(x, y)] = list = [];
                        }

                        list.Add(index);
                    }
                }
            }
        }

        private static int Cell(double value) => (int)Math.Floor(value / CellFt);

        /// <summary>The highest triangle over the point, interpolated in it, in shared feet; null where there is none.</summary>
        internal double? At(double east, double north)
        {
            if (!_cells.TryGetValue((Cell(east), Cell(north)), out List<int>? candidates))
            {
                return null;
            }

            double? best = null;
            foreach (int index in candidates)
            {
                (XYZ a, XYZ b, XYZ c) = _triangles[index];
                double det = ((b.Y - c.Y) * (a.X - c.X)) + ((c.X - b.X) * (a.Y - c.Y));
                double l1 = (((b.Y - c.Y) * (east - c.X)) + ((c.X - b.X) * (north - c.Y))) / det;
                double l2 = (((c.Y - a.Y) * (east - c.X)) + ((a.X - c.X) * (north - c.Y))) / det;
                double l3 = 1 - l1 - l2;
                if (l1 < -1e-9 || l2 < -1e-9 || l3 < -1e-9)
                {
                    continue;
                }

                double z = (l1 * a.Z) + (l2 * b.Z) + (l3 * c.Z);
                best = best is { } held ? Math.Max(held, z) : z;
            }

            return best;
        }
    }
}
