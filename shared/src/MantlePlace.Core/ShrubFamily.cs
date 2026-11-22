using System.Globalization;

namespace MantlePlace.Core;

public static class ShrubFamily
{
    public const string FamilyName = "Mantle Place Shrub";

    public const string FileName = FamilyName + ".rfa";

    public const string HeightParameter = "Shrub Height";

    public const string CrownRadiusParameter = TreeFamily.CrownRadiusParameter;

    public const double BaseRadiusFraction = 0.6;

    public const double WaistHeightFraction = 0.5;

    public const double ApexRadiusFraction = 0.05;

    public static string WaistHeightFormula => Formula(HeightParameter, WaistHeightFraction);

    public static string BaseRadiusFormula => Formula(CrownRadiusParameter, BaseRadiusFraction);

    public static string ApexRadiusFormula => Formula(CrownRadiusParameter, ApexRadiusFraction);

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

public static class PlantingFamilies
{
    public static string FamilyName(FoliageType foliage)
        => foliage == FoliageType.Shrub ? ShrubFamily.FamilyName : TreeFamily.FamilyName;

    public static string HeightParameter(FoliageType foliage)
        => foliage == FoliageType.Shrub ? ShrubFamily.HeightParameter : TreeFamily.HeightParameter;

    public static string DirectShapeName(FoliageType foliage)
        => foliage == FoliageType.Shrub ? "Shrub" : "Tree";

    public static FoliageType? FoliageTypeOf(string? familyName) => familyName switch
    {
        TreeFamily.FamilyName => FoliageType.Tree,
        ShrubFamily.FamilyName => FoliageType.Shrub,
        _ => null,
    };

    public static bool Fits(SiteTreePoint point)
        => point.FoliageType == FoliageType.Shrub ? ShrubFamily.Fits(point) : TreeFamily.Fits(point);
}
