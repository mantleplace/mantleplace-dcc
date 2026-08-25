using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>How one compound-structure layer splits into an imagery sliver plus what remains.</summary>
/// <param name="Ok">False when the split would build a degenerate structure and must not run.</param>
/// <param name="ImageryThickness">The thin top layer wearing the drape material.</param>
/// <param name="LowerThickness">The layer beneath, wearing the original material.</param>
public readonly record struct DrapeLayerSplit(bool Ok, double ImageryThickness, double LowerThickness);

/// <summary>What a type's EXISTING structure says should happen to it.</summary>
public enum DrapeLayerVerdict
{
    /// <summary>Splitting would build a degenerate structure. The caller rolls the drape back.</summary>
    Refuse,

    /// <summary>The top layer must be split into an imagery sliver plus the remainder.</summary>
    Layer,

    /// <summary>The imagery layer is already there. Write nothing.</summary>
    AlreadyLayered,
}

/// <summary>A verdict with the thicknesses it implies — both zero unless the verdict is <c>Layer</c>.</summary>
public readonly record struct DrapeLayerDecision(
    DrapeLayerVerdict Verdict,
    double ImageryThickness,
    double LowerThickness);

/// <summary>One layer of a compound structure, as a log line needs to see it.</summary>
/// <param name="Function">Revit's <c>MaterialFunctionAssignment</c>, as its own name.</param>
/// <param name="WidthInternalFeet">The layer's width in Revit's internal unit, decimal feet.</param>
/// <param name="MaterialName">The material the layer wears, or an empty string for "by category".</param>
public readonly record struct DrapeLayerLine(string Function, double WidthInternalFeet, string MaterialName);

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
/// internal feet on both arguments is correct by construction. <see cref="Describe"/> is the one
/// exception: a log line is read by a human, so it converts to millimetres.
/// </para>
/// </remarks>
public static class DrapeLayering
{
    /// <summary>Revit's internal length unit is the decimal INTERNATIONAL foot, exactly 0.3048 m.</summary>
    private const double MillimetresPerInternalFoot = 304.8;

    /// <summary>
    /// A layer thickness in the unit a human judges it in. One owner for the constant, so the log
    /// line and the curator's sentence can never quote two different numbers for one layer.
    /// </summary>
    public static double MillimetresFromInternalFeet(double internalFeet)
        => internalFeet * MillimetresPerInternalFoot;

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

    /// <summary>
    /// Whether a type still needs its imagery layer, already carries one, or must be refused.
    /// </summary>
    /// <param name="topLayerWearsImagery">
    /// Whether layer 0 already wears the drape material. This is the whole re-run guard, and it is a
    /// question about the STRUCTURE rather than about which import happens to be running.
    /// </param>
    /// <remarks>
    /// <para>
    /// ⛔ <b>The guard used to be "restructure only on the import that duplicated the type".</b> That
    /// kept the real guarantee — never a second imagery layer — but bought it with an assumption
    /// nobody checked: that a type found by name is already layered. A SINGLE-layer
    /// <c>Mantle Place Site Imagery</c> type, left by a build predating the layering or by a
    /// curator's edit, was therefore reused verbatim on every later import, wearing the photograph
    /// on every vertical face with no re-import able to repair it — the exact defect the layering
    /// exists to prevent. Asking the structure costs one read and cannot go stale.
    /// </para>
    /// <para>
    /// The signal is sound because the drape material is this plugin's own, named from the bundle's
    /// cache key and resolved by that name on every run: only a previous run of THIS import can have
    /// put it on layer 0.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Whether exactly one layer wears the imagery and it is layer 0 — the shape the drape claims to
    /// build, checked against what Revit actually stored.
    /// </summary>
    /// <remarks>
    /// Reported, never gated. <c>SetCompoundStructure</c>'s behaviour has never been observed inside
    /// Revit, and turning an unobserved read into a new refusal path is how a working drape gets
    /// declined for a reason nobody can diagnose.
    /// </remarks>
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

    /// <summary>
    /// A whole layer stack as one log line, thicknesses in millimetres.
    /// </summary>
    /// <remarks>
    /// Millimetres because the number this prints is what decides the documented escalation to
    /// <c>Document.Paint</c> — whether the imagery sliver is legible at site-view zoom — and nobody
    /// reads decimal feet to make that call. Three decimals: the sliver is a fraction of a
    /// millimetre, and rounding it to zero would hide the very quantity being judged.
    /// </remarks>
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
