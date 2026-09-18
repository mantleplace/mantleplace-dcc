// UseWPF switches the SDK to the WindowsDesktop implicit-usings set, which drops System.IO.
using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// Executes a <see cref="BundleImportPlan"/>'s steps against a Revit document, one at a time, for
/// <see cref="StagedImport"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every "should we" question was already answered by <see cref="BundleImportPlanner"/>. This type
/// only answers "how", in Revit's vocabulary: transactions, element creation, unit conversion into
/// Revit's internal decimal feet. Keeping it decision-free is what makes the policy testable
/// without a licence (HPS-02).
/// </para>
/// <para>
/// Two decisions used to leak in here and no longer do. The three-point minimum
/// <c>Toposolid.Create</c> imposes fired AFTER the planner had already reported <c>CanImport</c>,
/// and now lives in <see cref="SurfacePointsReader.MinimumPoints"/> where the headless suite can
/// reach it. The Transient/Retained choice was a per-handler literal, and now comes from
/// <see cref="ImportStepKinds.LifetimeOf"/> — so getting a new step kind's lifetime wrong is a
/// failing test rather than a Revit link that breaks on someone else's machine.
/// </para>
/// <para>
/// The class is split one file per step — <c>RevitBundleImporter.&lt;Step&gt;.cs</c> — so that two
/// changes to two steps do not touch one file. Three things stay here and nowhere else: the
/// state steps hand to each other (the fields below), the dispatch from a step kind to its work
/// (<see cref="Run"/>), and the session-wide failure hook (<see cref="InSlice"/>). The step ORDER,
/// when a step runs and when a cancel is honoured are <see cref="StagedImport"/>'s, in the pure core.
/// What every step shares — the log,
/// the transaction, finding the ground — is <c>RevitBundleImporter.Plumbing.cs</c>, and what
/// more than one vector step shares is <c>RevitBundleImporter.SiteVectors.cs</c>.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Where a line goes the MOMENT it is produced, when the caller offered somewhere to put it.
    /// </summary>
    /// <remarks>
    /// ⛔ The summary is returned to the caller and written once, after the last step has
    /// ended. A step that never returns therefore leaves no evidence at all — and the
    /// site-boundary step's commit has been observed spending over four minutes inside
    /// <c>updateElementRelations</c> on a large toposolid, which is exactly the shape of run this
    /// import needs a record of. Anything written here is diagnostic and does not appear in the
    /// curator's dialog.
    /// </remarks>
    private readonly Action<string>? _trace = trace;

    /// <summary>The toposolid this import built, so the boundary step drapes onto the right one.</summary>
    private ElementId _terrainId = ElementId.InvalidElementId;

    /// <summary>
    /// How many points that toposolid was built from, for the two steps that have to warn about it.
    /// </summary>
    /// <remarks>
    /// Remembered on the way past rather than asked for later, for the same reason as
    /// <see cref="_terrainId"/> — and because the number is only knowable from the points file this
    /// run read. Null when this run did not build the terrain, which is a re-import onto ground an
    /// earlier one laid; <see cref="SlowStepNotice"/> says so rather than inventing a count.
    /// </remarks>
    private int? _terrainVertexCount;

    /// <summary>
    /// The subdivisions this import created, as a supplement to finding them by stamp.
    /// </summary>
    /// <remarks>
    /// It is no longer the drape's only source — see <c>DrapeableSubDivisionIds</c>. This list is
    /// empty on a re-import, because boundaries that already exist are not created again, and a drape
    /// driven by it alone therefore touched nothing while reporting success. What it still covers is
    /// the subdivision this import created but could not stamp: it is not findable by stamp, and
    /// without this list it would not be draped on the very import that made it.
    /// </remarks>
    private readonly List<ElementId> _createdSubDivisionIds = [];

    /// <summary>
    /// Whether the smooth-shading decision has been made, and said, for this import.
    /// </summary>
    /// <remarks>
    /// <see cref="EnsureSmoothedSurface"/> runs twice: inside the drape step, before the photograph
    /// is written, so it is anchored for the renderer that will actually draw it; and after every
    /// step, for a bundle with no photograph at all. The second call must neither retry a refusal
    /// nor repeat the sentence.
    /// </remarks>
    private bool _smoothingSettled;

    /// <summary>
    /// Whether the step in flight had a commit Revit rolled back. Reset as each step starts, set by
    /// <see cref="CommitAndReport"/>, read by <see cref="Committed"/> — so a step whose work is gone
    /// reads as failed in the window and in a cancelled run's closing line, not as done.
    /// </summary>
    private bool _stepRolledBack;

    /// <summary>Times the step in flight, from its start slice to the slice that ends it.</summary>
    /// <remarks>
    /// Wall time, so on a staged run it includes Revit's own work between slices. The per-commit
    /// trace lines (<see cref="CommitAndReport"/>) are what to read when tuning a chunk size.
    /// </remarks>
    private readonly Stopwatch _stepClock = new();

    internal IReadOnlyList<string> Log => _log;

    /// <summary>
    /// Runs <paramref name="slice"/> — one <see cref="StagedImport.Advance"/> — with the import's
    /// failure hook attached for exactly as long as it runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ Revit's own IFC importer opens a transaction named "Import" inside
    /// RevitLinkType.CreateFromIFC, and a per-transaction preprocessor cannot reach it — which is
    /// how "Can't keep elements joined" and the IFC4 warning surfaced as a modal in the middle of an
    /// otherwise silent import. This is the only hook that sees a transaction we did not open.
    /// </para>
    /// <para>
    /// It is session-wide, so it is attached per slice and not for the length of the import. The
    /// import is staged: between two slices Revit is live, and the curator may be editing their own
    /// model while the trees go in. A hook left on across that gap would put this policy between
    /// them and their own edits.
    /// </para>
    /// <para>
    /// The same gap lets a curator undo an earlier step's transaction, leaving an id in the fields
    /// above pointing at nothing. The Revit call that meets it throws an ApplicationException, which
    /// <see cref="IsStepFailure"/> turns into that one step failing and saying why.
    /// </para>
    /// </remarks>
    internal bool InSlice(Func<bool> slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        ImportFailureSwallower session = new("The import");
        _application.FailuresProcessing += session.OnFailuresProcessing;
        try
        {
            return slice();
        }
        finally
        {
            _application.FailuresProcessing -= session.OnFailuresProcessing;
            SayAll(session.Lines);
        }
    }

    /// <summary>What every import does once its steps are over, however they ended.</summary>
    /// <remarks>
    /// After every step as well as inside the drape step: a bundle with no photograph still gets
    /// smooth ground, and for the bundle that had one this is a no-op read. It runs on a cancelled
    /// import too, because the terrain that import kept is the same ground a finished one would have
    /// left, and should look the same.
    /// </remarks>
    internal void Finish() => EnsureSmoothedSurface();

    /// <summary>Adds a line to the summary from outside the steps — the run's closing line.</summary>
    internal void Report(string line) => Say(line);

    /// <inheritdoc/>
    public IEnumerable<StepProgress> Run(ImportStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        switch (step.Kind)
        {
            case ImportStepKind.ToposurfaceFromPointsFile:
                return Once(() => ImportToposurfaceFromPoints(step));
            case ImportStepKind.ToposurfaceFromSurfaceTin:
                return Once(() => ImportToposurfaceFromTin(step));
            case ImportStepKind.ToposurfaceFromSurfaceDxf:
                return Once(() => LinkCadSurface(step));
            case ImportStepKind.LinkSiteIfc:
                return Once(() => LinkSiteIfc(step));
            case ImportStepKind.SetSharedCoordinates:
                return Once(() => SetSharedCoordinates(step));
            case ImportStepKind.RoadCentrelines:
                return Once(() => ImportRoadCentrelines(step));
            case ImportStepKind.SiteBoundaries:
                return Once(() => ImportSiteBoundaries(step));
            case ImportStepKind.Vegetation:
                return ImportVegetation(step);
            case ImportStepKind.ImageryDrape:
                return Once(() => ApplyImageryDrape(step));
            default:
                // Fail, do not log-and-continue. A step kind added to the pure core and never
                // dispatched here would otherwise import silently-incomplete: the plan says the bundle
                // is fully handled, the model is missing whatever the new kind was for, and the
                // summary reads like a success. StagedImport lets this one through to the host,
                // because it is not a step's own failure.
                throw new InvalidOperationException(
                    $"This build of the plugin does not know how to execute the import step "
                    + $"'{step.Kind}', so the import was stopped rather than left half-done. "
                    + "Update the Mantle Place add-in.");
        }
    }

    /// <inheritdoc/>
    public bool Committed(ImportStep step) => !_stepRolledBack;

    /// <inheritdoc/>
    /// <remarks>
    /// InvalidOperationException is deliberately NOT one: <see cref="Run"/> throws it for a step kind
    /// this build cannot dispatch, and that one must still stop the import.
    /// </remarks>
    public bool IsStepFailure(Exception exception)
        => exception is Autodesk.Revit.Exceptions.ApplicationException or IOException;

    /// <inheritdoc/>
    public void StepStarting(ImportStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        // A start marker and a duration, because a step is the unit a curator waits on and the unit
        // a slow one has to be attributed to. The marker is traced rather than said: it is only
        // useful in the streamed file, where it is the last line standing if a step hangs.
        Trace($"[{step.Kind}] started.");
        _stepRolledBack = false;
        _stepClock.Restart();
    }

    /// <inheritdoc/>
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
            // Pairs the "[Kind] started." marker, which was written before the cancel landed, so the
            // streamed file does not read as though the step began and then vanished.
            Trace($"[{step.Kind}] not run: the import was cancelled before it began.");
            return;
        }

        Say($"({step.Kind} took {_stepClock.Elapsed.TotalSeconds:N1} s.)");
    }

    /// <summary>A step that is one commit, as a step with no chunk boundaries in it.</summary>
    private static IEnumerable<StepProgress> Once(Action step)
    {
        step();
        yield break;
    }
}
