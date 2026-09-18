// UseWPF switches the SDK to the WindowsDesktop implicit-usings set, which drops System.IO.
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MantlePlace.Revit.Core;
using Microsoft.Win32;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// "Import Bundle": pick a downloaded bundle, read its manifest, and run the resulting plan.
/// </summary>
/// <remarks>
/// <para>
/// The whole command is orchestration — file picker, then an <see cref="ActiveImport"/> handed to
/// <see cref="BundleImportEventHandler"/> to run staged, with the import window. Every rule it appears
/// to enforce (the version gate, which topo path wins, whether shared coordinates may be set) is
/// enforced in <c>MantlePlace.Revit.Core</c> and asserted headlessly there.
/// </para>
/// <para>
/// <b>The picker is skippable, and that is the point.</b> Setting
/// <see cref="LocalBundleSource.PathVariable"/> names the zip up front, so this command runs
/// unattended from a Revit journal or a tester script — which is the only way
/// <c>Toposolid.Create</c>, <c>RevitLinkType.CreateFromIFC</c> and
/// <c>ProjectLocation.SetProjectPosition</c> ever execute inside Revit under test. An
/// unattended run raises no dialog: nothing is there to dismiss it, and playback would block on it
/// forever, so it writes to <see cref="LocalBundleSource.LogPathFor"/> instead.
/// </para>
/// </remarks>
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

        // One import at a time: two staged runs would interleave their slices in one document.
        if (unattended is null && MantlePlaceApplication.ImportHandler.IsImporting)
        {
            MantlePlaceApplication.ImportHandler.ShowRunning();
            Report(unattended, "Another import is open.", MantlePlaceApplication.ImportHandler.BusyReason);
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

        // ⛔ The record is written on the ATTENDED path too, and that is the point of it. Two steps of
        // this import freeze Revit for minutes inside one commit with a "this will take a while" line
        // that has to be readable WHILE they run (SlowStepNotice). A curator can open a text file
        // beside a frozen Revit. ActiveImport.Open makes it, and its first line begins it.
        if (ActiveImport.Open(commandData.Application.Application, document, zipPath, out ImportRefusal? refusal)
            is not { } import)
        {
            message = refusal!.Message;
            Report(unattended, refusal.Instruction, refusal.Message);
            return refusal.IsFailure ? Result.Failed : Result.Cancelled;
        }

        if (unattended is null)
        {
            // The import window opens on its checklist; once the curator presses Import the run is
            // staged, one step or one chunk per ExternalEvent raise, so Revit repaints between them
            // and Cancel is honoured. This command returns now; the handler owns the import from
            // here and closes it when the run is over.
            MantlePlaceApplication.ImportHandler.TakeOver(import, commandData.Application.MainWindowHandle);
            return Result.Succeeded;
        }

        // ⛔ Unattended: synchronous and log-only. A modeless window in a journal playback never
        // closes, and nothing is there to tick a box or click Cancel, so the same import brings in
        // every layer and runs to the end in place.
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

    /// <summary>
    /// Tells the curator why the import did not start, as a dialog when there is someone driving to
    /// read it. The log beside the zip already has it (<see cref="ActiveImport.Open"/>).
    /// </summary>
    /// <remarks>
    /// A <c>TaskDialog</c> raised during journal playback never gets dismissed, so the run that
    /// exists to prove the import works would hang instead — which is why the dialog, not the file,
    /// is the conditional half.
    /// </remarks>
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
