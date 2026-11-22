using MantlePlace.Core;

namespace MantlePlace.Core.Tests;

internal static class CacheKeySanitiserTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("a lossless uuid passes through and gets no suffix", () =>
        {
            SanitisedCacheKey key = CacheKeySanitiser.Sanitise("3f285101-0310-425b-b06b-bdb73b025b6a");
            run.Equal(key.Stem, "3f285101-0310-425b-b06b-bdb73b025b6a", "stem");
            run.False(key.IsLossy, "a uuid is not lossy");
            run.Equal(key.DirectoryName, "3f285101-0310-425b-b06b-bdb73b025b6a", "directory name");
        });

        run.Case("a non-latin id survives unchanged — 'alphanumeric' is Unicode-aware", () =>
        {
            SanitisedCacheKey key = CacheKeySanitiser.Sanitise("заказ-2026");
            run.Equal(key.Stem, "заказ-2026", "cyrillic stem");
            run.False(key.IsLossy, "cyrillic is alphanumeric, so not lossy");
            run.Equal(key.DirectoryName, "заказ-2026", "cyrillic directory name");
        });

        run.Case("the three reserved results map to _ and are lossy", () =>
        {
            run.Equal(CacheKeySanitiser.Sanitise("..").DirectoryName, "__5ec1f7e7", "..");
            run.Equal(CacheKeySanitiser.Sanitise(".").DirectoryName, "__cdb4ee2a", ".");
            run.Equal(CacheKeySanitiser.Sanitise("").DirectoryName, "__e3b0c442", "empty");
        });

        run.Case("path traversal is neutralised before it reaches the filesystem", () =>
        {
            SanitisedCacheKey key = CacheKeySanitiser.Sanitise("../../etc/passwd");
            run.Equal(key.Stem, ".._.._etc_passwd", "stem");
            run.True(key.IsLossy, "the slashes were replaced, so it is lossy");
            run.Equal(key.DirectoryName, ".._.._etc_passwd_3754d6cb", "directory name");
        });

        run.Case("two ids that sanitise alike get different directories", () =>
        {
            SanitisedCacheKey slash = CacheKeySanitiser.Sanitise("a/b");
            SanitisedCacheKey colon = CacheKeySanitiser.Sanitise("a:b");

            run.Equal(slash.Stem, "a_b", "a/b stem");
            run.Equal(colon.Stem, "a_b", "a:b stem");
            run.Equal(slash.DirectoryName, "a_b_c14cddc0", "a/b directory name");
            run.Equal(colon.DirectoryName, "a_b_6783a31e", "a:b directory name");
            run.True(
                !string.Equals(slash.DirectoryName, colon.DirectoryName, StringComparison.Ordinal),
                "the hash suffix is what stops one bundle overwriting the other");
        });

        run.Case("an id that is already a single underscore is NOT lossy", () =>
        {
            SanitisedCacheKey key = CacheKeySanitiser.Sanitise("_");
            run.False(key.IsLossy, "an underscore is kept verbatim");
            run.Equal(key.DirectoryName, "_", "directory name");
        });

        run.Case("an astral alphanumeric is not alphanumeric — the rule is BMP-bounded", () =>
        {
            SanitisedCacheKey key = CacheKeySanitiser.Sanitise("\U0001D7CE");
            run.Equal(key.Stem, "_", "one astral code point, one underscore");
            run.True(key.IsLossy, "an astral code point is never kept, so it is lossy");
            run.Equal(key.DirectoryName, "__867e9955", "directory name");
        });

        run.Case("an astral NON-alphanumeric collapses to exactly one underscore", () =>
        {
            SanitisedCacheKey key = CacheKeySanitiser.Sanitise("a\U0001F600b");
            run.Equal(key.Stem, "a_b", "one rune in, one underscore out");
            run.True(key.IsLossy, "the emoji was replaced");
            run.Equal(key.DirectoryName, "a_b_6fba5b2e", "directory name");
        });

        run.Case("an UNPAIRED surrogate is one non-alphanumeric code point, not two", () =>
        {
            SanitisedCacheKey key = CacheKeySanitiser.Sanitise("\ud835");
            run.Equal(key.Stem, "_", "an unpaired surrogate is exactly one underscore");
            run.True(key.IsLossy, "it was replaced, so it is lossy");
            run.Equal(key.DirectoryName, "__83d544cc", "directory name");
        });

        return run.Report("cache key sanitisation");
    }
}
