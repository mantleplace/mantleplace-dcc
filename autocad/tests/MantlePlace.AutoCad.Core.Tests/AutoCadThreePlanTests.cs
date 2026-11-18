using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MantlePlace.Client;
using MantlePlace.Core;
using MantlePlace.Core.Tests;

namespace MantlePlace.AutoCad.Tests;

using CorpusCase = ConformanceCorpus.CorpusCase;

/// <summary>
/// The AutoCAD import plan for an MPB 3.0 bundle: every part a 2.x bundle's <c>hosts.autocad</c> block
/// places is planned from the file 3.0 promises for it, found by name in the catalog and in the frame and
/// unit the manifest states for that file's family, or skipped with a stated reason.
/// </summary>
/// <remarks>
/// <para>
/// The bundles are the shared corpus's own (<c>corpus/bundle/v3-*.json</c>), read by
/// <see cref="BundleReader"/> exactly as the plugin reads them. A case that needs a value the corpus
/// bundle does not state edits a copy of its manifest and says which value, as the Revit 3.0 cases do.
/// </para>
/// <para>
/// Each file kind has a case that fails on a frame mix-up: a 3.0 bundle states two frames, the order's
/// (georeferenced) and the local site frame, and an AutoCAD drawing takes only the order's. The feet
/// cases graft the corpus's feet frames, whose local frame is in metres, onto the full bundle, so a part
/// that read the local frame's unit would say metres where the order is in US survey feet.
/// </para>
/// </remarks>
internal static class AutoCadThreePlanTests
{
    private const string FullBundle = "bundle/v3-full.json";
    private const string FeetBundle = "bundle/v3-frames-feet.json";
    private const string ReleaseNotes = "autocad/docs/release-notes-1.2.0.md";

    /// <summary>A 2.x fixture with the contours, the surface and the aerial.</summary>
    private const string CompleteBlock = "autocad-complete-block.json";

    /// <summary>A 2.x fixture with the public-record layers and the site linework.</summary>
    private const string RecordsBlock = "autocad-records-linework-present.json";

    /// <summary>The ten public-record layers the full bundle holds, by the name each file gives it (A06).</summary>
    private static readonly string[] RecordNames =
    [
        "Conservation", "Contamination", "Floodplain", "Habitat", "Heritage",
        "Hydrography", "Parcels", "Soils", "Wetlands", "Zoning",
    ];

    private static readonly AutoCadPart[] EveryPart = [AutoCadPart.Contours, AutoCadPart.Surface, AutoCadPart.Aerial, AutoCadPart.Records, AutoCadPart.Linework];

    internal static int Run()
    {
        TestRun run = new();

        RunCorpusCases(run);
        RunFrameCases(run);
        RunAerialCases(run);
        RunSurfaceCases(run);
        RunSurfaceFileCases(run);
        RunAbsenceCases(run);
        RunChooserCases(run);
        RunReleaseNotesCases(run);

        return run.Report("AutoCAD MPB 3.0 import plan");
    }

    private static void RunCorpusCases(TestRun run)
    {
        string? loadError = ConformanceCorpus.LoadGroup("reader", out List<CorpusCase> cases);
        if (loadError is not null)
        {
            run.Case("corpus loads", () => run.Fail($"conformance corpus unusable: {loadError}"));
            return;
        }

        foreach (CorpusCase corpusCase in cases)
        {
            using JsonDocument body = JsonDocument.Parse(corpusCase.Payload);
            string bundle = body.RootElement.GetProperty("bundle").GetString() ?? string.Empty;
            if (!bundle.StartsWith("bundle/v3-", StringComparison.Ordinal))
            {
                continue;
            }

            run.Case($"{corpusCase.Id}: every part is planned, or skipped with a stated reason", () =>
            {
                BundleManifest manifest = ThreePlanTests.Read(ThreePlanTests.Load(bundle));
                AutoCadBlockRead read = AutoCadBlockReader.Read(manifest, manifestText: null);
                AutoCadImportPlan plan = AutoCadImportPlanner.Plan(read, DrawingGeolocation.None, civil3d: null);

                if (plan.IsRefused)
                {
                    run.True(plan.Refusal.Length > 0, "a refusal says why");
                    if (!manifest.IsValid)
                    {
                        run.Equal(plan.Refusal, manifest.Error, "and a refused manifest says the reader's refusal");
                    }

                    return;
                }

                foreach (AutoCadPart kind in EveryPart)
                {
                    PlannedPart part = plan.Part(kind);
                    run.True(part.Sentence.Length > 0, $"{kind} says what happens to it");
                    if (!part.Place)
                    {
                        continue;
                    }

                    IEnumerable<string> paths = kind == AutoCadPart.Records ? part.Files.Select(f => f.Path) : [part.Path];
                    foreach (string path in paths)
                    {
                        run.True(
                            manifest.Contents!.Find(path) is not null
                                || manifest.Contents!.Skipped.Any(s => s.Why == SkipCause.FileMissing && s.Path == path),
                            $"{kind} reads {path}, a file the bundle's catalog names");
                    }
                }
            });
        }

        run.Case("the full bundle is not refused, and in Civil 3D places every part, the surface among them", () =>
        {
            AutoCadImportPlan plan = Civil(Full(WithTifRectangle), DrawingZone.None);

            run.False(plan.IsRefused, $"planned ({plan.Refusal})");
            foreach (AutoCadPart kind in EveryPart)
            {
                run.True(plan.Part(kind).Place, $"{kind}: {plan.Part(kind).Sentence}");
            }

            AutoCadImportRun steps = new(plan, civil3d: true);
            run.True(
                Enumerable.Range(0, steps.Count).Any(i => steps.PartAt(i) == AutoCadPart.Surface),
                "and the import runs a Civil 3D surface step");
        });

        run.Case("a 3.0 bundle is known by its manifest at the root, and a 2.x one is not taken for it", () =>
        {
            BundleManifest three = ThreePlanTests.Read(ThreePlanTests.Load(FullBundle));
            run.True(AutoCadThreeView.IsThree(three), "Manifest.json at the root is 3.0");
            run.False(AutoCadThreeView.IsThree(null), "no reading is not 3.0");

            BundleManifest? two = BundleReader.Read(["Metadata/manifest.json"], _ => TwoManifest(CompleteBlock));
            run.False(AutoCadThreeView.IsThree(two), "Metadata/manifest.json is 2.x");
        });
    }

    private static void RunFrameCases(TestRun run)
    {
        run.Case("the plan's frame is the order's georeferenced frame: its CRS and its two units, never the local frame", () =>
        {
            AutoCadImportPlan plan = Plan(Full());

            run.Equal(plan.Frame.Type, AutoCadFileFrame.Projected, "a projected frame");
            run.Equal(plan.Frame.Crs, "EPSG:32618", "the georeferenced frame's CRS");
            run.Equal(plan.HorizontalUnit, "m", "its horizontal unit");
            run.Equal(plan.VerticalUnit, "m", "its vertical unit");
        });

        run.Case("feet: the order's CRS, US survey feet and international feet, though the local frame is in metres", () =>
        {
            AutoCadImportPlan plan = Plan(Full(WithFeetFrames));

            run.False(plan.IsRefused, $"planned ({plan.Refusal})");
            run.Equal(plan.Frame.Crs, "EPSG:2263", "the feet order's CRS");
            run.Equal(plan.HorizontalUnit, "ftUS", "horizontal: the georeferenced frame's US survey foot, not the local metre");
            run.Equal(plan.VerticalUnit, "ft", "vertical: the georeferenced frame's foot");
            run.True(plan.Notices.Any(n => n.Contains("US survey feet", StringComparison.Ordinal)), "the units notice says feet");
        });

        run.Case("DXF contours: read from Elevation/Contours.dxf, absolute, in the order's units and digest", () =>
        {
            Dictionary<string, string> files = ThreePlanTests.Load(FullBundle, WithFeetFrames);
            AutoCadImportPlan plan = Plan(files);
            PlannedPart contours = plan.Part(AutoCadPart.Contours);

            run.True(contours.Place, contours.Sentence);
            run.Equal(contours.Path, "Elevation/Contours.dxf", "the promised file, by name");
            run.Equal(contours.Sha256, Sha(files, "Elevation/Contours.dxf"), "the catalog's digest");
            run.Equal(contours.HorizontalFrame, "absolute_projected", "absolute in the order's CRS");
            run.Equal(contours.HorizontalUnits, "ftUS", "X/Y in the order's unit, not the local metre");
            run.Equal(contours.VerticalUnits, "ft", "heights in the order's vertical unit");
        });

        run.Case("DXF records: each Records/*.dxf, named by its file, in the order's units", () =>
        {
            AutoCadImportPlan plan = Plan(Full(WithFeetFrames));
            PlannedPart records = plan.Part(AutoCadPart.Records);

            run.True(records.Place, records.Sentence);
            run.Equal(string.Join(",", records.Files.Select(f => f.Name)), string.Join(",", RecordNames), "the ten layers, Habitat among them");
            foreach (PlannedFile file in records.Files)
            {
                run.Equal(file.Path, $"Records/{file.Name}.dxf", $"{file.Name}: its own file");
                run.Equal(file.Layer, AutoCadLayers.Record(file.Name), $"{file.Name}: on its own layer");
                run.Equal(file.HorizontalUnits, "ftUS", $"{file.Name}: in the order's unit, not the local metre");
                run.Equal(file.HorizontalFrame, "absolute_projected", $"{file.Name}: absolute");
            }

            run.Equal(records.Files.Single(f => f.Name == "Zoning").Layer, "MantlePlace-Records-Zoning", "Records/Zoning.dxf is the Zoning layer (A06)");
        });

        run.Case("DXF site linework: Site/Site.dxf, in the order's units", () =>
        {
            PlannedPart linework = Plan(Full(WithFeetFrames)).Part(AutoCadPart.Linework);

            run.True(linework.Place, linework.Sentence);
            run.Equal(linework.Path, "Site/Site.dxf", "the promised file");
            run.Equal(linework.HorizontalUnits, "ftUS", "in the order's unit, not the local metre");
            run.Contains(linework.Sentence, "US survey feet", "and says so");
        });

        run.Case("the drawing's geographic location is checked against the order's CRS, not the local frame's", () =>
        {
            Dictionary<string, string> files = ThreePlanTests.Load(FullBundle);
            AutoCadImportPlan same = AutoCadImportPlanner.Plan(Read(files), DrawingGeolocation.Located("UTM84-18N", 32618), null);
            AutoCadImportPlan other = AutoCadImportPlanner.Plan(Read(files), DrawingGeolocation.Located("NY83-LIF", 2263), null);

            run.False(same.IsRefused, $"a drawing in EPSG:32618 places ({same.Refusal})");
            run.True(other.IsRefused, "a drawing in another CRS refuses");
            run.Contains(other.Refusal, "EPSG:32618", "naming the order's CRS");
        });

        run.Case("in Civil 3D the drawing's zone is checked against the order's CRS", () =>
        {
            Dictionary<string, string> files = ThreePlanTests.Load(FullBundle);
            AutoCadImportPlan same = AutoCadImportPlanner.Plan(Read(files), DrawingGeolocation.None, new DrawingZone("UTM84-18N", 32618));
            AutoCadImportPlan other = AutoCadImportPlanner.Plan(Read(files), DrawingGeolocation.None, new DrawingZone("NY83-LIF", 2263));

            run.False(same.IsRefused, $"the order's zone places ({same.Refusal})");
            run.True(other.IsRefused, "another zone refuses");
        });

        run.Case("in Civil 3D the zone check names the zone it found and the order's CRS, and the surface is built under the order's zone", () =>
        {
            Dictionary<string, string> files = ThreePlanTests.Load(FullBundle);
            AutoCadImportPlan same = Civil(files, new DrawingZone("UTM84-18N", 32618));
            AutoCadImportPlan other = Civil(files, new DrawingZone("NY83-LIF", 2263));
            AutoCadImportPlan none = Civil(files, DrawingZone.None);

            run.True(
                same.Notices.Any(n => n.Contains("Civil 3D coordinate zone, UTM84-18N (EPSG:32618), is the order's", StringComparison.Ordinal)),
                "the order's zone is named, with its EPSG code: " + string.Join(" | ", same.Notices));
            run.True(same.Part(AutoCadPart.Surface).Place, "and the surface is built");
            run.Contains(other.Refusal, "NY83-LIF (EPSG:2263)", "another zone's refusal names the drawing's zone");
            run.Contains(other.Refusal, "EPSG:32618", "and the order's CRS, the georeferenced frame's");
            run.False(other.Part(AutoCadPart.Surface).Place, "and builds no surface");
            run.True(
                none.Notices.Any(n => n.Contains("no Civil 3D coordinate zone", StringComparison.Ordinal) && n.Contains("EPSG:32618", StringComparison.Ordinal)),
                "a drawing with no zone is told the order's CRS: " + string.Join(" | ", none.Notices));
        });

        run.Case("feet: in Civil 3D the zone is checked against the feet order's CRS, not the local frame's", () =>
        {
            Dictionary<string, string> files = Full(WithFeetFrames);
            AutoCadImportPlan same = Civil(files, new DrawingZone("NY83-LIF", 2263));
            AutoCadImportPlan utm = Civil(files, new DrawingZone("UTM84-18N", 32618));

            run.False(same.IsRefused, $"the feet order's zone places ({same.Refusal})");
            run.True(same.Part(AutoCadPart.Surface).Place, "and builds the surface");
            run.True(utm.IsRefused, "a UTM zone refuses the feet order");
            run.Contains(utm.Refusal, "EPSG:2263", "naming the order's CRS");
        });

        run.Case("a CRS stated with no code is refused, never taken apart for one", () =>
        {
            AutoCadImportPlan plan = Plan(Full(m =>
            {
                JsonObject crs = m["frames"]!["georeferenced"]!["horizontal_crs"]!.AsObject();
                crs.Remove("code");
                crs["wkt2"] = "PROJCRS[\"Synthetic\"]";
            }));

            run.True(plan.IsRefused, "refused");
            run.Contains(plan.Refusal, "EPSG", "says what it needs");
            run.Contains(plan.Refusal, "Nothing was imported", "and that nothing went in");
        });
    }

    private static void RunAerialCases(TestRun run)
    {
        run.Case("GeoTIFF aerial: stated in the georeferenced frame, inserted at (left, bottom), right-left by top-bottom, unrotated in the order's CRS", () =>
        {
            Dictionary<string, string> files = ThreePlanTests.Load(FullBundle, WithTifRectangle);
            PlannedPart aerial = Plan(files).Part(AutoCadPart.Aerial);

            run.True(aerial.Place, aerial.Sentence);
            run.Equal(aerial.Path, "Imagery/Imagery.tif", "the georeferenced image, not the host-ready PNG");
            run.Equal(aerial.Sha256, Sha(files, "Imagery/Imagery.tif"), "the catalog's digest");
            run.Equal(aerial.HorizontalFrame, "absolute_projected", "placed at the order's coordinates");
            run.Within(aerial.Insertion!.X, 586000, 1e-9, "the rectangle's left edge, in the order's CRS");
            run.Within(aerial.Insertion.Y, 4511000, 1e-9, "its bottom edge");
            run.Within(aerial.Insertion.Width, 1024, 1e-9, "its width, right minus left");
            run.Within(aerial.Insertion.Height, 2048, 1e-9, "its height, top minus bottom");
            run.Within(aerial.Insertion.RotationDeg, 0, 0, "aligned with the order's axes: no rotation is derived, not the local frame's 0.67 degrees");
        });

        run.Case("GeoTIFF aerial: a raster entry that states no frame is skipped by name, saying the catalog states none", () =>
        {
            PlannedPart aerial = Plan(Full(m =>
            {
                WithTifRectangle(m);
                m["catalog"]!["Imagery/Imagery.tif"]!["raster"]!.AsObject().Remove("frame");
            })).Part(AutoCadPart.Aerial);

            run.False(aerial.Place, "skipped, never placed as georeferenced");
            run.Contains(aerial.Sentence, "Imagery/Imagery.tif", "naming the file");
            run.Contains(aerial.Sentence, "states no frame", "and saying the catalog states none");
        });

        run.Case("GeoTIFF aerial: a pointer outside the georeferenced family is skipped, never inserted at the order's coordinates", () =>
        {
            AutoCadBlockRead read = WithAerial(Read(Full(WithTifRectangle)), p => p with { HorizontalFrame = "local_enu" });
            PlannedPart aerial = AutoCadImportPlanner.Plan(read, DrawingGeolocation.None, null).Part(AutoCadPart.Aerial);

            run.False(aerial.Place, "skipped");
            run.Contains(aerial.Sentence, "local_enu", "naming the frame its file states");
            run.Contains(aerial.Sentence, "does not reproject", "and that the plugin does not convert it");
        });

        run.Case("GeoTIFF aerial: the PNG's local rectangle is never the insertion", () =>
        {
            // The PNG states [-512, -512, 512, 512] in the local frame; a plan that read it would place
            // the image a few hundred metres from the drawing's origin, thousands of kilometres off site.
            PlannedPart aerial = Plan(Full(WithTifRectangle)).Part(AutoCadPart.Aerial);

            run.True(aerial.Insertion is { X: > 0, Y: > 0 }, "the insertion is in the order's CRS");
            run.False(aerial.Path.EndsWith(".png", StringComparison.Ordinal), "and the image is not the host-ready PNG");
        });

        run.Case("GeoTIFF aerial: a rectangle stated in the local frame is skipped, never placed as a projected one", () =>
        {
            PlannedPart aerial = Plan(Full(m =>
            {
                WithTifRectangle(m);
                m["catalog"]!["Imagery/Imagery.tif"]!["raster"]!["frame"] = "local";
            })).Part(AutoCadPart.Aerial);

            run.False(aerial.Place, "skipped");
            run.Contains(aerial.Sentence, "local", "naming the frame it states");
            run.Contains(aerial.Sentence, "does not reproject", "and that the plugin does not convert it");
        });

        run.Case("GeoTIFF aerial: a raster entry with no rectangle is skipped, the insertion never worked out", () =>
        {
            // The corpus's full bundle states no rectangle for Imagery.tif.
            PlannedPart aerial = Plan(Full()).Part(AutoCadPart.Aerial);

            run.False(aerial.Place, "skipped");
            run.Contains(aerial.Sentence, "publishes no insertion", "the 2.x sentence for the same absence");
        });

        run.Case("GeoTIFF aerial: in feet, the size is said in US survey feet", () =>
        {
            PlannedPart aerial = Plan(Full(m =>
            {
                WithFeetFrames(m);
                WithTifRectangle(m);
            })).Part(AutoCadPart.Aerial);

            run.True(aerial.Place, aerial.Sentence);
            run.Contains(aerial.Sentence, "US survey feet", "the order's unit");
        });
    }

    private static void RunSurfaceCases(TestRun run)
    {
        run.Case("LandXML surface: in Civil 3D, built from Elevation/Terrain.landxml, absolute in the order's CRS and units, with the catalog's digest", () =>
        {
            Dictionary<string, string> files = Full();
            PlannedPart surface = Civil(files, DrawingZone.None).Part(AutoCadPart.Surface);

            run.True(surface.Place, surface.Sentence);
            run.Equal(surface.Path, AutoCadThreeView.SurfacePath, "the promised file, by name");
            run.Equal(surface.Sha256, Sha(files, AutoCadThreeView.SurfacePath), "the catalog's digest, checked before anything is drawn");
            run.Equal(surface.Format, AutoCadImportPlanner.LandXmlFormat, "read as LandXML");
            run.Equal(surface.HorizontalFrame, "absolute_projected", "absolute in the order's CRS, never offsets about the site origin");
            run.Equal(surface.HorizontalUnits, "m", "X/Y in the order's unit");
            run.Equal(surface.VerticalUnits, "m", "heights in the order's vertical unit");
            run.Equal(surface.Layer, AutoCadLayers.Surface, "on the surface's layer");
            run.Contains(surface.Sentence, "metres", "and the sentence says the heights' unit");
        });

        run.Case("LandXML surface, feet: US survey feet across and international feet up, though the local frame is in metres", () =>
        {
            PlannedPart surface = Civil(Full(WithFeetFrames), DrawingZone.None).Part(AutoCadPart.Surface);

            run.True(surface.Place, surface.Sentence);
            run.Equal(surface.HorizontalUnits, "ftUS", "X/Y in the georeferenced frame's US survey foot, not the local metre");
            run.Equal(surface.VerticalUnits, "ft", "heights in its international foot");
            run.Contains(surface.Sentence, "international feet", "and the sentence says the heights' unit");
        });

        run.Case("LandXML surface: in plain AutoCAD it is skipped, saying it needs Civil 3D", () =>
        {
            PlannedPart surface = Plan(Full()).Part(AutoCadPart.Surface);

            run.False(surface.Place, "not built");
            run.Contains(surface.Sentence, "needs Civil 3D", "why");
        });

        run.Case("LandXML surface, frame mix-up: a pointer that states the local frame is skipped, never built at the order's coordinates", () =>
        {
            AutoCadBlockRead read = WithSurface(Read(Full()), p => p with { HorizontalFrame = "local_enu" });
            PlannedPart surface = AutoCadImportPlanner.Plan(read, DrawingGeolocation.None, DrawingZone.None).Part(AutoCadPart.Surface);

            run.False(surface.Place, "skipped");
            run.Contains(surface.Sentence, "local_enu", "naming the frame its file states");
            run.Contains(surface.Sentence, "does not reproject", "and that the plugin does not move it");
        });

        run.Case("LandXML surface, unit mix-up: a pointer in another unit than the frame's is skipped, never scaled", () =>
        {
            AutoCadBlockRead across = WithSurface(Read(Full()), p => p with { HorizontalUnits = "ftUS" });
            AutoCadBlockRead up = WithSurface(Read(Full()), p => p with { VerticalUnits = "ft" });

            foreach (AutoCadBlockRead read in new[] { across, up })
            {
                PlannedPart surface = AutoCadImportPlanner.Plan(read, DrawingGeolocation.None, DrawingZone.None).Part(AutoCadPart.Surface);
                run.False(surface.Place, "skipped");
                run.Contains(surface.Sentence, "not the frame's m", "naming the frame's unit");
                run.Contains(surface.Sentence, "does not convert", "and that the plugin does not convert");
            }
        });
    }

    /// <summary>
    /// The surface's own file checked against the frame the manifest states for it, as the Civil 3D step
    /// reads it before Civil 3D is asked to build anything: a LandXML that states another unit or another
    /// coordinate system than the order's, or states none, builds no surface, and says which (HPS-53). Its
    /// one linear unit must be both the order's horizontal and its vertical unit.
    /// </summary>
    private static void RunSurfaceFileCases(TestRun run)
    {
        const string Metres = "<Units><Metric linearUnit=\"meter\" areaUnit=\"squareMeter\" volumeUnit=\"cubicMeter\"/></Units>";
        const string UsFeet = "<Units><Imperial linearUnit=\"USSurveyFoot\" areaUnit=\"squareFoot\" volumeUnit=\"cubicYard\"/></Units>";
        const string Feet = "<Units><Imperial linearUnit=\"foot\" areaUnit=\"squareFoot\" volumeUnit=\"cubicYard\"/></Units>";

        AutoCadImportPlan metric = Civil(Full(), DrawingZone.None);
        AutoCadImportPlan feet = Civil(Full(WithFeetFrames), DrawingZone.None);

        run.Case("LandXML file in the order's unit and CRS: its one surface is named for Civil 3D", () =>
        {
            string? problem = Check(metric, LandXml(Metres, "32618"), out string? name);

            run.Equal(problem, null, "no problem");
            run.Equal(name, "Terrain", "the file's surface, by its own name");
        });

        run.Case("LandXML file stating no linear unit builds no surface: an unstated unit is never assumed to match", () =>
        {
            string? problem = Check(metric, LandXml(null, "32618"), out string? name);

            run.Equal(name, null, "nothing to build");
            run.Contains(problem ?? string.Empty, "states no linear unit", "the sentence says the unit is not stated");
            run.Contains(problem ?? string.Empty, "metres", "the order's unit");
        });

        run.Case("LandXML file with no CoordinateSystem builds no surface: an unstated CRS is never assumed to match", () =>
        {
            string? problem = Check(metric, LandXml(Metres, null), out string? name);

            run.Equal(name, null, "nothing to build");
            run.Contains(problem ?? string.Empty, "states no EPSG code", "the sentence says the CRS is not stated");
            run.Contains(problem ?? string.Empty, "EPSG:32618", "the order's CRS");
        });

        run.Case("LandXML file whose CoordinateSystem has a name and no epsgCode builds no surface", () =>
        {
            string named = LandXmlWith(Metres, "<CoordinateSystem horizontalCoordinateSystemName=\"UTM84-18N\"/>");
            string? problem = Check(metric, named, out string? name);

            run.Equal(name, null, "nothing to build");
            run.Contains(problem ?? string.Empty, "states no EPSG code", "the sentence says the CRS is not stated as a code");
            run.Contains(problem ?? string.Empty, "EPSG:32618", "the order's CRS");
        });

        run.Case("LandXML file stating neither unit nor CRS builds no surface, the unit named first", () =>
        {
            string? problem = Check(metric, LandXml(null, null), out string? name);

            run.Equal(name, null, "nothing to build");
            run.Contains(problem ?? string.Empty, "states no linear unit", "the unit is not stated");
        });

        run.Case("LandXML file, unit mix-up: US survey feet in a metre order builds no surface, naming both units", () =>
        {
            string? problem = Check(metric, LandXml(UsFeet, "32618"), out string? name);

            run.Equal(name, null, "nothing to build");
            run.Contains(problem ?? string.Empty, "US survey feet", "the file's unit");
            run.Contains(problem ?? string.Empty, "metres", "the order's unit");
            run.Contains(problem ?? string.Empty, "does not convert", "and that the plugin does not convert");
        });

        run.Case("LandXML file, a frame of two units (X/Y US survey feet, heights feet): whichever one unit the file states, no surface", () =>
        {
            foreach (string units in new[] { UsFeet, Feet })
            {
                string? problem = Check(feet, LandXml(units, "2263"), out string? name);

                run.Equal(name, null, $"{units}: nothing to build");
                run.Contains(problem ?? string.Empty, units == UsFeet ? "US survey feet" : "international feet", $"{units}: the file's unit");
                run.Contains(problem ?? string.Empty, "coordinates in US survey feet", $"{units}: the order's X/Y unit");
                run.Contains(problem ?? string.Empty, "heights in international feet", $"{units}: the order's height unit");
                run.Contains(problem ?? string.Empty, "does not convert", $"{units}: and that the plugin does not convert");
            }
        });

        run.Case("LandXML file, a feet frame whose two units agree with the file: its one surface is named for Civil 3D", () =>
        {
            AutoCadImportPlan usFeet = Civil(Full(WithUsFeetHeights), DrawingZone.None);
            run.False(usFeet.IsRefused, $"planned ({usFeet.Refusal})");
            run.Equal(usFeet.VerticalUnit, "ftUS", "heights in US survey feet, as X/Y");

            run.Equal(Check(usFeet, LandXml(UsFeet, "2263"), out string? name), null, "the order's own US survey foot, X/Y and heights");
            run.Equal(name, "Terrain", "named");

            string? problem = Check(usFeet, LandXml(Feet, "2263"), out string? none);
            run.Equal(none, null, "the international foot in a US-survey-foot order: nothing to build");
            run.Contains(problem ?? string.Empty, "international feet", "the file's unit");
            run.Contains(problem ?? string.Empty, "US survey feet", "the order's unit");
        });

        run.Case("LandXML file, a linear unit this plugin does not know builds no surface, naming the value", () =>
        {
            string? problem = Check(metric, LandXml("<Units><Metric linearUnit=\"millimeter\"/></Units>", "32618"), out string? name);

            run.Equal(name, null, "nothing to build");
            run.Contains(problem ?? string.Empty, "millimeter", "the value it states");
        });

        run.Case("LandXML file, frame mix-up: another coordinate system than the order's builds no surface, naming both", () =>
        {
            string? problem = Check(metric, LandXml(Metres, "2263"), out string? name);

            run.Equal(name, null, "nothing to build");
            run.Contains(problem ?? string.Empty, "EPSG:2263", "the file's CRS");
            run.Contains(problem ?? string.Empty, "EPSG:32618", "the order's CRS");
            run.Contains(problem ?? string.Empty, "does not reproject", "and that the plugin does not reproject");
        });

        run.Case("LandXML file, a coordinate system code that is not one EPSG code builds no surface, naming the value", () =>
        {
            foreach (string code in new[] { "UTM84-18N", "32618+3855" })
            {
                string? problem = Check(metric, LandXml(Metres, code), out string? name);

                run.Equal(name, null, $"{code}: nothing to build");
                run.Contains(problem ?? string.Empty, code, $"{code}: the value it states");
            }
        });

        run.Case("LandXML file of a 2.x order: read as before, its units and CRS not checked against the block", () =>
        {
            string text = TwoManifest(CompleteBlock);
            AutoCadImportPlan two = AutoCadImportPlanner.Plan(text, DrawingGeolocation.None, DrawingZone.None);

            run.Equal(Check(two, LandXml(UsFeet, "2263"), out string? name), null, "a 2.x surface's file is named as 1.0 named it");
            run.Equal(name, "Terrain", "named");
        });
    }

    private static void RunAbsenceCases(TestRun run)
    {
        run.Case("LandXML surface not in the catalog, with no reason: skipped saying it is not in this bundle, and the rest imports", () =>
        {
            AutoCadImportPlan plan = Civil(Full(m => m["catalog"]!.AsObject().Remove(AutoCadThreeView.SurfacePath)), DrawingZone.None);
            PlannedPart surface = plan.Part(AutoCadPart.Surface);

            run.False(plan.IsRefused, $"not refused ({plan.Refusal})");
            run.False(surface.Place, "no surface is built");
            run.Contains(surface.Sentence, "Civil 3D surface: not in this bundle", "says the surface is not in this bundle");
            run.Contains(surface.Sentence, AutoCadThreeView.SurfacePath, "naming the file");
            run.Contains(surface.Sentence, "no reason", "and that the manifest gives none");
            run.True(plan.Part(AutoCadPart.Contours).Place, "the contours still go in");
            run.True(plan.Part(AutoCadPart.Records).Place, "and the public-record layers");
            run.True(plan.Part(AutoCadPart.Linework).Place, "and the site linework");
        });

        run.Case("LandXML surface listed as not held: skipped with the manifest's reason, verbatim, in Civil 3D and in AutoCAD, and the rest imports", () =>
        {
            Dictionary<string, string> files = Full(m => NotHeld(m, AutoCadThreeView.SurfacePath, "terrain_not_built_for_this_aoi"));
            foreach (AutoCadImportPlan plan in new[] { Civil(files, DrawingZone.None), Plan(files) })
            {
                PlannedPart surface = plan.Part(AutoCadPart.Surface);

                run.False(surface.Place, "no surface is built");
                run.Contains(surface.Sentence, "Civil 3D surface: not in this bundle", "says the surface is not in this bundle");
                run.Contains(surface.Sentence, AutoCadThreeView.SurfacePath, "naming the file");
                run.Contains(surface.Sentence, "terrain_not_built_for_this_aoi", "with the manifest's reason, verbatim");
                run.True(plan.Part(AutoCadPart.Contours).Place, "the contours still go in");
                run.True(plan.Part(AutoCadPart.Linework).Place, "and the site linework");
            }
        });

        run.Case("a part whose file the manifest lists as not held is skipped, naming the file and the manifest's reason", () =>
        {
            PlannedPart contours = Plan(Full(m => NotHeld(m, "Elevation/Contours.dxf", "no_contours_here"))).Part(AutoCadPart.Contours);

            run.False(contours.Place, "skipped");
            run.Contains(contours.Sentence, "Elevation/Contours.dxf", "names the file");
            run.Contains(contours.Sentence, "no_contours_here", "with the manifest's reason, verbatim");
        });

        run.Case("a part whose file the catalog does not name, and the manifest gives no reason for, says so", () =>
        {
            PlannedPart linework = Plan(Full(m => m["catalog"]!.AsObject().Remove("Site/Site.dxf"))).Part(AutoCadPart.Linework);

            run.False(linework.Place, "skipped");
            run.Contains(linework.Sentence, "Site/Site.dxf", "names the file");
            run.Contains(linework.Sentence, "no reason", "and that the manifest gives none");
        });

        run.Case("a file the catalog names and the bundle lacks is still planned, so the import says it is missing", () =>
        {
            Dictionary<string, string> files = ThreePlanTests.Load(FullBundle);
            files.Remove("Elevation/Contours.dxf");
            PlannedPart contours = Plan(files).Part(AutoCadPart.Contours);

            run.True(contours.Place, "planned from the catalog's name");
            run.Equal(contours.Path, "Elevation/Contours.dxf", "and the run's file check names it");
        });

        run.Case("a record layer not held is left out by name and reason, and the others are placed", () =>
        {
            PlannedPart records = Plan(Full(m => NotHeld(m, "Records/Habitat.dxf", "no_data"))).Part(AutoCadPart.Records);

            run.True(records.Place, records.Sentence);
            run.False(records.Files.Any(f => f.Name == "Habitat"), "Habitat is not among the layers placed");
            run.Equal(records.Files.Count, RecordNames.Length - 1, "the other nine are");
            run.Contains(records.Sentence, "Habitat", "the sentence names the layer left out");
            run.Contains(records.Sentence, "no_data", "with the manifest's reason");
        });

        run.Case("the corpus's records-not-held bundle plans what it holds and names what it does not", () =>
        {
            BundleManifest manifest = ThreePlanTests.Read(ThreePlanTests.Load("bundle/v3-records-not-held.json"));
            PlannedPart records = AutoCadImportPlanner.Plan(AutoCadBlockReader.Read(manifest, null), DrawingGeolocation.None, null).Part(AutoCadPart.Records);

            run.Contains(records.Sentence, "Conservation", "the layer not held is named");
            run.False(records.Files.Any(f => f.Name == "Conservation"), "and not placed");
        });

        run.Case("no record layer held: the part is skipped, each layer named with its reason", () =>
        {
            PlannedPart records = Plan(Full(m =>
            {
                foreach (string name in RecordNames)
                {
                    NotHeld(m, $"Records/{name}.dxf", "no_data");
                }
            })).Part(AutoCadPart.Records);

            run.False(records.Place, "skipped");
            run.Contains(records.Sentence, "Zoning", "every layer named");
            run.Contains(records.Sentence, "no_data", "with its reason");
        });

        run.Case("a record layer the catalog names is placed though a 3.0 download does not ask for it by name", () =>
        {
            // Habitat is not among the files the vault's description promises, so the download does not
            // name it; a bundle that holds it anyway is placed whole.
            run.False(AutoCadImportPlan.ThreeFiles.Contains("Records/Habitat.dxf"), "not asked for");
            run.True(Plan(Full()).Part(AutoCadPart.Records).Files.Any(f => f.Name == "Habitat"), "and placed when held");
        });

        run.Case("every record layer a 3.0 download asks for is one the plan places from its own file", () =>
        {
            foreach (string path in AutoCadImportPlan.ThreeFiles.Where(p => p.StartsWith(AutoCadThreeView.RecordsFolder, StringComparison.Ordinal)))
            {
                run.True(RecordNames.Contains(Path.GetFileNameWithoutExtension(path)), $"{path} is a layer the full bundle places");
            }
        });
    }

    private static void RunChooserCases(TestRun run)
    {
        foreach (string fixture in new[] { CompleteBlock, RecordsBlock })
        {
            run.Case($"2.x ({fixture}): the chooser plans exactly what the 2.x reader plans from the same manifest", () =>
            {
                string text = TwoManifest(fixture);
                BundleManifest? read = BundleReader.Read(["Metadata/manifest.json"], _ => text);
                AutoCadImportPlan chosen = AutoCadImportPlanner.Plan(AutoCadBlockReader.Read(read, text), DrawingGeolocation.None, null);
                AutoCadImportPlan before = AutoCadImportPlanner.Plan(text, DrawingGeolocation.None, null);

                run.False(AutoCadThreeView.IsThree(read), "read as 2.x");
                run.Equal(chosen.Summary(), before.Summary(), "the same summary, word for word");
                foreach (AutoCadPart kind in EveryPart)
                {
                    run.Equal(chosen.Part(kind).Place, before.Part(kind).Place, $"{kind}: placed alike");
                    run.Equal(chosen.Part(kind).Path, before.Part(kind).Path, $"{kind}: the same file");
                }
            });
        }

        run.Case("2.x: a GeoTIFF aerial pointer is still a format the 2.x plan does not know", () =>
        {
            JsonObject manifest = JsonNode.Parse(TwoManifest(CompleteBlock))!.AsObject();
            manifest["hosts"]!["autocad"]!["aerial"]!["format"] = AutoCadThreeView.GeoTiffFormat;
            PlannedPart aerial = AutoCadImportPlanner.Plan(manifest.ToJsonString(), DrawingGeolocation.None, null).Part(AutoCadPart.Aerial);

            run.False(aerial.Place, "a 2.x block keeps its one aerial format");
        });

        run.Case("a zip with neither manifest is not a bundle, and the refusal names both places a manifest goes", () =>
        {
            run.Contains(LocalBundleImport.NotABundle, "Manifest.json", "the 3.0 manifest at the root");
            run.Contains(LocalBundleImport.NotABundle, "Metadata/manifest.json", "the 2.x manifest");
            run.Contains(LocalBundleImport.NotABundle, "Nothing was imported", "and that nothing went in");
        });

        run.Case("a 3.0 zip on disk opens as a bundle, and plans from its catalog", () =>
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "autocad-three-zip-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(sandbox);
            try
            {
                string zip = Path.Combine(sandbox, "three.zip");
                using (ZipArchive archive = ZipFile.Open(zip, ZipArchiveMode.Create))
                {
                    foreach ((string name, string text) in ThreePlanTests.Load(FullBundle, WithTifRectangle))
                    {
                        using Stream stream = archive.CreateEntry(name).Open();
                        byte[] bytes = Encoding.UTF8.GetBytes(text);
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }

                bool opened = LocalBundleImport.TryOpen(zip, out LocalBundleArchive? bundle, out string? refusal);
                using (bundle)
                {
                    run.True(opened, $"opened ({refusal})");
                    AutoCadImportPlan plan = AutoCadImportPlanner.Plan(
                        AutoCadBlockReader.Read(bundle?.Manifest, bundle?.ManifestText),
                        DrawingGeolocation.None,
                        null);
                    run.False(plan.IsRefused, $"planned ({plan.Refusal})");
                    run.True(plan.Part(AutoCadPart.Contours).Place, "from the zip's own catalog");
                }
            }
            finally
            {
                try
                {
                    Directory.Delete(sandbox, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        });

        run.Case("this plugin imports and announces 3.x, and reads no major above it", () =>
        {
            run.Equal(HostIdentity.NewestImportedMajor, ManifestVersions.AutoCadNewestImportedMajor, "the shell's major, as the suite sets it");
            run.Equal(ManifestVersions.AutoCadNewestImportedMajor, 3, "3.x");
            run.True(ManifestVersions.AutoCadNewestImportedMajor <= ManifestVersions.NewestReadMajor, "no major the root reader refuses");
            run.Equal(ConnectedHosts.Announce("25.0", "1.2.0", null, "DESK").ReadsMaxMajor, 3, "the web is told 3.x");
            run.True(ExportBundles.CanRead("3.0.0"), "a 3.0 send is taken");
            run.False(ExportBundles.CanRead("4.0.0"), "a 4.0 one is not");
            run.True(ExportBundles.CanRead("2.4.0"), "and 2.x still is");
        });
    }

    private static void RunReleaseNotesCases(TestRun run)
    {
        // RepoTree finds folders: the notes' folder, holding them.
        string? notes = RepoTree.Find(Path.GetDirectoryName(ReleaseNotes)!.Replace('\\', '/'), dir => File.Exists(Path.Combine(dir, Path.GetFileName(ReleaseNotes)))) is { } folder
            ? File.ReadAllText(Path.Combine(folder, Path.GetFileName(ReleaseNotes)))
            : null;
        run.Case("the AutoCAD 1.2.0 release notes are written", () => run.True(notes is not null, ReleaseNotes));
        if (notes is null)
        {
            return;
        }

        run.Case("the release notes name every file the 3.0 plan reads", () =>
        {
            foreach (string path in new[] { AutoCadThreeView.ContoursPath, AutoCadThreeView.SurfacePath, AutoCadThreeView.AerialPath, AutoCadThreeView.LineworkPath, "Records/" })
            {
                run.Contains(notes, path, $"{path} is in the notes");
            }
        });

        run.Case("the release notes list each item a 3.0 import leaves out, and no longer the Civil 3D surface", () =>
        {
            run.Contains(notes, "## Left out of an MPB 3.0 import", "under the heading the Revit notes use");
            run.False(notes.Contains("builds no surface from an MPB 3.0 bundle", StringComparison.Ordinal), "the surface is built from a 3.0 bundle now");
            run.Contains(notes, "<CoordinateSystem>", "and the notes say what the surface's file is checked against");
        });
    }

    // --- helpers ---

    private static AutoCadImportPlan Plan(Dictionary<string, string> files) =>
        AutoCadImportPlanner.Plan(Read(files), DrawingGeolocation.None, null);

    /// <summary>The plan in Civil 3D, the part on, under the drawing's <paramref name="zone"/>.</summary>
    private static AutoCadImportPlan Civil(Dictionary<string, string> files, DrawingZone zone) =>
        AutoCadImportPlanner.Plan(Read(files), DrawingGeolocation.None, zone);

    private static AutoCadBlockRead Read(Dictionary<string, string> files) =>
        AutoCadBlockReader.Read(ThreePlanTests.Read(files), manifestText: null);

    private static Dictionary<string, string> Full(Action<JsonObject>? edit = null) => ThreePlanTests.Load(FullBundle, edit);

    /// <summary>The surface step's check of its file against <paramref name="plan"/>: the problem, or <c>null</c> and the surface's name.</summary>
    private static string? Check(AutoCadImportPlan plan, string landXml, out string? name)
    {
        using StringReader reader = new(landXml);
        return LandXmlSurface.TryReadName(reader, plan, out name);
    }

    /// <summary>A LandXML file of one surface named <c>Terrain</c>, with the <c>Units</c> element and the EPSG code given, or neither.</summary>
    private static string LandXml(string? units, string? epsg) =>
        LandXmlWith(units, epsg is null ? null : $"<CoordinateSystem epsgCode=\"{epsg}\" horizontalCoordinateSystemName=\"Synthetic\"/>");

    /// <summary>A LandXML file of one surface named <c>Terrain</c>, with the <c>Units</c> and <c>CoordinateSystem</c> elements given as written, or neither.</summary>
    private static string LandXmlWith(string? units, string? coordinateSystem) =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?><LandXML xmlns=\"http://www.landxml.org/schema/LandXML-1.2\" version=\"1.2\">"
        + (units ?? string.Empty)
        + (coordinateSystem ?? string.Empty)
        + "<Surfaces><Surface name=\"Terrain\"><Definition surfType=\"TIN\"><Pnts><P id=\"1\">4512000 586500 20</P></Pnts></Definition></Surface></Surfaces></LandXML>";

    /// <summary>
    /// The same reading with its surface pointer edited: a frame or unit a 3.0 catalog cannot state for
    /// <c>Elevation/Terrain.landxml</c>, whose extension fixes its family, so only the plan's own check stands.
    /// </summary>
    private static AutoCadBlockRead WithSurface(AutoCadBlockRead read, Func<AutoCadPointer, AutoCadPointer> edit) =>
        WithPointers(read, aerial: p => p, surface: edit);

    /// <summary>
    /// The same reading with its aerial pointer edited: a frame a 3.0 catalog cannot state for
    /// <c>Imagery/Imagery.tif</c>, whose extension fixes its family, so only the plan's own check stands.
    /// </summary>
    private static AutoCadBlockRead WithAerial(AutoCadBlockRead read, Func<AutoCadPointer, AutoCadPointer> edit) =>
        WithPointers(read, aerial: edit, surface: p => p);

    private static AutoCadBlockRead WithPointers(AutoCadBlockRead read, Func<AutoCadPointer, AutoCadPointer> aerial, Func<AutoCadPointer, AutoCadPointer> surface)
    {
        AutoCadBlock block = read.Block!;
        return new AutoCadBlockRead
        {
            Manifest = read.Manifest,
            Refusal = read.Refusal,
            Block = new AutoCadBlock
            {
                FileFrame = block.FileFrame,
                Contours = block.Contours,
                Surface = block.Surface is { } s ? surface(s) : null,
                Aerial = block.Aerial is { } a ? aerial(a) : null,
                Records = block.Records,
                Linework = block.Linework,
                ContoursReadiness = block.ContoursReadiness,
                SurfaceReadiness = block.SurfaceReadiness,
                AerialReadiness = block.AerialReadiness,
                RecordsReadiness = block.RecordsReadiness,
                LineworkReadiness = block.LineworkReadiness,
                FromThree = block.FromThree,
                LeftOut = block.LeftOut,
                RecordsLeftOut = block.RecordsLeftOut,
            },
        };
    }

    private static string Sha(Dictionary<string, string> files, string path) =>
        ThreePlanTests.Manifest(files)["catalog"]![path]!["sha256"]!.GetValue<string>();

    /// <summary>The rectangle Imagery.tif covers in the order's CRS, which the corpus bundle does not state.</summary>
    private static void WithTifRectangle(JsonObject manifest) =>
        manifest["catalog"]!["Imagery/Imagery.tif"]!["raster"]!["rectangle"] = new JsonArray(586000.0, 4511000.0, 587024.0, 4513048.0);

    /// <summary>The feet bundle's two frames in place of the full bundle's: the order in US survey feet, the local frame in metres.</summary>
    private static void WithFeetFrames(JsonObject manifest)
    {
        JsonObject feet = ThreePlanTests.Manifest(ThreePlanTests.Load(FeetBundle));
        manifest["frames"] = feet["frames"]!.DeepClone();
    }

    /// <summary>The feet frames with the order's heights in US survey feet too, so its two units agree.</summary>
    private static void WithUsFeetHeights(JsonObject manifest)
    {
        WithFeetFrames(manifest);
        manifest["frames"]!["georeferenced"]!["vertical_unit"] = new JsonObject { ["name"] = "US survey foot", ["to_metres"] = 0.3048006096012192 };
    }

    /// <summary>Takes <paramref name="path"/> out of the catalog and lists it as not held, for <paramref name="reason"/>.</summary>
    private static void NotHeld(JsonObject manifest, string path, string reason)
    {
        manifest["catalog"]!.AsObject().Remove(path);
        manifest["not_held"]!.AsArray().Add(new JsonObject { ["path"] = path, ["reason"] = reason });
    }

    /// <summary>A 2.x corpus fixture's manifest, as text.</summary>
    private static string TwoManifest(string fixture)
    {
        string root = ConformanceCorpus.FindCorpusDirectory() ?? throw new InvalidOperationException("the corpus is not found");
        return File.ReadAllText(Path.Combine(root, "manifest", fixture));
    }
}
