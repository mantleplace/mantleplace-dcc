using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The road stamp: a re-import of the same bundle finds the centrelines it drew and draws only the
/// rows that are missing, so the roads are no longer doubled.
/// </summary>
internal static class RoadIdentityTests
{
    private const string Stem = "order-7f3a";

    internal static int Run()
    {
        TestRun run = new();

        run.Case("the stamp names the order and the one-based row", () =>
            run.Equal(RoadIdentity.Stamp(Stem, 17), "Mantle Place Road order-7f3a/17", "stem and row"));

        run.Case("a first import creates every row", () =>
        {
            RoadDecision decision = RoadIdentity.Decide([], Stem, 290);
            run.Equal(decision.RowsToCreate.Count, 290, "every row");
            run.Equal(decision.RowsToCreate[0], 0, "zero-based, first row first");
            run.Equal(decision.AlreadyPresent, 0, "none present");
        });

        run.Case("a second import creates nothing", () =>
        {
            List<string?> existing = [.. Enumerable.Range(1, 21).Select(row => (string?)RoadIdentity.Stamp(Stem, row))];
            RoadDecision decision = RoadIdentity.Decide(existing, Stem, 21);
            run.Equal(decision.RowsToCreate.Count, 0, "no second set of roads");
            run.Equal(decision.AlreadyPresent, 21, "all 21 recognised");
        });

        run.Case("a partial earlier import creates only what is missing", () =>
        {
            List<string?> existing = [RoadIdentity.Stamp(Stem, 1), RoadIdentity.Stamp(Stem, 3)];
            RoadDecision decision = RoadIdentity.Decide(existing, Stem, 4);
            run.Equal(decision.AlreadyPresent, 2, "rows 1 and 3 present");
            run.Equal(decision.RowsToCreate.Count, 2, "two to create");
            run.Equal(decision.RowsToCreate[0], 1, "row 2, zero-based");
            run.Equal(decision.RowsToCreate[1], 3, "row 4, zero-based");
        });

        run.Case("another order's roads, a curator's comments and trees are not this bundle's roads", () =>
        {
            List<string?> existing =
            [
                null,
                "widened by the civil engineer",
                RoadIdentity.Stamp("order-other", 1),
                TreeIdentity.Stamp(Stem, null, 1),

                // A stem this one is a prefix of: without the separator in the comparison it would
                // read as this order's row 1.
                RoadIdentity.Stamp(Stem + "b", 1),
            ];

            RoadDecision decision = RoadIdentity.Decide(existing, Stem, 2);
            run.Equal(decision.AlreadyPresent, 0, "nothing of ours");
            run.Equal(decision.RowsToCreate.Count, 2, "both created");
        });

        run.Case("a stamp beyond the layer's rows is not counted as present", () =>
        {
            // An earlier, longer layer of the same order: its row 9 exists, this layer has four rows.
            RoadDecision decision = RoadIdentity.Decide([RoadIdentity.Stamp(Stem, 9)], Stem, 4);
            run.Equal(decision.AlreadyPresent, 0, "row 9 is not one of these four");
            run.Equal(decision.RowsToCreate.Count, 4, "all four created");
        });

        return run.Report("road identity");
    }
}
