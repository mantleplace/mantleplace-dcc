namespace MantlePlace.Core;

public static class MarkRenders
{
    public static IReadOnlyList<int> Sizes { get; } = [16, 24, 32, 48, 64];

    public const int HeaderSlotPixels = 32;

    public static int SizeFor(int slotPixels, double displayScale)
        => RenderSizes.Pick(Sizes, slotPixels, displayScale);

    public static string FileNameFor(int slotPixels, double displayScale)
        => FileNameOf(SizeFor(slotPixels, displayScale));

    public static string FileNameOf(int size) => $"MantlePlaceMark_{size}.png";
}
