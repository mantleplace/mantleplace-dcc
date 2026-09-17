namespace MantlePlace.Revit.Core;

/// <summary>
/// One sRGB colour, as three bytes.
/// </summary>
/// <remarks>
/// A bare triple rather than anything from a UI framework, because this assembly is pure: the shim
/// turns it into a <c>System.Windows.Media.Color</c>, and a headless test reads
/// <see cref="Hex"/>.
/// </remarks>
/// <param name="R">Red, 0–255.</param>
/// <param name="G">Green, 0–255.</param>
/// <param name="B">Blue, 0–255.</param>
public readonly record struct BrandColour(byte R, byte G, byte B)
{
    /// <summary>The colour as <c>#RRGGBB</c>, upper case — how every brand document writes it.</summary>
    public string Hex => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// The brand colours this host is allowed to spend, and the states derived from them.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>One accent, on one control per window.</b> A Revit add-in that paints its own windows in its
/// own colours stops looking like part of Revit, which is the failure the whole ribbon pass exists to
/// fix. Everything but the single primary action keeps the host's default chrome, and there is no
/// brand background, no brand list row and no brand text colour.
/// </para>
/// <para>
/// <see cref="Mantle"/> is the mark's own tile colour — the value <c>tools/brand-assets</c> mattes
/// the monogram out of, and the value the reference host names <c>MantlePlacePalette::Mantle()</c>.
/// Two hosts, one spelling. <see cref="OnMantle"/> is white for the same reason the monogram is:
/// this is the brand's own lockup, and the reference host's primary button already reads white on
/// orange.
/// </para>
/// </remarks>
public static class BrandPalette
{
    /// <summary>The brand orange: the mark's tile, and the fill of the one primary action.</summary>
    public static BrandColour Mantle => new(0xFF, 0x71, 0x10);

    /// <summary>What is legible on <see cref="Mantle"/> — white, as the monogram on the tile is.</summary>
    public static BrandColour OnMantle => new(0xFF, 0xFF, 0xFF);

    /// <summary>How far toward white the primary action's fill moves under the cursor.</summary>
    public const double HoverMix = 0.14;

    /// <summary>How far toward white it moves while held. The reference host's numbers, both.</summary>
    public const double PressedMix = 0.24;

    /// <summary>
    /// <paramref name="colour"/> moved <paramref name="amount"/> of the way to white.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A colourless overlay rather than a second brand colour: every channel closes the same fraction
    /// of its own distance to white, so hover and press read as the same button lit rather than as a
    /// jump to a different accent. That is what the reference host does for
    /// <c>MantlePlace.Button.Primary</c>, and matching it is the point.
    /// </para>
    /// <para>
    /// <paramref name="amount"/> outside 0–1, and NaN, clamp rather than throw. This is evaluated
    /// while a window is being built on Revit's dispatcher, where a throw is how an add-in ends the
    /// process rather than how it reports a problem.
    /// </para>
    /// </remarks>
    /// <param name="colour">The colour to lighten.</param>
    /// <param name="amount">0 for the colour itself, 1 for white.</param>
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
