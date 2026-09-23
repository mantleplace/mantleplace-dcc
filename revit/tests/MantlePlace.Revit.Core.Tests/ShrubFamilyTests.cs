using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The shrub Planting family's contract with the importer, and the dispatch that sends each tree
/// point to the family of its published foliage type.
/// </summary>
internal static class ShrubFamilyTests
{
    private static readonly CandidateLevel[] Levels = [new(300, "Level 1", 0.0)];

    private static SiteTreePoint Shrub(double heightM, double crownRadiusM)
        => new(0.0, 0.0, 0.0, heightM, crownRadiusM, FoliageType.Shrub);

    private static SiteTreePoint Tree(double heightM, double crownRadiusM)
        => new(0.0, 0.0, 0.0, heightM, crownRadiusM, FoliageType.Tree);

    internal static int Run()
    {
        TestRun run = new();

        run.Case("the family's name is the file it ships as, and its own", () =>
        {
            run.Equal(ShrubFamily.FamilyName, "Mantle Place Shrub", "the name a curator sees");
            run.Equal(ShrubFamily.FileName, "Mantle Place Shrub.rfa", "Revit names a loaded family after its file");
            run.Equal(ShrubFamily.HeightParameter, "Shrub Height", "not Height, which Planting owns as a type parameter");
            run.Equal(ShrubFamily.CrownRadiusParameter, TreeFamily.CrownRadiusParameter, "one crown word for both families");
        });

        run.Case("the dome's formulas are written from the proportions the fallback builds with", () =>
        {
            run.Equal(ShrubFamily.WaistHeightFormula, "Shrub Height * 0.5", "widest at half the height");
            run.Equal(ShrubFamily.BaseRadiusFormula, "Crown Radius * 0.6", "the base");
            run.Equal(ShrubFamily.ApexRadiusFormula, "Crown Radius * 0.05", "the apex, as the tree's");
        });

        run.Case("a published shrub fits", () =>
            run.True(ShrubFamily.Fits(Shrub(1.5, 1.0)), "an ordinary shrub"));

        run.Case("a shrub with no height or no crown does not", () =>
        {
            run.False(ShrubFamily.Fits(Shrub(0.0, 1.0)), "zero height");
            run.False(ShrubFamily.Fits(Shrub(1.5, 0.0)), "zero crown");
            run.False(ShrubFamily.Fits(Shrub(double.NaN, 1.0)), "NaN height");
            run.False(ShrubFamily.Fits(Shrub(1.5, double.PositiveInfinity)), "infinite crown");
        });

        run.Case("the apex is what a small crown runs out of first", () =>
        {
            // 0.05 of a 3 cm crown is 1.5 mm, under the 2 mm floor; 0.05 of 5 cm is 2.5 mm, over it.
            run.False(ShrubFamily.Fits(Shrub(1.5, 0.03)), "three-centimetre crown");
            run.True(ShrubFamily.Fits(Shrub(1.5, 0.05)), "five-centimetre crown");
            run.False(ShrubFamily.Fits(Shrub(0.003, 1.0)), "three-millimetre shrub: each half is under the floor");
        });

        run.Case("each point is judged by its own family, never by its size", () =>
        {
            // A 4 mm point halves to the shrub's floor but is under the tree's trunk fraction: the same
            // numbers, two foliage types, two answers. The foliage type picks the family; the size
            // only decides whether that family can build it.
            run.False(PlantingFamilies.Fits(Tree(12.0, 0.03)), "a tree with a three-centimetre crown");
            run.False(PlantingFamilies.Fits(Shrub(12.0, 0.03)), "a shrub with the same");
            run.True(PlantingFamilies.Fits(Shrub(0.004, 1.0)), "a four-millimetre shrub halves to the floor");
            run.False(PlantingFamilies.Fits(Tree(0.004, 1.0)), "the tree's trunk fraction does not");
        });

        run.Case("the dispatch names each foliage type's family and height parameter", () =>
        {
            run.Equal(PlantingFamilies.FamilyName(FoliageType.Tree), TreeFamily.FamilyName, "tree");
            run.Equal(PlantingFamilies.FamilyName(FoliageType.Shrub), ShrubFamily.FamilyName, "shrub");
            run.Equal(PlantingFamilies.HeightParameter(FoliageType.Tree), TreeFamily.HeightParameter, "tree height");
            run.Equal(PlantingFamilies.HeightParameter(FoliageType.Shrub), ShrubFamily.HeightParameter, "shrub height");
            run.Equal(PlantingFamilies.DirectShapeName(FoliageType.Tree), "Tree", "a fallback tree's name");
            run.Equal(PlantingFamilies.DirectShapeName(FoliageType.Shrub), "Shrub", "a fallback shrub's name");
        });

        run.Case("a family is known by its name, and only ours are", () =>
        {
            run.True(PlantingFamilies.FoliageTypeOf(TreeFamily.FamilyName) == FoliageType.Tree, "the tree family");
            run.True(PlantingFamilies.FoliageTypeOf(ShrubFamily.FamilyName) == FoliageType.Shrub, "the shrub family");
            run.True(PlantingFamilies.FoliageTypeOf("RPC Tree - Deciduous") is null, "a curator's own family");
            run.True(PlantingFamilies.FoliageTypeOf(null) is null, "a DirectShape has no family");
        });

        run.Case("the shrub family's choice asks for the shrub's own parameters", () =>
        {
            TreeFamilyDecision decision = TreeFamilyChoice.Decide(
                FoliageType.Shrub,
                null,
                1,
                [new(TreeFamily.HeightParameter, IsInstance: true), new(ShrubFamily.CrownRadiusParameter, IsInstance: true)],
                Levels);
            run.False(decision.UseFamily, "a tree's height parameter is not a shrub's");
            run.Contains(decision.Explanation, ShrubFamily.HeightParameter, "it names what is missing");
            run.Contains(decision.Explanation, ShrubFamily.FamilyName, "it names the shrub family");
        });

        run.Case("a shrub family with both instance parameters is used", () =>
        {
            TreeFamilyDecision decision = TreeFamilyChoice.Decide(
                FoliageType.Shrub,
                null,
                1,
                [new(ShrubFamily.HeightParameter, IsInstance: true), new(ShrubFamily.CrownRadiusParameter, IsInstance: true)],
                Levels);
            run.True(decision.UseFamily, "family");
        });

        run.Case("a shrub family that falls back says the shrubs, not the trees, were built otherwise", () =>
        {
            TreeFamilyDecision decision = TreeFamilyChoice.Decide(FoliageType.Shrub, "gone", 0, [], Levels);
            run.False(decision.UseFamily, "fallback");
            run.Contains(decision.Explanation, "the shrubs were built as DirectShapes", "its own curator sentence");
            run.False(decision.Explanation.Contains("the trees", StringComparison.Ordinal), "it does not speak for the trees");
            run.Contains(decision.Explanation, ShrubFamily.HeightParameter, "the parameter it cannot offer");
        });

        return run.Report("shrub family");
    }
}
