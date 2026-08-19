namespace MantlePlace.Revit.Core;

/// <summary>How one compound-structure layer splits into an imagery sliver plus what remains.</summary>
/// <param name="Ok">False when the split would build a degenerate structure and must not run.</param>
/// <param name="ImageryThickness">The thin top layer wearing the drape material.</param>
/// <param name="LowerThickness">The layer beneath, wearing the original material.</param>
public readonly record struct DrapeLayerSplit(bool Ok, double ImageryThickness, double LowerThickness);

/// <summary>
/// Splits a toposolid type's top layer into a thin imagery layer plus the remainder — the
/// mechanism that keeps the aerial photograph off the vertical faces.
/// </summary>
/// <remarks>
/// <para>
/// Total thickness is preserved: the imagery layer takes exactly the host's minimum layer
/// thickness and the remainder is what was there minus that, so whichever way Revit extends a
/// toposolid's structure from its points, the terrain cannot drift off its survey elevation.
/// </para>
/// <para>
/// Units are whatever the caller measured in — the arithmetic never converts, so handing it Revit's
/// internal feet on both arguments is correct by construction.
/// </para>
/// </remarks>
public static class DrapeLayering
{
    /// <summary>
    /// Splits <paramref name="totalThickness"/> into the imagery sliver and the remainder, or
    /// refuses.
    /// </summary>
    /// <remarks>
    /// Refused when either input is not a positive finite number, or when the remainder would be
    /// thinner than TWICE the minimum — a lower layer squeezed to the same order as the sliver on
    /// top of it is a degenerate structure, not a terrain, and the caller's rollback path is the
    /// honest answer.
    /// </remarks>
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

    private static bool IsPositiveFinite(double value)
        => double.IsFinite(value) && value > 0.0;
}
