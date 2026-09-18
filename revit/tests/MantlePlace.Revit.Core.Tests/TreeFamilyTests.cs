using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The Planting family's contract with the importer: which published trees Revit can build at all,
/// and when the tree step places family instances rather than falling back to DirectShapes.
/// </summary>
internal static class TreeFamilyTests
{
    private static readonly TreeFamilyParameter[] Complete =
    [
        new(TreeFamily.HeightParameter, IsInstance: true),
        new(TreeFamily.CrownRadiusParameter, IsInstance: true),
    ];

    private static readonly CandidateLevel[] Levels =
    [
        new(301, "Level 2", 4.0),
        new(300, "Level 1", 0.0),
        new(302, "Mantle Place Terrain Base", -12.5),
    ];

    internal static int Run()
    {
        TestRun run = new();

        run.Case("the family's name is the file it ships as, so loading it names it", () =>
        {
            run.Equal(TreeFamily.FamilyName, "Mantle Place Tree", "the name a curator sees");
            run.Equal(TreeFamily.FileName, "Mantle Place Tree.rfa", "Revit names a loaded family after its file");
        });

        run.Case("a published tree fits", () =>
            run.True(TreeFamily.Fits(new SiteTree(10.0, 20.0, 100.0, 12.0, 3.0)), "an ordinary tree"));

        run.Case("a tree with no height or no crown does not", () =>
        {
            run.False(TreeFamily.Fits(new SiteTree(0.0, 0.0, 0.0, 0.0, 3.0)), "zero height");
            run.False(TreeFamily.Fits(new SiteTree(0.0, 0.0, 0.0, 12.0, 0.0)), "zero crown");
            run.False(TreeFamily.Fits(new SiteTree(0.0, 0.0, 0.0, -1.0, 3.0)), "negative height");
            run.False(TreeFamily.Fits(new SiteTree(0.0, 0.0, 0.0, double.NaN, 3.0)), "NaN height");
            run.False(TreeFamily.Fits(new SiteTree(0.0, 0.0, 0.0, 12.0, double.PositiveInfinity)), "infinite crown");
        });

        run.Case("a crown too small for its apex to be a curve does not fit", () =>
        {
            // The apex is a fixed fraction of the crown, so a crown of one centimetre asks Revit for
            // a half-millimetre circle, which is under its shortest curve.
            run.False(TreeFamily.Fits(new SiteTree(0.0, 0.0, 0.0, 12.0, 0.01)), "one-centimetre crown");
            run.True(TreeFamily.Fits(new SiteTree(0.0, 0.0, 0.0, 12.0, 0.2)), "twenty-centimetre crown");
        });

        run.Case("the family's formulas are written from the same proportions the fallback builds with", () =>
        {
            run.Equal(TreeFamily.TrunkHeightFormula, "Tree Height * 0.35", "trunk height");
            run.Equal(TreeFamily.TrunkRadiusFormula, "Crown Radius * 0.12", "trunk radius");
            run.Equal(TreeFamily.CrownApexRadiusFormula, "Crown Radius * 0.05", "crown apex");
        });

        run.Case("a loaded family with both instance parameters is used, on the lowest level", () =>
        {
            TreeFamilyDecision decision = TreeFamilyChoice.Decide(null, 1, Complete, Levels);
            run.True(decision.UseFamily, "family");
            run.True(decision.LevelId == 302, "the lowest level hosts, the offset carries the rest");
            run.Equal(decision.Explanation, string.Empty, "nothing to tell anyone");
        });

        run.Case("a family that did not load falls back to DirectShapes and says why", () =>
        {
            TreeFamilyDecision decision = TreeFamilyChoice.Decide("the file is missing", 0, [], Levels);
            run.False(decision.UseFamily, "fallback");
            run.Contains(decision.Explanation, "the file is missing", "the reason is passed through");
            run.Contains(decision.Explanation, "DirectShape", "it says what was built instead");
            run.Contains(decision.Explanation, "Mantle Place Tree", "it names the family");
        });

        run.Case("a family of that name without the parameters is not ours to drive", () =>
        {
            TreeFamilyDecision decision = TreeFamilyChoice.Decide(
                null,
                1,
                [new(TreeFamily.HeightParameter, IsInstance: true)],
                Levels);
            run.False(decision.UseFamily, "fallback");
            run.Contains(decision.Explanation, TreeFamily.CrownRadiusParameter, "it names what is missing");
        });

        run.Case("a type parameter where an instance one belongs is missing, not close enough", () =>
        {
            TreeFamilyDecision decision = TreeFamilyChoice.Decide(
                null,
                1,
                [new(TreeFamily.HeightParameter, IsInstance: false), new(TreeFamily.CrownRadiusParameter, IsInstance: true)],
                Levels);
            run.False(decision.UseFamily, "every tree would share one height");
            run.Contains(decision.Explanation, "instance", "it says which kind was wanted");
        });

        run.Case("a family of that name with no type cannot be placed", () =>
        {
            TreeFamilyDecision decision = TreeFamilyChoice.Decide(null, 0, Complete, Levels);
            run.False(decision.UseFamily, "fallback");
            run.Contains(decision.Explanation, "no type", "it says why");
        });

        run.Case("the fallback names the parameters it cannot offer, from their constants", () =>
        {
            TreeFamilyDecision decision = TreeFamilyChoice.Decide("gone", 0, [], Levels);
            run.Contains(decision.Explanation, TreeFamily.HeightParameter, "the height parameter");
            run.Contains(decision.Explanation, TreeFamily.CrownRadiusParameter, "the crown parameter");
        });

        run.Case("a project with no level falls back rather than failing", () =>
        {
            TreeFamilyDecision decision = TreeFamilyChoice.Decide(null, 1, Complete, []);
            run.False(decision.UseFamily, "fallback");
            run.Contains(decision.Explanation, "level", "it says why");
        });

        return run.Report("tree family");
    }
}
