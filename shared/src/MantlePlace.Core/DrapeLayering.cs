using System.Globalization;

namespace MantlePlace.Core;

public readonly record struct DrapeLayerSplit(bool Ok, double ImageryThickness, double LowerThickness);

public enum DrapeLayerVerdict
{
    Refuse,

    Layer,

    AlreadyLayered,
}

public readonly record struct DrapeLayerDecision(
    DrapeLayerVerdict Verdict,
    double ImageryThickness,
    double LowerThickness);

public readonly record struct DrapeLayerLine(string Function, double WidthInternalFeet, string MaterialName);

public static class DrapeLayering
{
    private const double MillimetresPerInternalFoot = 304.8;

    public const string ImageryNamePrefix = "Mantle Place Site Imagery";

    public static string ImageryName(string bundleStem) => $"{ImageryNamePrefix} {bundleStem}";

    public static double MillimetresFromInternalFeet(double internalFeet)
        => internalFeet * MillimetresPerInternalFoot;

    public static DrapeLayerSplit Split(double totalThickness, double minimumLayerThickness)
    {
        if (!IsPositiveFinite(totalThickness) || !IsPositiveFinite(minimumLayerThickness))
        {
            return default;
        }

        double lower = totalThickness - minimumLayerThickness;
        if (lower < 2.0 * minimumLayerThickness)
        {
            return default;
        }

        return new DrapeLayerSplit(true, minimumLayerThickness, lower);
    }

    public static DrapeLayerDecision Decide(
        bool topLayerWearsImagery,
        double topLayerWidth,
        double minimumLayerThickness)
    {
        if (topLayerWearsImagery)
        {
            return new DrapeLayerDecision(DrapeLayerVerdict.AlreadyLayered, 0.0, 0.0);
        }

        DrapeLayerSplit split = Split(topLayerWidth, minimumLayerThickness);
        return split.Ok
            ? new DrapeLayerDecision(DrapeLayerVerdict.Layer, split.ImageryThickness, split.LowerThickness)
            : default;
    }

    public static bool ImageryIsTopAndOnly(IReadOnlyList<bool> layersWearingImagery)
    {
        ArgumentNullException.ThrowIfNull(layersWearingImagery);

        if (layersWearingImagery.Count == 0 || !layersWearingImagery[0])
        {
            return false;
        }

        for (int layer = 1; layer < layersWearingImagery.Count; layer++)
        {
            if (layersWearingImagery[layer])
            {
                return false;
            }
        }

        return true;
    }

    public static string Describe(IReadOnlyList<DrapeLayerLine> layers)
    {
        ArgumentNullException.ThrowIfNull(layers);

        if (layers.Count == 0)
        {
            return "no layers";
        }

        string[] described = new string[layers.Count];
        for (int layer = 0; layer < layers.Count; layer++)
        {
            DrapeLayerLine line = layers[layer];
            double millimetres = line.WidthInternalFeet * MillimetresPerInternalFoot;
            string material = string.IsNullOrEmpty(line.MaterialName) ? "by category" : line.MaterialName;
            described[layer] = string.Create(
                CultureInfo.InvariantCulture,
                $"{layer} {line.Function} {millimetres:F3} mm \"{material}\"");
        }

        return string.Join(" / ", described);
    }

    private static bool IsPositiveFinite(double value)
        => double.IsFinite(value) && value > 0.0;
}
