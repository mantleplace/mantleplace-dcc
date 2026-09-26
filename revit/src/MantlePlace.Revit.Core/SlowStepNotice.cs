using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>
/// What to tell a curator <em>before</em> an import step that will freeze Revit for minutes. Pure.
/// </summary>
/// <remarks>
/// <para>
/// Two kinds of step dominate a site import, and both pay their cost inside a single
/// <c>Transaction.Commit()</c>, in Revit's own <c>updateElementRelations</c>: the polygon layers'
/// subdivisions and the imagery drape's retype of the terrain. Measured on one order — an
/// 80,372-point toposolid with 17 land-use rings — the site boundaries and the drape took 610.6 s
/// and 409.1 s on a first import, 247.1 s and 249.6 s on a re-import, with Revit reporting "not
/// responding" for the whole of each. The polygon layers are now announced against a later
/// measurement that isolates one subdivision (<see cref="DescribePolygonLayer"/>); the drape is
/// still announced against this one.
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
    /// <summary>The point count of the terrain the drape's retype was measured on.</summary>
    /// <remarks>
    /// A reference, not a model. One measurement fixes a point; it does not establish how cost grows
    /// with vertex count, and this class deliberately publishes no formula — see
    /// <see cref="Describe"/>.
    /// </remarks>
    public const int MeasuredPointCount = 80_372;

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
            // Every polygon layer, in its own words, against one measurement. None of them names
            // another step or its place in the order: the planner has reordered them once already.
            _ when GroundCuts.LayerOf(kind) is { } layer => DescribePolygonLayer(
                "Next: the " + GroundLayerWords.For(layer).Label + " — "
                    + plannedWorkItems.ToString("N0", CultureInfo.InvariantCulture)
                    + " subdivision(s) to cut into the terrain.",
                terrainPointCount),

            ImportStepKind.ImageryDrape => Describe(
                "Next: the imagery drape. Retyping the terrain so it can wear the photograph costs "
                    + "minutes, and for the same reason the subdivisions do.",
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

    /// <summary>The point count of the terrain the subdivision measurements were taken on.</summary>
    public const int MeasuredSubDivisionTerrainPointCount = 74_855;

    /// <summary>
    /// Rounded minutes one land-cover subdivision covering the whole order took to commit on its own
    /// in Revit 2025, cut first, onto a terrain with nothing else cut into it.
    /// </summary>
    /// <remarks>
    /// 526.5 s, 2026-09-25, Revit 2025.4: a five-vertex grass ring of 199.7 ha, in its own
    /// transaction. One run.
    /// </remarks>
    public const int MeasuredWholeOrderSubDivisionAloneMinutes2025 = 9;

    /// <summary>
    /// Rounded minutes the same subdivision took to commit on its own in Revit 2025, cut last, onto a
    /// terrain already carrying 57 subdivisions.
    /// </summary>
    /// <remarks>1,210.4 s, 2026-09-25, Revit 2025.4. One run.</remarks>
    public const int MeasuredWholeOrderSubDivisionLastMinutes2025 = 20;

    /// <summary>
    /// Rounded minutes the whole land-cover layer took as one commit in Revit 2026 and 2027, cut after
    /// the site boundaries.
    /// </summary>
    /// <remarks>150.1 s in 2026 and 177.5 s in 2027, 2026-09-25, one run each.</remarks>
    public const int MeasuredLandCoverLayerMinutes2026And2027 = 3;

    /// <summary>
    /// The body for a polygon layer: what the cost appears to follow in Revit 2025, the one
    /// subdivision measured on its own, this terrain, and the reassurance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>This has been wrong twice, both times by explaining one run.</b> It first promised every
    /// later layer the site boundaries' speed, and the land cover then took fifty times as long. It
    /// then said the cost grows with the subdivisions already on the terrain, and a probe cut the
    /// order's whole-order grass ring first, alone, onto a bare terrain: nine minutes. Cut last, onto
    /// 57 other subdivisions, the same ring took twenty. So in Revit 2025 on this order both matter:
    /// how much of the terrain a subdivision covers, and how much of that ground other subdivisions
    /// already cover. Neither has been measured anywhere else, and 2026 and 2027 have only one
    /// whole-layer number each, so the sentence is scoped to 2025 and says "appears".
    /// </para>
    /// <para>
    /// The same rule as <see cref="Describe"/>: the measurements and this terrain are stated side by
    /// side, and no duration is predicted from them. It names no other step and no place in the
    /// order, because the order is the planner's and has changed once already.
    /// </para>
    /// </remarks>
    private static string DescribePolygonLayer(string opening, int? terrainPointCount)
        => string.Format(
            CultureInfo.InvariantCulture,
            "{0} Revit rebuilds the terrain's element relations when the transaction commits. In Revit "
            + "2025 that cost appears to follow how much of the terrain a new subdivision covers, and "
            + "to grow when it lands on ground other subdivisions already cover. On the one order this "
            + "has been measured on ({1:N0} points), a single land-cover subdivision covering the whole "
            + "order took about {2} minutes on its own in Revit 2025 when nothing else was cut into the "
            + "terrain, and about {3} minutes when cut after 57 others; in Revit 2026 and 2027 the whole "
            + "land-cover layer took about {4} minutes. How long this layer takes depends on the "
            + "polygons it carries, and no duration is predicted here. {5}. {6}",
            opening,
            MeasuredSubDivisionTerrainPointCount,
            MeasuredWholeOrderSubDivisionAloneMinutes2025,
            MeasuredWholeOrderSubDivisionLastMinutes2025,
            MeasuredLandCoverLayerMinutes2026And2027,
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
