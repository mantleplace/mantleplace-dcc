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

        run.Case("an error rolls back whatever its id, unless it is on the allowlist", () =>
        {
            // ⛔ Why severity leads and the id can only ever soften. The failure the first real import
            // hit is a composite: SlabShapeEditFailedError is "Slab Shape Edit failed. [Description]"
            // and it substitutes SlabShapeFailedTooThin's text into itself, so a policy keyed on the
            // inner id would never fire on the outer one — and the import would go straight back to
            // the dead-end modal this whole type exists to prevent.
            foreach (ImportFailureKind kind in Enum.GetValues<ImportFailureKind>())
            {
                if (kind == ImportFailureKind.CannotKeepElementsJoined)
                {
                    continue;
                }

                run.Equal(
                    ImportFailurePolicy.Decide(kind, isError: true, hasResolutions: true) == ImportFailureAction.RollBack,
                    true,
                    $"an error posted as {kind} rolls back even when Revit offers a way out");
            }
        });

        run.Case("the one allowlisted error is resolved instead", () =>
        {
            // "Can't keep elements joined" comes out of Revit's own IFC importer. Unjoining is what a
            // curator does by hand and it loses nothing: the elements stay, they stop sharing
            // geometry. It is an allowlist and not "any error with a resolution" because Revit's
            // resolution for the toposolid failures is to DELETE THE TOPOSOLID — which would turn a
            // refused import into a silently empty one.
            run.Equal(
                ImportFailurePolicy.Decide(ImportFailureKind.CannotKeepElementsJoined, isError: true, hasResolutions: true)
                    == ImportFailureAction.Resolve,
                true,
                "resolved, not rolled back");

            run.Equal(
                ImportFailurePolicy.Decide(ImportFailureKind.CannotKeepElementsJoined, isError: true, hasResolutions: false)
                    == ImportFailureAction.RollBack,
                true,
                "but never pretended to be resolvable when Revit offers nothing");
        });

        run.Case("every warning is swallowed, including ones this build has never seen", () =>
        {
            foreach (ImportFailureKind kind in Enum.GetValues<ImportFailureKind>())
            {
                run.Equal(
                    ImportFailurePolicy.Decide(kind, isError: false, hasResolutions: false) == ImportFailureAction.Swallow,
                    true,
                    $"a warning posted as {kind} is absorbed — an unattended run must never leave a modal");
            }
        });

        run.Case("a resolved error names the button that was pressed on the curator's behalf", () =>
        {
            string text = ImportFailurePolicy.ExplainResolved(
                ImportFailureKind.CannotKeepElementsJoined, "Unjoin Elements", 1);

            run.Contains(text, "Unjoin Elements", "the resolution's own caption, quoted");
            run.Contains(text, "Nothing was deleted", "and what it did not do");
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
                    or ImportFailureKind.SlabShapeEditFailed
                    or ImportFailureKind.CannotKeepElementsJoined)
                {
                    // The error kinds are worded by ExplainRollBack and ExplainResolved, not here.
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
