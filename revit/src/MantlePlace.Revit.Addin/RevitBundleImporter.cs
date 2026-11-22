using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal sealed partial class RevitBundleImporter(
    Autodesk.Revit.ApplicationServices.Application application,
    Document document,
    LocalBundleArchive archive,
    Action<string>? trace = null) : IImportStepRunner
{
    private readonly Autodesk.Revit.ApplicationServices.Application _application = application;
    private readonly Document _document = document;
    private readonly LocalBundleArchive _archive = archive;
    private readonly List<string> _log = [];

    private readonly Action<string>? _trace = trace;

    private ElementId _terrainId = ElementId.InvalidElementId;

    private int? _terrainVertexCount;

    private readonly List<ElementId> _createdSubDivisionIds = [];

    private readonly Dictionary<ElementId, string> _stampsCutWith = [];

    private bool _smoothingSettled;

    private bool _stepRolledBack;

    private readonly Stopwatch _stepClock = new();

    internal IReadOnlyList<string> Log => _log;

    internal event Action? CommitStarting;

    internal event Action? CommitEnded;

    internal event Action<string>? Announced;

    internal event Action<StepProgress>? Counted;

    internal bool InSlice(Func<bool> slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        ImportFailureSwallower session = new("The import");
        _sessionFailures = session;
        _application.FailuresProcessing += session.OnFailuresProcessing;
        try
        {
            return slice();
        }
        finally
        {
            _application.FailuresProcessing -= session.OnFailuresProcessing;
            _sessionFailures = null;
            SayAll(session.Lines);
        }
    }

    private ImportFailureSwallower? _sessionFailures;

    private int SessionErrorCount => _sessionFailures?.Errors.Count ?? 0;

    private string? SiteModelRefusal(string entryName, int errorsBefore, string lost)
        => _sessionFailures is { } session && session.Errors.Count > errorsBefore
            ? ImportFailurePolicy.ExplainSiteModelRefused(entryName, session.Errors[errorsBefore].Text, session.Errors[errorsBefore].Id, lost)
            : null;

    internal void Finish() => EnsureSmoothedSurface();

    internal void Report(string line) => Say(line);

    public IEnumerable<StepProgress> Run(ImportStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        ConfirmAnchor();
        IEnumerable<StepProgress> work = RunStep(step);
        return OdblContent.IsContained(step.Odbl) ? FiledInOdblContainer(step, work) : work;
    }

    private IEnumerable<StepProgress> RunStep(ImportStep step)
    {

        if (GroundCuts.LayerOf(step.Kind) is { } groundLayer)
        {
            return Once(() => CutSubdivisions(step, groundLayer));
        }

        switch (step.Kind)
        {
            case ImportStepKind.ToposurfaceFromPointsFile:
                return Once(() => ImportToposurfaceFromPoints(step));
            case ImportStepKind.ToposurfaceFromSurfaceTin:
                return Once(() => ImportToposurfaceFromTin(step));
            case ImportStepKind.ToposurfaceFromSurfaceDxf:
                return Once(() => LinkCadSurface(step));
            case ImportStepKind.PublishedContours:
                return Once(() => ImportPublishedContours(step));
            case ImportStepKind.LinkSiteIfc:
                return Once(() => LinkSiteIfc(step));
            case ImportStepKind.ContextBuildings:
                return ImportContextBuildings(step);
            case ImportStepKind.SetSharedCoordinates:
                return Once(() => SetSharedCoordinates(step));
            case ImportStepKind.SetSiteLocation:
                return Once(() => SetSiteLocation(step));
            case ImportStepKind.RoadCentrelines:
                return Once(() => ImportRoadCentrelines(step));
            case ImportStepKind.Vegetation:
                return ImportVegetation(step);
            case ImportStepKind.SiteContextView:
                return Once(EnsureSiteContextView);
            case ImportStepKind.ImageryDrape:
                return Once(() => ApplyImageryDrape(step));
            case ImportStepKind.AttributionAndProvenance:
                return Once(() => WriteAttributionAndProvenance(step));
            case ImportStepKind.LandUse:
                return Once(() => ImportLandPlan(step, LandLayer.LandUse));
            case ImportStepKind.LandCover:
                return Once(() => ImportLandPlan(step, LandLayer.LandCover));
            case ImportStepKind.FloodZones:
                return Once(() => ImportHazardLayer(step, HazardLayer.FloodZones));
            case ImportStepKind.SteepGround:
                return Once(() => ImportHazardLayer(step, HazardLayer.SteepGround));
            default:
                throw new InvalidOperationException(
                    $"This build of the plugin does not know how to execute the import step "
                    + $"'{step.Kind}', so the import was stopped rather than left half-done. "
                    + "Update the Mantle Place plugin.");
        }
    }

    public bool Committed(ImportStep step) => !_stepRolledBack;

    public bool IsStepFailure(Exception exception)
        => exception is Autodesk.Revit.Exceptions.ApplicationException or IOException;

    public void StepStarting(ImportStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        Trace($"[{step.Kind}] started.");
        _stepRolledBack = false;
        _stepClock.Restart();
    }

    public void StepEnded(ImportStep step, ImportStepState state, Exception? failure)
    {
        ArgumentNullException.ThrowIfNull(step);

        _stepClock.Stop();
        if (failure is not null)
        {
            Say($"The \"{step.Kind}\" step failed and was skipped — {failure.Message} Everything after "
                + "it was still attempted.");
        }

        if (state == ImportStepState.NotRun)
        {
            Trace($"[{step.Kind}] not run: the import was cancelled before it began.");
            return;
        }

        Say($"({step.Kind} took {_stepClock.Elapsed.TotalSeconds:N1} s.)");
    }

    private static IEnumerable<StepProgress> Once(Action step)
    {
        step();
        yield break;
    }
}
