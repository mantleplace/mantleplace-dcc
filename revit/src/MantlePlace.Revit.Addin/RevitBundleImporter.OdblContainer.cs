using Autodesk.Revit.DB;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal sealed partial class RevitBundleImporter
{
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
            bool shown = filed is { } done && OdblContainers.ShowInSiteContextView(_document, done.Filter);

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
