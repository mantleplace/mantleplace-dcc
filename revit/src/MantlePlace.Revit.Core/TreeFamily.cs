using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>One parameter a loaded tree family carries, as the tree step needs to see it.</summary>
public readonly record struct TreeFamilyParameter(string Name, bool IsInstance);

/// <summary>The tree step's choice of geometry, and the sentence a curator gets when it falls back.</summary>
public sealed class TreeFamilyDecision
{
    /// <summary>Place instances of <see cref="TreeFamily.FamilyName"/>; otherwise build DirectShapes.</summary>
    public required bool UseFamily { get; init; }

    /// <summary>The level instances are hosted on, or <c>0</c> on the fallback.</summary>
    public long LevelId { get; init; }

    /// <summary>One line for the log on the fallback; empty when the family is used.</summary>
    public string Explanation { get; init; } = string.Empty;
}

/// <summary>
/// The <c>Mantle Place Tree</c> Planting family: its name, the two instance parameters the tree step
/// drives, and the proportions its geometry takes from them. Pure.
/// </summary>
/// <remarks>
/// <para>
/// One family, two published numbers per tree. Height and crown radius come from
/// <c>TreePoints.csv</c> and are written to the instance verbatim; everything else about the shape —
/// how much of the height is trunk, how thick the trunk is, how narrow the crown's tip — is the
/// family's own fixed proportion, the same for every tree. That is Revit proxy geometry for a
/// published point, not a classification of it: nothing here picks a species, a shape or a family
/// from a tree's size, and a foliage type waits for the platform to publish one.
/// </para>
/// <para>
/// The proportions live here, not in the shim, because two things build from them and must agree:
/// the family's own formulas, written once by the developer command that authors the <c>.rfa</c>,
/// and the DirectShape fallback the tree step uses when that family cannot be loaded. A tree drawn by
/// either path is the same shape.
/// </para>
/// </remarks>
public static class TreeFamily
{
    /// <summary>The family's name in a project, which Revit takes from the file's name on load.</summary>
    public const string FamilyName = "Mantle Place Tree";

    /// <summary>The file the family ships as — its name, because a loaded family is named after it.</summary>
    public const string FileName = FamilyName + ".rfa";

    /// <summary>The instance parameter carrying <c>height_m</c>: total height, ground to apex.</summary>
    /// <remarks>
    /// Not <c>Height</c>: every Planting family already has a built-in <em>type</em> parameter of that
    /// name (<c>RENDER_PLANT_HEIGHT</c>), and Revit refuses both a second parameter with the name and
    /// making the built-in one per instance. Measured in Revit 2025's Planting template.
    /// </remarks>
    public const string HeightParameter = "Tree Height";

    /// <summary>The instance parameter carrying <c>crown_radius_m</c>: the crown at its widest.</summary>
    public const string CrownRadiusParameter = "Crown Radius";

    /// <summary>Trunk height as a fraction of total height — the rest is crown.</summary>
    public const double TrunkHeightFraction = 0.35;

    /// <summary>Trunk radius as a fraction of crown radius.</summary>
    public const double TrunkRadiusFraction = 0.12;

    /// <summary>Crown radius at the apex, as a fraction of its widest — never zero.</summary>
    /// <remarks>A cone to a point is not a curve loop, so the crown is a steep truncated cone.</remarks>
    public const double CrownApexFraction = 0.05;

    /// <summary>
    /// The smallest dimension either path asks Revit to build, in metres.
    /// </summary>
    /// <remarks>
    /// Revit refuses a curve shorter than its short-curve tolerance, a little under a millimetre,
    /// and inside a family that refusal is a flex failure that can take the whole chunk's transaction
    /// with it. Two millimetres keeps every circle clear of it with margin, and no real tree is
    /// anywhere near it — so a tree that does not fit is a malformed row, not a small tree.
    /// </remarks>
    public const double MinimumDimensionM = 0.002;

    /// <summary>The family's formula for the trunk's height.</summary>
    public static string TrunkHeightFormula => Formula(HeightParameter, TrunkHeightFraction);

    /// <summary>The family's formula for the trunk's radius.</summary>
    public static string TrunkRadiusFormula => Formula(CrownRadiusParameter, TrunkRadiusFraction);

    /// <summary>The family's formula for the crown's radius at its tip.</summary>
    public static string CrownApexRadiusFormula => Formula(CrownRadiusParameter, CrownApexFraction);

    /// <summary>
    /// Whether Revit can build this tree at its published size, as a family instance or a DirectShape.
    /// </summary>
    public static bool Fits(SiteTree tree)
        => double.IsFinite(tree.HeightM)
            && double.IsFinite(tree.CrownRadiusM)
            && tree.HeightM * TrunkHeightFraction >= MinimumDimensionM
            && tree.HeightM * (1.0 - TrunkHeightFraction) >= MinimumDimensionM
            && tree.CrownRadiusM * TrunkRadiusFraction >= MinimumDimensionM
            && tree.CrownRadiusM * CrownApexFraction >= MinimumDimensionM;

    private static string Formula(string parameter, double fraction)
        => parameter + " * " + fraction.ToString("0.0#", CultureInfo.InvariantCulture);
}

/// <summary>
/// Decides whether the tree step places <see cref="TreeFamily"/> instances or falls back to
/// DirectShapes. Pure.
/// </summary>
/// <remarks>
/// <para>
/// The fallback is the geometry the tree step built before the family existed, and it stays for the
/// day the family does not load: a Revit that refuses the file, a project where a curator's own
/// family already holds the name without the parameters this step writes. Either way the trees are
/// still built, at the same size and in the same place, and the log says which path ran and why —
/// what the curator loses is only the instance parameters and the renderer's ability to recognise a
/// Planting family.
/// </para>
/// <para>
/// ⛔ <b>A type parameter where an instance one belongs is missing.</b> Written to a type, every tree
/// would take the last row's height; the step would report success and every tree would be wrong.
/// </para>
/// </remarks>
public static class TreeFamilyChoice
{
    /// <param name="loadFailure">Why the family could not be loaded, or <c>null</c> when it is in the project.</param>
    /// <param name="typeCount">How many types the loaded family has. Unread when <paramref name="loadFailure"/> is set.</param>
    /// <param name="parameters">The loaded family's parameters. Unread when <paramref name="loadFailure"/> is set.</param>
    /// <param name="levels">Every level in the project.</param>
    public static TreeFamilyDecision Decide(
        string? loadFailure,
        int typeCount,
        IReadOnlyCollection<TreeFamilyParameter> parameters,
        IReadOnlyList<CandidateLevel> levels)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(levels);

        if (loadFailure is not null)
        {
            return Fallback($"could not be loaded ({loadFailure.TrimEnd('.')})");
        }

        if (typeCount == 0)
        {
            return Fallback("in this project has no type to place, so it is not the one this plugin ships");
        }

        foreach (string required in (string[])[TreeFamily.HeightParameter, TreeFamily.CrownRadiusParameter])
        {
            if (!parameters.Any(parameter => parameter.IsInstance
                    && string.Equals(parameter.Name, required, StringComparison.Ordinal)))
            {
                return Fallback($"in this project has no \"{required}\" instance parameter, so it is not "
                    + "the one this plugin ships and cannot be given each tree's size");
            }
        }

        if (levels.Count == 0)
        {
            return Fallback("needs a level to host it, and this project has none");
        }

        // The lowest level, so that the offset from it is the only thing carrying the ground elevation,
        // and it is never a negative offset from a level a curator put above the site.
        CandidateLevel host = levels.MinBy(level => level.Elevation);
        return new TreeFamilyDecision { UseFamily = true, LevelId = host.Id };
    }

    private static TreeFamilyDecision Fallback(string why) => new()
    {
        UseFamily = false,
        Explanation = $"The \"{TreeFamily.FamilyName}\" Planting family {why}, so the trees were built as "
            + "DirectShapes on the Planting category instead: the same size and place, but with no "
            + $"{TreeFamily.HeightParameter} or {TreeFamily.CrownRadiusParameter} to edit and no family type "
            + "to give a render substitution.",
    };
}
