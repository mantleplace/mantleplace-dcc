using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// "Prepare for Handover": makes the open project safe to give to a client. It removes the ODbL
/// content an import filed in its container, keeps a Produced Work terrain of the same order where
/// there is one (a swap), and leaves the data credit in the project.
/// </summary>
/// <remarks>
/// <para>
/// The work is <see cref="PrepareForHandover"/>'s, with no dialog in it; this asks first, because it
/// deletes, and says what was done. It works from the containers alone, so it needs no bundle and
/// works on a project a colleague saved.
/// </para>
/// <para>
/// One transaction, so one undo step, and it saves nothing. Deleting an element deletes what hangs on
/// it (a toposolid's subdivisions, a tree hosted on it), which is why the result counts what Revit
/// removed as well as what was filed.
/// </para>
/// </remarks>
[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class PrepareForHandoverCommand : IExternalCommand
{
    private const string Title = "Mantle Place: Prepare for Handover";

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        ArgumentNullException.ThrowIfNull(commandData);

        Document? document = commandData.Application.ActiveUIDocument?.Document;
        if (document is null || document.IsFamilyDocument)
        {
            new TaskDialog(Title)
            {
                MainInstruction = "Open a project first.",
                MainContent = "Prepare for Handover works on the open project.",
            }.Show();
            return Result.Cancelled;
        }

        PrepareForHandover.HandoverPlan plan = PrepareForHandover.Plan(document);
        TaskDialog ask = new(Title)
        {
            MainInstruction = "Prepare this project for handover?",
            MainContent = HandoverSummary.Ask(plan.Counts),
            CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
            DefaultButton = TaskDialogResult.No,
        };
        if (ask.Show() != TaskDialogResult.Yes)
        {
            return Result.Cancelled;
        }

        PrepareForHandover.HandoverResult result = PrepareForHandover.Run(document, plan);
        if (!result.Committed)
        {
            message = result.Failure ?? string.Empty;
            return Result.Failed;
        }

        new TaskDialog(Title)
        {
            MainInstruction = "This project is prepared for handover.",
            MainContent = HandoverSummary.Done(plan.Counts, result.Deleted.Count, result.CreditLine!),
        }.Show();
        return Result.Succeeded;
    }
}
