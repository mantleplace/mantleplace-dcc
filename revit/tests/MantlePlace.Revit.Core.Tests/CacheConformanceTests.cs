using System.Text;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Drives the shared corpus' <c>cache</c> and <c>digest</c> groups (<c>HPS-27</c>, <c>HPS-28</c>,
/// <c>HPS-30</c>).
/// </summary>
internal static class CacheConformanceTests
{
    internal static int Run()
    {
        TestRun run = new();
        DriveGroup(run, "cache");
        DriveGroup(run, "digest");
        return run.Report("cache and digest conformance");
    }

    private static void DriveGroup(TestRun run, string group)
    {
        if (ConformanceCorpus.LoadGroup(group, out List<ConformanceCorpus.CorpusCase> cases) is { } problem)
        {
            run.Fail(problem);
            return;
        }

        HashSet<string> driven = new(StringComparer.Ordinal);

        foreach (ConformanceCorpus.CorpusCase corpusCase in cases)
        {
            run.Case(corpusCase.Id, () =>
            {
                using VectorDocument vectors = VectorDocument.Parse(corpusCase.Payload);

                switch (corpusCase.Id)
                {
                    case "cache.validityTruthTable":
                        DriveValidity(run, vectors.Root);
                        break;
                    case "cache.keySanitisation":
                        DriveSanitisation(run, vectors.Root);
                        break;
                    case "digest.sha256Vectors":
                        DriveDigest(run, vectors.Root);
                        break;
                    default:
                        run.Fail($"no driver for corpus case '{corpusCase.Id}' (HPS-41)");
                        return;
                }

                driven.Add(corpusCase.Id);

                foreach (string unread in vectors.UnreadPaths())
                {
                    run.Fail($"'{unread}' is a vector value nothing in this suite read.");
                }
            });
        }

        foreach (string undriven in ConformanceCorpus.UndrivenCases(cases, driven))
        {
            run.Fail($"corpus case '{undriven}' loaded but nothing drove it (HPS-41)");
        }
    }

    private static void DriveValidity(TestRun run, VectorNode root)
    {
        // The table's floor and this host's floor must agree, or the "too old" row stops testing
        // anything. It sits exactly one below minSupportedManifestVersion by construction — and
        // across the era break that neighbour is an INTEGER, not a semver, which is the case a host
        // comparing only within one family gets wrong.
        run.Equal(
            root.Version("minSupportedManifestVersion") ?? "(absent)",
            ManifestVersions.MinSupportedManifestVersion,
            "the table's floor is this host's floor");

        List<string> precedence = [];
        foreach (VectorNode step in root.Items("precedence"))
        {
            precedence.Add(step.AsString()!);
        }

        HashSet<string> exercised = new(StringComparer.OrdinalIgnoreCase);

        foreach (VectorNode row in root.Items("rows"))
        {
            string name = row.Str("name")!;
            bool fileExists = row.Bool("fileExists") ?? false;

            CacheVerdict verdict = CacheValidity.Decide(
                fileExists,
                (long)(row.Double("onDiskSizeBytes") ?? 0.0),
                row.Str("computedSha256") ?? string.Empty,
                row.Str("expectedSha256"),
                row.Double("expectedSizeBytes") is { } expectedSize ? (long)expectedSize : null,
                row.Version("manifestVersion"));

            run.Equal(verdict.IsValid, row.Bool("valid") ?? false, $"[{name}] valid");
            run.Equal(verdict.Reason.ToString(), row.Str("reason")!, $"[{name}] reason");

            // Valid-and-unverified is a real state and the host must report it as such. Claiming
            // verification it did not do is a lie; calling it corrupt makes every legacy bundle
            // un-openable.
            run.Equal(verdict.IntegrityChecked, row.Bool("integrityChecked") ?? false, $"[{name}] integrityChecked");

            exercised.Add(verdict.IsValid ? "valid" : verdict.Reason.ToString());
        }

        // Every step of the declared precedence must actually be reached by some row, or the chain
        // is implied rather than exercised.
        foreach (string step in precedence)
        {
            run.True(exercised.Contains(step), $"a row exercises the '{step}' step of the precedence");
        }

        foreach (VectorNode state in root.Items("cacheState"))
        {
            bool fileExists = state.Bool("fileExists") ?? false;
            bool? valid = state.Bool("valid");

            CacheVerdict verdict = fileExists
                ? new CacheVerdict(valid ?? false, valid == true ? CacheInvalidReason.None : CacheInvalidReason.SizeMismatch, false)
                : new CacheVerdict(false, CacheInvalidReason.Missing, false);

            run.Equal(verdict.State(fileExists).ToString(), state.Str("state")!, "cache state");
        }
    }

    private static void DriveSanitisation(TestRun run, VectorNode root)
    {
        foreach (VectorNode vector in root.Items("vectors"))
        {
            string expectedStem = vector.Str("sanitisedDir")!;
            bool lossy = vector.Bool("lossy") ?? false;
            bool suffixed = vector.Bool("suffixed") ?? false;

            if (vector.Items("orderIdPair") is { Count: 2 } pair)
            {
                // Both sanitise alike; the hash suffix is what stops one bundle overwriting the
                // other. Asserting only "they differ" would pass for a random suffix, so both
                // stems are pinned too.
                SanitisedCacheKey left = CacheKeySanitiser.Sanitise(pair[0].AsString()!);
                SanitisedCacheKey right = CacheKeySanitiser.Sanitise(pair[1].AsString()!);

                run.Equal(left.Stem, expectedStem, "left stem");
                run.Equal(right.Stem, expectedStem, "right stem");
                run.Equal(left.IsLossy, lossy, "left lossy");
                run.Equal(right.IsLossy, lossy, "right lossy");

                if (vector.Bool("mustDiffer") == true)
                {
                    run.True(
                        !string.Equals(left.DirectoryName, right.DirectoryName, StringComparison.Ordinal),
                        "two ids that sanitise alike get different directories");
                }

                AssertSuffix(run, left, suffixed, pair[0].AsString()!);
                AssertSuffix(run, right, suffixed, pair[1].AsString()!);
                continue;
            }

            string orderId = vector.Str("orderId")!;
            SanitisedCacheKey key = CacheKeySanitiser.Sanitise(orderId);
            run.Equal(key.Stem, expectedStem, $"stem for '{orderId}'");
            run.Equal(key.IsLossy, lossy, $"lossy for '{orderId}'");
            AssertSuffix(run, key, suffixed, orderId);
        }

        VectorNode fileNames = root.Obj("fileNames")!;
        run.Equal(fileNames.Str("final")!, BundleCacheFileNames.Bundle, "final file name");
        run.Equal(fileNames.Str("partial")!, BundleCacheFileNames.Partial, "partial file name");
        run.Equal(fileNames.Str("sidecar")!, BundleCacheFileNames.Sidecar, "sidecar file name");
    }

    /// <summary>The suffix is the first 8 hex of sha256 over the RAW id, never over the mapped stem.</summary>
    private static void AssertSuffix(TestRun run, SanitisedCacheKey key, bool suffixed, string rawOrderId)
    {
        if (!suffixed)
        {
            run.Equal(key.DirectoryName, key.Stem, $"'{rawOrderId}' gets no suffix");
            return;
        }

        string expected = key.Stem + "_" + Sha256Digest.OfUtf8(rawOrderId)[..8];
        run.Equal(key.DirectoryName, expected, $"'{rawOrderId}' gets the digest of the RAW id");
    }

    private static void DriveDigest(TestRun run, VectorNode root)
    {
        run.Equal(root.Str("encoding")!, "64 lowercase hex characters", "the stated encoding");

        foreach (VectorNode vector in root.Items("vectors"))
        {
            string input = vector.Str("input")!;
            string expected = vector.Str("sha256")!;
            run.Equal(Sha256Digest.OfUtf8(input), expected, $"sha256('{Shorten(input)}')");
            run.Equal(expected.Length, 64, "64 characters");
        }

        foreach (VectorNode vector in root.Items("streamingEquivalence"))
        {
            StringBuilder joined = new();
            foreach (VectorNode chunk in vector.Items("chunks"))
            {
                joined.Append(chunk.AsString()!);
            }

            string oneShot = vector.Str("equalsOneShotOf")!;
            run.Equal(joined.ToString(), oneShot, "the chunks reassemble to the one-shot input");

            using MemoryStream stream = new(Encoding.UTF8.GetBytes(oneShot));
            run.Equal(
                Sha256Digest.OfStream(stream),
                Sha256Digest.OfUtf8(oneShot),
                $"streaming equals one-shot for '{Shorten(oneShot)}'");
        }

        // Trimmed and case-insensitive, then the rule that is easy to get backwards: two UNKNOWNS
        // are not equal. "unknown equals unknown" would report a verified match on a bundle nothing
        // checked, which inverts ⛔HPS-27.
        run.Contains(root.Str("comparison"), "case-insensitively", "the stated comparison rule");
        run.True(Sha256Digest.Equal(" AB12 ", "ab12"), "trimmed and case-insensitive");
        run.False(Sha256Digest.Equal("", ""), "two unknowns are not a match");
        run.False(Sha256Digest.Equal("ab12", null), "and neither is one unknown");
    }

    private static string Shorten(string text) => text.Length <= 16 ? text : text[..16] + "...";
}
