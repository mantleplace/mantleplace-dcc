using System.IO.Compression;
using System.Text;
using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.Core.Tests;

internal static class LocalBundleArchiveTests
{
    private const string OrderId = "3f285101-0310-425b-b06b-bdb73b025b6a";

    private const string PointsCsv = "1,2,3\n4,5,6\n7,8,9\n";

    private const string WrongSha256 = "0000000000000000000000000000000000000000000000000000000000000000";

    private static string PointsSha256 => Sha256Digest.OfUtf8(PointsCsv);

    internal static int Run()
    {
        TestRun run = new();
        string sandbox = Path.Combine(Path.GetTempPath(), "mp-archive-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandbox);

        try
        {
            RunCases(run, sandbox);
        }
        finally
        {
            TryDelete(sandbox);
        }

        return run.Report("local bundle archive");
    }

    private static void RunCases(TestRun run, string sandbox)
    {
        string cacheRoot = Path.Combine(sandbox, "cache");

        run.Case("a bundle is cached under its ORDER id, not its file name", () =>
        {
            string first = WriteBundle(Path.Combine(sandbox, "a", "download.zip"), OrderId);
            string second = WriteBundle(Path.Combine(sandbox, "b", "download.zip"), OrderId);

            using LocalBundleArchive one = LocalBundleArchive.Open(first, cacheRoot);
            using LocalBundleArchive two = LocalBundleArchive.Open(second, cacheRoot);

            run.Equal(one.Layout.Root, two.Layout.Root, "same order, same root");
            run.Equal(
                Path.GetFileName(one.Layout.Root),
                OrderId,
                "a uuid order id is lossless, so the directory is the id itself");
        });

        run.Case("two different orders never share a root", () =>
        {
            string other = "9c1d0000-0000-4000-8000-000000000001";
            string first = WriteBundle(Path.Combine(sandbox, "c", "download.zip"), OrderId);
            string second = WriteBundle(Path.Combine(sandbox, "d", "download.zip"), other);

            using LocalBundleArchive one = LocalBundleArchive.Open(first, cacheRoot);
            using LocalBundleArchive two = LocalBundleArchive.Open(second, cacheRoot);

            run.True(
                !string.Equals(one.Layout.Root, two.Layout.Root, StringComparison.Ordinal),
                "different orders, different roots");
        });

        run.Case("a bundle from before MPB 1.7.0 keeps the folder and stem every earlier import gave it", () =>
        {
            const string Legacy = "job-legacy-7";
            string zip = WriteBundle(
                Path.Combine(sandbox, "legacy", "download.zip"),
                orderId: null,
                manifestJson: $$"""
                    {
                      "version": "1.6.0",
                      "attribution": { "order_id": "{{Legacy}}", "sources": [] },
                      "packaging": { "delivery_model": "base_on_demand" }
                    }
                    """);

            using LocalBundleArchive archive = LocalBundleArchive.Open(zip, cacheRoot);

            run.Equal(archive.Layout.Root, BundleCacheLayout.ForOrder(Legacy, cacheRoot).Root, "the same folder, and so the same stem");
            run.Equal(archive.Manifest?.OrderId, string.Empty, "while the job id is not taken for the order");
        });

        run.Case("a zip with no manifest falls back to its full path, not its stem", () =>
        {
            string first = WriteBundle(Path.Combine(sandbox, "e", "download.zip"), orderId: null, withManifest: false);
            string second = WriteBundle(Path.Combine(sandbox, "f", "download.zip"), orderId: null, withManifest: false);

            using LocalBundleArchive one = LocalBundleArchive.Open(first, cacheRoot);
            using LocalBundleArchive two = LocalBundleArchive.Open(second, cacheRoot);

            run.True(one.Manifest is null, "no manifest means no manifest, not an empty one");
            run.True(
                !string.Equals(one.Layout.Root, two.Layout.Root, StringComparison.Ordinal),
                "two same-named files in different folders get different roots");
        });

        run.Case("the four cache file names are the ones the corpus pins", () =>
        {
            BundleCacheLayout layout = BundleCacheLayout.ForOrder(OrderId, cacheRoot);
            run.Equal(Path.GetFileName(layout.BundleZipPath), "bundle.zip", "final");
            run.Equal(Path.GetFileName(layout.PartialZipPath), "bundle.zip.part", "partial");
            run.Equal(Path.GetFileName(layout.SidecarPath), "cache.json", "sidecar");
            run.Equal(Path.GetFileName(layout.ExtractedRoot), "extracted", "extracted");
        });

        run.Case("a retained file survives dispose and a transient one does not", () =>
        {
            string zip = WriteBundle(Path.Combine(sandbox, "g", "download.zip"), OrderId);
            string transientPath;
            string retainedPath;

            using (LocalBundleArchive archive = LocalBundleArchive.Open(zip, cacheRoot))
            {
                transientPath = archive.Extract("Surface/SurfacePoints.csv", ExtractionLifetime.Transient, PointsSha256);
                retainedPath = archive.Extract("Surface/Surface.dxf", ExtractionLifetime.Retained, null);

                run.True(File.Exists(transientPath), "transient extracted");
                run.True(File.Exists(retainedPath), "retained extracted");
                run.Contains(retainedPath, archive.Layout.ExtractedRoot, "retained lands under the order root");
            }

            run.False(File.Exists(transientPath), "scratch is cleaned up");
            run.True(File.Exists(retainedPath), "retained survives — links point into it (HPS-44)");
        });

        run.Case("a retained file a host holds open is reused when it is already the published one", () =>
        {
            string zip = WriteBundle(Path.Combine(sandbox, "h", "download.zip"), OrderId);
            using LocalBundleArchive archive = LocalBundleArchive.Open(zip, Path.Combine(sandbox, "h", "cache"));
            string first = archive.Extract("Surface/SurfacePoints.csv", ExtractionLifetime.Retained, PointsSha256);

            using (new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                string again = archive.Extract("Surface/SurfacePoints.csv", ExtractionLifetime.Retained, PointsSha256);
                run.Equal(again, first, "the same file, not rewritten under the host's lock");
            }

            File.WriteAllText(first, "not the published bytes");
            archive.Extract("Surface/SurfacePoints.csv", ExtractionLifetime.Retained, PointsSha256);
            run.Equal(File.ReadAllText(first), PointsCsv, "a file that is not the published one is still replaced");
        });

        run.Case("entries keep their sub-path so two same-named files cannot overwrite each other", () =>
        {
            string zip = WriteBundle(Path.Combine(sandbox, "h", "download.zip"), OrderId);
            using LocalBundleArchive archive = LocalBundleArchive.Open(zip, cacheRoot);

            string surface = archive.Extract("Surface/notes.txt", ExtractionLifetime.Retained, null);
            string site = archive.Extract("Site/notes.txt", ExtractionLifetime.Retained, null);

            run.True(
                !string.Equals(surface, site, StringComparison.OrdinalIgnoreCase),
                "a flattened extraction would collapse these to one file");
            run.Equal(File.ReadAllText(surface), "surface", "surface content intact");
            run.Equal(File.ReadAllText(site), "site", "site content intact");
        });

        run.Case("a zip-slip entry is refused rather than written outside the root", () =>
        {
            string zip = Path.Combine(sandbox, "i", "evil.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
            using (FileStream stream = File.Create(zip))
            using (ZipArchive builder = new(stream, ZipArchiveMode.Create))
            {
                WriteEntry(builder, "Metadata/manifest.json", ManifestJson(OrderId));
                WriteEntry(builder, "../../escaped.txt", "pwned");
            }

            using LocalBundleArchive archive = LocalBundleArchive.Open(zip, cacheRoot);

            bool threw = false;
            try
            {
                archive.Extract("../../escaped.txt", ExtractionLifetime.Retained, null);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }

            run.True(threw, "the traversal entry was refused");
            run.False(
                File.Exists(Path.Combine(cacheRoot, "..", "escaped.txt")),
                "and nothing was written above the cache root");
        });

        run.Case("an entry named in the plan but absent from the zip is a named failure", () =>
        {
            string zip = WriteBundle(Path.Combine(sandbox, "j", "download.zip"), OrderId);
            using LocalBundleArchive archive = LocalBundleArchive.Open(zip, cacheRoot);

            bool threw = false;
            try
            {
                archive.Extract("Surface/absent.csv", ExtractionLifetime.Transient, null);
            }
            catch (InvalidOperationException ex)
            {
                threw = true;
                run.Contains(ex.Message, "Surface/absent.csv", "names the entry");
            }

            run.True(threw, "refused");
        });

        run.Case("a corrupt entry is refused before its bytes land on disk", () =>
        {
            string zip = WriteBundle(Path.Combine(sandbox, "k", "download.zip"), OrderId);
            using LocalBundleArchive archive = LocalBundleArchive.Open(zip, cacheRoot);

            string message = string.Empty;
            try
            {
                archive.Extract("Surface/SurfacePoints.csv", ExtractionLifetime.Transient, WrongSha256);
                run.Fail("a mismatched digest was extracted anyway");
            }
            catch (InvalidOperationException ex)
            {
                message = ex.Message;
            }

            run.Contains(message, "Integrity check failed", "the failure says what it was");
            run.Contains(message, "Surface/SurfacePoints.csv", "and which entry");
            run.False(
                File.Exists(Path.Combine(archive.Layout.ExtractedRoot, "Surface", "SurfacePoints.csv")),
                "nothing was written");
        });

        run.Case("a matching digest extracts, and an absent one is a skip not a failure (HPS-27)", () =>
        {
            string zip = WriteBundle(Path.Combine(sandbox, "l", "download.zip"), OrderId);
            using LocalBundleArchive archive = LocalBundleArchive.Open(zip, cacheRoot);

            string verified = archive.Extract("Surface/SurfacePoints.csv", ExtractionLifetime.Transient, PointsSha256);
            run.True(File.Exists(verified), "the declared hash matched, so the file was written");

            string unverified = archive.Extract("Surface/Surface.dxf", ExtractionLifetime.Transient, null);
            run.True(File.Exists(unverified), "an unadvertised hash is valid-but-unverified");
        });

        run.Case("the whole plan is verified before ANY step runs", () =>
        {
            string zip = WriteBundle(Path.Combine(sandbox, "m", "download.zip"), OrderId);
            using LocalBundleArchive archive = LocalBundleArchive.Open(zip, cacheRoot);

            BundleImportPlan good = new()
            {
                CanImport = true,
                Steps =
                [
                    new ImportStep
                    {
                        Kind = ImportStepKind.ToposurfaceFromPointsFile,
                        EntryName = "Surface/SurfacePoints.csv",
                        ExpectedSha256 = PointsSha256,
                    },
                ],
            };
            run.True(archive.VerifyPlan(good) is null, "a plan whose hashes all match passes");

            BundleImportPlan bad = new()
            {
                CanImport = true,
                Steps =
                [
                    new ImportStep
                    {
                        Kind = ImportStepKind.ToposurfaceFromPointsFile,
                        EntryName = "Surface/SurfacePoints.csv",
                        ExpectedSha256 = PointsSha256,
                    },
                    new ImportStep
                    {
                        Kind = ImportStepKind.LinkSiteIfc,
                        EntryName = "Site/notes.txt",
                        ExpectedSha256 = WrongSha256,
                    },
                ],
            };
            run.Contains(archive.VerifyPlan(bad), "Site/notes.txt", "and one bad artifact aborts the lot");

            BundleImportPlan placementOnly = new()
            {
                CanImport = true,
                Steps = [new ImportStep { Kind = ImportStepKind.SetSharedCoordinates }],
            };
            run.True(archive.VerifyPlan(placementOnly) is null, "an entry-less step is not an artifact");
        });

        run.Case("an unattended import takes its zip from the environment", () =>
        {
            run.Equal(LocalBundleSource.Unattended(@"C:\bundles\a.zip"), @"C:\bundles\a.zip", "a named path");
            run.Equal(LocalBundleSource.Unattended("  C:\\bundles\\a.zip  "), @"C:\bundles\a.zip", "trimmed");
            run.True(LocalBundleSource.Unattended(null) is null, "unset means ask the user");
            run.True(LocalBundleSource.Unattended("   ") is null, "exported-but-empty means ask the user too");
            run.Equal(
                LocalBundleSource.LogPathFor(@"C:\bundles\a.zip"),
                @"C:\bundles\a.zip.mantleplace-import.log",
                "and an unattended run reports beside the zip, never into a dialog nothing can dismiss");
        });

        run.Case("an entry's pixel grid is probed from its header, without extracting it", () =>
        {
            string zipPath = Path.Combine(sandbox, "probe", "download.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

            using (FileStream stream = File.Create(zipPath))
            using (ZipArchive builder = new(stream, ZipArchiveMode.Create))
            {
                WriteEntry(builder, "Metadata/manifest.json", ManifestJson(OrderId));
                WriteBytes(builder, "Imagery/Drape.png", PngPrefix(4767, 4733));
                WriteEntry(builder, "Imagery/NotAnImage.png", "certainly not a PNG");
            }

            using LocalBundleArchive archive = LocalBundleArchive.Open(zipPath, cacheRoot);

            ImageSize? drape = archive.ProbeImageSize("Imagery/Drape.png");
            run.Equal(drape?.Width ?? 0, 4767, "width off the wire");
            run.Equal(drape?.Height ?? 0, 4733, "height off the wire");

            run.True(archive.ProbeImageSize("Imagery/NotAnImage.png") is null, "a non-image is null");
            run.True(archive.ProbeImageSize("Imagery/Missing.png") is null, "so is an entry that is not there");
            run.True(archive.ProbeImageSize("") is null, "and so is no entry at all");

            run.False(
                File.Exists(Path.Combine(archive.RetainedDirectory, "Imagery", "Drape.png")),
                "the probe extracted nothing");
        });

        run.Case("an entry shorter than a PNG header is not a PNG", () =>
        {
            string zipPath = Path.Combine(sandbox, "short", "download.zip");
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

            using (FileStream stream = File.Create(zipPath))
            using (ZipArchive builder = new(stream, ZipArchiveMode.Create))
            {
                WriteEntry(builder, "Metadata/manifest.json", ManifestJson(OrderId));
                WriteBytes(builder, "Imagery/Drape.png", PngPrefix(4767, 4733)[..12]);
            }

            using LocalBundleArchive archive = LocalBundleArchive.Open(zipPath, cacheRoot);

            run.True(archive.ProbeImageSize("Imagery/Drape.png") is null, "a truncated header is refused");
        });
    }

    private static byte[] PngPrefix(uint width, uint height)
    {
        byte[] prefix =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D,
            0x49, 0x48, 0x44, 0x52,
            (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
            (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
        ];

        return prefix;
    }

    private static void WriteBytes(ZipArchive builder, string name, byte[] content)
    {
        using Stream entry = builder.CreateEntry(name).Open();
        entry.Write(content, 0, content.Length);
    }

    private static string WriteBundle(string zipPath, string? orderId, bool withManifest = true, string? manifestJson = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

        using FileStream stream = File.Create(zipPath);
        using ZipArchive builder = new(stream, ZipArchiveMode.Create);

        if (withManifest)
        {
            WriteEntry(builder, "Metadata/manifest.json", manifestJson ?? ManifestJson(orderId));
        }

        WriteEntry(builder, "Surface/SurfacePoints.csv", PointsCsv);
        WriteEntry(builder, "Surface/Surface.dxf", "0\nSECTION\n");
        WriteEntry(builder, "Surface/notes.txt", "surface");
        WriteEntry(builder, "Site/notes.txt", "site");

        return zipPath;
    }

    private static void WriteEntry(ZipArchive builder, string name, string content)
    {
        using Stream entry = builder.CreateEntry(name).Open();
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        entry.Write(bytes, 0, bytes.Length);
    }

    private static string ManifestJson(string? orderId)
        => $$"""
             {
               "version": "1.0.0",
               "order_id": "{{orderId}}",
               "packaging": { "delivery_model": "base_on_demand" }
             }
             """;

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
