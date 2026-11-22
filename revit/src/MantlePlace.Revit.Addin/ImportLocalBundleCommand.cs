using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MantlePlace.Core;
using Microsoft.Win32;

namespace MantlePlace.Revit.Addin;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public sealed class ImportLocalBundleCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        ArgumentNullException.ThrowIfNull(commandData);

        Document document = commandData.Application.ActiveUIDocument?.Document
            ?? throw new InvalidOperationException("no active document");

        string? unattended = LocalBundleSource.Unattended(
            Environment.GetEnvironmentVariable(LocalBundleSource.PathVariable));

        if (unattended is null && MantlePlaceApplication.ImportHandler.IsImporting)
        {
            Report(unattended, "Another import is open.", MantlePlaceApplication.ImportHandler.BusyReason);
            MantlePlaceApplication.ImportHandler.ShowRunning();
            return Result.Cancelled;
        }

        string zipPath;
        if (unattended is not null)
        {
            zipPath = unattended;
        }
        else
        {
            OpenFileDialog picker = new()
            {
                Title = "Choose a Mantle Place bundle",
                Filter = "Mantle Place bundle (*.zip)|*.zip",
                CheckFileExists = true,
            };

            if (picker.ShowDialog() != true)
            {
                return Result.Cancelled;
            }

            zipPath = picker.FileName;
        }

        if (ActiveImport.Open(commandData.Application.Application, document, zipPath, out ImportRefusal? refusal)
            is not { } import)
        {
            message = refusal!.Message;
            Report(unattended, refusal.Instruction, refusal.Message);
            return refusal.IsFailure ? Result.Failed : Result.Cancelled;
        }

        if (unattended is null)
        {
            MantlePlaceApplication.ImportHandler.TakeOver(import, commandData.Application.MainWindowHandle);
            return Result.Succeeded;
        }

        using (import)
        {
            import.Begin(ImportLayerChoice.All);
            import.RunToEnd();
            if (import.Failed)
            {
                message = import.Summary ?? import.Heading;
                return Result.Failed;
            }
        }

        return Result.Succeeded;
    }

    private static void Report(string? unattendedZipPath, string instruction, string body)
    {
        if (unattendedZipPath is not null)
        {
            return;
        }

        TaskDialog dialog = new("Mantle Place")
        {
            MainInstruction = instruction,
            MainContent = body,
        };
        dialog.Show();
    }
}
