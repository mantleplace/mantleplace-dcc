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
                    18,
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

            CacheEntry entry = cache.Inspect(OrderId, bytes.Length, null, 18);
            run.Equal(entry.State.ToString(), "CachedValid", "usable");
            run.False(entry.Verdict.IntegrityChecked, "and honestly reported as unchecked");
            run.Contains(entry.Describe(), "not a problem", "which the panel does not phrase as an error");
        });

        run.Case("inspect finds a verified entry and reports it as verified", () =>
        {
            BundleCache cache = new(Path.Combine(sandbox, "inspect"));
            Promote(cache, bytes, bytes.Length, sha).GetAwaiter().GetResult();

            CacheEntry entry = cache.Inspect(OrderId, bytes.Length, sha, 18);
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

        run.Case("a corrupted cached file is reported stale rather than imported", () =>
        {
            BundleCache cache = new(Path.Combine(sandbox, "corrupt"));
            Promote(cache, bytes, bytes.Length, sha).GetAwaiter().GetResult();
            File.WriteAllText(cache.LayoutFor(OrderId).BundleZipPath, "tampered");

            CacheEntry entry = cache.Inspect(OrderId, bytes.Length, sha, 18);
            run.Equal(entry.State.ToString(), "CachedStale", "stale");
            run.Contains(entry.Describe(), "again", "and the curator is told to download it again");
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

    private static Task<string?> Promote(BundleCache cache, byte[] bytes, long expectedSize, string? expectedSha)
        => cache.PromoteAsync(
            OrderId,
            async (destination, token) => await destination.WriteAsync(bytes, token).ConfigureAwait(false),
            expectedSize,
            expectedSha,
            18,
            DateTimeOffset.UnixEpoch,
            CancellationToken.None);

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
