using System.Security.Cryptography;
using System.Text;

namespace MantlePlace.Revit.Core;

/// <summary>
/// sha256 hex and the comparison rule (<c>HPS-28</c>).
/// </summary>
/// <remarks>
/// The reference host carries three hand-written FIPS 180-4 implementations because its engine
/// primitive asserts on Windows and a module boundary forced a second copy. .NET has a usable one,
/// so this is a thin wrapper — but the corpus vectors are still driven, because the thing being
/// pinned is the ENCODING and the COMPARISON, and those are where hosts diverge: an uppercase hex
/// string from the vault compared ordinally against a lowercase computed one invalidates every
/// cached bundle at once.
/// </remarks>
public static class Sha256Digest
{
    /// <summary>Lowercase hex, 64 characters.</summary>
    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    public static string OfUtf8(string text) => Hex(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// Streams a file. Equivalent to hashing the bytes in one shot, which the corpus pins with a
    /// split across a block boundary — the place a hand-rolled implementation's buffering goes wrong.
    /// </summary>
    public static string OfStream(Stream stream) => Hex(SHA256.HashData(stream));

    /// <summary>
    /// <c>HPS-28</c>: trimmed and case-insensitive.
    /// </summary>
    /// <remarks>
    /// Two empty or whitespace strings are NOT equal here. An absent digest is <em>unknown</em>, and
    /// "unknown equals unknown" would report a verified match on a bundle nothing checked — the
    /// exact inversion ⛔<c>HPS-27</c> exists to prevent.
    /// </remarks>
    public static bool Equal(string? left, string? right)
    {
        string a = (left ?? string.Empty).Trim();
        string b = (right ?? string.Empty).Trim();
        return a.Length > 0 && b.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
