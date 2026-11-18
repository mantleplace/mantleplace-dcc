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

        run.Case("the full bundle is not refused, and places every part but the surface", () =>
        {
            AutoCadImportPlan plan = Plan(Full(WithTifRectangle));

            run.False(plan.IsRefused, $"planned ({plan.Refusal})");
            run.True(plan.Part(AutoCadPart.Contours).Place, "contours: " + plan.Part(AutoCadPart.Contours).Sentence);
            run.True(plan.Part(AutoCadPart.Aerial).Place, "aerial: " + plan.Part(AutoCadPart.Aerial).Sentence);
            run.True(plan.Part(AutoCadPart.Records).Place, "records: " + plan.Part(AutoCadPart.Records).Sentence);
            run.True(plan.Part(AutoCadPart.Linework).Place, "linework: " + plan.Part(AutoCadPart.Linework).Sentence);
            run.False(plan.Part(AutoCadPart.Surface).Place, "the surface is left out");
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
        run.Case("GeoTIFF aerial: Imagery/Imagery.tif at the rectangle it covers in the order's CRS, north-up", () =>
        {
            Dictionary<string, string> files = ThreePlanTests.Load(FullBundle, WithTifRectangle);
            PlannedPart aerial = Plan(files).Part(AutoCadPart.Aerial);

            run.True(aerial.Place, aerial.Sentence);
            run.Equal(aerial.Path, "Imagery/Imagery.tif", "the georeferenced image, not the host-ready PNG");
            run.Equal(aerial.Sha256, Sha(files, "Imagery/Imagery.tif"), "the catalog's digest");
            run.Within(aerial.Insertion!.X, 586000, 1e-9, "the rectangle's west edge, in the order's CRS");
            run.Within(aerial.Insertion.Y, 4511000, 1e-9, "its south edge");
            run.Within(aerial.Insertion.Width, 1024, 1e-9, "its width, east minus west");
            run.Within(aerial.Insertion.Height, 2048, 1e-9, "its height, north minus south");
            run.Within(aerial.Insertion.RotationDeg, 0, 0, "north-up on the order's grid (A14), not the local frame's 0.67 degrees");
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

    private static void RunAbsenceCases(TestRun run)
    {
        run.Case("LandXML surface: skipped for 3.0 with a reason, in AutoCAD and in Civil 3D", () =>
        {
            Dictionary<string, string> files = ThreePlanTests.Load(FullBundle);
            PlannedPart plain = AutoCadImportPlanner.Plan(Read(files), DrawingGeolocation.None, null).Part(AutoCadPart.Surface);
            PlannedPart civil = AutoCadImportPlanner.Plan(Read(files), DrawingGeolocation.None, DrawingZone.None).Part(AutoCadPart.Surface);

            foreach (PlannedPart surface in new[] { plain, civil })
            {
                run.False(surface.Place, "not built");
                run.Contains(surface.Sentence, "Elevation/Terrain.landxml", "names the file it would read");
                run.Contains(surface.Sentence, "3.0", "and says why: the 3.0 surface is not built yet");
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
            string sandbox = Path.Combine(Path.GetTempPath(), "mantleplace-acad-three-" + Guid.NewGuid().ToString("N")[..8]);
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
            run.Equal(HostIdentity.NewestImportedMajor, AutoCadImportPlan.NewestImportedMajor, "the shell's major, as the suite sets it");
            run.Equal(AutoCadImportPlan.NewestImportedMajor, 3, "3.x");
            run.Equal(ConnectedHosts.Announce("25.0", "1.2.0", null, "DESK").ReadsMaxMajor, 3, "the web is told 3.x");
            run.True(ExportBundles.CanRead("3.0.0"), "a 3.0 send is taken");
            run.False(ExportBundles.CanRead("4.0.0"), "a 4.0 one is not");
            run.True(ExportBundles.CanRead("2.4.0"), "and 2.x still is");
        });

        run.Case("a host can name no major its reader does not read, and naming one fails before anything changes", () =>
        {
            run.Throws<ArgumentOutOfRangeException>(
                () => HostIdentity.Set(AutoCadBlockReader.HostKey, HostIdentity.PluginVersion, ManifestVersions.NewestReadMajor + 1),
                "a major above the reader's");
            run.Throws<ArgumentOutOfRangeException>(
                () => HostIdentity.Set(AutoCadBlockReader.HostKey, HostIdentity.PluginVersion, ManifestVersions.NewestSupportedMajor - 1),
                "a major below the 2.x import's");
            run.Equal(HostIdentity.NewestImportedMajor, AutoCadImportPlan.NewestImportedMajor, "and the identity set is unchanged");
        });

        run.Case("the shell names its newest major where it names its host", () =>
        {
            string? folder = RepoTree.Find("autocad/src/MantlePlace.AutoCad.Addin");
            run.True(folder is not null, "the shell's source is found");
            if (folder is not null)
            {
                run.Contains(File.ReadAllText(Path.Combine(folder, "HostRegistration.cs")), "AutoCadImportPlan.NewestImportedMajor", "HostRegistration passes it");
            }
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
            foreach (string path in new[] { AutoCadThreeView.ContoursPath, AutoCadThreeView.AerialPath, AutoCadThreeView.LineworkPath, "Records/" })
            {
                run.Contains(notes, path, $"{path} is in the notes");
            }
        });

        run.Case("the release notes list each item a 3.0 import leaves out", () =>
        {
            run.Contains(notes, AutoCadThreeView.SurfacePath, "the Civil 3D surface's file");
            run.Contains(notes, "## Left out of an MPB 3.0 import", "under the heading the Revit notes use");
        });
    }

    // --- helpers ---

    private static AutoCadImportPlan Plan(Dictionary<string, string> files) =>
        AutoCadImportPlanner.Plan(Read(files), DrawingGeolocation.None, null);

    private static AutoCadBlockRead Read(Dictionary<string, string> files) =>
        AutoCadBlockReader.Read(ThreePlanTests.Read(files), manifestText: null);

    private static Dictionary<string, string> Full(Action<JsonObject>? edit = null) => ThreePlanTests.Load(FullBundle, edit);

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
