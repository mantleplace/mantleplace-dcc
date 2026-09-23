using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>
/// What the Vault button shows while orders have news the curator has not seen — a Prepare that
/// ended, or an order new in the vault. Pure.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>A badge on the glyph, never a change to the face text.</b> <c>Vault</c> is one of the words
/// <c>HPS-51</c> fixes for every host, so <c>Vault (2)</c> would be a second spelling of it. The count
/// is drawn over the button's picture and said in its tooltip, and the face keeps its word.
/// </para>
/// <para>
/// The badge is the half of the notice that waits. The popup is gone after
/// <see cref="PrepareNotices.ShowSeconds"/>, and a curator looking at the model the whole time has
/// missed it; the badge stays until the vault is opened.
/// </para>
/// </remarks>
public static class VaultBadge
{
    /// <summary>The highest count drawn as itself. A 32 px badge holds one digit legibly.</summary>
    public const int MaxCount = 9;

    /// <summary>The badge's diameter as a share of the slot it sits in, in the top-right corner.</summary>
    public const double DiscFraction = 0.56;

    /// <summary>
    /// Whether a slot of <paramref name="slotPixels"/> logical pixels draws the count. The 16 px slot
    /// has a disc of nine pixels, where a digit is a smudge, so it shows a plain dot and the tooltip
    /// carries the number.
    /// </summary>
    public static bool DrawsCount(int slotPixels) => slotPixels >= 32;

    /// <summary>The Vault button's tooltip when there is no news.</summary>
    public const string ToolTip = "Browse the bundles in your vault and import one.";

    /// <summary>The text drawn in the badge, or <c>null</c> for no badge.</summary>
    public static string? CountText(int count) => count switch
    {
        <= 0 => null,
        > MaxCount => MaxCount.ToString(CultureInfo.InvariantCulture) + "+",
        _ => count.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>The Vault button's tooltip for <paramref name="count"/> orders with news.</summary>
    public static string ToolTipFor(int count) => count switch
    {
        <= 0 => ToolTip,
        1 => "1 update in your vault. Open the vault to see it.",
        _ => $"{count.ToString(CultureInfo.InvariantCulture)} updates in your vault. Open the vault to see them.",
    };
}
