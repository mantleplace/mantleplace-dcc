using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MantlePlace.Client;

namespace MantlePlace.Core.Tests;

/// <summary>
/// An MPB 3.0 zip on disk, opened the way Revit's import opens one: the archive reads it as a 3.x
/// bundle, files it under its order, plans it and checks its bytes, and the vault's download check
/// finds its manifest.
/// </summary>
/// <remarks>
/// The bundle is the shared corpus's full 3.0 download (<c>corpus/bundle/v3-full.json</c>), written
/// out as a zip, so nothing here transcribes a manifest (HPS-40).
/// </remarks>
internal static class ThreeArchiveTests
{
    private const string FullBundle = "bundle/v3-full.json";

    internal static int Run()
    {
        TestRun run = new();
        string sandbox = Path.Combine(Path.GetTempPath(), "mp-three-archive-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandbox);

        try
        {
            RunCases(run, sandbox);
        }
        finally
        {
            try
            {
                Directory.Delete(sandbox, recursive: true);
            }
            catch (IOException)
            {
                // A scratch folder a virus scanner still holds is left to the temp sweep.
            }
        }

        return run.Report("MPB 3.0 archive");
    }

    private static void RunCases(TestRun run, string sandbox)
    {
        string cacheRoot = Path.Combine(sandbox, "cache");
        Dictionary<string, byte[]> files = Load(FullBundle);
        string orderId = JsonNode.Parse(files["Manifest.json"])!["order"]!["id"]!.GetValue<string>();
        string zip = WriteZip(Path.Combine(sandbox, "a", "download.zip"), files);

        run.Case("a 3.0 zip opens as a 3.x bundle, not as a zip with no manifest", () =>
        {
            using LocalBundleArchive archive = LocalBundleArchive.Open(zip, cacheRoot);

            run.True(archive.Manifest is not null, "the archive reads a manifest from the root Manifest.json");
            run.Equal(archive.Manifest?.Contents?.Major ?? 0, 3, "read as a 3.x bundle");
            run.True(archive.Manifest?.IsValid ?? false, "the reading is not a refusal");
        });

        run.Case("a 3.0 bundle names its order, and is filed and stamped under it", () =>
        {
            // The re-import rule finds what an earlier import placed by the stem it was stamped with,
            // which is the filing key. A 3.0 bundle filed by its zip's path is a stranger to its own
            // ground when the same order arrives from the vault, or under another file name.
            using LocalBundleArchive archive = LocalBundleArchive.Open(zip, cacheRoot);
            string other = WriteZip(Path.Combine(sandbox, "b", "renamed.zip"), files);
            using LocalBundleArchive renamed = LocalBundleArchive.Open(other, cacheRoot);

            run.Equal(archive.Manifest?.OrderId, orderId, "the order id is order.id, verbatim");
            run.Equal(archive.Manifest?.FilingKey, orderId, "filed under the order id");
            run.Equal(archive.Layout.Root, BundleCacheLayout.ForOrder(orderId, cacheRoot).Root, "cached under the order");
            run.Equal(renamed.Layout.Root, archive.Layout.Root, "the same order under another file name is the same entry");
        });

        run.Case("a 3.0 zip's plan from the archive imports, and its bytes pass the integrity check", () =>
        {
            using LocalBundleArchive archive = LocalBundleArchive.Open(zip, cacheRoot);
            BundleManifest manifest = archive.Manifest!;
            BundleImportPlan plan = BundleImportPlanner.Plan(manifest, archive.EntryNames, archive.ProbeImageSize);

            run.True(plan.CanImport, $"the plan can import: {plan.BlockedReason}");
            run.True(plan.Steps.Any(step => step.Kind == ImportStepKind.SetSharedCoordinates), "the survey point is placed");
            run.True(plan.Steps.All(step => step.Level is null), "no step carries a per-category level");
            run.Equal(archive.VerifyPlan(plan), null, "every planned file matches the digest its catalog entry states");
            run.False(ImportChecklist.For(plan, "2027").OffersLevels, "the import window shows no per-category level column");
        });

        run.Case("a 3.0 zip's manifest digest is its root Manifest.json's, so a vault download of one is kept", () =>
        {
            string expected = Convert.ToHexString(SHA256.HashData(files["Manifest.json"])).ToLowerInvariant();

            run.Equal(LocalBundleArchive.ManifestSha256(zip), expected, "the digest the download view states is checked against Manifest.json");
        });

        run.Case("a 3.0 zip still has no 2.x manifest text, which the AutoCAD block reads", () =>
        {
            using LocalBundleArchive archive = LocalBundleArchive.Open(zip, cacheRoot);

            run.True(archive.ManifestText is null, "ManifestText is Metadata/manifest.json's text alone");
        });
    }

    private static string WriteZip(string path, Dictionary<string, byte[]> files)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using FileStream stream = File.Create(path);
        using ZipArchive builder = new(stream, ZipArchiveMode.Create);
        foreach ((string name, byte[] data) in files)
        {
            using Stream entry = builder.CreateEntry(name).Open();
            entry.Write(data);
        }

        return path;
    }

    /// <summary>A corpus bundle's files as bytes: a string is the file's text, and the manifest is written out as JSON.</summary>
    private static Dictionary<string, byte[]> Load(string bundleFile)
    {
        string root = ConformanceCorpus.FindCorpusDirectory() ?? throw new InvalidOperationException("the corpus is not found");
        JsonObject fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(root, bundleFile.Replace('/', Path.DirectorySeparatorChar))))!.AsObject();

        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach ((string name, JsonNode? value) in fixture["files"]!.AsObject())
        {
            files[name] = Encoding.UTF8.GetBytes(
                value is JsonValue text && text.TryGetValue(out string? content) ? content : value!.ToJsonString());
        }

        return files;
    }
}
