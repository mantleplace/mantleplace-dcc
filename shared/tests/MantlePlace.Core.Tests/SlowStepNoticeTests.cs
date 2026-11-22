using System.Globalization;
using MantlePlace.Core;

namespace MantlePlace.Core.Tests;

internal static class SlowStepNoticeTests
{
    private static readonly ImportStepKind[] MeasuredSlow =
    [
        ImportStepKind.ToposurfaceFromPointsFile,
        ImportStepKind.ToposurfaceFromSurfaceTin,
        ImportStepKind.ContextBuildings,
        ImportStepKind.LinkSiteIfc,
        ImportStepKind.RoadPolygons,
        ImportStepKind.Vegetation,
        ImportStepKind.ImageryDrape,
        ImportStepKind.FloodZones,
    ];

    private static readonly ImportStepKind[] CommitKinds =
    [
        ImportStepKind.RoadPolygons,
        ImportStepKind.ImageryDrape,
    ];

    private static string MeasuredTerrainOf(ImportStepKind kind)
        => GroundCuts.LayerOf(kind) is null ? "80,372" : "75,314";

    private static SiteContextDecision ContextDecision(NamedElementAction view, NamedElementAction filter) => new()
    {
        View = view,
        Filter = filter,
        ApplyFilterToView = view != NamedElementAction.Refuse && filter != NamedElementAction.Refuse,
        Explanation = string.Empty,
    };

    internal static int Run()
    {
        TestRun run = new();

        run.Case("the road-surface step announces itself, with this terrain's own count", () =>
        {
            string? notice = SlowStepNotice.For(ImportStepKind.RoadPolygons, 80_372, 17);
            run.True(notice is not null, "the road surfaces are announced");
            run.Contains(notice, "80,372", "it names the terrain's point count");
            run.Contains(notice, "17", "it names how many subdivisions are coming");
            run.Contains(notice, "not responding", "it says what Revit is about to look like");
            run.Contains(notice, "has not crashed", "it says the freeze is not a crash");
        });

        run.Case("the imagery drape announces itself too — the fix is not half of one", () =>
        {
            string? notice = SlowStepNotice.For(ImportStepKind.ImageryDrape, 80_372, 1);
            run.True(notice is not null, "the drape is announced");
            run.Contains(notice, "80,372", "it names the terrain's point count");
            run.Contains(notice, "not responding", "it says what Revit is about to look like");
        });

        run.Case("a polygon layer quotes its own row of the measurement, and explains nothing", () =>
        {
            string? notice = SlowStepNotice.For(ImportStepKind.RoadPolygons, 74_855, 300);
            run.True(notice is not null, "announced");
            run.Contains(notice, "Next: the road surfaces — 300", "it names the layer and how many are coming");
            run.Contains(notice, "(75,314 points, 344 road surfaces)", "the order it was measured on, and its own count there");
            run.Contains(notice, SlowStepNotice.Measured(ImportStepKind.RoadPolygons)!.Describe(), "its own row");
            run.Contains(notice, "This terrain has 74,855 points", "this terrain, beside it");
            run.Contains(notice, "no duration is predicted", "and no duration is made from them");
            run.False(notice is not null && notice.Contains("appears to", StringComparison.Ordinal), "no cause is offered");
            run.False(notice is not null && notice.Contains("land cover", StringComparison.Ordinal), "no other layer's figure");
            run.Contains(notice, "not responding", "it says what Revit is about to look like");
            run.Contains(notice, "has not crashed", "it says the freeze is not a crash");
        });

        run.Case("the two notices are not the same sentence", () =>
        {
            string? roads = SlowStepNotice.For(ImportStepKind.RoadPolygons, 80_372, 17);
            string? drape = SlowStepNotice.For(ImportStepKind.ImageryDrape, 80_372, 1);
            run.False(
                string.Equals(roads, drape, StringComparison.Ordinal),
                "each step says what IT is about to do — a copy-paste that names the road surfaces "
                + "before the drape is the likely regression");
            run.Contains(roads, "road surfaces", "the road notice names the road surfaces");
            run.Contains(drape, "drape", "the drape notice names the drape");
        });

        run.Case("no work means no warning", () =>
        {
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

            run.Equal(SlowStepNotice.MeasuredOrderVertexCount, 75_314, "the measured order's terrain");
            run.Equal(SlowStepNotice.MeasuredOrderBuildings, 1_319, "its site model's buildings");
            run.Equal(SlowStepNotice.MeasuredOrderPlantings, 19_755, "its trees and shrubs");
            run.Equal(SlowStepNotice.MeasuredOrderSubDivisions, 405, "its subdivisions"); 
        });

        run.Case("every slow notice ends on the same reassurance", () =>
        {
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
            foreach (GroundLayer layer in (GroundLayer[])[GroundLayer.RoadSurface])
            {
                string? notice = SlowStepNotice.ForTypesAtCut(layer, SubDivisionMaterialRoute.Type, drapePlanned: true, 40, 12_000);
                SlowStepNotice.TypeAtCutMeasurement measured = SlowStepNotice.MeasuredTypeAtCut(layer);

                run.True(notice is not null, $"{layer}: announced");
                run.Contains(notice, "40 subdivision(s)", $"{layer}: it names how many are coming");
                run.Contains(notice, "as it is cut", $"{layer}: it says when the type is given");
                run.Contains(notice, GroundLayerWords.For(layer).Label, $"{layer}: it names its own layer");
                run.Contains(notice, "75,314", $"{layer}: the terrain it was measured on");
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

            run.Contains(
                SlowStepNotice.ForTypesAtCut(GroundLayer.RoadSurface, SubDivisionMaterialRoute.Type, true, 344, 74_855),
                "about 1.2 s",
                "one import gives one figure, not a range");
            run.Contains(
                SlowStepNotice.ForTypesAtCut(GroundLayer.RoadSurface, SubDivisionMaterialRoute.Type, true, 5, null),
                "not known to this run",
                "an unknown count is said, never invented");
        });

        run.Case("nothing to type says nothing", () =>
        {
            run.True(
                SlowStepNotice.ForTypesAtCut(GroundLayer.RoadSurface, SubDivisionMaterialRoute.Instance, drapePlanned: true, 40, 74_855) is null,
                "a typeless cut is not typed, so nothing is announced");
            run.True(
                SlowStepNotice.ForTypesAtCut(GroundLayer.RoadSurface, SubDivisionMaterialRoute.Type, drapePlanned: false, 40, 74_855) is null,
                "no drape, no typing");
            run.True(
                SlowStepNotice.ForTypesAtCut(GroundLayer.RoadSurface, SubDivisionMaterialRoute.Refused, drapePlanned: true, 40, 74_855) is null,
                "a cut whose route could not be read, so the next one that can announces it");
            run.True(
                SlowStepNotice.ForTypesAtCut(GroundLayer.RoadSurface, SubDivisionMaterialRoute.Type, drapePlanned: true, 0, 74_855) is null,
                "a re-import that cuts nothing");
        });

        run.Case("a drape over subdivisions already on their types announces its commit", () =>
        {
            string? notice = SlowStepNotice.ForDrapeCommit(405, subDivisionsToRetype: 0, terrainRetypes: 0, 12_000);
            run.True(notice is not null, "announced");
            run.Contains(notice, "405 subdivision(s)", "it names how many");
            run.Contains(notice, "75,314", "the terrain it was measured on");
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
            run.True(SlowStepNotice.ForDrapeCommit(0, 0, 0, 74_855) is null, "no typed subdivision, no notice");

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
            run.Equal(SlowStepNotice.ForSiteModel(convertedFileExists: false), notice, "the shim says whether the converted file exists");
            run.True(SlowStepNotice.ForSiteModel(convertedFileExists: true) is null, "and the core decides that one there means nothing to say");
        });

        run.Case("the site context view, made before the subdivisions, is quiet whatever it makes", () =>
        {
            run.False(SlowStepNotice.IsAnnounced(ImportStepKind.SiteContextView), "it is measured under the line");
            run.True(SlowStepNotice.For(ImportStepKind.SiteContextView, null, 2) is null, "making both is not announced");
            run.True(
                SlowStepNotice.ForSiteContextView(ContextDecision(NamedElementAction.Create, NamedElementAction.Create)) is null,
                "both made: nothing");
            run.True(
                SlowStepNotice.ForSiteContextView(ContextDecision(NamedElementAction.Reuse, NamedElementAction.Create)) is null,
                "only the filter made: nothing");
            run.True(
                SlowStepNotice.ForSiteContextView(ContextDecision(NamedElementAction.Create, NamedElementAction.Reuse)) is null,
                "only the view made: nothing");
            run.True(
                SlowStepNotice.ForSiteContextView(ContextDecision(NamedElementAction.Reuse, NamedElementAction.Reuse)) is null,
                "both reused: nothing");
            run.True(
                SlowStepNotice.ForSiteContextView(ContextDecision(NamedElementAction.Refuse, NamedElementAction.Refuse)) is null,
                "both refused: nothing");
        });

        run.Case("the terrain announces itself when it is built for the drape, as the default import builds it", () =>
        {
            string? notice = SlowStepNotice.For(ImportStepKind.ToposurfaceFromSurfaceTin, 60_000, 1);
            run.Contains(notice, "Next: the terrain", "it names the step");
            run.Contains(notice, "(75,314 points)", "the order it was measured on");
            run.Contains(
                notice,
                "about 26 to 42 seconds in Revit 2025, 57 to 58 seconds in Revit 2026 and 60 to 79 seconds in Revit 2027",
                "what it measured at in full imports");
            run.Contains(notice, "5 to 7 seconds", "and without the drape");
            run.Contains(notice, "This terrain has 60,000 points", "this terrain");
            run.Contains(notice, "one commit", "it says where the wait is");
            run.Equal(
                SlowStepNotice.For(ImportStepKind.ToposurfaceFromPointsFile, 60_000, 1),
                notice,
                "the points path says the same as the TIN path");
            run.True(SlowStepNotice.For(ImportStepKind.ToposurfaceFromSurfaceTin, 60_000, 0) is null, "not built for the drape: unannounced");

            run.Equal(
                SlowStepNotice.ForTerrain(ImportStepKind.ToposurfaceFromSurfaceTin, onImageryType: true, 60_000),
                notice,
                "on the imagery type: the notice");
            run.True(
                SlowStepNotice.ForTerrain(ImportStepKind.ToposurfaceFromSurfaceTin, onImageryType: false, 60_000) is null,
                "fallen back to the default type, 5 to 7 s of work: nothing");
        });

        run.Case("the context buildings announce the site model's opening, and then count", () =>
        {
            string? notice = SlowStepNotice.For(ImportStepKind.ContextBuildings, null, 1_000);
            run.Contains(notice, "Next: the context buildings — 1,000", "it names the step and how many");
            run.Contains(
                notice,
                "about 34 to 57 seconds in Revit 2025, 32 to 41 seconds in Revit 2026 and 36 to 59 seconds in Revit 2027",
                "what it measured at");
            run.Contains(notice, "(1,319 buildings)", "the order it was measured on");
            run.False(notice is not null && notice.Contains("834", StringComparison.Ordinal), "not another site model's count");
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
            run.Contains(notice, "(19,755 trees and shrubs)", "the order it was measured on");
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
            run.Equal(SlowStepNotice.ForHazardLayer(ImportStepKind.FloodZones, createsPlan: true, 65), notice, "drawn on a new plan: the notice");
            run.True(
                SlowStepNotice.ForHazardLayer(ImportStepKind.FloodZones, createsPlan: false, 65) is null,
                "drawn onto a plan that is already there: not the work that was measured");
            run.True(SlowStepNotice.ForHazardLayer(ImportStepKind.SteepGround, createsPlan: true, 32) is null, "steep ground stays quiet");
        });

        run.Case("the land plans were measured, at seconds, and are quiet", () =>
        {
            run.True(SlowStepNotice.Measured(ImportStepKind.LandUse) is not null, "the land use has its measurement");
            run.True(SlowStepNotice.Measured(ImportStepKind.LandCover) is not null, "and so does the land cover");
            run.True(SlowStepNotice.For(ImportStepKind.LandUse, null, 76) is null, "76 land-use regions are drawn in under two seconds, unannounced");
            run.True(SlowStepNotice.For(ImportStepKind.LandCover, null, 18) is null, "the land cover too");
        });

        run.Case("smooth shading after the last step announces itself when there are subdivisions to shade", () =>
        {
            string? notice = SlowStepNotice.ForSmoothShading(405, 60_000);
            run.Contains(notice, "Next: smooth shading", "it names the work");
            run.Contains(notice, "(75,314 points)", "the order it was measured on");
            run.Contains(notice, "405 subdivision(s)", "and how many it shades");
            run.Contains(
                notice,
                "about 4 seconds in Revit 2025, 111 seconds in Revit 2026 and 98 seconds in Revit 2027",
                "what it measured at with 405");
            run.Contains(notice, "about 2 seconds in Revit 2025 and 14 seconds in Revit 2027", "and with 61");
            run.Contains(notice, "one commit", "it says where the wait is");
            run.True(SlowStepNotice.ForSmoothShading(0, 60_000) is null, "a terrain with no subdivisions is shaded in about a second, unannounced");
        });

        run.Case("a step is announced exactly when it was measured at 30 s or more in some version", () =>
        {
            run.Within(SlowStepNotice.AnnouncedFromSeconds, 30, 0, "the threshold");
            foreach (ImportStepKind kind in Enum.GetValues<ImportStepKind>())
            {
                bool slow = Array.IndexOf(MeasuredSlow, kind) >= 0;
                run.Equal(SlowStepNotice.IsAnnounced(kind), slow, $"{kind} is classified by its measurement");
                run.Equal(SlowStepNotice.For(kind, 74_855, 5) is not null, slow, $"{kind} has a notice exactly when it is slow");
            }
        });

        run.Case("the drape over subdivisions that take the photograph on themselves announces itself", () =>
        {
            string? notice = SlowStepNotice.ForDrapeOnInstances(405, 60_000);
            run.Contains(notice, "Next: the imagery drape", "it names the step");
            run.Contains(notice, "405 subdivision(s)", "and how many");
            run.Contains(notice, "about 26 to 62 seconds in Revit 2025", "what it measured at");
            run.Contains(notice, "(75,314 points, 405 subdivisions)", "the order it was measured on");
            run.Contains(notice, "This terrain has 60,000 points", "this terrain");
            run.Contains(notice, "has not crashed", "it says the freeze is not a crash");
            run.True(SlowStepNotice.ForDrapeOnInstances(0, 60_000) is null, "no such subdivision: nothing");
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
            run.Contains(SlowStepNotice.For(ImportStepKind.RoadPolygons, 80_372, 4), "Next: the road surfaces — 4", "road surfaces");
        });

        run.Case("no polygon layer promises another step's speed or its place in the order", () =>
        {
            foreach (ImportStepKind kind in (ImportStepKind[])
                [ImportStepKind.RoadPolygons])
            {
                string? notice = SlowStepNotice.For(kind, 80_372, 2);
                foreach (string claim in (string[])["as slow as", "slowest", "the layer before", "the first layer", "site boundaries were"])
                {
                    run.False(
                        notice is not null && notice.Contains(claim, StringComparison.Ordinal),
                        $"{kind} does not say \"{claim}\"");
                }

                run.Contains(notice, "75,314", $"{kind} quotes the order its row was measured on");
                run.Contains(notice, "80,372", $"{kind} still names this terrain");
            }
        });

        return run.Report("slow step notice");
    }
}
