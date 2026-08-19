// UseWPF switches the SDK to the WindowsDesktop implicit-usings set, which drops System.IO.
using System.IO;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;
using Microsoft.Win32;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// "Import bundle zip": pick a downloaded bundle, read its manifest, and run the resulting plan.
/// </summary>
/// <remarks>
/// <para>
/// The whole command is orchestration — file picker, plan, execute, report. Every rule it appears
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

        // The picker guarantees this; an environment variable does not, and an unhandled
        // FileNotFoundException during journal playback is Revit's internal-error dialog with
        // nothing there to dismiss it.
        if (!File.Exists(zipPath))
        {
            message = $"No bundle zip at \"{zipPath}\".";
            Report(unattended, "Bundle not found.", message);
            return Result.Failed;
        }

        using LocalBundleArchive archive = LocalBundleArchive.Open(zipPath);

        if (archive.Manifest is not { } manifest)
        {
            message = "That zip has no Metadata/manifest.json, so it is not a Mantle Place bundle.";
            Report(unattended, "Not a Mantle Place bundle.", message);
            return Result.Failed;
        }

        BundleImportPlan plan = BundleImportPlanner.Plan(
            manifest,
            archive.EntryNames,
            archive.ProbeImageSize);

        if (!plan.CanImport)
        {
            // Not an error: an unimportable bundle is a state the manifest explains, and the
            // skipped list carries the manifest's own reasons (HPS-36).
            Report(
                unattended,
                "Nothing to import from this bundle.",
                plan.BlockedReason + Environment.NewLine + Summarise(plan, []));
            return Result.Cancelled;
        }

        // Fail-closed, and BEFORE any element exists: a bundle whose bytes do not match the hashes
        // its own manifest publishes creates nothing at all (⛔HPS-26).
        if (archive.VerifyPlan(plan) is { } integrityFailure)
        {
            message = integrityFailure;
            Report(unattended, "This bundle failed its integrity check.", integrityFailure);
            return Result.Failed;
        }

        RevitBundleImporter importer = new(commandData.Application.Application, document, archive);

        // The Revit API throws for a long tail of document states this command cannot anticipate —
        // a template with no toposolid type, an IFC that will not convert, a degenerate TIN. An
        // unhandled one surfaces as Revit's internal-error dialog, which tells the user nothing and
        // implicates the whole session. Catching it here keeps the failure attributable.
        try
        {
            importer.Execute(plan);
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException
                                       or InvalidOperationException
                                       or IOException)
        {
            message = $"The import failed partway through: {ex.Message}";
            Report(unattended, "The import failed partway through.", message);
            return Result.Failed;
        }

        Report(
            unattended,
            "Bundle imported.",
            Summarise(plan, importer.Log)
                + Environment.NewLine
                + $"Linked files live in {archive.RetainedDirectory} — moving or deleting that folder "
                + "will break the links.");

        return Result.Succeeded;
    }

    /// <summary>
    /// Tells the curator what happened — a dialog when one is driving, a file beside the zip when
    /// the run was unattended.
    /// </summary>
    /// <remarks>
    /// A <c>TaskDialog</c> raised during journal playback never gets dismissed, so the run that
    /// exists to prove the import works would hang instead. Writing the log is best-effort: an
    /// unwritable path must not turn a successful import into a failure.
    /// </remarks>
    private static void Report(string? unattendedZipPath, string instruction, string body)
    {
        if (unattendedZipPath is null)
        {
            TaskDialog dialog = new("Mantle Place")
            {
                MainInstruction = instruction,
                MainContent = body,
            };
            dialog.Show();
            return;
        }

        try
        {
            File.WriteAllText(
                LocalBundleSource.LogPathFor(unattendedZipPath),
                instruction + Environment.NewLine + body);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Reports what happened AND what did not. A skipped artifact with its manifest-stated reason
    /// is the difference between "the plugin is broken" and "this bundle does not carry that yet"
    /// (HPS-36).
    /// </summary>
    private static string Summarise(BundleImportPlan plan, IReadOnlyList<string> log)
    {
        StringBuilder text = new();
        foreach (string line in log)
        {
            text.AppendLine(line);
        }

        if (plan.Skipped.Count > 0)
        {
            text.AppendLine().AppendLine("Not imported:");
            foreach (SkippedImport skipped in plan.Skipped)
            {
                text.Append("  • ").AppendLine(skipped.Reason);
            }
        }

        if (plan.AvailableButNotImported.Count > 0)
        {
            text.AppendLine().AppendLine("Also in this bundle:");
            foreach (string available in plan.AvailableButNotImported)
            {
                text.Append("  • ").AppendLine(available);
            }
        }

        return text.ToString();
    }
}
