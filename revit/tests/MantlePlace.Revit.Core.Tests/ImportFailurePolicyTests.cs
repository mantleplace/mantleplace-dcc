using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Which Revit failures an unattended import absorbs, and how it accounts for the ones it did.
/// </summary>
internal static class ImportFailurePolicyTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("ANY error rolls back, whatever its id", () =>
        {
            // ⛔ The reason the policy takes severity and not an id. The failure the first real import
            // hit is a composite: SlabShapeEditFailedError is "Slab Shape Edit failed. [Description]"
            // and it substitutes SlabShapeFailedTooThin's text into itself, so a policy keyed on the
            // inner id would never fire on the outer one — and the import would go straight back to
            // the dead-end modal this whole type exists to prevent.
            foreach (ImportFailureKind kind in Enum.GetValues<ImportFailureKind>())
            {
                run.Equal(
                    ImportFailurePolicy.Decide(isError: true) == ImportFailureAction.RollBack,
                    true,
                    $"an error posted as {kind} rolls back");
            }
        });

        run.Case("every warning is swallowed, including ones this build has never seen", () =>
        {
            run.Equal(
                ImportFailurePolicy.Decide(isError: false) == ImportFailureAction.Swallow,
                true,
                "an unattended run must never leave a modal for nobody to dismiss");
        });

        run.Case("the eight overlaps read as an explanation, not as a failure", () =>
        {
            // The real case: site context imported into a project that already contained a building.
            // Correct geometry, indistinguishable today from the one error that killed the import.
            string text = ImportFailurePolicy.Explain(ImportFailureKind.ToposolidFloorOverlap, 8);
            run.Contains(text, "8 places", "it says how many");
            run.Contains(text, "expected", "and that it is expected");
            run.Contains(text, "nothing in those floors was changed", "and that nothing was harmed");
        });

        run.Case("one is singular", () =>
        {
            run.Contains(
                ImportFailurePolicy.Explain(ImportFailureKind.ToposolidFloorOverlap, 1),
                "1 place",
                "not \"1 places\"");
        });

        run.Case("every known kind has wording of its own", () =>
        {
            foreach (ImportFailureKind kind in Enum.GetValues<ImportFailureKind>())
            {
                if (kind is ImportFailureKind.Unknown
                    or ImportFailureKind.SlabShapeTooThin
                    or ImportFailureKind.SlabShapeEditFailed)
                {
                    // The error kinds are worded by ExplainRollBack, not here.
                    continue;
                }

                string text = ImportFailurePolicy.Explain(kind, 3);
                run.False(text.Contains(kind.ToString(), StringComparison.Ordinal),
                    $"{kind} should not fall through to the enum-name arm");
            }
        });

        run.Case("a rollback quotes Revit rather than paraphrasing it", () =>
        {
            const string revit = "Slab Shape Edit failed. The Floor or Roof or Toposolid is too thin for its given type.";
            string text = ImportFailurePolicy.ExplainRollBack("Building the terrain", revit);

            run.Contains(text, revit, "the curator's only searchable string survives verbatim");
            run.Contains(text, "Nothing from this step was left", "and they are told the project is clean");
        });

        run.Case("a rollback with no text from Revit still says something", () =>
        {
            run.Contains(
                ImportFailurePolicy.ExplainRollBack("Building the terrain", "   "),
                "no reason given",
                "an empty description is stated rather than rendered as empty quotes");
        });

        run.Case("an unrecognised warning carries its id so the next session can add the case", () =>
        {
            string text = ImportFailurePolicy.ExplainUnknownWarning("Autodesk.Revit.DB.Whatever", "Something odd.", 2);
            run.Contains(text, "Autodesk.Revit.DB.Whatever", "the id is the only actionable part");
            run.Contains(text, "Something odd.", "and Revit's own words");
            run.Contains(text, "nothing was rolled back", "and the curator knows the import stood");
        });

        return run.Report("import failure policy");
    }
}
