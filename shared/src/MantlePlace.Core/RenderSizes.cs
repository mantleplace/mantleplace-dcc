namespace MantlePlace.Core;

public static class RenderSizes
{
    public static int Pick(IReadOnlyList<int> sizes, int slotPixels, double displayScale)
    {
        ArgumentNullException.ThrowIfNull(sizes);

        if (sizes.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizes), "there is no render to pick");
        }

        if (slotPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slotPixels), slotPixels, "a slot has a positive size");
        }

        double scale = double.IsFinite(displayScale) && displayScale > 1.0 ? displayScale : 1.0;

        double wanted = (slotPixels * scale) - 1e-9;

        for (int i = 0; i < sizes.Count; i++)
        {
            if (sizes[i] >= wanted)
            {
                return sizes[i];
            }
        }

        return sizes[^1];
    }
}
