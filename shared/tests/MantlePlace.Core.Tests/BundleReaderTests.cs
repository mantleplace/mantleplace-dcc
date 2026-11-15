using System.Text.Json;
using System.Text.Json.Nodes;

namespace MantlePlace.Core.Tests;

using CorpusCase = ConformanceCorpus.CorpusCase;

/// <summary>
/// <see cref="BundleReader"/> beyond the corpus's answer sheet: which path a manifest takes, the
/// refusals a 3.x reading makes, that no 3.x path reads a per-host block, and that a 2.x bundle reads
/// exactly as <see cref="BundleManifestReader.Parse"/> reads it.
/// </summary>
internal static class BundleReaderTests
{
    /// <summary>Where the 3.x reader's source is, which must never name a per-host block.</summary>
    private const string ThreeReaderFolder = "shared/src/MantlePlace.Core";

    private const string ThreeReaderFile = "ManifestThreeReader.cs";

    internal static int Run()
    {
        TestRun run = new();

        RunPathCases(run);
        RunThreeCases(run);
        RunHostBlockCases(run);
        RunTwoCases(run);

        return run.Report("bundle reader");
    }

    private static void RunPathCases(TestRun run)
    {
        run.Case("a zip with neither manifest is not a bundle", () =>
        {
            BundleManifest? manifest = Read(new Dictionary<string, string> { ["Readme.md"] = "hello" });
            run.True(manifest is null, "no manifest, no result");
        });

        run.Case("a manifest at the root is read as 3.x", () =>
        {
            BundleManifest? manifest = Read(Three());
            run.True(manifest?.IsValid == true, $"read (refused with: {manifest?.Error})");
            run.Equal(manifest?.Contents?.ManifestPath, "Manifest.json", "found at the root");
            run.Equal(manifest?.Contents?.Major ?? 0, 3, "major");
        });

        run.Case("a root manifest wins over a 2.x one beside it", () =>
        {
            Dictionary<string, string> files = Three();
            files["Metadata/manifest.json"] = "{\"version\": \"2.0.0\"}";
            run.Equal(Read(files)?.Contents?.ManifestPath, "Manifest.json", "the root is looked at first");
        });

        run.Case("the root manifest is found regardless of case and inside one wrapper folder", () =>
        {
            Dictionary<string, string> files = Three()
                .ToDictionary(pair => "download/" + pair.Key.ToLowerInvariant(), pair => pair.Value);
            BundleManifest? manifest = Read(files);
            run.True(manifest?.IsValid == true, $"read (refused with: {manifest?.Error})");
            run.True(
                manifest?.Contents?.Files.Any(file => file.Path == "Elevation/Elevation.tif") == true,
                "a catalog name is reported as the catalog spells it");
        });
    }

    private static void RunThreeCases(TestRun run)
    {
        run.Case("a major above 3 is refused with the update message, before the catalog is read", () =>
        {
            BundleManifest? manifest = Read(Three(version: "4.0.0", catalog: "not a catalog"));
            run.False(manifest?.IsValid ?? true, "refused");
            run.Contains(manifest?.Error, "Bundle manifest version 4.0.0 is newer than this plugin understands", "the update refusal");
            run.Contains(manifest?.Error, "1.x to 3.x", "says which majors it reads");
            run.Contains(manifest?.Error, "Update the Mantle Place plugin", "the remedy");
            run.Equal(manifest?.Contents?.Refusal ?? BundleRefusal.None, BundleRefusal.MajorAhead, "major-ahead");
            run.Equal(manifest?.Contents?.Files.Count ?? -1, 0, "no file found");
        });

        run.Case("a root manifest of a major before 3 is refused: that manifest lives in Metadata/", () =>
        {
            BundleManifest? manifest = Read(Three(version: "2.2.0"));
            run.False(manifest?.IsValid ?? true, "refused");
            run.Contains(manifest?.Error, "Metadata/manifest.json", "names where a 2.x manifest is");
            run.Equal(manifest?.Contents?.Refusal ?? BundleRefusal.None, BundleRefusal.Unreadable, "not a major-ahead refusal");
        });

        run.Case("a root manifest that is not a version is refused, naming what it says", () =>
        {
            BundleManifest? manifest = Read(Three(version: "3.0"));
            run.False(manifest?.IsValid ?? true, "refused");
            run.Contains(manifest?.Error, "3.0", "the value, verbatim");
        });

        run.Case("a root manifest that is not JSON is refused", () =>
        {
            BundleManifest? manifest = Read(new Dictionary<string, string> { ["Manifest.json"] = "{ nope" });
            run.False(manifest?.IsValid ?? true, "refused");
            run.Contains(manifest?.Error, "not valid JSON", "says why");
        });

        run.Case("a level the reader does not know is refused, naming it (fail closed)", () =>
        {
            BundleManifest? manifest = Read(Three(level: "\"Ultra\""));
            run.False(manifest?.IsValid ?? true, "refused");
            run.Contains(manifest?.Error, "Ultra", "names the value");

            BundleManifest? none = Read(Three(level: null));
            run.False(none?.IsValid ?? true, "a manifest that states no level is refused too");
        });

        run.Case("a manifest with no catalog is refused", () =>
        {
            BundleManifest? manifest = Read(Three(catalog: null));
            run.False(manifest?.IsValid ?? true, "refused");
            run.Contains(manifest?.Error, "catalog", "says what is missing");
        });

        run.Case("a catalog path that leaves the bundle is refused, naming it", () =>
        {
            BundleManifest? manifest = Read(Three(extraEntry: "../Escape.tif"));
            run.False(manifest?.IsValid ?? true, "refused");
            run.Contains(manifest?.Error, "../Escape.tif", "names the path");
        });

        run.Case("a found file carries its digest and its terms from the catalog", () =>
        {
            BundleManifest? manifest = Read(Three());
            BundleFile? tif = manifest?.Contents?.Find("elevation/elevation.TIF");
            run.Equal(tif?.Path, "Elevation/Elevation.tif", "found regardless of case, reported as catalogued");
            run.Equal(tif?.Sha256, new string('a', 64), "the digest a step verifies against");
            run.True(tif?.Count == 4_294_967_296L, "a count past a 32-bit integer is kept whole");
            run.Equal(tif?.Frame ?? FileCoordinates.None, FileCoordinates.Order, "georeferenced, in the order's frame");
            run.True(
                manifest?.FileEntries.TryGetValue("Elevation/Elevation.tif", out FileLicensing? terms) == true
                    && terms.License == "LicenseRef-MantlePlace-Terms",
                "the file's terms are read as 2.x reads them");
            run.True(manifest?.Contents?.Find("Records/Zoning.gpkg") is null, "a name the catalog lacks is not in this bundle");
        });

        run.Case("a host-ready file is in the site frame, in metres", () =>
        {
            BundleFile? glb = Read(Three())?.Contents?.Find("Elevation/Terrain.glb");
            run.Equal(glb?.Family ?? FileFamily.None, FileFamily.HostReady, "host-ready by its extension");
            run.Equal(glb?.Frame ?? FileCoordinates.None, FileCoordinates.Site, "the local site frame");
            run.Equal(glb?.Unit ?? LinearUnit.Unspecified, LinearUnit.Metre, "metres, on every order");
        });
    }

    private static void RunHostBlockCases(TestRun run)
    {
        run.Case("a 3.x manifest's per-host block is never read", () =>
        {
            BundleManifest? plain = Read(Three());
            BundleManifest? withBlock = Read(Three(hosts: HostsBlock));

            run.True(withBlock?.IsValid == true, $"read (refused with: {withBlock?.Error})");
            run.True(withBlock?.ToposurfacePoints is null, "no Revit deliverable read from hosts.revit");
            run.True(withBlock?.SurveyPoint is null, "no origin read from hosts.revit.georeference");
            run.True(withBlock?.Georeference.Declared == false, "the own block is not declared");
            run.Equal(Describe(withBlock), Describe(plain), "the result is the same with or without the block");
        });

        run.Case("the 3.x reader's source names no per-host block", () =>
        {
            string? folder = RepoTree.Find(
                ThreeReaderFolder, dir => File.Exists(Path.Combine(dir, ThreeReaderFile)));
            if (folder is null)
            {
                run.Fail($"could not find {ThreeReaderFolder}/{ThreeReaderFile}");
                return;
            }

            string source = File.ReadAllText(Path.Combine(folder, ThreeReaderFile));
            run.False(source.Contains("\"hosts\"", StringComparison.Ordinal), "no `hosts` member read");
            run.False(source.Contains("RevitHostBlock", StringComparison.Ordinal), "no host block helper called");
            run.False(source.Contains("HostKey", StringComparison.Ordinal), "no host key looked up");
        });
    }

    private static void RunTwoCases(TestRun run)
    {
        run.Case("every manifest corpus case reads through the bundle reader exactly as Parse reads it", () =>
        {
            if (ConformanceCorpus.LoadGroup("manifest", out List<CorpusCase> cases) is { } loadError)
            {
                run.Fail($"conformance corpus unusable: {loadError}");
                return;
            }

            foreach (CorpusCase corpusCase in cases)
            {
                BundleManifest parsed = BundleManifestReader.Parse(corpusCase.Payload);
                BundleManifest? read = Read(new Dictionary<string, string> { ["Metadata/manifest.json"] = corpusCase.Payload });
                run.Equal(Describe(read), Describe(parsed), $"{corpusCase.Id} reads as before");
                run.Equal(read?.Contents?.ManifestPath, "Metadata/manifest.json", $"{corpusCase.Id} found at the 2.x location");
            }
        });

        run.Case("a 2.x manifest of a later major is refused with the update message, as before", () =>
        {
            BundleManifest? manifest = Read(new Dictionary<string, string>
            {
                ["Metadata/manifest.json"] = "{\"version\": \"3.0.0\"}",
            });
            run.False(manifest?.IsValid ?? true, "refused");
            run.Contains(manifest?.Error, "newer than this plugin understands (it reads 1.x to 2.x)", "the 2.x path's own message");
            run.Equal(manifest?.Contents?.Refusal ?? BundleRefusal.None, BundleRefusal.MajorAhead, "major-ahead");
        });
    }

    private const string HostsBlock = """
        {
          "revit": {
            "georeference": {
              "crs_projected": "EPSG:32618",
              "origin": {"lon": -73.9, "lat": 40.7, "easting": 585000.0, "northing": 4506000.0, "epsg": 32618}
            },
            "toposurface_points": {"path": "Elevation/Elevation.tif", "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}
          },
          "autocad": {"civil3d": {"surface": {"path": "Elevation/Terrain.dxf"}}}
        }
        """;

    private static Dictionary<string, string> Three(
        string version = "3.0.0",
        string? level = "\"Max\"",
        string? catalog = "",
        string? extraEntry = null,
        string? hosts = null)
    {
        string entries = """
            "Elevation/Elevation.tif": {"odbl_role": "none", "license": "LicenseRef-MantlePlace-Terms", "sources": [], "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "count": 4294967296},
            "Elevation/Terrain.glb": {"odbl_role": "none", "license": "LicenseRef-MantlePlace-Terms", "sources": [], "count": 1200},
            "Readme.md": {"odbl_role": "none", "license": "LicenseRef-MantlePlace-Terms", "sources": []}
            """;
        if (extraEntry is not null)
        {
            entries += $",\n\"{extraEntry}\": {{\"odbl_role\": \"none\"}}";
        }

        List<string> members = [$"\"version\": \"{version}\""];
        if (level is not null)
        {
            members.Add($"\"level\": {level}");
        }

        if (catalog is not null)
        {
            members.Add(catalog.Length == 0 ? $"\"file_entries\": {{{entries}}}" : $"\"file_entries\": \"{catalog}\"");
        }

        if (hosts is not null)
        {
            members.Add($"\"hosts\": {hosts}");
        }

        return new Dictionary<string, string>
        {
            ["Manifest.json"] = "{" + string.Join(",\n", members) + "}",
            ["Readme.md"] = "A synthetic bundle.",
            ["Elevation/Elevation.tif"] = "tif",
            ["Elevation/Terrain.glb"] = "glb",
        };
    }

    private static BundleManifest? Read(Dictionary<string, string> files) =>
        BundleReader.Read(files.Keys, entry => files.TryGetValue(entry, out string? text) ? text : null);

    /// <summary>
    /// Every public fact of a reading but its <see cref="BundleManifest.Contents"/>, as JSON: the
    /// comparison that says a reading is the same one.
    /// </summary>
    private static string Describe(BundleManifest? manifest)
    {
        if (manifest is null)
        {
            return "null";
        }

        JsonObject node = JsonSerializer.SerializeToNode(
            manifest,
            new JsonSerializerOptions
            {
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
            })!.AsObject();
        node.Remove(nameof(BundleManifest.Contents));
        return node.ToJsonString();
    }
}
