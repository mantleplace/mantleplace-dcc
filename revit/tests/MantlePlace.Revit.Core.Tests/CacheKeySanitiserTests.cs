using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Host-local coverage for the ⛔<c>HPS-30</c> implementation, mirroring every row of corpus
/// <c>cache.keySanitisation</c> by hand.
/// </summary>
/// <remarks>
/// <para>
/// The corpus driver claims the <c>cache</c> group separately. These stay because they pin the two
/// halves the rule says pull in opposite directions — neutralise traversal, stay collision-free —
/// against literal expected strings rather than against whatever the vector happens to say. A
/// hand-written expectation and a vector-driven one failing together is a real regression; only one
/// failing localises it to the reader or to the implementation.
/// </para>
/// <para>
/// The suffix values are the real first-8 of <c>sha256(utf8(rawOrderId))</c>, computed independently
/// of this implementation. Asserting the literal rather than "some 8 hex characters" is what proves
/// the digest is over the RAW id and not over the already-sanitised stem — the mistake that makes
/// <c>a/b</c> and <c>a:b</c> collide again after all the work of avoiding it.
/// </para>
/// </remarks>
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
            // A host that hardcodes [A-Za-z0-9] mangles this to "_____-2026", marks it lossy, and
            // lands the same order in a different directory than the reference. Re-downloaded every
            // session, silently. This case is the only thing that catches it.
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
            // "_" is in the keep set and is not one of the three reserved results, so nothing
            // changed and no suffix is appended. Getting this wrong suffixes a stable id and
            // orphans its existing cache directory.
            SanitisedCacheKey key = CacheKeySanitiser.Sanitise("_");
            run.False(key.IsLossy, "an underscore is kept verbatim");
            run.Equal(key.DirectoryName, "_", "directory name");
        });

        run.Case("an astral alphanumeric is not alphanumeric — the rule is BMP-bounded", () =>
        {
            // U+1D7CE MATHEMATICAL BOLD DIGIT ZERO is Unicode category Nd, and this host could
            // keep it. It does not, because the reference's alnum predicate takes a UTF-16 code
            // UNIT and cannot classify a code point above U+FFFF at all — so the only way to make
            // the two hosts agree on "keep" is two hand-maintained Unicode tables that will drift.
            // HPS-30 bounds "alphanumeric" to the BMP instead: above U+FFFF the answer is "no" on
            // every host, needing no table anywhere. The collision suffix is over the RAW id, so
            // two distinct astral ids still get distinct directories.
            SanitisedCacheKey key = CacheKeySanitiser.Sanitise("\U0001D7CE");
            run.Equal(key.Stem, "_", "one astral code point, one underscore");
            run.True(key.IsLossy, "an astral code point is never kept, so it is lossy");
            run.Equal(key.DirectoryName, "__867e9955", "directory name");
        });

        run.Case("an astral NON-alphanumeric collapses to exactly one underscore", () =>
        {
            // U+1F600 GRINNING FACE is one rune and therefore one replacement. Enumerating chars
            // would emit two.
            SanitisedCacheKey key = CacheKeySanitiser.Sanitise("a\U0001F600b");
            run.Equal(key.Stem, "a_b", "one rune in, one underscore out");
            run.True(key.IsLossy, "the emoji was replaced");
            run.Equal(key.DirectoryName, "a_b_6fba5b2e", "directory name");
        });

        run.Case("an UNPAIRED surrogate is one non-alphanumeric code point, not two", () =>
        {
            // A lone high surrogate is not a code point at all, so it can never be alphanumeric.
            // EnumerateRunes yields a single U+FFFD for it and Encoding.UTF8 substitutes the same
            // replacement character before hashing, which is what the suffix below is over. This
            // is the row a hand-rolled UTF-16 decoder gets wrong — by running off the end looking
            // for the low half, or by emitting two underscores for one broken character.
            SanitisedCacheKey key = CacheKeySanitiser.Sanitise("\ud835");
            run.Equal(key.Stem, "_", "an unpaired surrogate is exactly one underscore");
            run.True(key.IsLossy, "it was replaced, so it is lossy");
            run.Equal(key.DirectoryName, "__83d544cc", "directory name");
        });

        return run.Report("cache key sanitisation");
    }
}
