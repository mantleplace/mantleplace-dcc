using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The terrain's re-import identity: whether a second import of one bundle builds a second ground,
/// reuses the first, or refuses — all decided from the stamp the earlier import wrote.
/// </summary>
internal static class TerrainIdentityTests
{
    private const string Stem = "eb00f56f-74f5-4134-9c8a-baf82215191a";
    private const string OtherStem = "f8d950ba-33f6-4c7c-a562-9c2124f35105";

    /// <summary>A surface artifact's sha256, and the first twelve characters the stamp keeps.</summary>
    private const string Sha = "9f2c41ab77de5310ac4b0e6612f8a7d3b1c05e94aa2f6d8813bb47c0192e5f6a";

    private const string ShaToken = "9f2c41ab77de";

    /// <summary>A rebuild of the same order: a different surface, under the same identity.</summary>
    private const string RebuiltSha = "0a1b2c3d4e5f60718293a4b5c6d7e8f9001122334455667788990aabbccddeeff";

    private static string StampFor(string stem, string token) => $"Mantle Place Terrain {stem}/{token}";

    internal static int Run()
    {
        TestRun run = new();

        run.Case("the stamp is the cache-key stem and a short surface digest", () =>
        {
            run.Equal(TerrainIdentity.Stamp(Stem, Sha), StampFor(Stem, ShaToken), "stamp shape");
        });

        run.Case("a bundle that declares no digest stamps its build half unknown", () =>
        {
            run.Equal(TerrainIdentity.Stamp(Stem, null), StampFor(Stem, "unknown"), "null sha");
            run.Equal(TerrainIdentity.Stamp(Stem, "   "), StampFor(Stem, "unknown"), "blank sha");
        });

        run.Case("a digest is lower-cased before it is truncated", () =>
        {
            run.Equal(TerrainIdentity.Stamp(Stem, Sha.ToUpperInvariant()), StampFor(Stem, ShaToken), "upper-case sha");
        });

        run.Case("a first import into a project with no ground builds and says nothing", () =>
        {
            TerrainDecision decision = TerrainIdentity.Decide([], Stem, Sha);
            run.Equal((int)decision.Disposition, (int)TerrainDisposition.Create, "creates");
            run.Equal(decision.Stamp, StampFor(Stem, ShaToken), "carries the stamp to write");
            run.Equal(decision.Explanation, string.Empty, "nothing worth saying");
        });

        run.Case("a re-import of the same build reuses the ground it already made", () =>
        {
            TerrainDecision decision = TerrainIdentity.Decide(
                [new ExistingTerrain(1721188, StampFor(Stem, ShaToken))],
                Stem,
                Sha);

            run.Equal((int)decision.Disposition, (int)TerrainDisposition.Reuse, "reuses");
            run.True(decision.ExistingElementId == 1721188, "names the terrain to go on using");
            run.Contains(decision.Explanation, "already in this project", "says which arm it took");
        });

        run.Case("a re-import of a REBUILT order refuses rather than stacking or deleting", () =>
        {
            TerrainDecision decision = TerrainIdentity.Decide(
                [new ExistingTerrain(1721188, StampFor(Stem, ShaToken))],
                Stem,
                RebuiltSha);

            run.Equal((int)decision.Disposition, (int)TerrainDisposition.RefuseStale, "refuses");
            run.True(decision.ExistingElementId == 1721188, "names the stale terrain");
            run.Contains(decision.Explanation, "EARLIER build", "says why");
            run.Contains(decision.Explanation, "1721188", "names the element to remove");
            run.Contains(decision.Explanation, "delete", "says how to remove it");
        });

        run.Case("an unknown build meeting a declared digest is stale, not the same ground", () =>
        {
            // ⛔HPS-28: a null sha is UNKNOWN. Reusing here would be claiming a sameness nothing
            // demonstrates, and the whole cost of being wrong is a stale ground kept silently.
            TerrainDecision decision = TerrainIdentity.Decide(
                [new ExistingTerrain(90, StampFor(Stem, "unknown"))],
                Stem,
                Sha);

            run.Equal((int)decision.Disposition, (int)TerrainDisposition.RefuseStale, "refuses");
        });

        run.Case("two imports of one digest-less bundle still reuse, and say the digest is missing", () =>
        {
            TerrainDecision decision = TerrainIdentity.Decide(
                [new ExistingTerrain(90, StampFor(Stem, "unknown"))],
                Stem,
                null);

            run.Equal((int)decision.Disposition, (int)TerrainDisposition.Reuse, "reuses");
            run.Contains(decision.Explanation, "no digest", "the caveat is stated, not hidden");
        });

        run.Case("a curator's own ground is never claimed, reused or refused", () =>
        {
            TerrainDecision decision = TerrainIdentity.Decide(
                [new ExistingTerrain(500, null), new ExistingTerrain(501, "site model, do not delete")],
                Stem,
                Sha);

            run.Equal((int)decision.Disposition, (int)TerrainDisposition.Create, "builds its own");
            run.True(decision.ExistingElementId == 0, "claims nothing of the curator's");
            run.Contains(decision.Explanation, "not this bundle's", "the second ground is not silent");
            run.Contains(decision.Explanation, "2 ground toposolids", "counts what it found");
        });

        run.Case("another order's terrain is a stranger, so both grounds coexist", () =>
        {
            TerrainDecision decision = TerrainIdentity.Decide(
                [new ExistingTerrain(700, StampFor(OtherStem, ShaToken))],
                Stem,
                Sha);

            run.Equal((int)decision.Disposition, (int)TerrainDisposition.Create, "adjacent sites both import");
            run.Contains(decision.Explanation, "another order's", "says what it might be");
        });

        run.Case("a stem is not claimed by a stem it is a prefix of", () =>
        {
            // Cache-key stems are truncated hashes, so one being a prefix of another is a collision
            // waiting rather than a hypothetical.
            TerrainDecision decision = TerrainIdentity.Decide(
                [new ExistingTerrain(800, StampFor("abcdef", ShaToken))],
                "abc",
                Sha);

            run.Equal((int)decision.Disposition, (int)TerrainDisposition.Create, "abc does not own abcdef's ground");
        });

        run.Case("a project already carrying two stamped grounds uses the first and says so", () =>
        {
            // The state the bug left behind: this is what a re-import finds in a project imported
            // twice by a version with no terrain identity at all.
            TerrainDecision decision = TerrainIdentity.Decide(
                [
                    new ExistingTerrain(1721188, StampFor(Stem, ShaToken)),
                    new ExistingTerrain(1761038, StampFor(Stem, ShaToken)),
                ],
                Stem,
                Sha);

            run.Equal((int)decision.Disposition, (int)TerrainDisposition.Reuse, "still reuses");
            run.True(decision.ExistingElementId == 1721188, "the first one wins");
            run.Contains(decision.Explanation, "2 ground toposolids here carry", "the duplication is reported");
        });

        run.Case("only this bundle's stamps read as an identity", () =>
        {
            run.True(TerrainIdentity.IsStampFor(StampFor(Stem, ShaToken), Stem), "own stamp");
            run.False(TerrainIdentity.IsStampFor(null, Stem), "no comments");
            run.False(TerrainIdentity.IsStampFor(string.Empty, Stem), "empty comments");
            run.False(TerrainIdentity.IsStampFor($"Mantle Place Terrain {Stem}/", Stem), "prefix with no build half");
            run.False(TerrainIdentity.IsStampFor(StampFor(OtherStem, ShaToken), Stem), "another order's");
            run.False(
                TerrainIdentity.IsStampFor($"Mantle Place Site Boundary {Stem}/Zone A", Stem),
                "a subdivision's stamp is not a terrain's");
        });

        run.Case("the build half is readable back off a stamp", () =>
        {
            run.Equal(TerrainIdentity.BuildTokenOf(StampFor(Stem, ShaToken), Stem), ShaToken, "own stamp");
            run.Equal(TerrainIdentity.BuildTokenOf("something a curator typed", Stem), null, "not a stamp");
        });

        return run.Report("terrain identity");
    }
}
