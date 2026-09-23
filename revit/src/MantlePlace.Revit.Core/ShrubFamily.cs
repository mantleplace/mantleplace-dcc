using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>
/// The <c>Mantle Place Shrub</c> Planting family: its name, the two instance parameters the planting
/// step drives, and the proportions its dome takes from them. Pure.
/// </summary>
/// <remarks>
/// <para>
/// A family of its own rather than a second type of <see cref="TreeFamily"/>, for a tree point the
/// platform published as a shrub (<see cref="FoliageType.Shrub"/>). The shape is a dome with no trunk,
/// approximated by two stacked blends: from <see cref="BaseRadiusFraction"/> of the crown at the
/// ground out to the full crown at <see cref="WaistHeightFraction"/> of the height, then in to
/// <see cref="ApexRadiusFraction"/> of it at the top. Two blends rather than a revolve, because the
/// developer command that authors the family drives every size by a radial dimension on each arc of
/// a sketch — a circle has one, a revolve's profile does not.
/// </para>
/// <para>
/// As with the tree, the proportions live here because two things build from them and must agree:
/// the family's own formulas, written once by the authoring command, and the DirectShape fallback
/// the planting step uses when this family cannot be loaded.
/// </para>
/// <para>
/// ⛔ Nothing here classifies. A point is a shrub because the platform said so; its height and crown
/// only size the dome and decide whether Revit can build it (<see cref="Fits"/>).
/// </para>
/// </remarks>
public static class ShrubFamily
{
    /// <summary>The family's name in a project, which Revit takes from the file's name on load.</summary>
    public const string FamilyName = "Mantle Place Shrub";

    /// <summary>The file the family ships as — its name, because a loaded family is named after it.</summary>
    public const string FileName = FamilyName + ".rfa";

    /// <summary>The instance parameter carrying <c>height_m</c>: total height, ground to apex.</summary>
    /// <remarks>
    /// Not <c>Height</c>, for <see cref="TreeFamily.HeightParameter"/>'s reason: every Planting family
    /// owns a built-in type parameter of that name.
    /// </remarks>
    public const string HeightParameter = "Shrub Height";

    /// <summary>The instance parameter carrying <c>crown_radius_m</c>: the dome at its widest.</summary>
    public const string CrownRadiusParameter = TreeFamily.CrownRadiusParameter;

    /// <summary>The dome's radius at the ground, as a fraction of its widest.</summary>
    public const double BaseRadiusFraction = 0.6;

    /// <summary>Where the dome is widest, as a fraction of its height.</summary>
    public const double WaistHeightFraction = 0.5;

    /// <summary>The dome's radius at its apex, as a fraction of its widest — never zero.</summary>
    /// <remarks>A blend to a point is not a curve loop, so the dome ends in a small flat top.</remarks>
    public const double ApexRadiusFraction = 0.05;

    /// <summary>The family's formula for the height of its widest circle.</summary>
    public static string WaistHeightFormula => Formula(HeightParameter, WaistHeightFraction);

    /// <summary>The family's formula for its radius at the ground.</summary>
    public static string BaseRadiusFormula => Formula(CrownRadiusParameter, BaseRadiusFraction);

    /// <summary>The family's formula for its radius at the apex.</summary>
    public static string ApexRadiusFormula => Formula(CrownRadiusParameter, ApexRadiusFraction);

    /// <summary>
    /// Whether Revit can build this shrub at its published size, as a family instance or a DirectShape.
    /// </summary>
    /// <remarks>The same floor as the tree's (<see cref="TreeFamily.MinimumDimensionM"/>).</remarks>
    public static bool Fits(SiteTreePoint shrub)
        => double.IsFinite(shrub.HeightM)
            && double.IsFinite(shrub.CrownRadiusM)
            && shrub.HeightM * WaistHeightFraction >= TreeFamily.MinimumDimensionM
            && shrub.HeightM * (1.0 - WaistHeightFraction) >= TreeFamily.MinimumDimensionM
            && shrub.CrownRadiusM * BaseRadiusFraction >= TreeFamily.MinimumDimensionM
            && shrub.CrownRadiusM * ApexRadiusFraction >= TreeFamily.MinimumDimensionM;

    private static string Formula(string parameter, double fraction)
        => parameter + " * " + fraction.ToString("0.0#", CultureInfo.InvariantCulture);
}

/// <summary>
/// Which Planting family a tree point goes to, by its published foliage type. Pure.
/// </summary>
/// <remarks>
/// A dispatch over <see cref="TreeFamily"/> and <see cref="ShrubFamily"/>, not an abstraction over
/// them: two families with different shapes share a crown parameter and nothing else worth naming.
/// </remarks>
public static class PlantingFamilies
{
    /// <summary>The family a tree point of this foliage type is an instance of.</summary>
    public static string FamilyName(FoliageType foliage)
        => foliage == FoliageType.Shrub ? ShrubFamily.FamilyName : TreeFamily.FamilyName;

    /// <summary>The instance parameter that family carries <c>height_m</c> in.</summary>
    public static string HeightParameter(FoliageType foliage)
        => foliage == FoliageType.Shrub ? ShrubFamily.HeightParameter : TreeFamily.HeightParameter;

    /// <summary>The name a fallback DirectShape carries — the one thing a curator can read on one.</summary>
    public static string DirectShapeName(FoliageType foliage)
        => foliage == FoliageType.Shrub ? "Shrub" : "Tree";

    /// <summary>The foliage type one of our families stands for, or <c>null</c> for any other name.</summary>
    /// <param name="familyName">A family instance's family name, or <c>null</c> for a DirectShape.</param>
    public static FoliageType? FoliageTypeOf(string? familyName) => familyName switch
    {
        TreeFamily.FamilyName => FoliageType.Tree,
        ShrubFamily.FamilyName => FoliageType.Shrub,
        _ => null,
    };

    /// <summary>Whether the family of this point's foliage type can build it at its published size.</summary>
    public static bool Fits(SiteTreePoint point)
        => point.FoliageType == FoliageType.Shrub ? ShrubFamily.Fits(point) : TreeFamily.Fits(point);
}
