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

        run.Case("a refused subdivision is deleted alone when the error names only this step's own new elements", () =>
        {
            // The refusal arrives at commit, after every cut has returned, and names the one
            // subdivision Revit could not make. Deleting that element loses one published feature;
            // rolling back loses the layer.
            run.Equal(
                ImportFailurePolicy.Decide(ImportFailureKind.SubDivisionRefused, isError: true, hasResolutions: false, namesOnlyOwnNewElements: true),
                ImportFailureAction.DeleteNamedElements,
                "deleted and counted, not rolled back");
        });

        run.Case("a refused subdivision still rolls back when it names anything the step did not just create", () =>
        {
            // ⛔ The terrain itself, a curator's element or an earlier import's subdivision is never
            // deleted to make a commit go through — and nor is nothing, when the error names no element.
            run.Equal(
                ImportFailurePolicy.Decide(ImportFailureKind.SubDivisionRefused, isError: true, hasResolutions: false, namesOnlyOwnNewElements: false),
                ImportFailureAction.RollBack,
                "an error naming anything else rolls back");
        });

        run.Case("a deleted subdivision names its published feature, Revit's words and the failure id", () =>
        {
            string text = ImportFailurePolicy.ExplainRefusedSubDivision(
                31,
                "(unnamed)",
                "An error occurred during the sub-divide action. The sub-divide can not be completed.",
                "07338aaa-c5fe-4aa0-91e1-fa0569a8fe76",
                "4012");
            run.Contains(text, "feature 31", "the position in the layer, the only identity an unnamed feature has");
            run.Contains(text, "\"(unnamed)\"", "and its name");
            run.Contains(text, "The sub-divide can not be completed.", "Revit's own text, searchable");
            run.Contains(text, "(id 07338aaa-c5fe-4aa0-91e1-fa0569a8fe76, ", "and the failure id");
            run.Contains(text, "element 4012", "and the element Revit named, which was deleted");
            run.Contains(text, "the rest of the layer was kept", "and the curator knows the layer stood");
        });

        run.Case("a rollback traces each element the error named back to its feature, or says it could not", () =>
        {
            const string id = "07338aaa-c5fe-4aa0-91e1-fa0569a8fe76";
            string cut = ImportFailurePolicy.ExplainRefusalNamedFeature(id, 1234, "Home Ranch", "4012");
            run.Contains(cut, "feature 1,234", "the feature's position, formatted invariantly");
            run.Contains(cut, "\"Home Ranch\"", "and its name");
            run.Contains(cut, "element 4012", "and the element");
            run.Contains(cut, id, "and the failure it belongs to");

            string other = ImportFailurePolicy.ExplainRefusalNamedOther(id, "2785");
            run.Contains(other, "element 2785", "an element that is not one of this step's cuts is named by id");
            run.Contains(other, "not one of this step's cuts", "and said to be someone else's");

            run.Contains(ImportFailurePolicy.ExplainRefusalNamedNothing(id), "named no element", "an error naming nothing says so");
        });

        run.Case("no other error is deleted away, whatever it names", () =>
        {
            foreach (ImportFailureKind kind in Enum.GetValues<ImportFailureKind>())
            {
                if (kind == ImportFailureKind.SubDivisionRefused)
                {
                    continue;
                }

                run.Equal(
                    ImportFailurePolicy.Decide(kind, isError: true, hasResolutions: false, namesOnlyOwnNewElements: true)
                        == ImportFailureAction.DeleteNamedElements,
                    false,
                    $"an error posted as {kind} is not deleted away");
            }
        });

        run.Case("a warning is swallowed even when it names only this step's new elements", () =>
        {
            run.Equal(
                ImportFailurePolicy.Decide(ImportFailureKind.SubDivisionRefused, isError: false, hasResolutions: false, namesOnlyOwnNewElements: true),
                ImportFailureAction.Swallow,
                "severity still decides first");
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
                    or ImportFailureKind.CannotKeepElementsJoined
                    or ImportFailureKind.SubDivisionRefused)
                {
                    // The error kinds are worded by ExplainRollBack, ExplainResolved and
                    // ExplainRefusedSubDivision, not here.
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
            string text = ImportFailurePolicy.ExplainRollBack("Building the terrain", revit, "0f1e2d3c");

            run.Contains(text, revit, "the curator's only searchable string survives verbatim");
            run.Contains(text, "Nothing from this step was left", "and they are told the project is clean");
        });

        run.Case("a rollback with no text from Revit still says something", () =>
        {
            run.Contains(
                ImportFailurePolicy.ExplainRollBack("Building the terrain", "   ", "0f1e2d3c"),
                "no reason given",
                "an empty description is stated rather than rendered as empty quotes");
        });

        run.Case("a rollback carries Revit's failure id, so the next session can map it without a rerun", () =>
        {
            string text = ImportFailurePolicy.ExplainRollBack(
                "Importing the site boundaries",
                "An error occurred during the sub-divide action. The sub-divide can not be completed.",
                "4c9b0f2a-7e61-4d3b-9a55-0123456789ab");
            run.Contains(text, "(id 4c9b0f2a-7e61-4d3b-9a55-0123456789ab)", "the id is quoted after Revit's text");
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
