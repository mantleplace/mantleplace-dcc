using System.Globalization;
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
        ImportStepKind.PublishedContours,
        ImportStepKind.LinkSiteIfc,
        ImportStepKind.ContextBuildings,
        ImportStepKind.SetSharedCoordinates,
        ImportStepKind.SetSiteLocation,
        ImportStepKind.RoadCentrelines,
        ImportStepKind.Vegetation,
        ImportStepKind.SiteContextView,
        ImportStepKind.AttributionAndProvenance,

        // Filled regions in a plan view: flat, with no toposolid to rebuild.
        ImportStepKind.FloodZones,
        ImportStepKind.SteepGround,
    ];

    private static readonly ImportStepKind[] SlowKinds =
    [
        ImportStepKind.SiteBoundaries,
        ImportStepKind.LandCover,
        ImportStepKind.Water,
        ImportStepKind.RoadPolygons,
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
            // ⛔ The measurement's second correction: the drape's ChangeTypeId costs nearly as much
            // as creating all 17 subdivisions, so a notice on the boundaries alone leaves half the
            // freeze silent.
            string? notice = SlowStepNotice.For(ImportStepKind.ImageryDrape, 80_372, 1);
            run.True(notice is not null, "the drape is announced");
            run.Contains(notice, "80,372", "it names the terrain's point count");
            run.Contains(notice, "not responding", "it says what Revit is about to look like");
        });

        run.Case("the land-cover step announces itself — it is the same commit as the boundaries'", () =>
        {
            // Cutting a subdivision costs the same whichever layer published its polygon: the commit
            // rebuilds the whole terrain's element relations either way.
            string? notice = SlowStepNotice.For(ImportStepKind.LandCover, 80_372, 10);
            run.True(notice is not null, "announced");
            run.Contains(notice, "land cover", "it names the land cover, not the boundaries");
            run.Contains(notice, "10", "it names how many subdivisions are coming");
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

        run.Case("it says what cannot be shown and why, not that nothing can", () =>
        {
            // The staged import shows every step and every chunk. What it still cannot show is the
            // inside of one commit, and a notice that still said "there is no progress to show"
            // would contradict the window it is read beside.
            foreach (ImportStepKind kind in SlowKinds)
            {
                string? notice = SlowStepNotice.For(kind, 80_372, 1);
                run.Contains(notice, "one commit", $"{kind} names where the wait is");
                run.Contains(notice, "cannot report part of itself", $"{kind} says why that part is dark");
                run.Contains(notice, "Cancel takes effect when it finishes", $"{kind} says what Cancel can and cannot do");
                run.False(
                    notice is not null && notice.Contains("no progress to show", StringComparison.Ordinal),
                    $"{kind} no longer claims there is no progress at all");
            }
        });

        run.Case("retyping subdivisions for the photograph announces itself", () =>
        {
            // Revit 2026 and later: every subdivision is typed and takes the photograph only through
            // its type. Measured in a real 2027 import, the wait is the calls AND a two-minute commit,
            // so the sentence names both — a probe on a reopened project had suggested the commit was
            // free, and a notice built on that would have understated the wait by more than half.
            string? notice = SlowStepNotice.ForSubDivisionRetypes(33, 74_852);
            run.True(notice is not null, "announced");
            run.Contains(notice, "33 subdivision(s)", "it names how many are coming");
            run.Contains(notice, "74,852", "it names this terrain's point count");
            run.Contains(notice, "Revit 2026 and later", "it says why this Revit does it at all");
            run.Contains(notice, "one commit", "it says most of the wait is inside a commit");
            run.Contains(notice, "cannot report part of itself", "and why that part is dark");
            run.Contains(notice, "not responding", "it says what Revit is about to look like");
            run.Contains(notice, "has not crashed", "it says the freeze is not a crash");
        });

        run.Case("the retype notice quotes its measurement and does not extrapolate", () =>
        {
            string? notice = SlowStepNotice.ForSubDivisionRetypes(800, 12_000);
            run.Contains(notice, SlowStepNotice.MeasuredRetypeSubDivisions.ToString("N0", CultureInfo.InvariantCulture),
                "the measured count");
            run.Contains(notice, SlowStepNotice.MeasuredRetypeSeconds.ToString("N0", CultureInfo.InvariantCulture) + " seconds",
                "the measured duration");
            run.Contains(notice, "74,852", "the terrain it was measured on");
            run.False(
                notice is not null && notice.Contains("minutes", StringComparison.Ordinal),
                "no duration is predicted for this terrain");
        });

        run.Case("no retypes, or an unknown terrain, is handled as the other notices handle it", () =>
        {
            run.True(SlowStepNotice.ForSubDivisionRetypes(0, 74_852) is null, "nothing to retype is silent");
            run.True(SlowStepNotice.ForSubDivisionRetypes(-1, 74_852) is null, "a negative count is silent");
            run.Contains(SlowStepNotice.ForSubDivisionRetypes(5, null), "not known to this run",
                "an unknown count is said, never invented");
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

        run.Case("each polygon layer's notice is in that layer's own words", () =>
        {
            run.Contains(SlowStepNotice.For(ImportStepKind.LandCover, 80_372, 10), "Next: the land cover — 10", "land cover");
            run.Contains(SlowStepNotice.For(ImportStepKind.Water, 80_372, 2), "Next: the water bodies — 2", "water");
            run.Contains(SlowStepNotice.For(ImportStepKind.RoadPolygons, 80_372, 4), "Next: the road surfaces — 4", "road surfaces");
            run.Contains(
                SlowStepNotice.For(ImportStepKind.Water, 80_372, 2),
                "as slow as the site boundaries were",
                "the cost is the terrain's relation rebuild, so it is the boundaries' measurement");
            run.Contains(
                SlowStepNotice.For(ImportStepKind.SiteBoundaries, 80_372, 10),
                "This is the slowest step of the import.",
                "the site boundaries keep the line they have always had");
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
