namespace MantlePlace.Revit.Core;

/// <summary>An image's pixel dimensions.</summary>
public readonly record struct ImageSize(int Width, int Height)
{
    /// <summary>True only when both dimensions are positive.</summary>
    public bool IsUsable => Width > 0 && Height > 0;

    public override string ToString() => $"{Width} × {Height}";
}

/// <summary>
/// Reads a PNG's pixel dimensions out of its first 24 bytes. Pure: bytes in, facts out.
/// </summary>
/// <remarks>
/// <para>
/// It exists for one caller — the imagery drape, whose UV extent this host will not accept on the
/// manifest's word alone. The drape's extent is corroborated against the file's own
/// grid, and that needs the file's own grid, which is 24 bytes at a fixed offset.
/// </para>
/// <para>
/// <b>Only the header, never the pixels.</b> A drape is ~50 MB and the planner runs before anything
/// is extracted, so decoding the image would trade a refusal that costs nothing for one that costs
/// a minute. IHDR is mandatory and, by the spec, always the first chunk — so the answer is at a
/// known offset in a fixed-size prefix, and a file where it is not there is not a PNG.
/// </para>
/// </remarks>
public static class PngHeader
{
    /// <summary>The eight-byte PNG signature.</summary>
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>IHDR, as the four ASCII bytes the chunk type is spelled with.</summary>
    private static ReadOnlySpan<byte> Ihdr => [0x49, 0x48, 0x44, 0x52];

    /// <summary>The declared length of an IHDR chunk's data, which the spec fixes at 13.</summary>
    private const int IhdrDataLength = 13;

    /// <summary>Bytes needed to answer: signature, chunk length, chunk type, width, height.</summary>
    public const int PrefixLength = 24;

    /// <summary>
    /// The image's dimensions, or <c>null</c> when <paramref name="prefix"/> is not the start of a
    /// PNG.
    /// </summary>
    /// <remarks>
    /// Every rejection returns <c>null</c> rather than throwing, and the caller turns that into a
    /// stated refusal. A truncated prefix, a JPEG, and a PNG whose IHDR has been corrupted are all
    /// the same answer to the only question asked here — <em>can this file's grid be read</em> — and
    /// the drape is skipped with a reason either way.
    /// </remarks>
    public static ImageSize? TryReadSize(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length < PrefixLength
            || !prefix[..8].SequenceEqual(Signature)
            || ReadBigEndianUInt32(prefix[8..12]) != IhdrDataLength
            || !prefix[12..16].SequenceEqual(Ihdr))
        {
            return null;
        }

        uint width = ReadBigEndianUInt32(prefix[16..20]);
        uint height = ReadBigEndianUInt32(prefix[20..24]);

        // The spec forbids a zero dimension, and anything past int.MaxValue is a corrupt header
        // rather than an image — either way there is no grid to corroborate an extent against.
        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
        {
            return null;
        }

        return new ImageSize((int)width, (int)height);
    }

    private static uint ReadBigEndianUInt32(ReadOnlySpan<byte> four)
        => ((uint)four[0] << 24) | ((uint)four[1] << 16) | ((uint)four[2] << 8) | four[3];
}
