using System.Security.Cryptography;
using System.Text;

namespace MantlePlace.Revit.Core;

/// <summary>The result of turning one order id into one filesystem-safe directory name.</summary>
/// <param name="Stem">The mapped string, before any collision suffix.</param>
/// <param name="IsLossy">True when the mapping changed the string, which is what earns a suffix.</param>
/// <param name="DirectoryName">What actually goes on disk: <paramref name="Stem"/>, suffixed when lossy.</param>
public readonly record struct SanitisedCacheKey(string Stem, bool IsLossy, string DirectoryName);

/// <summary>
/// ⛔<c>HPS-30</c>: an order id becomes a filesystem path only after sanitisation, and lossy
/// sanitisation gets a hash suffix.
/// </summary>
/// <remarks>
/// <para>
/// The two halves pull in opposite directions — neutralise traversal, but stay collision-free — and
/// implementing only the first is the common mistake. It is the mistake this host made: the Revit
/// shim's own <c>SanitiseDirectoryName</c> mapped <see cref="System.IO.Path.GetInvalidFileNameChars"/>
/// to <c>_</c> with no suffix, so two bundles differing only in an invalid character shared one
/// retained directory and one set of Revit links pointed into whichever extracted last.
/// </para>
/// <para>
/// This lives in the pure core rather than beside the file system it feeds because it is protocol,
/// not I/O: every host must derive the same directory from the same order id or the same purchase
/// is cached twice. Corpus <c>cache.keySanitisation</c> is the cross-host pin.
/// </para>
/// </remarks>
public static class CacheKeySanitiser
{
    /// <summary>Characters kept verbatim alongside the Unicode alphanumerics.</summary>
    private const string AlsoKept = "._-";

    /// <summary>Hex characters of <c>sha256(utf8(rawOrderId))</c> appended to a lossy stem.</summary>
    private const int SuffixHexLength = 8;

    /// <summary>
    /// The last code point <c>HPS-30</c> asks a host to classify — the top of the Basic Multilingual
    /// Plane. Anything above it is non-alphanumeric by rule rather than by lookup.
    /// </summary>
    private const int MaxClassifiedCodePoint = 0xFFFF;

    /// <summary>Maps one raw order id to the directory name it is cached under.</summary>
    public static SanitisedCacheKey Sanitise(string rawOrderId)
    {
        ArgumentNullException.ThrowIfNull(rawOrderId);

        string stem = MapCharacters(rawOrderId);

        // Applied after the mapping, not before: "a/b" maps to "a_b" and is fine, but ".." maps to
        // ".." — unchanged and still a traversal — so the reserved-result check has to see the
        // mapped value.
        if (stem is "" or "." or "..")
        {
            stem = "_";
        }

        bool isLossy = !string.Equals(stem, rawOrderId, StringComparison.Ordinal);

        return new SanitisedCacheKey(
            stem,
            isLossy,
            isLossy ? stem + "_" + ShortDigestOf(rawOrderId) : stem);
    }

    /// <summary>
    /// Keeps Unicode alphanumerics plus <c>.</c>, <c>_</c> and <c>-</c>; maps everything else to
    /// <c>_</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Enumerated by <see cref="Rune"/>, not by <see cref="char"/>: one rune in, exactly one
    /// character out. A UTF-16 walk sees an astral character as two surrogate halves and writes
    /// <c>__</c> where this writes <c>_</c> — same order id, different directory, re-downloaded
    /// every session, and neither host able to notice.
    /// </para>
    /// <para>
    /// "Alphanumeric" means the platform's Unicode-aware classification <b>bounded to the Basic
    /// Multilingual Plane</b>: every code point above U+FFFF is non-alphanumeric here, whatever its
    /// Unicode category. That is not what this host would choose alone — U+1D7CE is category Nd and
    /// <see cref="Rune.IsLetterOrDigit(Rune)"/> would keep it. It is what the reference host can
    /// implement, because its predicate takes a UTF-16 code unit and cannot classify an astral code
    /// point at all; agreeing on "keep" would take a hand-maintained Unicode table per host, and
    /// two of those drift. The distinctness the keeping would have bought is already carried by the
    /// collision suffix, which hashes the RAW id.
    /// </para>
    /// </remarks>
    private static string MapCharacters(string raw)
    {
        StringBuilder mapped = new(raw.Length);

        foreach (Rune rune in raw.EnumerateRunes())
        {
            if (rune.Value > MaxClassifiedCodePoint)
            {
                mapped.Append('_');
                continue;
            }

            if (Rune.IsLetterOrDigit(rune) || (rune.IsAscii && AlsoKept.Contains((char)rune.Value, StringComparison.Ordinal)))
            {
                mapped.Append(rune);
            }
            else
            {
                mapped.Append('_');
            }
        }

        return mapped.ToString();
    }

    /// <summary>
    /// The first 8 hex characters of the digest of the RAW id — never of the mapped stem, which is
    /// the value two colliding ids already share.
    /// </summary>
    private static string ShortDigestOf(string rawOrderId)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(rawOrderId));

        // ToHexStringLower is .NET 9; this library's floor is net8.0 so the runtime Revit 2025
        // hosts can load it.
        return Convert.ToHexString(digest).ToLowerInvariant()[..SuffixHexLength];
    }
}
