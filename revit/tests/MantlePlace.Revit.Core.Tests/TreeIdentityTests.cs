using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The tree stamp and ADR 0004's table applied per row: a re-import of the same build creates only
/// the rows that are missing — which is what makes a cancelled tree step resumable — and a later
/// build of the same order is refused rather than stacked.
/// </summary>
internal static class TreeIdentityTests
{
    private const string Stem = "order-7f3a";
    private const string Build = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Rebuild = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    internal static int Run()
    {
        TestRun run = new();

        run.Case("the stamp names the order, the build and the row", () =>
            run.Equal(
                TreeIdentity.Stamp(Stem, Build, 17),
                "Mantle Place Tree order-7f3a/0123456789ab/17",
                "stem, twelve-character build token, one-based row"));

        run.Case("a bundle with no digest stamps its build unknown", () =>
            run.Equal(TreeIdentity.Stamp(Stem, null, 1), "Mantle Place Tree order-7f3a/unknown/1", "HPS-28: null is unknown"));

        run.Case("a first import creates every row and says nothing", () =>
        {
            TreeDecision decision = TreeIdentity.Decide([], Stem, Build, 600);
            run.True(decision.Disposition == TreeDisposition.Create, "create");
            run.Equal(decision.RowsToCreate.Count, 600, "every row");
            run.Equal(decision.RowsToCreate[0], 0, "zero-based, first row first");
            run.Equal(decision.AlreadyPresent, 0, "none present");
            run.Equal(decision.Explanation, string.Empty, "nothing to tell anyone");
        });

        run.Case("a re-import after a cancel creates only the rows that are missing", () =>
        {
            List<string?> existing = [.. Enumerable.Range(1, 250).Select(row => (string?)TreeIdentity.Stamp(Stem, Build, row))];
            TreeDecision decision = TreeIdentity.Decide(existing, Stem, Build, 600);

            run.True(decision.Disposition == TreeDisposition.Create, "still create");
            run.Equal(decision.AlreadyPresent, 250, "the cancelled run's chunk is reused");
            run.Equal(decision.RowsToCreate.Count, 350, "the rest is created");
            run.Equal(decision.RowsToCreate[0], 250, "starting at the first missing row");
        });

        run.Case("a re-import of a complete set creates nothing", () =>
        {
            List<string?> existing = [.. Enumerable.Range(1, 44).Select(row => (string?)TreeIdentity.Stamp(Stem, Build, row))];
            TreeDecision decision = TreeIdentity.Decide(existing, Stem, Build, 44);

            run.Equal(decision.RowsToCreate.Count, 0, "nothing to create");
            run.Equal(decision.AlreadyPresent, 44, "all 44 reused");
        });

        run.Case("a later build of the same order is refused, not stacked and not deleted", () =>
        {
            List<string?> existing = [TreeIdentity.Stamp(Stem, Build, 1), TreeIdentity.Stamp(Stem, Build, 2)];
            TreeDecision decision = TreeIdentity.Decide(existing, Stem, Rebuild, 600);

            run.True(decision.Disposition == TreeDisposition.RefuseStale, "refuse");
            run.Equal(decision.RowsToCreate.Count, 0, "no second forest");
            run.Contains(decision.Explanation, "EARLIER build", "it says why");
            run.Contains(decision.Explanation, "Mantle Place Tree order-7f3a/", "it names what to delete");
            run.Contains(decision.Explanation, "2 ", "it counts them");
        });

        run.Case("another order's trees and a curator's own comments are not this bundle's", () =>
        {
            List<string?> existing =
            [
                null,
                "planted by the landscape architect",
                TreeIdentity.Stamp("order-other", Rebuild, 1),

                // A stem that this one is a prefix of: without the separator in the comparison, it
                // would read as this order's earlier build and refuse the import.
                TreeIdentity.Stamp(Stem + "def", Rebuild, 1),
            ];
            TreeDecision decision = TreeIdentity.Decide(existing, Stem, Build, 10);

            run.True(decision.Disposition == TreeDisposition.Create, "create");
            run.Equal(decision.RowsToCreate.Count, 10, "every row");
        });

        run.Case("an unknown build reuses an unknown build, and never a known one", () =>
        {
            TreeDecision same = TreeIdentity.Decide([TreeIdentity.Stamp(Stem, null, 1)], Stem, null, 2);
            run.Equal(same.AlreadyPresent, 1, "unknown matches unknown");

            TreeDecision differs = TreeIdentity.Decide([TreeIdentity.Stamp(Stem, null, 1)], Stem, Build, 2);
            run.True(differs.Disposition == TreeDisposition.RefuseStale, "unknown is not a sameness anyone showed");
        });

        return run.Report("tree identity");
    }
}
