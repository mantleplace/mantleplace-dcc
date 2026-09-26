using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>
/// What to tell a curator <em>before</em> an import step that will freeze Revit for minutes. Pure.
/// </summary>
/// <remarks>
/// <para>
/// Two steps dominate a site import, and both pay their cost inside a single
/// <c>Transaction.Commit()</c>, in Revit's own <c>updateElementRelations</c>: the site-boundary
/// subdivisions and the imagery drape's retype of the terrain. Measured on one order — an
/// 80,372-point toposolid with 17 land-use rings — that is 610.6 s and 409.1 s on a first import,
/// 247.1 s and 249.6 s on a re-import, with Revit reporting "not responding" for the whole of each.
/// A polygon layer cut after the boundaries can cost far more than they did, and is announced against
/// its own measurement (<see cref="DescribeLaterLayer"/>).
/// </para>
/// <para>
/// The drape's half is now paid only on ground that is not already on the imagery type — an
/// earlier import's, or this run's when the terrain step could not prepare that type. A terrain
/// built for a planned drape is otherwise created on it (<see cref="ImportStep.ToposolidType"/>),
/// so its drape has no retype to announce and the shim hands over zero work.
/// </para>
/// <para>
/// ⛔ <b>Nothing here makes the import faster, and that is the decision, not an omission.</b> The
/// alternatives were weighed and refused: <c>Toposolid.Simplify</c> is decimation and the terrain is
/// already too coarse to read as terrain, so it makes the worse defect worse to soften the lesser
/// one; sampling fewer points is the same trade in a different wrapper and has never been measured;
/// and skipping the boundaries addresses at most half the cost while putting the first
/// non-data-driven toggle into <see cref="BundleImportPlanner"/>. What was actually wrong was that a
/// ten-minute freeze arrived with no warning and read as a crash. So it is announced instead.
/// </para>
/// <para>
/// ⛔ <b>What cannot be shown is the inside of one commit.</b> This used to say that a progress bar
/// was not available at all, and that was the decision: the whole import ran in one call on Revit's
/// thread, so nothing could repaint until it returned. That is reversed — the import is staged
/// (<see cref="StagedImport"/>), one step or one chunk per <c>ExternalEvent</c> raise, so the import
/// window moves between steps and between chunks and Cancel is honoured at each boundary. What is
/// still dark is a single <c>Transaction.Commit()</c>: it cannot yield, report part of itself, or be
/// interrupted, and both of these steps spend their whole cost inside one. So the line before it is
/// still the only line there will be <em>for that step</em>, and it says exactly that much.
/// </para>
/// <para>
/// This is text, so it lives where text can be asserted. The shim decides nothing: it hands over the
/// kind, the terrain's point count and how much work the step actually has, and says whatever comes
/// back (<c>HPS-02</c>).
/// </para>
/// </remarks>
public static class SlowStepNotice
{
    /// <summary>The point count of the terrain the site boundaries and the drape were measured on.</summary>
    /// <remarks>
    /// A reference, not a model. One measurement fixes a point; it does not establish how cost grows
    /// with vertex count, and this class deliberately publishes no formula — see
    /// <see cref="Describe"/>.
    /// </remarks>
    public const int MeasuredPointCount = 80_372;

    /// <summary>Rounded minutes the site-boundary commit took on <see cref="MeasuredPointCount"/>.</summary>
    public const int MeasuredSiteBoundariesMinutes = 10;

    /// <summary>Rounded minutes the drape's retype took on <see cref="MeasuredPointCount"/>.</summary>
    public const int MeasuredImageryDrapeMinutes = 7;

    /// <summary>
    /// The line to say before <paramref name="kind"/>, or <c>null</c> when there is nothing worth
    /// warning about.
    /// </summary>
    /// <param name="kind">The step about to run.</param>
    /// <param name="terrainPointCount">
    /// The host toposolid's point count, or <c>null</c> when this run did not build the terrain and
    /// therefore does not know it. A count is never invented for the null case.
    /// </param>
    /// <param name="plannedWorkItems">
    /// How many things the step is about to create or retype. Zero means the transaction has nothing
    /// to commit, so it will not be slow and must not be announced as though it will.
    /// </param>
    public static string? For(ImportStepKind kind, int? terrainPointCount, int plannedWorkItems)
    {
        if (plannedWorkItems <= 0)
        {
            return null;
        }

        return kind switch
        {
            ImportStepKind.SiteBoundaries => Describe(
                "Next: the site boundaries — "
                    + plannedWorkItems.ToString("N0", CultureInfo.InvariantCulture)
                    + " subdivision(s) to cut into the terrain. Cutting subdivisions is the slowest "
                    + "work in an import.",
                MeasuredSiteBoundariesMinutes,
                terrainPointCount),

            // Every other polygon layer, in its own words, and against its own measurement rather
            // than the boundaries': see DescribeLaterLayer for why the boundaries' speed is no guide.
            _ when GroundCuts.LayerOf(kind) is { } layer => DescribeLaterLayer(
                "Next: the " + GroundLayerWords.For(layer).Label + " — "
                    + plannedWorkItems.ToString("N0", CultureInfo.InvariantCulture)
                    + " subdivision(s) to cut into the terrain.",
                terrainPointCount),

            ImportStepKind.ImageryDrape => Describe(
                "Next: the imagery drape. Retyping the terrain so it can wear the photograph costs "
                    + "almost as much as cutting the boundaries did, and for the same reason.",
                MeasuredImageryDrapeMinutes,
                terrainPointCount),

            // Every other step is seconds. Announcing them would make the two that matter unreadable.
            _ => null,
        };
    }

    /// <summary>
    /// The shared body: where the time goes, the one measurement, this terrain, and the reassurance.
    /// </summary>
    /// <remarks>
    /// ⛔ It states the measurement and this terrain's count side by side and stops there. It does
    /// <em>not</em> scale one into the other. The cost is known to track the toposolid's vertex count
    /// — <c>updateGReps</c> reports 17 elements after ten minutes of relation bookkeeping — but the
    /// shape of that relationship has been measured exactly once, and a predicted duration derived
    /// from a single point would be a guess wearing a number's clothes. The reader can compare two
    /// counts; the plugin should not pretend to interpolate between them.
    /// </remarks>
    private static string Describe(string opening, int measuredMinutes, int? terrainPointCount)
    {
        string thisTerrain = ThisTerrain(terrainPointCount);

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} Revit rebuilds the whole terrain's element relations when the transaction commits, "
            + "and that cost tracks the terrain's point count rather than how much is being added. "
            + "On the one terrain this has been measured on ({1:N0} points) it took about {2} "
            + "minutes. {3}. {4}",
            opening,
            MeasuredPointCount,
            measuredMinutes,
            thisTerrain,
            InsideOneCommit);
    }

    /// <summary>The reassurance every whole-commit notice ends on: what cannot be shown, and why.</summary>
    private const string InsideOneCommit =
        "That cost is inside one commit, and a commit cannot report part of itself or be interrupted: "
        + "the import window shows every step and every chunk of trees, but not this, so Revit will "
        + "report \"not responding\" until it finishes, and Cancel takes effect when it finishes. It "
        + "has not crashed; leave it alone.";

    /// <summary>The point count of the terrain <see cref="DescribeLaterLayer"/> was measured on.</summary>
    public const int MeasuredLaterLayerPointCount = 74_855;

    /// <summary>How many land-cover subdivisions that measurement cut.</summary>
    public const int MeasuredLaterLayerSubDivisions = 18;

    /// <summary>How many site-boundary subdivisions were already on that terrain when it did.</summary>
    /// <remarks>40 in Revit 2025 and 2026; 39 in 2027, which refused one at commit.</remarks>
    public const int MeasuredLaterLayerSubDivisionsAlreadyCut = 40;

    /// <summary>Rounded minutes of the faster of the two Revit 2025 land-cover commits.</summary>
    /// <remarks>
    /// Two runs of the same order in Revit 2025.4, 2026-09-25: 969.7 s from the code before a
    /// refused cut stopped costing the whole layer, and 1,782.5 s from the code after it. Two runs
    /// do not say whether that gap is the change or the variation between runs, so both ends are
    /// quoted. The site boundaries on the same terrain committed in 28.9 s and 35.8 s.
    /// </remarks>
    public const int MeasuredLaterLayerMinutes2025Fastest = 16;

    /// <summary>Rounded minutes of the slower of the two Revit 2025 land-cover commits.</summary>
    public const int MeasuredLaterLayerMinutes2025Slowest = 30;

    /// <summary>Rounded minutes the same land-cover commit took in Revit 2026 and 2027.</summary>
    /// <remarks>150.1 s in 2026 and 177.5 s in 2027, one run each.</remarks>
    public const int MeasuredLaterLayerMinutes2026And2027 = 3;

    /// <summary>
    /// The body for a polygon layer other than the site boundaries: why it may be slower than the
    /// first layer cut, its own measurement, this terrain, and the reassurance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>This used to promise the site boundaries' speed, and that was wrong by fifty times.</b>
    /// The reasoning was that every subdivision costs the terrain's relation rebuild, whichever layer
    /// published it. On one order in Revit 2025 the land cover then took 16 and 30 minutes in two
    /// runs, after about half a minute of site boundaries on the same terrain; 2026 and 2027 took
    /// about three. The likeliest reading is that a layer cut onto ground that already carries
    /// subdivisions pays for them too, and the notice says that much as what it <em>appears</em> to
    /// be: it is one order, and why the cost grows has not been measured.
    /// </para>
    /// <para>
    /// The same rule as <see cref="Describe"/>: the measurement and this terrain are stated side by
    /// side, and no duration is predicted from them. It names the layer that was measured, because a
    /// water or road notice quoting a land-cover number must say so. It does not name the site
    /// boundaries, and its comparison is conditional on ground that already carries subdivisions: a
    /// bundle with no <c>land_use</c> cuts this layer first.
    /// </para>
    /// </remarks>
    private static string DescribeLaterLayer(string opening, int? terrainPointCount)
        => string.Format(
            CultureInfo.InvariantCulture,
            "{0} On ground that already carries subdivisions this can take far longer than the first "
            + "layer cut did: Revit rebuilds the whole terrain's element relations when the transaction "
            + "commits, and that cost appears to grow with the subdivisions already on the terrain. It "
            + "has been measured on one order ({1:N0} points), cutting {2:N0} land-cover subdivisions "
            + "onto a terrain already carrying {3:N0}: {4} and {5} minutes in two runs in Revit 2025, "
            + "and about {6} in Revit 2026 and 2027. {7}. {8}",
            opening,
            MeasuredLaterLayerPointCount,
            MeasuredLaterLayerSubDivisions,
            MeasuredLaterLayerSubDivisionsAlreadyCut,
            MeasuredLaterLayerMinutes2025Fastest,
            MeasuredLaterLayerMinutes2025Slowest,
            MeasuredLaterLayerMinutes2026And2027,
            ThisTerrain(terrainPointCount),
            InsideOneCommit);

    /// <summary>The point count of the terrain <see cref="ForSubDivisionRetypes"/> was measured on.</summary>
    public const int MeasuredRetypePointCount = 74_852;

    /// <summary>How many subdivisions that measurement retyped, one <c>ChangeTypeId</c> each.</summary>
    public const int MeasuredRetypeSubDivisions = 33;

    /// <summary>Rounded seconds those retypes took in a real Revit 2027 import, commit included.</summary>
    /// <remarks>
    /// 2026-09-19, bundle <c>9d2dfdbf</c>, on ground already on the imagery type so nothing else was
    /// retyped: 71 s of calls, then a 121 s commit. A probe on a saved and reopened project committed
    /// the same retypes in about a second; an import does not, so the import is what is quoted.
    /// </remarks>
    public const int MeasuredRetypeSeconds = 190;

    /// <summary>
    /// The line to say before the drape retypes <paramref name="subDivisionCount"/> subdivisions so
    /// they can wear the photograph, or <c>null</c> when there are none to retype.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Revit 2026 and later give every subdivision a type and no material of its own, so the drape
    /// reaches one only by retyping it (<see cref="SubDivisionMaterial"/>). The wait is in two
    /// halves, and the sentence says both: each <c>ChangeTypeId</c> takes seconds, one after another
    /// inside one slice of the import, and then Revit rebuilds the terrain's element relations when
    /// the transaction commits — the same dark commit the other notices describe.
    /// </para>
    /// <para>
    /// The same rule as <see cref="Describe"/>: the measurement and this terrain are stated side by
    /// side, and no duration is predicted from them.
    /// </para>
    /// </remarks>
    public static string? ForSubDivisionRetypes(int subDivisionCount, int? terrainPointCount)
    {
        if (subDivisionCount <= 0)
        {
            return null;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "Next: giving {0:N0} subdivision(s) the photograph. Revit 2026 and later give a subdivision "
            + "a type and no material of its own, so each one is moved onto a type that wears the "
            + "photograph, one at a time, and Revit then rebuilds the terrain's element relations when "
            + "the transaction commits. On the one terrain this has been measured on ({1:N0} points), "
            + "{2:N0} subdivisions took about {3:N0} seconds, most of it inside that one commit. {4}. "
            + "A commit cannot report part of itself, so Revit will report \"not responding\" until it "
            + "finishes, and Cancel takes effect when it finishes. It has not crashed; leave it alone.",
            subDivisionCount,
            MeasuredRetypePointCount,
            MeasuredRetypeSubDivisions,
            MeasuredRetypeSeconds,
            ThisTerrain(terrainPointCount));
    }

    /// <summary>This terrain's point count as a sentence, or the admission that it is not known.</summary>
    private static string ThisTerrain(int? terrainPointCount)
        => terrainPointCount is { } count
            ? string.Format(CultureInfo.InvariantCulture, "This terrain has {0:N0} points", count)
            : "This terrain's point count is not known to this run — it was built by an earlier import";
}
