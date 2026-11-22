using MantlePlace.Core;

namespace MantlePlace.Core.Tests;

internal static class SubDivisionDrapeTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("nothing to say when there were no subdivisions at all", () =>
        {
            run.True(SubDivisionDrape.Clause(0, 0, []) is null, "no subdivisions produces no clause");
        });

        run.Case("every subdivision draped", () =>
        {
            string? clause = SubDivisionDrape.Clause(4, 0, []);
            run.Contains(clause, "4 subdivision(s)", "the count");
            run.True(clause?.StartsWith("; ", StringComparison.Ordinal) == true,
                "the clause appends to the drape summary");
            run.True(clause?.Contains("untextured", StringComparison.Ordinal) == false,
                "no refusal language when nothing was refused");
        });

        run.Case("a refusal names the consequence, not just the count", () =>
        {
            string? clause = SubDivisionDrape.Clause(0, 17, ["This Element cannot have type assigned."]);
            run.Contains(clause, "17 subdivision(s)", "the count");
            run.Contains(clause, "show through the photograph as untextured patches",
                "what the curator will actually SEE — \"kept their original look\" read as cosmetic");
            run.Contains(clause, "This Element cannot have type assigned.",
                "Revit's own words, which is the half that was discarded for two sessions");
        });

        run.Case("a partial refusal reports both halves", () =>
        {
            string? clause = SubDivisionDrape.Clause(12, 5, ["a subdivision had no writable Material parameter"]);
            run.Contains(clause, "12 subdivision(s)", "the ones that worked");
            run.Contains(clause, "5 subdivision(s) would not take it", "the ones that did not");
        });

        run.Case("distinct reasons are all carried, not just the first", () =>
        {
            string? clause = SubDivisionDrape.Clause(0, 2, ["cannot have type assigned", "the material did not hold"]);
            run.Contains(clause, "cannot have type assigned", "the first reason");
            run.Contains(clause, "the material did not hold", "the second reason");
        });

        run.Case("a refusal with no reason says so out loud", () =>
        {
            string? clause = SubDivisionDrape.Clause(0, 3, []);
            run.Contains(clause, "3 subdivision(s)", "the count survives");
            run.Contains(clause, "no reason", "the absence of a reason is itself reported");
            run.Contains(clause, "defect", "and named as a defect, not as a normal outcome");
        });

        run.Case("negative counts are refused rather than formatted", () =>
        {
            bool threw = false;
            try
            {
                SubDivisionDrape.Clause(-1, 0, []);
            }
            catch (ArgumentOutOfRangeException)
            {
                threw = true;
            }

            run.True(threw, "a negative count is a caller bug, not a sentence to render");
        });

        return run.Report("subdivision drape reporting");
    }
}
