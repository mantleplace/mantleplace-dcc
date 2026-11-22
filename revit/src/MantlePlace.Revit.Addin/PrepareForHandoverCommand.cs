using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

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
