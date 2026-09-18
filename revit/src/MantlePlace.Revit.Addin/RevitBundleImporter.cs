// UseWPF switches the SDK to the WindowsDesktop implicit-usings set, which drops System.IO.
using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// Executes a <see cref="BundleImportPlan"/> against a Revit document.
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
/// state steps hand to each other (the fields below), the step order (<see cref="ExecuteSteps"/>),
/// and the session-wide failure hook (<see cref="Execute"/>). What every step shares — the log,
/// the transaction, finding the ground — is <c>RevitBundleImporter.Plumbing.cs</c>, and what
/// more than one vector step shares is <c>RevitBundleImporter.SiteVectors.cs</c>.
/// </para>
/// </remarks>
internal sealed partial class RevitBundleImporter(
    Autodesk.Revit.ApplicationServices.Application application,
    Document document,
    LocalBundleArchive archive,
    Action<string>? trace = null)
{
    private readonly Autodesk.Revit.ApplicationServices.Application _application = application;
    private readonly Document _document = document;
    private readonly LocalBundleArchive _archive = archive;
    private readonly List<string> _log = [];

    /// <summary>
    /// Where a line goes the MOMENT it is produced, when the caller offered somewhere to put it.
    /// </summary>
    /// <remarks>
    /// ⛔ The summary is returned to the caller and written once, after <see cref="Execute"/> has
    /// returned. A step that never returns therefore leaves no evidence at all — and the
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

    internal IReadOnlyList<string> Log => _log;

    /// <summary>Runs every step in the plan. One transaction per step, so a late failure keeps the earlier work.</summary>
    internal void Execute(BundleImportPlan plan)
    {
        // ⛔ Revit's own IFC importer opens a transaction named "Import" inside
        // RevitLinkType.CreateFromIFC, and a per-transaction preprocessor cannot reach it — which is
        // how "Can't keep elements joined" and the IFC4 warning surfaced as a modal in the middle of
        // an otherwise silent import. This is the only hook that sees a transaction we did not open.
        // It is session-wide, so it is attached for the length of the import and detached in the
        // finally: leaving it on would put this policy between the curator and their own edits.
        ImportFailureSwallower session = new("The import");
        _application.FailuresProcessing += session.OnFailuresProcessing;

        try
        {
            ExecuteSteps(plan);

            // After every step as well as inside the drape step: a bundle with no photograph still
            // gets smooth ground, and for the bundle that had one this is a no-op read.
            EnsureSmoothedSurface();
        }
        finally
        {
            _application.FailuresProcessing -= session.OnFailuresProcessing;
            SayAll(session.Lines);
        }
    }

    private void ExecuteSteps(BundleImportPlan plan)
    {
        foreach (ImportStep step in plan.Steps)
        {
            // A start marker and a duration, because a step is the unit a curator waits on and the
            // unit a slow one has to be attributed to. The marker is traced rather than said: it is
            // only useful in the streamed file, where it is the last line standing if a step hangs.
            Trace($"[{step.Kind}] started.");
            Stopwatch clock = Stopwatch.StartNew();

            // ⛔ One step's exception used to abandon every step after it. The only catch was at the
            // top of the command, so a re-import that tripped over the IFC link — step two of eight
            // — reported a single sentence and silently never attempted the terrain, the
            // boundaries, the vegetation or the drape. The steps already take a transaction each
            // precisely so a late failure keeps the earlier work; that promise is only half kept if
            // the LATER work is what disappears instead.
            //
            // InvalidOperationException is deliberately NOT caught here: the default arm throws it
            // for a step kind this build cannot dispatch, and that one must still stop the import
            // rather than quietly produce a model missing whatever the new kind was for.
            try
            {
                switch (step.Kind)
                {
                    case ImportStepKind.ToposurfaceFromPointsFile:
                        ImportToposurfaceFromPoints(step);
                        break;
                    case ImportStepKind.ToposurfaceFromSurfaceTin:
                        ImportToposurfaceFromTin(step);
                        break;
                    case ImportStepKind.ToposurfaceFromSurfaceDxf:
                        LinkCadSurface(step);
                        break;
                    case ImportStepKind.LinkSiteIfc:
                        LinkSiteIfc(step);
                        break;
                    case ImportStepKind.SetSharedCoordinates:
                        SetSharedCoordinates(step);
                        break;
                    case ImportStepKind.RoadCentrelines:
                        ImportRoadCentrelines(step);
                        break;
                    case ImportStepKind.SiteBoundaries:
                        ImportSiteBoundaries(step);
                        break;
                    case ImportStepKind.Vegetation:
                        ImportVegetation(step);
                        break;
                    case ImportStepKind.ImageryDrape:
                        ApplyImageryDrape(step);
                        break;
                    default:
                        // Fail, do not log-and-continue. A step kind added to the pure core and
                        // never dispatched here would otherwise import silently-incomplete: the plan
                        // says the bundle is fully handled, the model is missing whatever the new
                        // kind was for, and the summary reads like a success.
                        throw new InvalidOperationException(
                            $"This build of the plugin does not know how to execute the import step "
                            + $"'{step.Kind}', so the import was stopped rather than left half-done. "
                            + "Update the Mantle Place add-in.");
                }
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException or IOException)
            {
                Say($"The \"{step.Kind}\" step failed and was skipped — {ex.Message} Everything after "
                    + "it was still attempted.");
            }

            clock.Stop();
            Say($"({step.Kind} took {clock.Elapsed.TotalSeconds:N1} s.)");
        }
    }
}
