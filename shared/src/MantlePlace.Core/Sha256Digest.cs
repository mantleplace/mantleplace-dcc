using System.Security.Cryptography;
using System.Text;

namespace MantlePlace.Core;

public static class Sha256Digest
{
    public static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    public static string OfUtf8(string text) => Hex(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string OfStream(Stream stream) => Hex(SHA256.HashData(stream));

    public static bool Equal(string? left, string? right)
    {
        string a = (left ?? string.Empty).Trim();
        string b = (right ?? string.Empty).Trim();
        return a.Length > 0 && b.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
