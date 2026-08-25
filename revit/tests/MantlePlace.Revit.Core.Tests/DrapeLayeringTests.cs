using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The layering arithmetic: the imagery sliver takes exactly the minimum, the remainder takes the rest,
/// the total never moves, and anything degenerate is refused rather than built.
/// </summary>
internal static class DrapeLayeringTests
{
    private const double Tight = 1e-12;

    internal static int Run()
    {
        TestRun run = new();

        run.Case("a comfortable split preserves the total exactly", () =>
        {
            // 300 mm structure, ~3 mm minimum, in internal feet — the shape of a plausible type.
            double total = 0.984252;
            double minimum = 0.0104166;
            DrapeLayerSplit split = DrapeLayering.Split(total, minimum);
            run.True(split.Ok, "a 300 mm layer takes a 3 mm sliver");
            run.Within(split.ImageryThickness, minimum, Tight, "the sliver is exactly the minimum");
            run.Within(split.ImageryThickness + split.LowerThickness, total, Tight,
                "the total is preserved — the terrain must not drift off its survey elevation");
        });

        run.Case("exactly at the 2×-minimum boundary is allowed", () =>
        {
            // total = 3 × minimum leaves the lower layer at exactly twice the minimum, the last
            // allowed shape — the refusal is strictly-below, not at. Dyadic values, so "exactly"
            // survives binary floating point.
            DrapeLayerSplit split = DrapeLayering.Split(0.375, 0.125);
            run.True(split.Ok, "lower layer of exactly 2× the minimum is not degenerate");
            run.Within(split.LowerThickness, 0.25, Tight, "lower layer");
        });

        run.Case("just below the boundary refuses", () =>
        {
            DrapeLayerSplit split = DrapeLayering.Split(0.375 - 1e-9, 0.125);
            run.False(split.Ok, "a lower layer thinner than twice the minimum is refused");
        });

        run.Case("comfortably above the boundary splits", () =>
        {
            DrapeLayerSplit split = DrapeLayering.Split(0.31, 0.1);
            run.True(split.Ok, "just above the boundary");
            run.Within(split.LowerThickness, 0.21, Tight, "lower layer takes the rest");
        });

        run.Case("NaN, infinity, zero and negative inputs all refuse", () =>
        {
            run.False(DrapeLayering.Split(double.NaN, 0.1).Ok, "NaN total");
            run.False(DrapeLayering.Split(0.3, double.NaN).Ok, "NaN minimum");
            run.False(DrapeLayering.Split(double.PositiveInfinity, 0.1).Ok, "infinite total");
            run.False(DrapeLayering.Split(0.3, double.PositiveInfinity).Ok, "infinite minimum");
            run.False(DrapeLayering.Split(0.0, 0.1).Ok, "zero total");
            run.False(DrapeLayering.Split(0.3, 0.0).Ok, "zero minimum");
            run.False(DrapeLayering.Split(-0.3, 0.1).Ok, "negative total");
            run.False(DrapeLayering.Split(0.3, -0.1).Ok, "negative minimum");
        });

        run.Case("a refusal carries zeroed thicknesses, not garbage", () =>
        {
            DrapeLayerSplit split = DrapeLayering.Split(0.1, 0.1);
            run.False(split.Ok, "refused");
            run.Within(split.ImageryThickness, 0.0, Tight, "imagery thickness on refusal");
            run.Within(split.LowerThickness, 0.0, Tight, "lower thickness on refusal");
        });

        // ── The re-run guard ──────────────────────────────────────────────────────────────────
        // ⛔ This used to be a comment in the shim plus a flag on the caller — "restructure only on
        // the import that duplicated the type". It is the guarantee that a re-import cannot stack a
        // second imagery layer, and it now answers to the structure instead of to the caller, so a
        // single-layer drape type left by an older build gets repaired rather than reused forever.

        run.Case("a type whose top layer already wears the imagery is left alone", () =>
        {
            DrapeLayerDecision decision = DrapeLayering.Decide(true, 3.2808, 0.0104166);

            run.True(decision.Verdict == DrapeLayerVerdict.AlreadyLayered,
                "a second import must not insert a second imagery layer");
            run.Within(decision.ImageryThickness, 0.0, Tight, "nothing to write");
            run.Within(decision.LowerThickness, 0.0, Tight, "nothing to write");
        });

        run.Case("already-layered wins even when the top layer would still split", () =>
        {
            // The ordering matters: ask "is it already there" BEFORE "could it be split". Splitting
            // a sliver again is exactly the stacking this refuses.
            run.True(
                DrapeLayering.Decide(true, 1.0, 0.01).Verdict == DrapeLayerVerdict.AlreadyLayered,
                "the material on layer 0 decides, not the arithmetic");
        });

        run.Case("a type not yet layered is layered, with Split's own numbers", () =>
        {
            const double Total = 3.2808;      // "Generic - 1000mm", in internal feet
            const double Minimum = 0.0104166; // the ~3 mm host floor

            DrapeLayerDecision decision = DrapeLayering.Decide(false, Total, Minimum);
            DrapeLayerSplit split = DrapeLayering.Split(Total, Minimum);

            run.True(decision.Verdict == DrapeLayerVerdict.Layer, "the top layer is split");
            run.Within(decision.ImageryThickness, split.ImageryThickness, Tight, "same sliver as Split");
            run.Within(decision.LowerThickness, split.LowerThickness, Tight, "same remainder as Split");
            run.Within(decision.ImageryThickness + decision.LowerThickness, Total, Tight,
                "and the total is still preserved");
        });

        run.Case("the real type the plugin chooses leaves a sub-5 mm sliver", () =>
        {
            // The number that decides the documented escalation to Document.Paint: whether the
            // imagery layer is legible at site-view zoom. On "Generic - 1000mm" it is not close.
            DrapeLayerDecision decision = DrapeLayering.Decide(false, 3.2808, 0.0104166);
            double sliverMm = DrapeLayering.MillimetresFromInternalFeet(decision.ImageryThickness);

            run.True(decision.Verdict == DrapeLayerVerdict.Layer, "it splits");
            run.True(sliverMm < 5.0, $"the sliver is {sliverMm} mm — a metre-thick terrain barely notices");
            run.True(
                DrapeLayering.MillimetresFromInternalFeet(decision.LowerThickness) > 990.0,
                "and the terrain below it keeps essentially all of its thickness");
        });

        run.Case("a degenerate top layer refuses whether or not it wears the imagery", () =>
        {
            run.True(DrapeLayering.Decide(false, 0.1, 0.1).Verdict == DrapeLayerVerdict.Refuse,
                "too thin to split");
            run.True(DrapeLayering.Decide(false, double.NaN, 0.1).Verdict == DrapeLayerVerdict.Refuse,
                "NaN is refused, not layered");
            run.Within(DrapeLayering.Decide(false, 0.1, 0.1).ImageryThickness, 0.0, Tight,
                "a refusal carries zeroed thicknesses here too");
        });

        // ── The read-back ─────────────────────────────────────────────────────────────────────
        // SetCompoundStructure was the last write in the drape step nothing read back. These two are
        // what let the import log state the answer to "does the photograph reach a vertical face"
        // instead of asserting it.

        run.Case("the imagery must be on layer 0 and nowhere else", () =>
        {
            run.True(DrapeLayering.ImageryIsTopAndOnly([true, false]), "the shape the drape builds");
            run.True(DrapeLayering.ImageryIsTopAndOnly([true, false, false]), "a multi-layer original");
            run.False(DrapeLayering.ImageryIsTopAndOnly([false, true]),
                "on a lower layer the photograph is buried, not draped");
            run.False(DrapeLayering.ImageryIsTopAndOnly([true, true]),
                "twice is the stacking the guard exists to prevent");
            run.False(DrapeLayering.ImageryIsTopAndOnly([false]), "not there at all");
            run.False(DrapeLayering.ImageryIsTopAndOnly([]), "no structure is not a pass");
        });

        run.Case("a layer stack describes itself in millimetres, invariantly", () =>
        {
            string described = DrapeLayering.Describe(
            [
                new DrapeLayerLine("Structure", 0.0104166, "Mantle Place Site Imagery f93bc782"),
                new DrapeLayerLine("Structure", 3.2703834, "Grass"),
            ]);

            run.Contains(described, "0 Structure 3.175 mm \"Mantle Place Site Imagery f93bc782\"",
                "the sliver, in the unit the escalation is judged in");
            run.Contains(described, "1 Structure 996.813 mm \"Grass\"", "and what is left beneath it");
            run.Contains(described, " / ", "one line, not one per layer");
        });

        run.Case("a layer with no material reads as by-category rather than as an empty name", () =>
        {
            run.Contains(
                DrapeLayering.Describe([new DrapeLayerLine("Structure", 1.0, "")]),
                "by category",
                "an unset material is a real Revit state, not a formatting failure");
            run.Equal(DrapeLayering.Describe([]), "no layers", "and nothing at all says so");
        });

        return run.Report("drape layering");
    }
}
