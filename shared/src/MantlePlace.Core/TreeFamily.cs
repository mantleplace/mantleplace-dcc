using System.Globalization;

namespace MantlePlace.Core;

public readonly record struct TreeFamilyParameter(string Name, bool IsInstance);

public sealed class TreeFamilyDecision
{
    public required bool UseFamily { get; init; }

    public long LevelId { get; init; }

    public string Explanation { get; init; } = string.Empty;
}

public static class TreeFamily
{
    public const string FamilyName = "Mantle Place Tree";

    public const string FileName = FamilyName + ".rfa";

    public const string HeightParameter = "Tree Height";

    public const string CrownRadiusParameter = "Crown Radius";

    public const double TrunkHeightFraction = 0.35;

    public const double TrunkRadiusFraction = 0.12;

    public const double CrownApexFraction = 0.05;

    public const double MinimumDimensionM = 0.002;

    public static string TrunkHeightFormula => Formula(HeightParameter, TrunkHeightFraction);

    public static string TrunkRadiusFormula => Formula(CrownRadiusParameter, TrunkRadiusFraction);

    public static string CrownApexRadiusFormula => Formula(CrownRadiusParameter, CrownApexFraction);

    public static bool Fits(SiteTreePoint tree)
        => double.IsFinite(tree.HeightM)
            && double.IsFinite(tree.CrownRadiusM)
            && tree.HeightM * TrunkHeightFraction >= MinimumDimensionM
            && tree.HeightM * (1.0 - TrunkHeightFraction) >= MinimumDimensionM
            && tree.CrownRadiusM * TrunkRadiusFraction >= MinimumDimensionM
            && tree.CrownRadiusM * CrownApexFraction >= MinimumDimensionM;

    private static string Formula(string parameter, double fraction)
        => parameter + " * " + fraction.ToString("0.0#", CultureInfo.InvariantCulture);
}

public static class TreeFamilyChoice
{
    public static TreeFamilyDecision Decide(
        FoliageType foliage,
        string? loadFailure,
        int typeCount,
        IReadOnlyCollection<TreeFamilyParameter> parameters,
        IReadOnlyList<CandidateLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(levels);

        string height = PlantingFamilies.HeightParameter(foliage);
        if (loadFailure is not null)
        {
            return Fallback(foliage, $"could not be loaded ({loadFailure.TrimEnd('.')})");
        }

        if (typeCount == 0)
        {
            return Fallback(foliage, "in this project has no type to place, so it is not the one this plugin ships");
        }

        foreach (string required in (string[])[height, TreeFamily.CrownRadiusParameter])
        {
            if (!parameters.Any(parameter => parameter.IsInstance
                    && string.Equals(parameter.Name, required, StringComparison.Ordinal)))
            {
                return Fallback(foliage, $"in this project has no \"{required}\" instance parameter, so it is not "
                    + $"the one this plugin ships and cannot be given each {Singular(foliage)}'s size");
            }
        }

        if (levels.Count == 0)
        {
            return Fallback(foliage, "needs a level to host it, and this project has none");
        }

        CandidateLevel host = levels.MinBy(level => level.Elevation);
        return new TreeFamilyDecision { UseFamily = true, LevelId = host.Id };
    }

    private static TreeFamilyDecision Fallback(FoliageType foliage, string why) => new()
    {
        UseFamily = false,
        Explanation = $"The \"{PlantingFamilies.FamilyName(foliage)}\" Planting family {why}, so the "
            + $"{Singular(foliage)}s were built as DirectShapes on the Planting category instead: the same size "
            + $"and place, but with no {PlantingFamilies.HeightParameter(foliage)} or "
            + $"{TreeFamily.CrownRadiusParameter} to edit and no family type to give a render substitution.",
    };

    private static string Singular(FoliageType foliage) => foliage == FoliageType.Shrub ? "shrub" : "tree";
}
