using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The context-building stamp and ADR 0004's table applied per building: a re-import of the same
/// build creates only the buildings that are missing, and a later build of the same order is refused
/// rather than stacked.
/// </summary>
internal static class BuildingIdentityTests
{
    private const string Stem = "order-7f3a";
    private const string Build = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Rebuild = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    private static readonly string[] ThreeBuildings =
    [
        "1Kx0mPq8T3uBv9Wc2Yd4Ze",
        "0Qn7Hs2Lf5Rj8Tg1Vb3Xc_",
        "3pA9zE4kM6nB0rC2dF8gH$",
    ];

    internal static int Run()
    {
        TestRun run = new();

        run.Case("the stamp names the order, the build and the IFC GlobalId", () =>
            run.Equal(
                BuildingIdentity.Stamp(Stem, Build, "1Kx0mPq8T3uBv9Wc2Yd4Ze"),
                "Mantle Place Building order-7f3a/0123456789ab/1Kx0mPq8T3uBv9Wc2Yd4Ze",
                "stem, twelve-character build token of the site model's sha256, GlobalId"));

        run.Case("a site model with no digest stamps its build unknown", () =>
            run.Equal(
                BuildingIdentity.Stamp(Stem, null, "1Kx0mPq8T3uBv9Wc2Yd4Ze"),
                "Mantle Place Building order-7f3a/unknown/1Kx0mPq8T3uBv9Wc2Yd4Ze",
                "HPS-28: null is unknown"));

        run.Case("a first import creates every building and says nothing", () =>
        {
            BuildingDecision decision = BuildingIdentity.Decide([], Stem, Build, ThreeBuildings);

            run.True(decision.Disposition == BuildingDisposition.Create, "create");
            run.Equal(decision.GlobalIdsToCreate.Count, 3, "every building");
            run.Equal(decision.GlobalIdsToCreate[0], ThreeBuildings[0], "in the site model's order");
            run.Equal(decision.AlreadyPresent, 0, "none present");
            run.Equal(decision.Explanation, string.Empty, "nothing to tell anyone");
        });

        run.Case("a second import of the same build creates nothing", () =>
        {
            List<string?> existing = [.. ThreeBuildings.Select(id => (string?)BuildingIdentity.Stamp(Stem, Build, id))];
            BuildingDecision decision = BuildingIdentity.Decide(existing, Stem, Build, ThreeBuildings);

            run.True(decision.Disposition == BuildingDisposition.Create, "not a refusal");
            run.Equal(decision.GlobalIdsToCreate.Count, 0, "nothing to create");
            run.Equal(decision.AlreadyPresent, 3, "all three reused");
        });

        run.Case("a re-import after a cancel creates only the buildings that are missing", () =>
        {
            List<string?> existing = [BuildingIdentity.Stamp(Stem, Build, ThreeBuildings[0])];
            BuildingDecision decision = BuildingIdentity.Decide(existing, Stem, Build, ThreeBuildings);

            run.Equal(decision.AlreadyPresent, 1, "the cancelled run's chunk is reused");
            run.Equal(decision.GlobalIdsToCreate.Count, 2, "the rest is created");
            run.Equal(decision.GlobalIdsToCreate[0], ThreeBuildings[1], "starting at the first missing one");
        });

        run.Case("a later build of the same order is refused, not stacked and not deleted", () =>
        {
            List<string?> existing =
            [
                BuildingIdentity.Stamp(Stem, Build, ThreeBuildings[0]),
                BuildingIdentity.Stamp(Stem, Build, ThreeBuildings[1]),
            ];

            // The same GlobalIds in the new build — an emitter that keeps them stable across builds —
            // must still refuse: the build half is what says these are the older extrusions.
            BuildingDecision decision = BuildingIdentity.Decide(existing, Stem, Rebuild, ThreeBuildings);

            run.True(decision.Disposition == BuildingDisposition.RefuseStale, "refuse");
            run.Equal(decision.GlobalIdsToCreate.Count, 0, "no second set of buildings");
            run.Contains(decision.Explanation, "EARLIER build", "it says why");
            run.Contains(decision.Explanation, "Mantle Place Building order-7f3a/", "it names what to delete");
            run.Contains(decision.Explanation, "2 ", "it counts them");
        });

        run.Case("another order's buildings and a curator's own comments are not this bundle's", () =>
        {
            List<string?> existing =
            [
                null,
                "existing hall, keep",
                BuildingIdentity.Stamp("order-other", Rebuild, ThreeBuildings[0]),

                // A stem that this one is a prefix of: without the separator in the comparison, it
                // would read as this order's earlier build and refuse the import.
                BuildingIdentity.Stamp(Stem + "def", Rebuild, ThreeBuildings[0]),

                // Another kind's stamp for this very order is not a building.
                TreeIdentity.Stamp(Stem, Rebuild, 1),
            ];
            BuildingDecision decision = BuildingIdentity.Decide(existing, Stem, Build, ThreeBuildings);

            run.True(decision.Disposition == BuildingDisposition.Create, "create");
            run.Equal(decision.GlobalIdsToCreate.Count, 3, "every building");
        });

        run.Case("an unknown build reuses an unknown build, and never a known one", () =>
        {
            List<string?> existing = [BuildingIdentity.Stamp(Stem, null, ThreeBuildings[0])];

            BuildingDecision same = BuildingIdentity.Decide(existing, Stem, null, ThreeBuildings);
            run.Equal(same.AlreadyPresent, 1, "unknown matches unknown");

            BuildingDecision differs = BuildingIdentity.Decide(existing, Stem, Build, ThreeBuildings);
            run.True(differs.Disposition == BuildingDisposition.RefuseStale, "unknown is not a sameness anyone showed");
        });

        run.Case("a GlobalId listed twice is created once", () =>
        {
            BuildingDecision decision = BuildingIdentity.Decide(
                [],
                Stem,
                Build,
                [ThreeBuildings[0], ThreeBuildings[0], ThreeBuildings[1]]);

            run.Equal(decision.GlobalIdsToCreate.Count, 2, "one element per stamp");
        });

        return run.Report("building identity");
    }
}
