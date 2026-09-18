// UseWPF switches the SDK to the WindowsDesktop implicit-usings set, which drops System.IO.
using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>Why an import stopped before it created anything, worded for a dialog or a status line.</summary>
/// <param name="Instruction">The dialog's one-line heading.</param>
/// <param name="Message">What happened, in full.</param>
/// <param name="IsFailure">
/// <c>false</c> for a bundle whose manifest explains why there is nothing to import — a state, not an
/// error (<c>HPS-36</c>).
/// </param>
internal sealed record ImportRefusal(string Instruction, string Message, bool IsFailure);

/// <summary>
/// The bundle import in progress, from the moment its plan is verified to the moment its log is
/// closed: the open archive, the checklist, the importer, the staged run and the record on disk.
/// </summary>
/// <remarks>
/// <para>
/// Both entry points — the ribbon's Import Bundle and the vault window's Import — used to carry their
/// own copy of open, plan, verify, execute and summarise, and the two had drifted: different words
/// for the same refusal, one catching an exception the other did not. They now open an import
/// through <see cref="Open"/> and differ only in how they drive it: the import window's checklist,
/// then <see cref="Begin"/> with what was ticked and <see cref="Advance"/> once per
/// <c>ExternalEvent</c> raise, or <see cref="Begin"/> with everything and <see cref="RunToEnd"/> for
/// the unattended path, where nothing is watching and nobody is there to choose.
/// </para>
/// <para>
/// <b>Planned twice.</b> <see cref="Open"/> plans everything, which is what the checklist offers and
/// what the integrity check covers; <see cref="Begin"/> plans again with the curator's choice, which
/// is what runs. The planner is pure and cheap, and planning again rather than filtering the first
/// plan is what lets the choice reach decisions taken before any step exists — the terrain is only
/// built on the imagery type when the drape was ticked.
/// </para>
/// <para>
/// ⛔ <b>It owns the archive.</b> A staged import outlives the command or event that opened it, and
/// disposing the archive deletes the transient scratch directory the steps extract into. So the
/// import holds it until <see cref="Dispose"/>, which the driver calls once the run is over.
/// </para>
/// </remarks>
internal sealed class ActiveImport : IDisposable
{
    private readonly Document _document;
    private readonly LocalBundleArchive _archive;
    private readonly BundleManifest _manifest;
    private readonly ImportLog _log;
    private readonly RevitBundleImporter _importer;
    private BundleImportPlan _plan;
    private string? _summary;

    private ActiveImport(
        Autodesk.Revit.ApplicationServices.Application application,
        Document document,
        LocalBundleArchive archive,
        BundleManifest manifest,
        BundleImportPlan plan,
        ImportLog log,
        string zipPath)
    {
        _document = document;
        _archive = archive;
        _manifest = manifest;
        _plan = plan;
        _log = log;
        ZipPath = zipPath;
        _importer = new RevitBundleImporter(application, document, archive, log.Append);
        Checklist = ImportChecklist.For(plan);
    }

    internal string ZipPath { get; }

    /// <summary>What the bundle carries, for the import window to offer before anything runs.</summary>
    internal ImportChecklist Checklist { get; }

    /// <summary>
    /// The chosen steps and where each stands, for the import window — which cancels through it too.
    /// <c>null</c> until <see cref="Begin"/>.
    /// </summary>
    internal StagedImport? Staged { get; private set; }

    /// <summary>The end-of-run report, or <c>null</c> while the run is still going.</summary>
    internal string? Summary => _summary;

    /// <summary>Whether the run was stopped by an exception rather than ending, or being cancelled.</summary>
    internal bool Failed { get; private set; }

    /// <summary>How the run ended, in one line — a dialog's heading and the log block's first line.</summary>
    internal string Heading => (Failed, Staged) switch
    {
        (true, null) => "The import failed before it started.",
        (true, _) => "The import failed partway through.",
        (false, null) => "The import was cancelled before it started.",
        (false, { WasCancelled: true }) => "The import was cancelled.",
        _ => "Bundle imported.",
    };

    /// <summary>
    /// Opens a bundle, plans it and verifies it — everything that must pass before any element is
    /// created — or says why not.
    /// </summary>
    /// <remarks>
    /// The log is begun here, on both paths, before anything can fail: see <see cref="ImportLog"/>
    /// for why the record is streamed rather than written at the end. A refusal is appended to it as
    /// well as returned.
    /// </remarks>
    internal static ActiveImport? Open(
        Autodesk.Revit.ApplicationServices.Application application,
        Document document,
        string zipPath,
        out ImportRefusal? refusal)
    {
        ImportLog log = new(zipPath);
        log.Begin();

        // A picker guarantees this; an environment variable and a cache path do not, and an unhandled
        // FileNotFoundException during journal playback is Revit's internal-error dialog with nothing
        // there to dismiss it.
        if (!File.Exists(zipPath))
        {
            return Refuse(log, new ImportRefusal("Bundle not found.", $"No bundle zip at \"{zipPath}\".", true), out refusal);
        }

        LocalBundleArchive archive;
        try
        {
            archive = LocalBundleArchive.Open(zipPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Refuse(log, new ImportRefusal("That bundle could not be opened.", ex.Message, true), out refusal);
        }

        if (archive.Manifest is not { } manifest)
        {
            archive.Dispose();
            return Refuse(
                log,
                new ImportRefusal(
                    "Not a Mantle Place bundle.",
                    "That zip has no Metadata/manifest.json, so it is not a Mantle Place bundle.",
                    true),
                out refusal);
        }

        BundleImportPlan plan = BundleImportPlanner.Plan(manifest, archive.EntryNames, archive.ProbeImageSize);
        if (!plan.CanImport)
        {
            // Not an error: an unimportable bundle is a state the manifest explains, and the skipped
            // list carries the manifest's own reasons (HPS-36).
            archive.Dispose();
            return Refuse(
                log,
                new ImportRefusal(
                    "Nothing to import from this bundle.",
                    plan.BlockedReason + Environment.NewLine + Summarise(plan, []),
                    false),
                out refusal);
        }

        // Fail-closed, and BEFORE any element exists: a bundle whose bytes do not match the hashes its
        // own manifest publishes creates nothing at all (⛔HPS-26). The download path already verified
        // on the way in; this covers the zip that arrived some other way. Over the whole plan, not the
        // curator's choice: a bundle some of whose bytes are wrong is not one to take any part of.
        if (archive.VerifyPlan(plan) is { } integrityFailure)
        {
            archive.Dispose();
            return Refuse(log, new ImportRefusal("This bundle failed its integrity check.", integrityFailure, true), out refusal);
        }

        refusal = null;
        return new ActiveImport(application, document, archive, manifest, plan, log, zipPath);
    }

    /// <summary>
    /// Plans the import again with only the layers chosen, and readies it to run. Once.
    /// </summary>
    /// <remarks>
    /// The layers left out are written to the log now, not only in the closing summary, so a curator
    /// reading the streamed record of a run that is still going can see the trees were not wanted
    /// rather than wonder whether they are still to come.
    /// </remarks>
    internal void Begin(ImportLayerChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (Staged is not null || _summary is not null)
        {
            throw new InvalidOperationException("This import has already begun.");
        }

        BundleImportPlan plan = BundleImportPlanner.Plan(_manifest, _archive.EntryNames, _archive.ProbeImageSize, choice);

        // The checklist lights Import only when a layer is ticked, and the unattended path ticks all
        // of them, so this is a caller's defect rather than a curator's state.
        if (!plan.CanImport)
        {
            throw new InvalidOperationException(plan.BlockedReason);
        }

        foreach (SkippedImport skip in plan.Skipped.Where(skip => skip.ReasonCode == SkipReasonCode.LeftOutByChoice))
        {
            _log.Append(skip.Reason);
        }

        _plan = plan;
        Staged = new StagedImport(plan.Steps, _importer);
    }

    /// <summary>Ends an import the curator dismissed before choosing to run it.</summary>
    internal void CancelBeforeStart() => Close("Nothing was imported: the window was closed before the import began.");

    /// <summary>
    /// Does one slice of the import on Revit's thread, and closes the record when it was the last.
    /// </summary>
    /// <returns><c>true</c> while there is more to do.</returns>
    internal bool Advance()
    {
        if (_summary is not null)
        {
            return false;
        }

        StagedImport staged = Staged ?? throw new InvalidOperationException("The import has not begun.");

        // The window is modeless and Revit is live between slices, so the project this import was
        // writing into can be closed under it. Every Revit call after that would throw.
        if (!_document.IsValidObject)
        {
            _log.Append("The project was closed during the import, so nothing more was imported into it.");
            staged.RequestCancel();
            staged.RunToEnd();
            Close("The project was closed during the import, so it stopped. " + staged.Outcome);
            return false;
        }

        try
        {
            if (_importer.InSlice(staged.Advance))
            {
                return true;
            }

            _importer.InSlice(() =>
            {
                _importer.Finish();
                return false;
            });

            if (staged.Outcome is { } outcome)
            {
                _importer.Report(outcome);
            }

            Close(Summarise(_plan, _importer.Log)
                + Environment.NewLine
                + $"Linked files live in {_archive.RetainedDirectory} — moving or deleting that folder will "
                + "break the links.");
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException
                                       or InvalidOperationException
                                       or IOException)
        {
            // The Revit API throws for a long tail of document states this cannot anticipate. An
            // unhandled one surfaces as Revit's internal-error dialog, which tells the curator nothing
            // and implicates the whole session.
            Failed = true;
            Close($"The import failed partway through: {ex.Message}");
        }

        return false;
    }

    /// <summary>Runs every remaining slice now — the unattended path, which has no window to repaint.</summary>
    internal void RunToEnd()
    {
        while (Advance())
        {
        }
    }

    /// <summary>
    /// Ends the import on an exception nothing above anticipated, so the driver is never left holding
    /// a run that can neither advance nor close.
    /// </summary>
    /// <remarks>
    /// ⛔ A staged import is state that outlives one call. The single-call import this replaced could
    /// throw and simply be over; this one, thrown out of, kept its handler believing an import was
    /// running and its window refusing to close, until Revit was restarted.
    /// </remarks>
    internal void Abandon(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Failed = true;
        Close($"The import stopped on an error it did not expect: {exception.GetType().Name}: {exception.Message}");
    }

    public void Dispose() => _archive.Dispose();

    private void Close(string summary)
    {
        if (_summary is not null)
        {
            return;
        }

        _summary = summary;

        // Appended, because the streamed lines are already in the file and they carry the timings
        // the summary does not.
        _log.AppendBlock(Heading + Environment.NewLine + summary);
    }

    private static ActiveImport? Refuse(ImportLog log, ImportRefusal reason, out ImportRefusal? refusal)
    {
        log.AppendBlock(reason.Instruction + Environment.NewLine + reason.Message);
        refusal = reason;
        return null;
    }

    /// <summary>
    /// Reports what happened AND what did not. A skipped artifact with its manifest-stated reason is
    /// the difference between "the plugin is broken" and "this bundle does not carry that yet"
    /// (<c>HPS-36</c>).
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
