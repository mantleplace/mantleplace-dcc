namespace MantlePlace.Core;

public readonly record struct ImageSize(int Width, int Height)
{
    public bool IsUsable => Width > 0 && Height > 0;

    public override string ToString() => $"{Width} × {Height}";
}

public static class PngHeader
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> Ihdr => [0x49, 0x48, 0x44, 0x52];

    private const int IhdrDataLength = 13;

    public const int PrefixLength = 24;

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

        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
        {
            return null;
        }

        return new ImageSize((int)width, (int)height);
    }

    private static uint ReadBigEndianUInt32(ReadOnlySpan<byte> four)
        => ((uint)four[0] << 24) | ((uint)four[1] << 16) | ((uint)four[2] << 8) | four[3];
}
