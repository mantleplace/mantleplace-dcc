using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The one sentence the planting step ends on: one count of tree points, a breakdown by foliage type
/// when the manifest publishes one, and the single-total suffixes it has always had.
/// </summary>
internal static class PlantingSummaryTests
{
    private static PlantingTally Base => new()
    {
        EntryName = "Landcover/TreePoints.csv",
        PointCount = 130,
        HasVocabulary = true,
        TreesAsFamily = true,
        ShrubsAsFamily = true,
    };

    internal static int Run()
    {
        TestRun run = new();

        run.Case("a manifest with no vocabulary reads exactly as it did before shrubs", () =>
        {
            run.Equal(
                PlantingSummary.Sentence(Base with { HasVocabulary = false, TreesCreated = 120 }),
                "Imported 120 tree(s) of 130 from Landcover/TreePoints.csv as \"Mantle Place Tree\" instances.",
                "today's orders, today's words");
            run.Equal(
                PlantingSummary.Sentence(Base with { HasVocabulary = false, TreesCreated = 120, TreesAsFamily = false }),
                "Imported 120 tree(s) of 130 from Landcover/TreePoints.csv as DirectShapes.",
                "and today's fallback");
        });

        run.Case("a vocabulary counts tree points, then breaks them down by foliage type", () =>
            run.Equal(
                PlantingSummary.Sentence(Base with { TreesCreated = 100, ShrubsCreated = 20 }),
                "Imported 120 tree point(s) of 130 from Landcover/TreePoints.csv: 100 tree(s) as \"Mantle Place Tree\" "
                    + "instances, 20 shrub(s) as \"Mantle Place Shrub\" instances.",
                "one count, then the breakdown"));

        run.Case("each family says its own path", () =>
            run.Equal(
                PlantingSummary.Sentence(Base with { TreesCreated = 100, ShrubsCreated = 20, ShrubsAsFamily = false }),
                "Imported 120 tree point(s) of 130 from Landcover/TreePoints.csv: 100 tree(s) as \"Mantle Place Tree\" "
                    + "instances, 20 shrub(s) as DirectShapes.",
                "a shrub fallback does not pull the trees with it"));

        run.Case("a foliage type with nothing created is left out of the breakdown", () =>
        {
            run.Equal(
                PlantingSummary.Sentence(Base with { TreesCreated = 120 }),
                "Imported 120 tree point(s) of 130 from Landcover/TreePoints.csv: 120 tree(s) as \"Mantle Place Tree\" instances.",
                "no shrubs");
            run.Equal(
                PlantingSummary.Sentence(Base),
                "Imported 0 tree point(s) of 130 from Landcover/TreePoints.csv.",
                "nothing built, nothing to say about how");
        });

        run.Case("the suffixes stay single totals, in their old order", () =>
            run.Equal(
                PlantingSummary.Sentence(Base with
                {
                    TreesCreated = 90,
                    ShrubsCreated = 10,
                    AlreadyPresent = 5,
                    Unbuildable = 4,
                    Unsized = 3,
                    Unstamped = 2,
                }),
                "Imported 100 tree point(s) of 130 from Landcover/TreePoints.csv: 90 tree(s) as \"Mantle Place Tree\" "
                    + "instances, 10 shrub(s) as \"Mantle Place Shrub\" instances"
                    + "; 5 from an earlier import of this build were already present and left alone"
                    + "; 4 had a height or crown too small for Revit to build and were left out"
                    + "; 3 would not take their published size or elevation and stand at the family's default"
                    + "; 2 could not be stamped and will not be recognised by a re-import.",
                "totals, not per type"));

        run.Case("empty and unknown foliage cells are counted, each read as a tree", () =>
            run.Equal(
                PlantingSummary.Sentence(Base with { TreesCreated = 120, EmptyFoliageCells = 7, UnknownFoliageValues = 1 }),
                "Imported 120 tree point(s) of 130 from Landcover/TreePoints.csv: 120 tree(s) as \"Mantle Place Tree\" "
                    + "instances; 7 had no foliage type and were read as trees"
                    + "; 1 had a foliage type this add-in does not know and were read as trees.",
                "the log counts empty cells"));

        run.Case("large counts are grouped, as every other count in the log is", () =>
            run.Contains(
                PlantingSummary.Sentence(Base with { PointCount = 9293, TreesCreated = 9000, ShrubsCreated = 293 }),
                "Imported 9,293 tree point(s) of 9,293",
                "N0"));

        return run.Report("planting summary");
    }
}
