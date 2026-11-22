using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal sealed record ImportRefusal(string Instruction, string Message, bool IsFailure, string Code);

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
        string zipPath,
        AnchorDecision anchoring,
        SurveyPointPlacement? surveyPoint)
    {
        _document = document;
        _archive = archive;
        _manifest = manifest;
        _plan = plan;
        _log = log;
        ZipPath = zipPath;
        _importer = new RevitBundleImporter(application, document, archive, log.Append)
        {
            Anchoring = anchoring,
            IncomingSurveyPoint = surveyPoint,
        };
        _importer.CommitStarting += () => Tell(staged => staged.CommitStarted());
        _importer.CommitEnded += () => Tell(staged => staged.CommitEnded());
        _importer.Announced += notice => Tell(staged => staged.Announce(notice));
        _importer.Counted += progress => Tell(staged => staged.ReportProgress(progress));
        Checklist = ImportChecklist.For(plan, application.VersionNumber, ChoiceStore.Read());
        DeliveryLine = DeliveryHeader.Describe(manifest.Delivery);
        UnitsDisagreement = DeliveryHeader.DisplayDisagreement(manifest.Delivery, LengthUnitTypeId(document));
    }

    internal static ImportChoiceStore ChoiceStore { get; } = new(ImportChoiceStore.DefaultPath);

    internal static FeedbackAskedStore AskedStore { get; } = new(FeedbackAskedStore.DefaultPath);

    internal string ZipPath { get; }

    internal string OrderId => _manifest.OrderId;

    internal string? DeliveryLine { get; }

    internal string? UnitsDisagreement { get; }

    internal ImportChecklist Checklist { get; }

    internal StagedImport? Staged { get; private set; }

    internal event Action? Changed;

    internal string? Summary => _summary;

    internal bool Failed { get; private set; }

    internal string Heading => (Failed, Staged) switch
    {
        (true, null) => "The import failed before it started.",
        (true, _) => "The import failed partway through.",
        (false, null) => "The import was cancelled before it started.",
        (false, { WasCancelled: true }) => "The import was cancelled.",
        _ => "Bundle imported.",
    };

    private static string LengthUnitTypeId(Document document)
        => document.GetUnits().GetFormatOptions(SpecTypeId.Length).GetUnitTypeId().TypeId;

    internal static ActiveImport? Open(
        Autodesk.Revit.ApplicationServices.Application application,
        Document document,
        string zipPath,
        out ImportRefusal? refusal)
    {
        ImportLog log = new(zipPath);

        if (!File.Exists(zipPath))
        {
            return Refuse(log, new ImportRefusal("Bundle not found.", $"No bundle zip at \"{zipPath}\".", true, ExportReasons.DownloadFailed), out refusal);
        }

        LocalBundleArchive archive;
        try
        {
            archive = LocalBundleArchive.Open(zipPath);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Refuse(log, new ImportRefusal("That bundle could not be opened.", ex.Message, true, ExportReasons.NotABundle), out refusal);
        }

        if (archive.Manifest is not { } manifest)
        {
            archive.Dispose();
            return Refuse(
                log,
                new ImportRefusal(
                    "Not a Mantle Place bundle.",
                    "That zip has no Manifest.json at its root and no Metadata/manifest.json, so it is not a Mantle Place bundle.",
                    true,
                    ExportReasons.NotABundle),
                out refusal);
        }

        BundleImportPlan plan = BundleImportPlanner.Plan(manifest, archive.EntryNames, archive.ProbeImageSize);
        if (!plan.CanImport)
        {
            archive.Dispose();
            return Refuse(
                log,
                new ImportRefusal(
                    "Nothing to import from this bundle.",
                    plan.BlockedReason + Environment.NewLine + Summarise(plan, []),
                    false,
                    ExportReasons.NothingToImport),
                out refusal);
        }

        SurveyPointPlacement? surveyPoint = plan.Steps
            .FirstOrDefault(step => step.Kind == ImportStepKind.SetSharedCoordinates)?.SurveyPoint;
        AnchorDecision anchoring = AdjacentBundle.Decide(ProjectAnchorStore.Read(document), surveyPoint);
        if (anchoring.IsRefusal)
        {
            archive.Dispose();
            return Refuse(
                log,
                new ImportRefusal(
                    "This bundle cannot be placed beside the one already in this project.",
                    anchoring.Message,
                    true,
                    ExportReasons.ImportFailed),
                out refusal);
        }

        if (archive.VerifyPlan(plan) is { } integrityFailure)
        {
            archive.Dispose();
            return Refuse(log, new ImportRefusal("This bundle failed its integrity check.", integrityFailure, true, ExportReasons.IntegrityFailed), out refusal);
        }

        refusal = null;
        return new ActiveImport(application, document, archive, manifest, plan, log, zipPath, anchoring, surveyPoint);
    }

    internal void Begin(ImportLayerChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (Staged is not null || _summary is not null)
        {
            throw new InvalidOperationException("This import has already begun.");
        }

        BundleImportPlan plan = BundleImportPlanner.Plan(_manifest, _archive.EntryNames, _archive.ProbeImageSize, choice);

        if (!plan.CanImport)
        {
            throw new InvalidOperationException(plan.BlockedReason);
        }

        foreach (SkippedImport skip in plan.Skipped.Where(skip => skip.ReasonCode == SkipReasonCode.LeftOutByChoice))
        {
            _log.Append(skip.Reason);
        }

        foreach (ImportStep step in plan.Steps)
        {
            if (FidelityCuts.LevelLogLine(WindowLabels.StepName(step.Kind), step.Level) is { } line)
            {
                _log.Append(line);
            }
        }

        _plan = plan;
        Staged = new StagedImport(plan.Steps, _importer);
    }

    internal void CancelBeforeStart() => _summary ??= "Nothing was imported: the import was cancelled before it began.";

    internal bool Advance()
    {
        if (_summary is not null)
        {
            return false;
        }

        StagedImport staged = Staged ?? throw new InvalidOperationException("The import has not begun.");

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

            Tell(finishing => finishing.FinishStarted());
            try
            {
                _importer.InSlice(() =>
                {
                    _importer.Finish();
                    return false;
                });
            }
            finally
            {
                staged.FinishEnded();
            }

            if (staged.Outcome is { } outcome)
            {
                _importer.Report(outcome);
            }

            bool linksFiles = _plan.Steps.Any(step => step.EntryName.Length > 0
                && step.Lifetime == ExtractionLifetime.Retained);
            Close(Summarise(_plan, _importer.Log)
                + (linksFiles
                    ? Environment.NewLine
                        + $"Linked files live in {_archive.RetainedDirectory} — moving or deleting that folder "
                        + "will break the links."
                    : string.Empty));
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException
                                       or InvalidOperationException
                                       or IOException)
        {
            Failed = true;
            Close($"The import failed partway through: {ex.Message}");
        }

        return false;
    }

    internal void RunToEnd()
    {
        while (Advance())
        {
        }
    }

    internal void Abandon(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        Failed = true;
        Close($"The import stopped on an error it did not expect: {exception.GetType().Name}: {exception.Message}");
    }

    public void Dispose() => _archive.Dispose();

    internal bool DocumentIsOpen => _document.IsValidObject;

    internal void Note(string line) => _log.Append(line);

    private void Tell(Action<StagedImport> change)
    {
        if (Staged is { } staged)
        {
            change(staged);
            Changed?.Invoke();
        }
    }

    private void Close(string summary)
    {
        if (_summary is not null)
        {
            return;
        }

        _summary = summary;

        _log.AppendBlock(Heading + Environment.NewLine + summary);
    }

    private static ActiveImport? Refuse(ImportLog log, ImportRefusal reason, out ImportRefusal? refusal)
    {
        log.AppendBlock(reason.Instruction + Environment.NewLine + reason.Message);
        refusal = reason;
        return null;
    }

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
