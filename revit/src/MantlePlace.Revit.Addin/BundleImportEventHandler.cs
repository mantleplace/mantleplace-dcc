// UseWPF switches the SDK to the WindowsDesktop implicit-usings set, which drops System.IO.
using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// Runs an import against the active document, on Revit's thread.
/// </summary>
/// <remarks>
/// <para>
/// Revit's document API is main-thread-only and there is no supported way to marshal onto it except
/// <see cref="ExternalEvent"/>. The vault browser is modeless and does its downloading on a
/// background task, so the moment it has a zip on disk it raises this and gets out of the way.
/// </para>
/// <para>
/// Everything it does was decided elsewhere: <see cref="BundleImportPlanner"/> chose the steps and
/// <see cref="RevitBundleImporter"/> knows how to execute them. This is the thread hop and the
/// report, and nothing else.
/// </para>
/// </remarks>
internal sealed class BundleImportEventHandler : IExternalEventHandler
{
    private readonly object _gate = new();
    private string? _zipPath;

    /// <summary>Raised on Revit's thread when an import finishes, so the browser can update.</summary>
    internal event EventHandler<string>? Completed;

    /// <summary>Queues a zip. The last one queued before the event fires is the one that runs.</summary>
    internal void QueueImport(string zipPath)
    {
        lock (_gate)
        {
            _zipPath = zipPath;
        }
    }

    public void Execute(UIApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        string? zipPath;
        lock (_gate)
        {
            zipPath = _zipPath;
            _zipPath = null;
        }

        if (zipPath is null)
        {
            return;
        }

        Completed?.Invoke(this, Import(application, zipPath));
    }

    public string GetName() => "Mantle Place bundle import";

    private static string Import(UIApplication application, string zipPath)
    {
        Document? document = application.ActiveUIDocument?.Document;
        if (document is null)
        {
            return "Open a project first — there is no active document to import into.";
        }

        // ⛔ This path had no file record at all: the summary went to the vault window and nowhere
        // else, so an import that hung, crashed or was clicked away left nothing behind. That is the
        // path a curator actually uses, and it is the one that most needs a record — the
        // site-boundary step has been measured spending ten minutes inside a single commit. Same
        // file beside the zip that the ribbon command writes, same streaming, same best-effort
        // contract: an unwritable path never turns an import into a failure.
        ImportLog log = new(zipPath);
        log.Begin();

        try
        {
            using LocalBundleArchive archive = LocalBundleArchive.Open(zipPath);

            if (archive.Manifest is not { } manifest)
            {
                return log.AndSay(
                    "That download has no Metadata/manifest.json, so it is not a Mantle Place bundle.");
            }

            BundleImportPlan plan = BundleImportPlanner.Plan(
                manifest,
                archive.EntryNames,
                archive.ProbeImageSize);
            if (!plan.CanImport)
            {
                return log.AndSay(plan.BlockedReason + Environment.NewLine + Summarise(plan, []));
            }

            // Fail-closed, and BEFORE any element exists. The download path already verifies on the
            // way in (⛔HPS-26, BundleCache); this covers the zip that arrived some other way, and
            // costs one pass over three files.
            if (archive.VerifyPlan(plan) is { } integrityFailure)
            {
                return log.AndSay(integrityFailure);
            }

            RevitBundleImporter importer = new(application.Application, document, archive, log.Append);
            importer.Execute(plan);

            string summary = Summarise(plan, importer.Log)
                + Environment.NewLine
                + $"Linked files live in {archive.RetainedDirectory} — moving or deleting that folder will "
                + "break the links.";
            log.Append(Environment.NewLine + summary);
            return summary;
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException
                                       or InvalidOperationException
                                       or IOException)
        {
            // The Revit API throws for a long tail of document states this cannot anticipate. An
            // unhandled one surfaces as Revit's internal-error dialog, which tells the curator
            // nothing and implicates the whole session.
            string failure = $"The import failed partway through: {ex.Message}";
            log.Append(Environment.NewLine + failure);
            return failure;
        }
    }

    /// <summary>
    /// Reports what happened AND what did not — the difference between "the plugin is broken" and
    /// "this bundle does not carry that yet" (<c>HPS-36</c>).
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
