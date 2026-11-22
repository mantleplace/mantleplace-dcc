namespace MantlePlace.Core.Tests;

internal static class PngHeaderTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("a valid header yields the image's dimensions", () =>
        {
            ImageSize? size = PngHeader.TryReadSize(Png(4767, 4733));

            run.Equal(size?.Width ?? 0, 4767, "width");
            run.Equal(size?.Height ?? 0, 4733, "height");
            run.True(size?.IsUsable ?? false, "and it is usable");
        });

        run.Case("dimensions are read big-endian, as the spec writes them", () =>
        {
            ImageSize? size = PngHeader.TryReadSize(Png(258, 1));

            run.Equal(size?.Width ?? 0, 258, "width");
            run.Equal(size?.Height ?? 0, 1, "height");
        });

        run.Case("a truncated file is not a PNG", () =>
        {
            byte[] full = Png(16, 16);
            run.True(PngHeader.TryReadSize(full.AsSpan(0, 12)) is null, "twelve bytes is not an answer");
            run.True(PngHeader.TryReadSize([]) is null, "nor is nothing");
        });

        run.Case("the wrong magic is not a PNG", () =>
        {
            byte[] jpeg = new byte[PngHeader.PrefixLength];
            jpeg[0] = 0xFF;
            jpeg[1] = 0xD8;
            jpeg[2] = 0xFF;
            jpeg[3] = 0xE0;

            run.True(PngHeader.TryReadSize(jpeg) is null, "a JPEG is refused");
        });

        run.Case("correct magic with a corrupt IHDR is not a PNG", () =>
        {
            byte[] wrongChunk = Png(16, 16);
            wrongChunk[12] = (byte)'I';
            wrongChunk[13] = (byte)'D';
            wrongChunk[14] = (byte)'A';
            wrongChunk[15] = (byte)'T';
            run.True(PngHeader.TryReadSize(wrongChunk) is null, "IHDR must be the first chunk");

            byte[] wrongLength = Png(16, 16);
            wrongLength[11] = 12;
            run.True(PngHeader.TryReadSize(wrongLength) is null, "an IHDR is 13 bytes by the spec");
        });

        run.Case("a zero dimension is refused — there is no grid to corroborate against", () =>
        {
            run.True(PngHeader.TryReadSize(Png(0, 4733)) is null, "zero width");
            run.True(PngHeader.TryReadSize(Png(4767, 0)) is null, "zero height");
        });

        run.Case("a dimension past int.MaxValue is a corrupt header, not an image", () =>
        {
            byte[] huge = Png(1, 1);
            huge[16] = 0xFF;
            huge[17] = 0xFF;
            huge[18] = 0xFF;
            huge[19] = 0xFF;

            run.True(PngHeader.TryReadSize(huge) is null, "and it must not wrap to a negative width");
        });

        return run.Report("png header");
    }

    private static byte[] Png(uint width, uint height)
    {
        byte[] prefix =
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D,
            0x49, 0x48, 0x44, 0x52,
            0, 0, 0, 0,
            0, 0, 0, 0,
        ];

        WriteBigEndian(prefix.AsSpan(16, 4), width);
        WriteBigEndian(prefix.AsSpan(20, 4), height);
        return prefix;
    }

    private static void WriteBigEndian(Span<byte> four, uint value)
    {
        four[0] = (byte)(value >> 24);
        four[1] = (byte)(value >> 16);
        four[2] = (byte)(value >> 8);
        four[3] = (byte)value;
    }
}
