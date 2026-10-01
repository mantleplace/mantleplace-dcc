using System.Globalization;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The notices said before a long wait: which steps have one, when, and what each quotes. A step
/// measured at 30 s or more in any version is announced, only when it has the work that was measured,
/// it names this terrain without inventing a count, and it never predicts a duration.
/// </summary>
internal static class SlowStepNoticeTests
{
    /// <summary>
    /// What the measurements make slow — 30 s or more in at least one version, read off Phase 1's full
    /// imports, the IFC conversions and the hazard runs. Every other kind is quiet: contours, the
    /// coordinates and the location, the centrelines, water, attribution, steep ground on a plan that
    /// already exists, and the DXF terrain, which was never measured.
    /// </summary>
    private static readonly ImportStepKind[] MeasuredSlow =
    [
        ImportStepKind.ToposurfaceFromPointsFile,
        ImportStepKind.ToposurfaceFromSurfaceTin,
        ImportStepKind.ContextBuildings,
        ImportStepKind.LinkSiteIfc,
        ImportStepKind.SiteBoundaries,
        ImportStepKind.LandCover,
        ImportStepKind.RoadPolygons,
        ImportStepKind.Vegetation,
        ImportStepKind.SiteContextView,
        ImportStepKind.ImageryDrape,
        ImportStepKind.FloodZones,
    ];

    /// <summary>The steps whose notice is about one commit on the terrain: the polygon layers and the drape.</summary>
    private static readonly ImportStepKind[] CommitKinds =
    [
        ImportStepKind.SiteBoundaries,
        ImportStepKind.LandCover,
        ImportStepKind.RoadPolygons,
        ImportStepKind.ImageryDrape,
    ];

    /// <summary>
    /// The point count a commit kind's notice quotes: every polygon layer shares one measurement, and
    /// the drape has its own.
    /// </summary>
    private static string MeasuredTerrainOf(ImportStepKind kind)
        => GroundCuts.LayerOf(kind) is null ? "80,372" : "74,855";

    internal static int Run()
    {
        TestRun run = new();

        run.Case("the site-boundary step announces itself, with this terrain's own count", () =>
        {
            // The measured order: an 80,372-point toposolid and 17 land-use rings.
            string? notice = SlowStepNotice.For(ImportStepKind.SiteBoundaries, 80_372, 17);
            run.True(notice is not null, "the site boundaries are announced");
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

        run.Case("the land-cover step announces itself, against what was measured", () =>
        {
            // ⛔ What was measured, in Revit 2025 on one order: a land-cover subdivision covering the
            // whole order took nine minutes alone on a bare terrain, and twenty when cut after 57
            // others.
            string? notice = SlowStepNotice.For(ImportStepKind.LandCover, 74_855, 18);
            run.True(notice is not null, "announced");
            run.Contains(notice, "Next: the land cover — 18", "it names the layer and how many are coming");
            run.Contains(notice, "how much of the terrain", "it names what the cost appears to follow");
            run.Contains(notice, "other subdivisions already cover", "and that ground already covered costs more");
            run.Contains(notice, "In Revit 2025", "a 2025-only observation is scoped to 2025");
            run.Contains(notice, "appears to", "a cause measured on one order is not stated as settled");
            run.Contains(
                notice,
                $"about {SlowStepNotice.MeasuredWholeOrderSubDivisionAloneMinutes2025} minutes on its own",
                "it quotes the one subdivision that was measured alone");
            run.Contains(
                notice,
                $"about {SlowStepNotice.MeasuredWholeOrderSubDivisionLastMinutes2025} minutes when cut after 57 others",
                "and the same subdivision cut last");
            run.Contains(notice, "not responding", "it says what Revit is about to look like");
            run.Contains(notice, "has not crashed", "it says the freeze is not a crash");
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
            foreach (ImportStepKind kind in MeasuredSlow)
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
            foreach (ImportStepKind kind in CommitKinds)
            {
                string? notice = SlowStepNotice.For(kind, null, 1);
                run.True(notice is not null, $"{kind} is still announced without a count");
                run.Contains(notice, "not known to this run", "it says the count is unknown");
                run.Contains(
                    notice,
                    MeasuredTerrainOf(kind),
                    "the measured reference is still quoted — it is what makes the wait legible");
                run.False(
                    notice is not null && notice.Contains(" 0 points", StringComparison.Ordinal),
                    "an unknown count never renders as zero");
            }
        });

        run.Case("the measured reference is quoted on every slow step", () =>
        {
            foreach (ImportStepKind kind in CommitKinds)
            {
                run.Contains(
                    SlowStepNotice.For(kind, 1_000, 1),
                    MeasuredTerrainOf(kind),
                    $"{kind} quotes the terrain it was measured on");
            }

            run.Equal(SlowStepNotice.MeasuredPointCount, 80_372, "the measured reference count");
            run.Equal(SlowStepNotice.MeasuredSubDivisionTerrainPointCount, 74_855, "the polygon layers' measured count");
        });

        run.Case("every slow notice ends on the same reassurance", () =>
        {
            // One sentence, said once: a copy per notice is how the later layers' version lost the
            // line about the import window. The window now has a thread of its own and stays live
            // through the commit, so the line says what it shows there and what it still cannot.
            foreach (ImportStepKind kind in CommitKinds)
            {
                string? notice = SlowStepNotice.For(kind, 80_372, 1);
                run.Contains(
                    notice,
                    "the import window names this step and keeps its clock running, but cannot show how far the commit has got",
                    $"{kind} says what the window shows through the commit, and what it cannot");
                run.False(
                    notice is not null && notice.Contains("but not this", StringComparison.Ordinal),
                    $"{kind} no longer says the window goes dark for it");
            }
        });

        run.Case("it says what cannot be shown and why, not that nothing can", () =>
        {
            // The staged import shows every step and every chunk. What it still cannot show is the
            // inside of one commit, and a notice that still said "there is no progress to show"
            // would contradict the window it is read beside.
            foreach (ImportStepKind kind in CommitKinds)
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

        run.Case("a cut that takes a type says the typing is coming, in its own layer's figure", () =>
        {
            // The retype the drape used to announce now happens as each subdivision is cut, so the
            // wait moved into the polygon step and the sentence moved with it. Said at the first cut
            // that shows it takes a type, so it names no version.
            foreach (GroundLayer layer in Enum.GetValues<GroundLayer>().Where(layer => layer != GroundLayer.Water))
            {
                string? notice = SlowStepNotice.ForTypesAtCut(layer, SubDivisionMaterialRoute.Type, drapePlanned: true, 40, 12_000);
                SlowStepNotice.TypeAtCutMeasurement measured = SlowStepNotice.MeasuredTypeAtCut(layer);

                run.True(notice is not null, $"{layer}: announced");
                run.Contains(notice, "40 subdivision(s)", $"{layer}: it names how many are coming");
                run.Contains(notice, "as it is cut", $"{layer}: it says when the type is given");
                run.Contains(notice, GroundLayerWords.For(layer).Label, $"{layer}: it names its own layer");
                run.Contains(notice, "74,855", $"{layer}: the terrain it was measured on");
                run.Contains(notice, "This terrain has 12,000 points", $"{layer}: this terrain, beside it");
                run.Contains(
                    notice,
                    measured.LowSeconds.ToString("0.0", CultureInfo.InvariantCulture),
                    $"{layer}: its own measured seconds a subdivision");
                run.Contains(
                    notice,
                    measured.SubDivisions.ToString("N0", CultureInfo.InvariantCulture) + " subdivision",
                    $"{layer}: how many that was measured on, since two water bodies are not 344 roads");
                run.False(
                    notice is not null && notice.Contains("minutes", StringComparison.Ordinal),
                    $"{layer}: no duration is predicted");

                foreach (GroundLayer other in Enum.GetValues<GroundLayer>().Where(other => other != layer))
                {
                    run.False(
                        notice is not null && notice.Contains(GroundLayerWords.For(other).Label, StringComparison.Ordinal),
                        $"{layer}: no other layer's figure is borrowed ({other})");
                }
            }

            // Land cover's cuts varied most, and the notice gives the range, not the fastest run.
            run.Contains(
                SlowStepNotice.ForTypesAtCut(GroundLayer.LandCover, SubDivisionMaterialRoute.Type, true, 19, 74_855),
                "3.3 to 5.0 s",
                "land cover's range over four imports");
            run.Contains(
                SlowStepNotice.ForTypesAtCut(GroundLayer.RoadSurface, SubDivisionMaterialRoute.Type, true, 344, 74_855),
                "about 1.2 s",
                "one import gives one figure, not a range");
            run.Contains(
                SlowStepNotice.ForTypesAtCut(GroundLayer.LandUse, SubDivisionMaterialRoute.Type, true, 5, null),
                "not known to this run",
                "an unknown count is said, never invented");
        });

        run.Case("nothing to type says nothing", () =>
        {
            // Revit 2025: a cut reports no type and keeps its instance material.
            run.True(
                SlowStepNotice.ForTypesAtCut(GroundLayer.LandCover, SubDivisionMaterialRoute.Instance, drapePlanned: true, 40, 74_855) is null,
                "a typeless cut is not typed, so nothing is announced");
            run.True(
                SlowStepNotice.ForTypesAtCut(GroundLayer.LandCover, SubDivisionMaterialRoute.Type, drapePlanned: false, 40, 74_855) is null,
                "no drape, no typing");
            run.True(
                SlowStepNotice.ForTypesAtCut(GroundLayer.LandCover, SubDivisionMaterialRoute.Refused, drapePlanned: true, 40, 74_855) is null,
                "a cut whose route could not be read, so the next one that can announces it");
            run.True(
                SlowStepNotice.ForTypesAtCut(GroundLayer.LandCover, SubDivisionMaterialRoute.Type, drapePlanned: true, 0, 74_855) is null,
                "a re-import that cuts nothing");
        });

        run.Case("a drape over subdivisions already on their types announces its commit", () =>
        {
            // With every cut typed as it was cut, the drape has no retype to announce, and its one
            // commit is still the longest wait left in the step.
            string? notice = SlowStepNotice.ForDrapeCommit(405, subDivisionsToRetype: 0, terrainRetypes: 0, 12_000);
            run.True(notice is not null, "announced");
            run.Contains(notice, "405 subdivision(s)", "it names how many");
            run.Contains(notice, "74,855", "the terrain it was measured on");
            run.Contains(notice, "This terrain has 12,000 points", "this terrain, beside it");
            run.Contains(
                notice,
                SlowStepNotice.MeasuredDrapeCommitSeconds.ToString("N0", CultureInfo.InvariantCulture) + " seconds",
                "the measured commit");
            run.Contains(notice, "not responding", "it says what Revit is about to look like");
            run.Contains(notice, "has not crashed", "it says the freeze is not a crash");
            run.False(
                notice is not null && notice.Contains("minutes", StringComparison.Ordinal),
                "no duration is predicted for this terrain");
        });

        run.Case("the drape's commit is announced exactly when neither retype notice is", () =>
        {
            // Revit 2025: every subdivision takes the photograph on the instance, the commit is
            // seconds, and nothing about it changed.
            run.True(SlowStepNotice.ForDrapeCommit(0, 0, 0, 74_855) is null, "no typed subdivision, no notice");

            // The guard is the other two notices themselves, so the three cannot drift apart.
            foreach ((int toRetype, int terrainRetypes) in (ValueTuple<int, int>[])[(0, 0), (5, 0), (0, 1), (5, 1)])
            {
                bool retypeSaid = SlowStepNotice.ForSubDivisionRetypes(toRetype, 74_855) is not null
                    || SlowStepNotice.For(ImportStepKind.ImageryDrape, 74_855, terrainRetypes) is not null;
                run.Equal(
                    SlowStepNotice.ForDrapeCommit(10, toRetype, terrainRetypes, 74_855) is not null,
                    !retypeSaid,
                    $"retypes {toRetype}, terrain retypes {terrainRetypes}");
            }
        });

        run.Case("converting the site model announces itself, and linking one already converted does not", () =>
        {
            // The conversion is the cost: linking a file an earlier import converted took about 5 s.
            string? notice = SlowStepNotice.For(ImportStepKind.LinkSiteIfc, null, 1);
            run.Contains(notice, "Next: the site model.", "it names the step");
            run.Contains(
                notice,
                "about 41 to 71 seconds in Revit 2025, 39 to 40 seconds in Revit 2026 and 44 to 45 seconds in Revit 2027",
                "what converting it measured at");
            run.Contains(notice, "reuses", "it says a later import does not pay it again");
            run.Contains(notice, "not responding", "it says what Revit is about to look like");
            run.Contains(notice, "has not crashed", "it says the freeze is not a crash");
            run.Contains(notice, "the import window names this step and keeps its clock running", "it says what the window shows");
            run.True(SlowStepNotice.For(ImportStepKind.LinkSiteIfc, null, 0) is null, "a file already converted is linked in seconds, unannounced");
        });

        run.Case("making the site context view announces itself, and reusing it does not", () =>
        {
            string? notice = SlowStepNotice.For(ImportStepKind.SiteContextView, null, 2);
            run.Contains(notice, "Next: the site context view", "it names the step");
            run.Contains(
                notice,
                "about 1 to 5 seconds in Revit 2025, 42 seconds in Revit 2026 and 33 to 53 seconds in Revit 2027",
                "what it measured at");
            run.Contains(notice, "not responding", "it says what Revit is about to look like");
            run.True(SlowStepNotice.For(ImportStepKind.SiteContextView, null, 0) is null, "an earlier import's view and filter, reused, are not announced");
        });

        run.Case("the terrain announces itself when it is built for the drape, as the default import builds it", () =>
        {
            // Built on the type that will wear the photograph, its commit took 45 s where on the default
            // type it took 3 s: the drape, ticked by default, is what makes this step slow.
            string? notice = SlowStepNotice.For(ImportStepKind.ToposurfaceFromSurfaceTin, 74_855, 1);
            run.Contains(notice, "Next: the terrain", "it names the step");
            run.Contains(
                notice,
                "about 26 to 42 seconds in Revit 2025, 57 seconds in Revit 2026 and 60 to 79 seconds in Revit 2027",
                "what it measured at in full imports");
            run.Contains(notice, "5 to 7 seconds", "and without the drape");
            run.Contains(notice, "This terrain has 74,855 points", "this terrain");
            run.Contains(notice, "one commit", "it says where the wait is");
            run.Equal(
                SlowStepNotice.For(ImportStepKind.ToposurfaceFromPointsFile, 74_855, 1),
                notice,
                "the points path says the same as the TIN path");
            run.True(SlowStepNotice.For(ImportStepKind.ToposurfaceFromSurfaceTin, 74_855, 0) is null, "not built for the drape: unannounced");
        });

        run.Case("the context buildings announce the site model's opening, and then count", () =>
        {
            string? notice = SlowStepNotice.For(ImportStepKind.ContextBuildings, null, 834);
            run.Contains(notice, "Next: the context buildings — 834", "it names the step and how many");
            run.Contains(
                notice,
                "about 34 to 57 seconds in Revit 2025, 32 to 34 seconds in Revit 2026 and 36 to 59 seconds in Revit 2027",
                "what it measured at");
            run.Contains(notice, "opens the site model", "it says where the dark part is");
            run.Contains(notice, "between chunks", "and that Cancel works once the copying starts");
            run.Contains(notice, "has not crashed", "it says the freeze is not a crash");
            run.True(SlowStepNotice.For(ImportStepKind.ContextBuildings, null, 0) is null, "nothing to copy: the IFC is never opened, unannounced");
        });

        run.Case("planting announces itself, and says the window counts it", () =>
        {
            string? notice = SlowStepNotice.For(ImportStepKind.Vegetation, null, 19_755);
            run.Contains(notice, "Next: planting — 19,755", "it names the step and how many");
            run.Contains(
                notice,
                "about 206 to 415 seconds in Revit 2025, 266 to 275 seconds in Revit 2026 and 231 to 338 seconds in Revit 2027",
                "what it measured at in full imports");
            run.Contains(notice, ImportChunking.ElementsPerTransaction.ToString(CultureInfo.InvariantCulture) + " at a time", "the chunk it commits in");
            run.Contains(notice, "next chunk", "where Cancel lands");
        });

        run.Case("the flood zones announce themselves, against the one bundle that has them", () =>
        {
            string? notice = SlowStepNotice.For(ImportStepKind.FloodZones, null, 65);
            run.Contains(notice, "Next: the flood zones — 65", "it names the step and how many");
            run.Contains(notice, "about 33 seconds in Revit 2025, 32 seconds in Revit 2026 and 44 seconds in Revit 2027", "what it measured at");
            run.Contains(notice, "the one bundle", "it says what that was measured on");
            run.True(SlowStepNotice.For(ImportStepKind.SteepGround, null, 32) is null, "steep ground, added to a plan in under a second, is quiet");
        });

        run.Case("smooth shading after the last step announces itself when there are subdivisions to shade", () =>
        {
            // Committed after every row when the drape did not settle it first: 98 to 111 s in Revit
            // 2026 and 2027 with the order's 405 subdivisions, about a second with none.
            string? notice = SlowStepNotice.ForSmoothShading(405, 74_855);
            run.Contains(notice, "Next: smooth shading", "it names the work");
            run.Contains(notice, "405 subdivision(s)", "and how many it shades");
            run.Contains(
                notice,
                "about 4 seconds in Revit 2025, 111 seconds in Revit 2026 and 98 seconds in Revit 2027",
                "what it measured at with 405");
            run.Contains(notice, "about 2 seconds in Revit 2025 and 14 seconds in Revit 2027", "and with 61");
            run.Contains(notice, "one commit", "it says where the wait is");
            run.True(SlowStepNotice.ForSmoothShading(0, 74_855) is null, "a terrain with no subdivisions is shaded in about a second, unannounced");
        });

        run.Case("a step is announced exactly when it was measured at 30 s or more in some version", () =>
        {
            // The rule, whose one home is revit/CLAUDE.md: the quiet steps follow from the measurements
            // rather than being kept by hand.
            run.Within(SlowStepNotice.AnnouncedFromSeconds, 30, 0, "the threshold");
            foreach (ImportStepKind kind in Enum.GetValues<ImportStepKind>())
            {
                bool slow = Array.IndexOf(MeasuredSlow, kind) >= 0;
                run.Equal(SlowStepNotice.IsAnnounced(kind), slow, $"{kind} is classified by its measurement");
                run.Equal(SlowStepNotice.For(kind, 74_855, 5) is not null, slow, $"{kind} has a notice exactly when it is slow");
            }
        });

        run.Case("the water bodies are quiet: seconds in every version", () =>
        {
            run.True(SlowStepNotice.For(ImportStepKind.Water, 74_855, 2) is null, "no step notice");
            run.True(
                SlowStepNotice.ForTypesAtCut(GroundLayer.Water, SubDivisionMaterialRoute.Type, true, 2, 74_855) is null,
                "and no typing notice inside it");
        });

        run.Case("each polygon layer's notice is in that layer's own words", () =>
        {
            run.Contains(SlowStepNotice.For(ImportStepKind.SiteBoundaries, 80_372, 10), "Next: the site boundaries — 10", "site boundaries");
            run.Contains(SlowStepNotice.For(ImportStepKind.LandCover, 80_372, 10), "Next: the land cover — 10", "land cover");
            run.Contains(SlowStepNotice.For(ImportStepKind.RoadPolygons, 80_372, 4), "Next: the road surfaces — 4", "road surfaces");
        });

        run.Case("no polygon layer promises another step's speed or its place in the order", () =>
        {
            // The order is the planner's to change, and it has changed once. A notice that ranked the
            // steps, or compared one with another, went stale when it did.
            foreach (ImportStepKind kind in (ImportStepKind[])
                [ImportStepKind.SiteBoundaries, ImportStepKind.LandCover, ImportStepKind.RoadPolygons])
            {
                string? notice = SlowStepNotice.For(kind, 80_372, 2);
                foreach (string claim in (string[])["as slow as", "slowest", "the layer before", "the first layer", "site boundaries were"])
                {
                    run.False(
                        notice is not null && notice.Contains(claim, StringComparison.Ordinal),
                        $"{kind} does not say \"{claim}\"");
                }

                run.Contains(notice, "74,855", $"{kind} quotes the polygon layers' measured terrain");
                run.Contains(notice, "80,372", $"{kind} still names this terrain");
            }
        });

        return run.Report("slow step notice");
    }
}
