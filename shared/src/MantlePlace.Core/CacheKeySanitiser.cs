using System.Security.Cryptography;
using System.Text;

namespace MantlePlace.Core;

public readonly record struct SanitisedCacheKey(string Stem, bool IsLossy, string DirectoryName);

public static class CacheKeySanitiser
{
    private const string AlsoKept = "._-";

    private const int SuffixHexLength = 8;

    private const int MaxClassifiedCodePoint = 0xFFFF;

    public static SanitisedCacheKey Sanitise(string rawOrderId)
    {
        ArgumentNullException.ThrowIfNull(rawOrderId);

        string stem = MapCharacters(rawOrderId);

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

    private static string ShortDigestOf(string rawOrderId)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(rawOrderId));

        return Convert.ToHexString(digest).ToLowerInvariant()[..SuffixHexLength];
    }
}
