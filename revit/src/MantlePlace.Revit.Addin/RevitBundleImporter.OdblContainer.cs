using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// The ODbL container: whatever a step whose files are `database` (a missing role included) made is
// filed, after the step, in a selection filter named for the order. Which steps those are was decided
// by the planner (ImportStep.Odbl); Prepare for handover works from the filter alone.
internal sealed partial class RevitBundleImporter
{
    /// <summary>
    /// <paramref name="work"/>, then the elements it made filed in the ODbL container.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What a step made is the stamped elements there are after it and were not before. That is every
    /// kind of element a step writes (all are stamped, and the site context filter relies on it), and
    /// it needs no step to report its ids. An element an earlier import made and this one reused is
    /// not new, so it stays where that import left it: a project imported before the container
    /// existed is not re-filed.
    /// </para>
    /// <para>
    /// The filing runs in a <c>finally</c>, so a step that fails half-way still files the elements it
    /// left behind. It is not the step's own commit: a filing that fails is said, and the step is not
    /// marked rolled back for it.
    /// </para>
    /// </remarks>
    private IEnumerable<StepProgress> FiledInOdblContainer(ImportStep step, IEnumerable<StepProgress> work)
    {
        HashSet<ElementId> before = OdblContainers.StampedElementIds(_document);
        try
        {
            foreach (StepProgress progress in work)
            {
                yield return progress;
            }
        }
        finally
        {
            FileInOdblContainer(step, before);
        }
    }

    private void FileInOdblContainer(ImportStep step, HashSet<ElementId> before)
    {
        try
        {
            List<ElementId> made = [.. OdblContainers.StampedElementIds(_document).Where(id => !before.Contains(id))];
            if (made.Count == 0)
            {
                return;
            }

            string identity = _archive.Layout.Key.Stem;
            string group = OdblContent.GroupOf(step.Kind);
            string name = OdblContent.ContainerName(identity, group);

            bool stepRolledBack = _stepRolledBack;
            ImportFailureSwallower swallower = new("Filing the ODbL content");
            using Transaction transaction = BeginTransaction("Mantle Place: ODbL container", swallower);

            (SelectionFilterElement Filter, bool Created)? filed = OdblContainers.File(_document, identity, group, made);
            bool shown = filed is { Created: true } created && OdblContainers.ShowInSiteContextView(_document, created.Filter);

            bool committed = CommitAndReport(transaction, swallower);
            _stepRolledBack = stepRolledBack;
            if (!committed)
            {
                return;
            }

            Say(filed is null
                ? $"A filter other than a selection filter already holds the name \"{name}\", so this step's "
                    + $"{made.Count} ODbL element(s) were not filed in a container. They are still stamped, and "
                    + "Prepare for handover will not find them."
                : $"Filed {made.Count} element(s) from the \"{step.Kind}\" step in the selection filter \"{name}\": "
                    + "their source files are an ODbL Derivative Database (or the manifest states no role, which "
                    + "reads as one). Prepare for handover removes them."
                    + (shown ? $" The \"{SiteContext.ViewName}\" view draws them in orange." : string.Empty));
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
        {
            Say($"The \"{step.Kind}\" step's ODbL content could not be filed in a container — {ex.Message} "
                + "The elements are in the project and stamped.");
        }
    }
}
