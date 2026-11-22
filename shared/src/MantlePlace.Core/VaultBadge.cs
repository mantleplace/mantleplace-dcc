using System.Globalization;

namespace MantlePlace.Core;

public static class VaultBadge
{
    public const int MaxCount = 9;

    public const double DiscFraction = 0.56;

    public static bool DrawsCount(int slotPixels) => slotPixels >= 32;

    public const string ToolTip = "Browse the bundles in your vault and import one.";

    public static string? CountText(int count) => count switch
    {
        <= 0 => null,
        > MaxCount => MaxCount.ToString(CultureInfo.InvariantCulture) + "+",
        _ => count.ToString(CultureInfo.InvariantCulture),
    };

    public static string ToolTipFor(int count) => count switch
    {
        <= 0 => ToolTip,
        1 => "1 update in your vault. Open the vault to see it.",
        _ => $"{count.ToString(CultureInfo.InvariantCulture)} updates in your vault. Open the vault to see them.",
    };
}
