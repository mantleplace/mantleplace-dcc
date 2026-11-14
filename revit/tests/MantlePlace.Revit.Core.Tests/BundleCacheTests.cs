using System.Text;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// ⛔<c>HPS-26</c>'s promote-by-rename path, exercised rather than reviewed.
/// </summary>
/// <remarks>
/// The rule names <c>automation-test</c> as an enforcer alongside <c>agent-review</c>. That second
/// enforcer only became real when the cache moved out of the Revit shim, which CI cannot build,
/// into <c>MantlePlace.Revit.Client</c>, which it can.
/// </remarks>
internal static class BundleCacheTests
{
    private const string OrderId = "3f285101-0310-425b-b06b-bdb73b025b6a";
    private const string Payload = "PK pretend this is a bundle";

    internal static int Run()
    {
        TestRun run = new();
        string sandbox = Path.Combine(Path.GetTempPath(), "mp-cache-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandbox);

        try
        {
            RunCases(run, sandbox);
        }
        finally
        {
            TryDelete(sandbox);
        }

        return run.Report("bundle cache");
    }

    private static void RunCases(TestRun run, string sandbox)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Payload);
        string sha = Sha256Digest.OfUtf8(Payload);

        run.Case("a verified download is promoted and its sidecar written", () =>
        {
            BundleCache cache = new(Path.Combine(sandbox, "ok"));
            string? error = Promote(cache, bytes, bytes.Length, sha).GetAwaiter().GetResult();

            run.True(error is null, $"promoted ({error})");

            BundleCacheLayout layout = cache.LayoutFor(OrderId);
            run.True(File.Exists(layout.BundleZipPath), "bundle.zip exists");
            run.False(File.Exists(layout.PartialZipPath), "the .part is gone");
            run.True(File.Exists(layout.SidecarPath), "cache.json written");

            CacheSidecar? sidecar = CacheSidecars.TryParse(File.ReadAllText(layout.SidecarPath));
            run.True(sidecar is not null, "sidecar parses");
            run.Equal(sidecar!.OrderId, OrderId, "the sidecar records the RAW order id");
            run.Equal(sidecar.Sha256, sha, "and the digest the platform advertised");
            run.Equal(sidecar.IntegrityChecked, true, "and that the check was performed");
        });

        run.Case("a hash mismatch deletes the .part and never creates the bundle", () =>
        {
            BundleCache cache = new(Path.Combine(sandbox, "badsha"));
            string? error = Promote(cache, bytes, bytes.Length, new string('a', 64)).GetAwaiter().GetResult();

            run.True(error is not null, "refused");
            run.Contains(error, "checksum", "and says why");

            BundleCacheLayout layout = cache.LayoutFor(OrderId);

            // The rename is what makes the cache crash-safe. A host that streams onto the final
            // path leaves a truncated bundle that looks cached, and the next run imports it.
            run.False(File.Exists(layout.BundleZipPath), "no bundle.zip was ever created");
            run.False(File.Exists(layout.PartialZipPath), "and the .part was cleaned up");
        });

        run.Case("a size mismatch is caught before the hash is even consulted", () =>
        {
            BundleCache cache = new(Path.Combine(sandbox, "badsize"));
            string? error = Promote(cache, bytes, bytes.Length + 1, sha).GetAwaiter().GetResult();

            run.True(error is not null, "refused");
            run.Contains(error, "size", "and says why");
            run.False(File.Exists(cache.LayoutFor(OrderId).BundleZipPath), "nothing promoted");
        });

        run.Case("a cancellation deletes the .part and fires no completion", () =>
        {
            BundleCache cache = new(Path.Combine(sandbox, "cancel"));
            using CancellationTokenSource cancelled = new();
            cancelled.Cancel();

            bool threw = false;
            try
            {
                cache.PromoteAsync(
                    OrderId,
                    (_, token) => Task.FromCanceled(token),
                    bytes.Length,
                    sha,
                    ManifestVersions.MinSupportedManifestVersion,
                    DateTimeOffset.UnixEpoch,
                    cancelled.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                threw = true;
            }

            run.True(threw, "cancellation propagates rather than reporting a failure");

            BundleCacheLayout layout = cache.LayoutFor(OrderId);
            run.False(File.Exists(layout.PartialZipPath), "the .part is gone");
            run.False(File.Exists(layout.BundleZipPath), "and nothing was promoted");
        });

        run.Case("a .part left by a killed process is discarded, never resumed", () =>
        {
            BundleCache cache = new(Path.Combine(sandbox, "stale"));
            BundleCacheLayout layout = cache.LayoutFor(OrderId);
            Directory.CreateDirectory(layout.Root);

            // Nothing recorded how many of these bytes are good, so they are dead weight rather
            // than a resume point. Appending to them would produce a file that hashes to nothing.
            File.WriteAllBytes(layout.PartialZipPath, Encoding.UTF8.GetBytes("garbage from last time"));

            string? error = Promote(cache, bytes, bytes.Length, sha).GetAwaiter().GetResult();
            run.True(error is null, $"promoted ({error})");
            run.Equal(File.ReadAllText(layout.BundleZipPath), Payload, "the new download replaced it entirely");
        });

        run.Case("an advertised digest that nobody could compute is valid but UNVERIFIED", () =>
        {
            // The state every Revit bundle is in today: the ETL publishes no
            // sha256 for the Revit deliverables. Refusing here would make the plugin unusable;
            // claiming verification would be a lie.
            BundleCache cache = new(Path.Combine(sandbox, "unverified"));
            string? error = Promote(cache, bytes, bytes.Length, expectedSha: null).GetAwaiter().GetResult();
            run.True(error is null, $"promoted ({error})");

            CacheEntry entry = cache.Inspect(OrderId, bytes.Length, null, ManifestVersions.MinSupportedManifestVersion);
            run.Equal(entry.State.ToString(), "CachedValid", "usable");
            run.False(entry.Verdict.IntegrityChecked, "and honestly reported as unchecked");
            run.Contains(entry.Describe(), "checked against", "which the panel frames as a gap, not a fault");
            run.False(
                entry.Describe().Contains("could not be verified.", StringComparison.Ordinal),
                "and does not end on the missing check — the per-entry checks still happen at import");
        });

        run.Case("inspect finds a verified entry and reports it as verified", () =>
        {
            BundleCache cache = new(Path.Combine(sandbox, "inspect"));
            Promote(cache, bytes, bytes.Length, sha).GetAwaiter().GetResult();

            CacheEntry entry = cache.Inspect(OrderId, bytes.Length, sha, ManifestVersions.MinSupportedManifestVersion);
            run.Equal(entry.State.ToString(), "CachedValid", "valid");
            run.True(entry.Verdict.IntegrityChecked, "verified");
            run.Contains(entry.Describe(), "verified", "and says so");
        });

        run.Case("inspect falls back to the sidecar when no listing is at hand", () =>
        {
            // The offline case: Revit opened on a plane, no vault call possible, but the sidecar
            // still knows what the platform said at download time.
            BundleCache cache = new(Path.Combine(sandbox, "offline"));
            Promote(cache, bytes, bytes.Length, sha).GetAwaiter().GetResult();

            CacheEntry entry = cache.Inspect(OrderId, expectedSizeBytes: null, expectedSha256: null, manifestVersion: null);
            run.True(entry.Verdict.IsValid, "still valid");
            run.True(entry.Verdict.IntegrityChecked, "and still verified, from the sidecar's digest");
        });

        run.Case("InspectQuick does NOT hash, and says so honestly", () =>
        {
            // The listing calls this per row per refresh. Inspect hashes the whole zip with no size
            // cap, which is right once per import and wrong 275 MB at a time whenever a window
            // redraws. Tampering with the bytes WITHOUT changing the length is what proves the hash
            // was skipped: Inspect catches it, InspectQuick cannot and does not claim to.
            BundleCache cache = new(Path.Combine(sandbox, "quick"));
            Promote(cache, bytes, bytes.Length, sha).GetAwaiter().GetResult();

            string path = cache.LayoutFor(OrderId).BundleZipPath;
            byte[] tampered = [.. bytes];
            tampered[0] ^= 0xFF;
            File.WriteAllBytes(path, tampered);

            CacheEntry quick = cache.InspectQuick(OrderId, bytes.Length, sha, "1.0.0");
            run.Equal(quick.State.ToString(), "CachedValid", "the listing still shows it as downloaded");
            run.False(quick.Verdict.IntegrityChecked, "but never claims it was verified");

            CacheEntry full = cache.Inspect(OrderId, bytes.Length, sha, "1.0.0");
            run.Equal(full.State.ToString(), "CachedStale", "and the import gate still catches it");
        });

        run.Case("InspectQuick still catches a size mismatch, which costs nothing", () =>
        {
            BundleCache cache = new(Path.Combine(sandbox, "quick-size"));
            Promote(cache, bytes, bytes.Length, sha).GetAwaiter().GetResult();
            File.WriteAllText(cache.LayoutFor(OrderId).BundleZipPath, "short");

            CacheEntry quick = cache.InspectQuick(OrderId, bytes.Length, sha, "1.0.0");
            run.Equal(quick.State.ToString(), "CachedStale", "the free check is still made");
        });

        run.Case("an unverified bundle says WHICH checksum is missing", () =>
        {
            // The old wording claimed "the platform published no checksum for this bundle", which is
            // not true as a whole: the manifest inside publishes one per Revit deliverable and
            // LocalBundleArchive.VerifyPlan checks every one of them before an element exists. What
            // is absent is a digest for the zip.
            BundleCache cache = new(Path.Combine(sandbox, "nodigest"));
            Promote(cache, bytes, bytes.Length, expectedSha: null).GetAwaiter().GetResult();

            CacheEntry entry = cache.Inspect(OrderId, bytes.Length, expectedSha256: null, manifestVersion: "1.0.0");
            run.False(entry.Verdict.IntegrityChecked, "nothing to check the zip against");
            run.Contains(entry.Describe(), "zip as a whole", "the sentence names what is missing");
            run.Contains(entry.Describe(), "when you import", "and what is checked instead");
        });

        run.Case("a corrupted cached file is reported stale rather than imported", () =>
        {
            BundleCache cache = new(Path.Combine(sandbox, "corrupt"));
            Promote(cache, bytes, bytes.Length, sha).GetAwaiter().GetResult();
            File.WriteAllText(cache.LayoutFor(OrderId).BundleZipPath, "tampered");

            CacheEntry entry = cache.Inspect(OrderId, bytes.Length, sha, ManifestVersions.MinSupportedManifestVersion);
            run.Equal(entry.State.ToString(), "CachedStale", "stale");
            run.Contains(entry.Describe(), "again", "and the curator is told to download it again");
        });

        run.Case("a download cached for one host is never served to another", () =>
        {
            // Revit and AutoCAD share one cache folder, and a host's view holds only that host's files.
            string root = Path.Combine(sandbox, "hosts");
            BundleCache revit = new(root, "revit");
            BundleCache autocad = new(root, "autocad");
            Promote(revit, bytes, bytes.Length, sha).GetAwaiter().GetResult();

            run.Equal(revit.Inspect(OrderId, bytes.Length, sha, "1.0.0").State.ToString(), "CachedValid", "its own host hits");

            CacheEntry other = autocad.Inspect(OrderId, bytes.Length, sha, "1.0.0");
            run.Equal(other.State.ToString(), "NotCached", "the other host misses");
            run.Equal(autocad.InspectQuick(OrderId, bytes.Length, sha, "1.0.0").State.ToString(), "NotCached", "in the listing too");
            run.Equal(other.Describe(), "Not downloaded.", "and is told so, not that the file is damaged");
        });

        run.Case("a cache entry written before the host was recorded is fetched again", () =>
        {
            BundleCache cache = new(Path.Combine(sandbox, "legacy"));
            Promote(cache, bytes, bytes.Length, sha).GetAwaiter().GetResult();

            BundleCacheLayout layout = cache.LayoutFor(OrderId);
            CacheSidecar current = CacheSidecars.TryParse(File.ReadAllText(layout.SidecarPath))!;
            run.Equal(current.Host, "revit", "the sidecar records the host");
            run.Equal(current.Level, BundleCache.HighestLevel, "and the level");

            // The sidecar the shipped plugin wrote: no host, no level.
            File.WriteAllText(
                layout.SidecarPath,
                CacheSidecars.Serialize(new CacheSidecar
                {
                    OrderId = OrderId,
                    SizeBytes = bytes.Length,
                    Sha256 = sha,
                    ManifestVersion = ManifestVersions.MinSupportedManifestVersion,
                    IntegrityChecked = true,
                }));
            run.False(File.ReadAllText(layout.SidecarPath).Contains("\"host\"", StringComparison.Ordinal), "no host key written");
            run.Equal(cache.Inspect(OrderId, bytes.Length, sha, "1.0.0").State.ToString(), "NotCached", "a miss");

            File.Delete(layout.SidecarPath);
            run.Equal(cache.Inspect(OrderId, bytes.Length, sha, "1.0.0").State.ToString(), "NotCached", "no sidecar at all is a miss too");
        });

        run.Case("the same host and level hits; another level misses and replaces the slot", () =>
        {
            BundleCache cache = new(Path.Combine(sandbox, "levels"));
            Promote(cache, bytes, bytes.Length, sha, "MED").GetAwaiter().GetResult();

            run.Equal(cache.Inspect(OrderId, bytes.Length, sha, "1.0.0", "MED").State.ToString(), "CachedValid", "same level hits");
            run.Equal(cache.Inspect(OrderId, bytes.Length, sha, "1.0.0", "MAX").State.ToString(), "NotCached", "another level misses");

            byte[] second = Encoding.UTF8.GetBytes("PK the MAX download");
            Promote(cache, second, second.Length, Sha256Digest.OfUtf8("PK the MAX download"), "MAX").GetAwaiter().GetResult();

            run.Equal(File.ReadAllText(cache.LayoutFor(OrderId).BundleZipPath), "PK the MAX download", "the download replaced the slot");
            run.Equal(cache.Inspect(OrderId, second.Length, null, "1.0.0", "MAX").State.ToString(), "CachedValid", "the new level hits");
            run.Equal(cache.Inspect(OrderId, second.Length, null, "1.0.0", "MED").State.ToString(), "NotCached", "the old level no longer does");
        });

        run.Case("a host's download replaces the other host's in the shared folder", () =>
        {
            string root = Path.Combine(sandbox, "shared");
            BundleCache revit = new(root, "revit");
            BundleCache autocad = new(root, "autocad");
            Promote(revit, bytes, bytes.Length, sha).GetAwaiter().GetResult();

            byte[] view = Encoding.UTF8.GetBytes("PK autocad view");
            Promote(autocad, view, view.Length, null).GetAwaiter().GetResult();

            run.Equal(autocad.Inspect(OrderId, null, null, "1.0.0").State.ToString(), "CachedValid", "AutoCAD hits its own");
            run.Equal(revit.Inspect(OrderId, bytes.Length, sha, "1.0.0").State.ToString(), "NotCached", "Revit fetches its own again");
        });

        run.Case("eviction is explicit and per-order, and there is no other kind", () =>
        {
            // HPS-44. A host that reclaims disk on the curator's behalf has silently converted an
            // owned asset into a streamed one.
            BundleCache cache = new(Path.Combine(sandbox, "evict"));
            Promote(cache, bytes, bytes.Length, sha).GetAwaiter().GetResult();

            BundleCacheLayout layout = cache.LayoutFor(OrderId);
            run.True(Directory.Exists(layout.Root), "cached");

            cache.Remove(OrderId);
            run.False(Directory.Exists(layout.Root), "removed on request");

            cache.Remove(OrderId);
            run.True(true, "and removing twice is not an error");
        });

        run.Case("the sidecar round-trips, and null sha stays null", () =>
        {
            CacheSidecar written = new()
            {
                OrderId = "a/b",
                SizeBytes = 1234,
                Sha256 = null,
                ManifestVersion = null,
                DownloadedAtUtc = "2026-08-09T00:00:00.0000000Z",
                IntegrityChecked = false,
            };

            CacheSidecar? read = CacheSidecars.TryParse(CacheSidecars.Serialize(written));
            run.True(read is not null, "parsed");
            run.Equal(read!.OrderId, "a/b", "the raw order id survives, unsanitised");
            run.True(read.Sha256 is null, "null stays null — unknown, not empty");
            run.True(read.ManifestVersion is null, "and so does an unknown manifest version");
            run.Equal(read.SizeBytes == 1234, true, "size");
            run.False(read.IntegrityChecked, "integrity flag");
        });

        run.Case("an unreadable sidecar means nothing is remembered, not an error", () =>
        {
            run.True(CacheSidecars.TryParse("{ not json") is null, "malformed");
            run.True(CacheSidecars.TryParse("{}") is null, "no order id");
            run.True(CacheSidecars.TryParse(null) is null, "absent");
        });
    }

    private static Task<string?> Promote(BundleCache cache, byte[] bytes, long expectedSize, string? expectedSha, string level = BundleCache.HighestLevel)
        => cache.PromoteAsync(
            OrderId,
            async (destination, token) => await destination.WriteAsync(bytes, token).ConfigureAwait(false),
            expectedSize,
            expectedSha,
            ManifestVersions.MinSupportedManifestVersion,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None,
            level: level);

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
