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
/// ⛔ <b>A progress bar is not available.</b> There is no partial-progress UI inside a Revit API
/// transaction, and the whole cost is inside one commit — so no line can be written while it runs.
/// The line before it is the only line there will ever be, which is why it carries the whole
/// explanation rather than a terse "working…".
/// </para>
/// <para>
/// This is text, so it lives where text can be asserted. The shim decides nothing: it hands over the
/// kind, the terrain's point count and how much work the step actually has, and says whatever comes
/// back (<c>HPS-02</c>).
/// </para>
/// </remarks>
public static class SlowStepNotice
{
    /// <summary>The point count of the one terrain this has ever been measured on.</summary>
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
                    + " subdivision(s) to cut into the terrain. This is the slowest step of the import.",
                MeasuredSiteBoundariesMinutes,
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
        string thisTerrain = terrainPointCount is { } count
            ? string.Format(
                CultureInfo.InvariantCulture,
                "This terrain has {0:N0} points",
                count)
            : "This terrain's point count is not known to this run — it was built by an earlier import";

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0} Revit rebuilds the whole terrain's element relations when the transaction commits, "
            + "and that cost tracks the terrain's point count rather than how much is being added. "
            + "On the one terrain this has been measured on ({1:N0} points) it took about {2} "
            + "minutes. {3}. Revit will report \"not responding\" until it finishes and there is no "
            + "progress to show — a Revit transaction has no partial-progress display. It has not "
            + "crashed; leave it alone.",
            opening,
            MeasuredPointCount,
            measuredMinutes,
            thisTerrain);
    }
}
