using System.IO.Compression;
using System.Text;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The bundle archive and its on-disk layout.
/// </summary>
/// <remarks>
/// These assertions could not exist while <c>LocalBundleArchive</c> lived in the Revit shim: a
/// hosted runner has no Revit, so the shim is never built in CI and everything in it was covered by
/// <c>agent-review</c> alone. Moving it to <c>MantlePlace.Revit.Client</c> is what makes the two
/// lifetimes, the zip-slip guard and the per-order keying testable (<c>HPS-02</c>, <c>HPS-42</c>).
/// </remarks>
internal static class LocalBundleArchiveTests
{
    private const string OrderId = "3f285101-0310-425b-b06b-bdb73b025b6a";

    private const string PointsCsv = "1,2,3\n4,5,6\n7,8,9\n";

    private const string WrongSha256 = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>
    /// Computed from the fixture rather than transcribed: a literal would need re-deriving every
    /// time the fixture changes, and the version that silently stopped matching would look like a
    /// corrupt bundle rather than like corpus rot.
    /// </summary>
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
            // The collision this replaces: every bundle the platform emitted was called
            // download.zip, so two orders shared one retained directory and the first order's
            // Revit links resolved to the second order's extracted files.
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

            // The failure this guards: deleting the retained directory on dispose leaves every
            // Revit link the import just created pointing at nothing, and Revit says so only the
            // next time the project is opened.
            run.False(File.Exists(transientPath), "scratch is cleaned up");
            run.True(File.Exists(retainedPath), "retained survives — links point into it (HPS-44)");
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
            // The defect: sha256 was parsed off the manifest and consumed by nothing. Verification
            // existed only on the DOWNLOAD path (⛔HPS-26, BundleCache), so a zip that arrived any
            // other way was extracted and imported unchecked.
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

            // Below v19 the Revit deliverables published no hash at all. Treating that as corrupt
            // makes every one of those bundles un-importable; treating it as verified is a lie.
            string unverified = archive.Extract("Surface/Surface.dxf", ExtractionLifetime.Transient, null);
            run.True(File.Exists(unverified), "an unadvertised hash is valid-but-unverified");
        });

        run.Case("the whole plan is verified before ANY step runs", () =>
        {
            // Per-entry checking alone would abort the bad artifact only after the earlier steps had
            // already created a toposolid in the user's model. The reference host sweeps the whole
            // set up front and creates nothing on a mismatch; this is the same shape.
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

            // SetSharedCoordinates names no entry — a sweep that hashed "" would report a bundle
            // corrupt over a step that touches no bytes.
            BundleImportPlan placementOnly = new()
            {
                CanImport = true,
                Steps = [new ImportStep { Kind = ImportStepKind.SetSharedCoordinates }],
            };
            run.True(archive.VerifyPlan(placementOnly) is null, "an entry-less step is not an artifact");
        });

        run.Case("an unattended import takes its zip from the environment", () =>
        {
            // Every entry point was UI-gated, and the file dialog is created by the add-in rather
            // than by Revit, so journal playback could not drive the import at all — which is why
            // Toposolid.Create and friends had never executed inside Revit.
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

            // The drape itself was never written out: probing happens while the plan is still being
            // made, and a ~50 MB extraction to answer a 24-byte question would make every refusal
            // cost what a successful import costs.
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

            // A stream that ends mid-header must read as "not an image" rather than blocking or
            // handing the caller a half-filled buffer of zeroes.
            run.True(archive.ProbeImageSize("Imagery/Drape.png") is null, "a truncated header is refused");
        });
    }

    /// <summary>The first 24 bytes of a PNG declaring the given dimensions.</summary>
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

    /// <summary>Writes a bundle zip with the entries the import path actually reaches for.</summary>
    private static string WriteBundle(string zipPath, string? orderId, bool withManifest = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);

        using FileStream stream = File.Create(zipPath);
        using ZipArchive builder = new(stream, ZipArchiveMode.Create);

        if (withManifest)
        {
            WriteEntry(builder, "Metadata/manifest.json", ManifestJson(orderId));
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
               "version": 18,
               "orderId": "{{orderId}}",
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
