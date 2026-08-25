using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The freeze notice: it fires for exactly the two steps that freeze Revit, only when they have work
/// to do, it names this terrain without inventing a count, and it never predicts a duration.
/// </summary>
internal static class SlowStepNoticeTests
{
    /// <summary>Every kind that is seconds, not minutes. Adding a kind should land it here or below.</summary>
    private static readonly ImportStepKind[] FastKinds =
    [
        ImportStepKind.ToposurfaceFromPointsFile,
        ImportStepKind.ToposurfaceFromSurfaceTin,
        ImportStepKind.ToposurfaceFromSurfaceDxf,
        ImportStepKind.LinkSiteIfc,
        ImportStepKind.SetSharedCoordinates,
        ImportStepKind.RoadCentrelines,
        ImportStepKind.Vegetation,
    ];

    private static readonly ImportStepKind[] SlowKinds =
    [
        ImportStepKind.SiteBoundaries,
        ImportStepKind.ImageryDrape,
    ];

    internal static int Run()
    {
        TestRun run = new();

        run.Case("the site-boundary step announces itself, with this terrain's own count", () =>
        {
            // The measured order: an 80,372-point toposolid and 17 land-use rings.
            string? notice = SlowStepNotice.For(ImportStepKind.SiteBoundaries, 80_372, 17);
            run.True(notice is not null, "the slowest step is announced");
            run.Contains(notice, "80,372", "it names the terrain's point count");
            run.Contains(notice, "17", "it names how many subdivisions are coming");
            run.Contains(notice, "not responding", "it says what Revit is about to look like");
            run.Contains(notice, "has not crashed", "it says the freeze is not a crash");
        });

        run.Case("the imagery drape announces itself too — the fix is not half of one", () =>
        {
            // ⛔ #89's second correction: the drape's ChangeTypeId costs nearly as much as creating
            // all 17 subdivisions, so a notice on the boundaries alone leaves half the freeze silent.
            string? notice = SlowStepNotice.For(ImportStepKind.ImageryDrape, 80_372, 1);
            run.True(notice is not null, "the drape is announced");
            run.Contains(notice, "80,372", "it names the terrain's point count");
            run.Contains(notice, "not responding", "it says what Revit is about to look like");
        });

        run.Case("the two notices are not the same sentence", () =>
        {
            string? boundaries = SlowStepNotice.For(ImportStepKind.SiteBoundaries, 80_372, 17);
            string? drape = SlowStepNotice.For(ImportStepKind.ImageryDrape, 80_372, 1);
            run.False(
                string.Equals(boundaries, drape, StringComparison.Ordinal),
                "each step says what IT is about to do — a copy-paste that names the boundaries "
                + "before the drape is the likely regression");
            run.Contains(boundaries, "site boundaries", "the boundary notice names the boundaries");
            run.Contains(drape, "drape", "the drape notice names the drape");
        });

        run.Case("no work means no warning", () =>
        {
            // A re-import whose 17 rings are all already on the terrain commits an empty transaction.
            // Announcing a ten-minute freeze there teaches a curator to ignore the line.
            foreach (ImportStepKind kind in SlowKinds)
            {
                run.True(
                    SlowStepNotice.For(kind, 80_372, 0) is null,
                    $"{kind} with nothing to do is silent");
                run.True(
                    SlowStepNotice.For(kind, 80_372, -1) is null,
                    $"{kind} with a negative count is silent rather than throwing");
            }
        });

        run.Case("an unknown point count is said, never invented", () =>
        {
            // The terrain step did not run this time — a boundaries-only re-import onto a toposolid
            // an earlier import built. There is no N to report and none is made up.
            foreach (ImportStepKind kind in SlowKinds)
            {
                string? notice = SlowStepNotice.For(kind, null, 1);
                run.True(notice is not null, $"{kind} is still announced without a count");
                run.Contains(notice, "not known to this run", "it says the count is unknown");
                run.Contains(
                    notice,
                    "80,372",
                    "the measured reference is still quoted — it is what makes the wait legible");
                run.False(
                    notice is not null && notice.Contains(" 0 points", StringComparison.Ordinal),
                    "an unknown count never renders as zero");
            }
        });

        run.Case("the measured reference is quoted on both slow steps", () =>
        {
            foreach (ImportStepKind kind in SlowKinds)
            {
                run.Contains(
                    SlowStepNotice.For(kind, 1_000, 1),
                    "80,372",
                    $"{kind} quotes the one terrain this was measured on");
            }

            run.Equal(SlowStepNotice.MeasuredPointCount, 80_372, "the measured reference count");
        });

        run.Case("every other step stays quiet", () =>
        {
            foreach (ImportStepKind kind in FastKinds)
            {
                run.True(
                    SlowStepNotice.For(kind, 80_372, 5_000) is null,
                    $"{kind} runs in seconds and is not announced");
            }
        });

        run.Case("every kind is classified — a new one cannot be forgotten silently", () =>
        {
            // FastKinds ∪ SlowKinds must be the whole enum. A kind added to the core and left out of
            // both lists means nobody decided whether it freezes Revit, and this is where that shows.
            ImportStepKind[] all = Enum.GetValues<ImportStepKind>();
            run.Equal(all.Length, FastKinds.Length + SlowKinds.Length, "every kind is classified");
            foreach (ImportStepKind kind in all)
            {
                run.True(
                    Array.IndexOf(FastKinds, kind) >= 0 || Array.IndexOf(SlowKinds, kind) >= 0,
                    $"{kind} is classified as fast or slow");
            }
        });

        return run.Report("slow step notice");
    }
}
