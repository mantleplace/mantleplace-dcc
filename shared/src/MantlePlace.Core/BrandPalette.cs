namespace MantlePlace.Core;

public readonly record struct BrandColour(byte R, byte G, byte B)
{
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
}

public static class BrandPalette
{
    public static BrandColour Mantle => new(0xFF, 0x71, 0x10);

    public static BrandColour OnMantle => new(0xFF, 0xFF, 0xFF);

    public const double HoverMix = 0.14;

    public const double PressedMix = 0.24;

    public static BrandColour MixToWhite(BrandColour colour, double amount)
    {
        double mix = double.IsNaN(amount) ? 0 : Math.Clamp(amount, 0, 1);

        return new BrandColour(
            Lift(colour.R, mix),
            Lift(colour.G, mix),
            Lift(colour.B, mix));
    }

    private static byte Lift(byte channel, double mix)
        => (byte)Math.Round(channel + ((255 - channel) * mix), MidpointRounding.AwayFromZero);
}
