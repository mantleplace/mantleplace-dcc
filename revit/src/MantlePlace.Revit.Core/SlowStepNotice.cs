using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>
/// What to tell a curator <em>before</em> an import step that will keep Revit busy for a long time.
/// Pure.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>A step measured at <see cref="AnnouncedFromSeconds"/> or more in any version is announced,
/// and no other.</b> The rule is <c>revit/docs/api-record.md</c>'s; the figures it reads
/// are <see cref="Measured"/>, and which steps stay quiet follows from them rather than being kept
/// by hand. A step is announced only when it has the work that was measured — a terrain built for
/// the drape, an IFC still to convert, buildings still to copy — so a re-import that has none says
/// nothing.
/// </para>
/// <para>
/// The polygon layers and the imagery drape pay their cost inside a single
/// <c>Transaction.Commit()</c>, in Revit's own <c>updateElementRelations</c>. Measured on one order —
/// an 80,372-point toposolid with 17 land-use rings — the site boundaries and the drape took 610.6 s
/// and 409.1 s on a first import, 247.1 s and 249.6 s on a re-import, with Revit reporting "not
/// responding" for the whole of each. The polygon layers are now announced against their own rows
/// of <see cref="Measured"/> (<see cref="DescribePolygonLayer"/>); the drape is still announced
/// against this one.
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
/// ten-minute freeze arrived with no warning and read as a crash. So it is announced instead. The
/// checklist has since started the slowest subdivision layer unticked, on a whole-import
/// measurement of every box (<see cref="StepMeasurement.Saves"/>), and the land use and land cover
/// left the terrain for land plans (<c>docs/adr/0015-revit-subdivisions-are-for-built-surfaces.md</c>);
/// the planner still takes no toggle of its own, and what is ticked is still built at the cost said
/// here.
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
/// What the import window shows beside the line meanwhile is <c>revit/README.md</c>'s.
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
    /// A step is announced when it was measured at this many seconds or more in any Revit version
    /// (<c>revit/CLAUDE.md</c>).
    /// </summary>
    public const double AnnouncedFromSeconds = 30;

    /// <summary>Seconds a step took in one Revit version, the fastest and the slowest run measured.</summary>
    public readonly record struct SecondsRange(double Low, double High);

    /// <summary>
    /// The Revit versions every per-version figure here is for, oldest first. A figure's place in its
    /// record is its version's place in this list (<see cref="ForVersion"/>).
    /// </summary>
    public static IReadOnlyList<int> MeasuredVersions { get; } = [2025, 2026, 2027];

    /// <summary>The figure for Revit <paramref name="version"/>, or <c>null</c> for a version not in <see cref="MeasuredVersions"/>.</summary>
    private static T? ForVersion<T>(int version, T? revit2025, T? revit2026, T? revit2027)
        where T : struct
    {
        int index = MeasuredVersions.ToList().IndexOf(version);
        return index < 0 ? null : new[] { revit2025, revit2026, revit2027 }[index];
    }

    /// <summary>What one step was measured at, per Revit version; <c>null</c> where it was not measured.</summary>
    /// <remarks>
    /// The three ranges are the step's <b>own</b> seconds. <see cref="Saves"/>, where a row has it, is a
    /// different figure, and this is the one place the difference is stated: what leaving the step's
    /// checklist box out saved a whole import, which also counts every later step the box makes
    /// dearer. The two part company where a box slows what comes after it: the land cover, while it
    /// was still cut into the terrain, took minutes of its own, and leaving it out saved four to ten
    /// times that, because the subdivisions cut after it took far longer with it.
    /// </remarks>
    public sealed record StepMeasurement(SecondsRange? Revit2025, SecondsRange? Revit2026, SecondsRange? Revit2027)
    {
        /// <summary>
        /// What leaving this step's box out saved a full import, or <c>null</c> for a step whose box
        /// was not measured that way. A row with one is a slow box: it starts unticked
        /// (<see cref="ImportLayers.OnByDefault"/>) and warns when ticked (<see cref="ForTickedBox"/>).
        /// </summary>
        public ImportSaving? Saves { get; init; }

        /// <summary>The step's own seconds in Revit <paramref name="version"/>, or <c>null</c> where not measured.</summary>
        public SecondsRange? In(int version) => ForVersion(version, Revit2025, Revit2026, Revit2027);

        /// <summary>The slowest run in any version.</summary>
        public double Slowest => MeasuredVersions
            .Select(In)
            .Where(range => range is not null)
            .Select(range => range!.Value.High)
            .DefaultIfEmpty(0)
            .Max();

        /// <summary>The figures as a notice says them: <c>about 26 to 42 seconds in Revit 2025, 57 seconds in Revit 2026 and …</c>.</summary>
        public string Describe()
        {
            List<string> parts = [];
            foreach (int year in MeasuredVersions)
            {
                if (In(year) is { } seconds)
                {
                    parts.Add(string.Format(CultureInfo.InvariantCulture, "{0} seconds in Revit {1}", Seconds(seconds), year));
                }
            }

            return "about " + (parts.Count > 1 ? string.Join(", ", parts[..^1]) + " and " + parts[^1] : string.Join(string.Empty, parts));
        }

        private static string Seconds(SecondsRange range)
        {
            double low = Math.Round(range.Low, MidpointRounding.AwayFromZero);
            double high = Math.Round(range.High, MidpointRounding.AwayFromZero);
            return low.Equals(high)
                ? low.ToString("N0", CultureInfo.InvariantCulture)
                : string.Format(CultureInfo.InvariantCulture, "{0:N0} to {1:N0}", low, high);
        }
    }

    /// <summary>
    /// What <paramref name="kind"/> was measured at, or <c>null</c> for a step never measured — which
    /// is then not announced, whatever it costs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Order <c>4276ef78</c> (<see cref="MeasuredOrderVertexCount"/> points, <see cref="MeasuredOrderBuildings"/>
    /// buildings, <see cref="MeasuredOrderPlantings"/> trees and shrubs, <see cref="MeasuredOrderSubDivisions"/>
    /// subdivisions), full imports
    /// with every layer ticked, Revit 2025.4, 2026.5 and 2027.2, 2026-09-29 and -30: the window and
    /// layers runs of the measurement on issue 258's first phase, with other Revits importing on the
    /// same machine for most of them. The terrain is built on the imagery type, as the default import
    /// builds it. Exceptions are named. The site model is the step that converts the IFC: the full
    /// imports' cold runs in 2025 and 2026 with the layer-alone pairs, and the layer-alone pair in 2027,
    /// whose full imports reused a converted file. The flood zones are one hand-made bundle's 65 zones
    /// on a new hazard plan, one import each, 2026-09-24; steep ground the same bundle's 32 polygons
    /// added to that plan. Coordinates, location and centrelines were timed together.
    /// </para>
    /// <para>
    /// The drape is 2025's alone: the 2026 and 2027 runs predate its typed cuts, which took most of its
    /// cost, so they are not quoted. Each of its notices says the measurement of the route it takes —
    /// 2025's typeless subdivisions this row (<see cref="ForDrapeOnInstances"/>), 2026 and 2027's typed
    /// ones the 65 s commit measured since (<see cref="ForDrapeCommit"/>), a terrain still to retype its
    /// own (<see cref="For"/>).
    /// </para>
    /// <para>
    /// The two subdivision steps' 2026 and 2027 ranges are later: the window run and the first and last
    /// layers runs with every box ticked, Revit 2026.5 and 2027.2, 2026-09-30 and 2026-10-01, after the
    /// subdivisions took their drape types as they were cut, which moved cost into these steps. Their
    /// 2025 ranges stand, because that change does not reach 2025.
    /// </para>
    /// <para>
    /// The savings (<see cref="StepMeasurement.Saves"/>) are the layers runs of the same order: per
    /// version, the mean of the first and last full imports minus one import with only that box
    /// unticked; 2025.4 on 2026-09-30, and 2026 and 2027 from the later runs above. Those two full
    /// imports differed by about nine minutes in 2025 and 2026 and eleven in 2027.
    /// </para>
    /// <para>
    /// The land use and land cover are not from this order: what they were measured at there was the
    /// cost of cutting them into the terrain, and those rows went with the subdivisions
    /// (<c>docs/adr/0015-revit-subdivisions-are-for-built-surfaces.md</c>). Their rows are each layer
    /// drawn alone on a new land plan, one import per bundle and version, Revit 2025.4 and 2027.2,
    /// 2026-10-03, on two imperial reference bundles: one of 28 land-use and 18 land-cover polygons,
    /// one of 76 and 7. Revit 2026 was not measured.
    /// </para>
    /// </remarks>
    public static StepMeasurement? Measured(ImportStepKind kind) => kind switch
    {
        ImportStepKind.ToposurfaceFromPointsFile or ImportStepKind.ToposurfaceFromSurfaceTin
            => new(new(25.9, 41.9), new(57.3, 57.4), new(60.1, 79.1)),
        ImportStepKind.PublishedContours => new(new(2.1, 3.1), new(2.2, 2.2), new(2.2, 3.3)),
        ImportStepKind.ContextBuildings => new(new(33.6, 57.4), new(32.4, 33.7), new(35.9, 59.2)),
        ImportStepKind.LinkSiteIfc => new(new(41.1, 71.2), new(39.3, 39.5), new(43.8, 44.6)),
        ImportStepKind.SetSharedCoordinates or ImportStepKind.SetSiteLocation or ImportStepKind.RoadCentrelines
            => new(new(1.6, 3.1), new(1.9, 1.9), new(1.6, 2.8)),
        ImportStepKind.Water => new(new(2.1, 2.8), new(5.7, 11.4), new(7.7, 11.1)),
        ImportStepKind.RoadPolygons => new(new(870, 1_112), new(1_318.6, 1_754.6), new(1_337.0, 1_781.7)) { Saves = new(1_169, 1_888, 1_900) },
        ImportStepKind.Vegetation => new(new(206, 415), new(266, 275), new(231, 338)),
        ImportStepKind.AttributionAndProvenance => new(new(0.1, 0.6), new(0.1, 0.1), new(0.1, 0.2)),
        ImportStepKind.SiteContextView => new(new(1.4, 4.8), new(41.5, 42.4), new(32.7, 52.6)),
        ImportStepKind.ImageryDrape => new(new(25.6, 61.6), null, null),
        ImportStepKind.FloodZones => new(new(32.9, 32.9), new(31.7, 31.7), new(43.5, 43.5)),
        ImportStepKind.SteepGround => new(new(0.8, 0.8), new(0.8, 0.8), new(0.9, 0.9)),
        ImportStepKind.LandUse => new(new(1.5, 1.7), null, new(1.5, 1.8)),
        ImportStepKind.LandCover => new(new(0.3, 1.0), null, new(0.3, 1.0)),

        // The DXF terrain links a CAD file and has never been timed.
        _ => null,
    };

    /// <summary>Whether <paramref name="kind"/> is announced: measured at <see cref="AnnouncedFromSeconds"/> or more in some version.</summary>
    public static bool IsAnnounced(ImportStepKind kind) => Measured(kind) is { } measured && measured.Slowest >= AnnouncedFromSeconds;

    /// <summary>
    /// The line to say before <paramref name="kind"/>, or <c>null</c> when it is not announced or has
    /// no work to announce.
    /// </summary>
    /// <param name="kind">The step about to run.</param>
    /// <param name="terrainPointCount">
    /// The host toposolid's point count, or <c>null</c> when this run did not build the terrain and
    /// therefore does not know it. A count is never invented for the null case.
    /// </param>
    /// <param name="plannedWorkItems">
    /// How much of the work that was measured the step has: subdivisions to cut, buildings to copy,
    /// trees to place, zones to draw, things to create; 1 for a terrain built for the drape and for an
    /// IFC still to convert. Zero means none of it, so the step will not be slow and must not be
    /// announced as though it will.
    /// </param>
    public static string? For(ImportStepKind kind, int? terrainPointCount, int plannedWorkItems)
    {
        if (plannedWorkItems <= 0 || !IsAnnounced(kind) || Measured(kind) is not { } measured)
        {
            return null;
        }

        string count = plannedWorkItems.ToString("N0", CultureInfo.InvariantCulture);
        return kind switch
        {
            // Every polygon layer, in its own words, against one measurement. None of them names
            // another step or its place in the order: the planner has reordered them once already.
            _ when GroundCuts.LayerOf(kind) is { } layer => DescribePolygonLayer(
                "Next: the " + GroundLayerWords.For(layer).Label + " — "
                    + plannedWorkItems.ToString("N0", CultureInfo.InvariantCulture)
                    + " subdivision(s) to cut into the terrain.",
                layer,
                measured,
                terrainPointCount),

            ImportStepKind.ImageryDrape => Describe(
                "Next: the imagery drape. Retyping the terrain so it can wear the photograph costs "
                    + "minutes, and for the same reason the subdivisions do.",
                MeasuredImageryDrapeMinutes,
                terrainPointCount),

            ImportStepKind.ToposurfaceFromPointsFile or ImportStepKind.ToposurfaceFromSurfaceTin => string.Format(
                CultureInfo.InvariantCulture,
                "Next: the terrain, on the type that will wear the photograph, as it is built when the imagery "
                + "drape is chosen. On the one order this has been measured on ({0:N0} points), that took {1} "
                + "in full imports, most of it in its commit; on the default type the same terrain took 5 to 7 "
                + "seconds. {2}. {3}",
                MeasuredOrderVertexCount,
                measured.Describe(),
                ThisTerrain(terrainPointCount),
                InsideOneCommit),

            ImportStepKind.LinkSiteIfc => string.Format(
                CultureInfo.InvariantCulture,
                "Next: the site model. Revit converts the IFC into a Revit file before it can link it, once for "
                + "each order and Revit version; a later import reuses the converted file. On the one order this "
                + "has been measured on, converting and linking it took {0}. {1}",
                measured.Describe(),
                InsideRevitsOwnCalls),

            ImportStepKind.ContextBuildings => string.Format(
                CultureInfo.InvariantCulture,
                "Next: the context buildings — {0} to copy out of the site model. Revit opens the site model's "
                + "IFC first, in one call, and the import window counts the buildings in once it is open. On the "
                + "one order this has been measured on ({2:N0} buildings), the step took {1}. Revit will report "
                + "\"not responding\" while it opens the IFC, which cannot report part of itself, and Cancel "
                + "takes effect once it has, between chunks. It has not crashed; leave it alone.",
                count,
                measured.Describe(),
                MeasuredOrderBuildings),

            ImportStepKind.Vegetation => string.Format(
                CultureInfo.InvariantCulture,
                "Next: planting — {0} tree(s) and shrub(s), placed and committed {1} at a time, which the import "
                + "window counts in as each chunk commits. On the one order this has been measured on ({3:N0} "
                + "trees and shrubs), planting took {2} in full imports, most of it in Revit's commits. Revit may "
                + "report \"not responding\" during a chunk's commit, and Cancel takes effect at the next chunk, "
                + "keeping every chunk already in. It has not crashed; leave it alone.",
                count,
                ImportChunking.ElementsPerTransaction,
                measured.Describe(),
                MeasuredOrderPlantings),

            ImportStepKind.SiteContextView => SiteContextViewNotice("the view and its filter", measured),

            ImportStepKind.FloodZones => string.Format(
                CultureInfo.InvariantCulture,
                "Next: the flood zones — {0} drawn on the hazard plan. On the one bundle this has been measured "
                + "on (65 flood zones, on a new hazard plan), the step took {1}. {2}",
                count,
                measured.Describe(),
                InsideRevitsOwnCalls),

            // A step measured slow that has no words of its own still says its measurement.
            _ => string.Format(
                CultureInfo.InvariantCulture,
                "Next: {0}. On the one order this has been measured on, this step took {1}. {2}",
                WindowLabels.StepName(kind),
                measured.Describe(),
                InsideRevitsOwnCalls),
        };
    }

    /// <summary>The order's smooth shading with its 405 subdivisions, measured with the drape left out.</summary>
    /// <remarks>
    /// 3.8 s in Revit 2025.4 and 98.2 s in 2027.2, the shading commit; 110.7 s in 2026.5, the finishing
    /// work it was the whole of: one import each, 2026-09-30, every layer but the drape.
    /// </remarks>
    public static readonly StepMeasurement MeasuredSmoothShadingWith405 = new(new(3.8, 3.8), new(110.7, 110.7), new(98.2, 98.2));

    /// <summary>The same with 61 subdivisions, the road surfaces left out too: 1.5 s in 2025 and 14.1 s in 2027.</summary>
    public static readonly StepMeasurement MeasuredSmoothShadingWith61 = new(new(1.5, 1.5), null, new(14.1, 14.1));

    /// <summary>
    /// The line to say before smooth shading is committed across a terrain with subdivisions on it, or
    /// <c>null</c> when there are none — a bare terrain is shaded in about a second.
    /// </summary>
    /// <param name="subDivisions">The subdivisions on the terrain being shaded.</param>
    /// <param name="terrainPointCount">The host toposolid's point count, or <c>null</c> when this run did not build it.</param>
    /// <remarks>
    /// Said where it is committed: inside the drape, or after the last step when the drape did not
    /// settle it, where the window shows it beside its clock as the finishing work
    /// (<see cref="StagedImport.Finishing"/>). The work is announced because it was measured at 98 to
    /// 111 s in Revit 2026 and 2027 with the order's 405 subdivisions; with 61 it took 2 and 14 s, under
    /// <see cref="AnnouncedFromSeconds"/>. How the cost grows between the two has not been measured, so
    /// it is said whenever there are subdivisions, and both figures are given rather than scaled.
    /// </remarks>
    public static string? ForSmoothShading(int subDivisions, int? terrainPointCount)
    {
        if (subDivisions <= 0)
        {
            return null;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "Next: smooth shading, which Revit commits across the terrain and the {0:N0} subdivision(s) on it. "
            + "On the one order this has been measured on ({1:N0} points), with its {6:N0} subdivisions that took "
            + "{2}, and with 61 {3}. {4}. {5}",
            subDivisions,
            MeasuredOrderVertexCount,
            MeasuredSmoothShadingWith405.Describe(),
            MeasuredSmoothShadingWith61.Describe(),
            ThisTerrain(terrainPointCount),
            InsideOneCommit,
            MeasuredOrderSubDivisions);
    }

    /// <summary>The terrain step's line, from which type it is being built on.</summary>
    /// <param name="kind">The terrain step running.</param>
    /// <param name="onImageryType">Whether the terrain is being built on the type that will wear the photograph.</param>
    /// <param name="pointCount">The points it is being built from.</param>
    /// <remarks>
    /// Built on the imagery type it was measured at tens of seconds; on the default type, which it falls
    /// back to when that type cannot be made, at 5 to 7. So the shim says which type it is building on,
    /// once that is decided, and only the first is announced.
    /// </remarks>
    public static string? ForTerrain(ImportStepKind kind, bool onImageryType, int pointCount)
        => onImageryType ? For(kind, pointCount, 1) : null;

    /// <summary>The site model's line, from whether this Revit's converted file of it is already there.</summary>
    /// <remarks>Converting the IFC is the work measured; linking a file an earlier import converted takes seconds.</remarks>
    public static string? ForSiteModel(bool convertedFileExists)
        => convertedFileExists ? null : For(ImportStepKind.LinkSiteIfc, null, 1);

    /// <summary>The site context view's line, from what the step will make; <c>null</c> when it makes nothing.</summary>
    /// <remarks>
    /// The figure is for making both, and the line says so when only one is made: how the time divides
    /// between the view and the filter has not been measured.
    /// </remarks>
    public static string? ForSiteContextView(SiteContextDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        string? making = (decision.View == NamedElementAction.Create, decision.Filter == NamedElementAction.Create) switch
        {
            (true, true) => "the view and its filter",
            (true, false) => "the view",
            (false, true) => "the filter",
            _ => null,
        };

        return making is null || Measured(ImportStepKind.SiteContextView) is not { } measured
            ? null
            : SiteContextViewNotice(making, measured);
    }

    /// <summary>A hazard layer's line, from whether it makes the hazard plan and how many regions it draws.</summary>
    /// <remarks>
    /// The flood zones were measured making a new plan; drawn onto a plan that is already there, they
    /// are not the work that was measured, and steep ground was measured in under a second.
    /// </remarks>
    public static string? ForHazardLayer(ImportStepKind kind, bool createsPlan, int regions)
        => createsPlan ? For(kind, null, regions) : null;

    /// <summary>
    /// The drape's line for subdivisions that take the photograph through their own Material, one at a
    /// time — Revit 2025's typeless ones; <c>null</c> when there are none.
    /// </summary>
    /// <param name="subDivisionsOnInstance">Subdivisions the drape will give the photograph through their own Material.</param>
    /// <param name="terrainPointCount">The host toposolid's point count, or <c>null</c> when this run did not build it.</param>
    /// <remarks>
    /// The drape is measured over <see cref="AnnouncedFromSeconds"/> in Revit 2025, and that is the
    /// route 2025 takes: its subdivisions are typeless, so neither of the typed notices
    /// (<see cref="ForSubDivisionRetypes"/>, <see cref="ForDrapeCommit"/>) is ever said there.
    /// </remarks>
    public static string? ForDrapeOnInstances(int subDivisionsOnInstance, int? terrainPointCount)
    {
        if (subDivisionsOnInstance <= 0 || Measured(ImportStepKind.ImageryDrape) is not { } measured)
        {
            return null;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "Next: the imagery drape — the photograph written into the material of each of {0:N0} subdivision(s), "
            + "one at a time, which the import window counts, then committed in one transaction. On the one order "
            + "this has been measured on ({1:N0} points, {2:N0} subdivisions), the drape took {3}, most of it "
            + "the subdivisions one at a time. {4}. Revit will report \"not responding\" until the step finishes, "
            + "and Cancel takes effect when it finishes. It has not crashed; leave it alone.",
            subDivisionsOnInstance,
            MeasuredOrderVertexCount,
            MeasuredOrderSubDivisions,
            measured.Describe(),
            ThisTerrain(terrainPointCount));
    }

    /// <summary>The site context view's line, for whichever of the view and the filter is made.</summary>
    private static string SiteContextViewNotice(string making, StepMeasurement measured)
        => string.Format(
            CultureInfo.InvariantCulture,
            "Next: the site context view, making {0}: the view is a 3D view, and the filter finds what an import "
            + "stamped across every model category. On the one order this has been measured on, making both took "
            + "{1}. {2}",
            making,
            measured.Describe(),
            InsideRevitsOwnCalls);

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
        + "the import window names this step and keeps its clock running, but cannot show how far the "
        + "commit has got. Revit will report \"not responding\" until it finishes, and Cancel takes "
        + "effect when it finishes. It has not crashed; leave it alone.";

    /// <summary>
    /// The terrain of order <c>4276ef78</c>, the one every whole-import figure here was measured on:
    /// Phase 1, the on/off matrix, and the typed cuts. Its TIN has this many vertices.
    /// </summary>
    public const int MeasuredOrderVertexCount = 75_314;

    /// <summary>The buildings that order's site model gives the context buildings step.</summary>
    public const int MeasuredOrderBuildings = 1_319;

    /// <summary>The trees and shrubs that order plants.</summary>
    public const int MeasuredOrderPlantings = 19_755;

    /// <summary>
    /// The subdivisions that order cut when it was measured: 19 land cover, 40 land use, 2 water and
    /// 344 road surfaces. The land layers are drawn on land plans now, so an import of it cuts 346.
    /// </summary>
    public const int MeasuredOrderSubDivisions = 405;

    /// <summary>
    /// The body for a polygon layer: where the time goes, the layer's own measurement on the one order,
    /// this terrain, and the reassurance.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>This has been wrong twice, both times by explaining one run.</b> It first promised every
    /// later layer the site boundaries' speed, and the land cover then took fifty times as long. It
    /// then said the cost grows with the subdivisions already on the terrain, and a probe of one
    /// whole-order land-cover ring took nine minutes alone in Revit 2025 and twenty cut after 57
    /// others. So it no longer explains: it quotes the layer's own row of <see cref="Measured"/>, with
    /// how many of that layer's subdivisions the order cut, and says nothing about how the cost grows.
    /// </para>
    /// <para>
    /// The same rule as <see cref="Describe"/>: the measurements and this terrain are stated side by
    /// side, and no duration is predicted from them. It names no other step and no place in the
    /// order, because the order is the planner's and has changed more than once.
    /// </para>
    /// </remarks>
    private static string DescribePolygonLayer(string opening, GroundLayer layer, StepMeasurement measured, int? terrainPointCount)
        => string.Format(
            CultureInfo.InvariantCulture,
            "{0} Revit rebuilds the terrain's element relations when the transaction commits. On the one "
            + "order this has been measured on ({1:N0} points, {2:N0} {3}), in full imports with every box "
            + "ticked, this layer took {4}. How long it takes depends on the polygons it carries, and no "
            + "duration is predicted here. {5}. {6}",
            opening,
            MeasuredOrderVertexCount,
            MeasuredTypeAtCut(layer).SubDivisions,
            GroundLayerWords.For(layer).Label,
            measured.Describe(),
            ThisTerrain(terrainPointCount),
            InsideOneCommit);

    /// <summary>The point count of the terrain <see cref="ForSubDivisionRetypes"/> was measured on.</summary>
    /// <remarks>revit/docs/api-record.md's bullet on typed subdivisions records the run.</remarks>
    public const int MeasuredRetypePointCount = 74_852;

    /// <summary>How many subdivisions that measurement retyped, one <c>ChangeTypeId</c> each.</summary>
    /// <remarks>revit/docs/api-record.md's bullet on typed subdivisions records the run.</remarks>
    public const int MeasuredRetypeSubDivisions = 33;

    /// <summary>Rounded seconds those retypes took in a real Revit 2027 import, commit included.</summary>
    /// <remarks>revit/docs/api-record.md's bullet on typed subdivisions records the run.</remarks>
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

    /// <summary>
    /// What giving one layer's subdivisions their drape types as they were cut measured, per
    /// subdivision, in Revit 2027 on <see cref="MeasuredOrderVertexCount"/> points.
    /// </summary>
    /// <param name="LowSeconds">The fastest import's seconds a subdivision.</param>
    /// <param name="HighSeconds">The slowest import's, equal to <paramref name="LowSeconds"/> after one import.</param>
    /// <param name="SubDivisions">How many subdivisions each import cut.</param>
    /// <param name="Imports">How many imports it was measured over.</param>
    public readonly record struct TypeAtCutMeasurement(double LowSeconds, double HighSeconds, int SubDivisions, int Imports);

    /// <summary>What typing <paramref name="layer"/>'s cuts measured.</summary>
    /// <remarks>revit/docs/api-record.md's bullet on typed subdivisions records the run.</remarks>
    public static TypeAtCutMeasurement MeasuredTypeAtCut(GroundLayer layer) => layer switch
    {
        GroundLayer.Water => new(1.0, 1.3, 2, 4),
        GroundLayer.RoadSurface => new(1.2, 1.2, 344, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, "a ground layer with no typing measurement"),
    };

    /// <summary>
    /// The line to say at a polygon step's first cut that takes a type, when its subdivisions are to
    /// be given their drape types as they are cut (<see cref="SubDivisionMaterial.TakesTypeAtCut"/>);
    /// <c>null</c> for a cut with nothing to type.
    /// </summary>
    /// <param name="layer">The layer being cut, whose own measurement is quoted.</param>
    /// <param name="cut">What this cut was found to accept.</param>
    /// <param name="drapePlanned">Whether the plan's drape will run (<see cref="ImportStep.DrapePlanned"/>).</param>
    /// <param name="newSubDivisions">How many subdivisions the step is cutting.</param>
    /// <param name="terrainPointCount">The host toposolid's point count, or <c>null</c> when this run did not build it.</param>
    /// <remarks>
    /// <para>
    /// The wait the drape used to announce (<see cref="ForSubDivisionRetypes"/>) moved here with the
    /// work: one <c>ChangeTypeId</c> per subdivision, seconds each, inside the slice that cuts them.
    /// Whether this Revit gives a subdivision a type is only known once one exists, so the line is
    /// said then, into the log a curator can read beside a frozen Revit, and never where there is
    /// nothing to type: Revit 2025's typeless subdivisions hear nothing, and a cut whose route could
    /// not be read leaves it to the next cut that can.
    /// </para>
    /// <para>
    /// The same rule as <see cref="Describe"/>: the measurement and this terrain are stated side by
    /// side, and no duration is predicted from them. Only this layer's figure is quoted, with what it
    /// was measured on, since two water bodies are not 344 road surfaces.
    /// </para>
    /// </remarks>
    public static string? ForTypesAtCut(
        GroundLayer layer,
        SubDivisionMaterialRoute cut,
        bool drapePlanned,
        int newSubDivisions,
        int? terrainPointCount)
    {
        if (newSubDivisions <= 0
            || !SubDivisionMaterial.TakesTypeAtCut(cut, drapePlanned)
            || !IsAnnounced(Enum.GetValues<ImportStepKind>().First(kind => GroundCuts.LayerOf(kind) == layer)))
        {
            return null;
        }

        TypeAtCutMeasurement measured = MeasuredTypeAtCut(layer);
        string perSubDivision = measured.LowSeconds.Equals(measured.HighSeconds)
            ? string.Format(CultureInfo.InvariantCulture, "about {0:0.0} s", measured.LowSeconds)
            : string.Format(CultureInfo.InvariantCulture, "{0:0.0} to {1:0.0} s", measured.LowSeconds, measured.HighSeconds);

        return string.Format(
            CultureInfo.InvariantCulture,
            "This Revit gives a subdivision a type of its own, so each of these {0:N0} subdivision(s) is "
            + "also moved onto the type that will wear the photograph as it is cut, one at a time, before "
            + "the commit; the imagery drape would otherwise do it after them. On the one terrain this has "
            + "been measured on ({1:N0} points), in Revit 2027, that took {2} a subdivision for the {3}, "
            + "over {4:N0} import(s) of {5:N0} subdivision(s) each. {6}.",
            newSubDivisions,
            MeasuredOrderVertexCount,
            perSubDivision,
            GroundLayerWords.For(layer).Label,
            measured.Imports,
            measured.SubDivisions,
            ThisTerrain(terrainPointCount));
    }

    /// <summary>How many subdivisions <see cref="MeasuredDrapeCommitSeconds"/> was measured with.</summary>
    /// <remarks>revit/docs/api-record.md's bullet on typed subdivisions records the run.</remarks>
    public const int MeasuredDrapeCommitSubDivisions = 405;

    /// <summary>
    /// Rounded seconds the drape's commit took in Revit 2027 with that many subdivisions already on
    /// their types, on <see cref="MeasuredOrderVertexCount"/> points.
    /// </summary>
    /// <remarks>revit/docs/api-record.md's bullet on typed subdivisions records the run.</remarks>
    public const int MeasuredDrapeCommitSeconds = 65;

    /// <summary>
    /// The line to say before the drape writes the photograph for subdivisions already on their own
    /// types, or <c>null</c> when there are none or another notice already describes the commit.
    /// </summary>
    /// <param name="subDivisionsOnTheirTypes">Typed subdivisions the drape will write for without a retype.</param>
    /// <param name="subDivisionsToRetype">Typed subdivisions it will retype first, as <see cref="ForSubDivisionRetypes"/> is told.</param>
    /// <param name="terrainRetypes">The terrain's retype, 0 or 1, as <see cref="For"/> is told for the drape.</param>
    /// <param name="terrainPointCount">The host toposolid's point count, or <c>null</c> when this run did not build it.</param>
    /// <remarks>
    /// Once every cut takes its type as it is cut, the drape has no retype to announce, and its one
    /// commit is still the longest wait left in it. Said exactly when neither retype notice is, which
    /// the guard asks of those notices themselves so the three cannot drift apart. Revit 2025's
    /// subdivisions are typeless, so none counts here and its drape, whose commit is seconds, stays
    /// quiet as it always has.
    /// </remarks>
    public static string? ForDrapeCommit(
        int subDivisionsOnTheirTypes,
        int subDivisionsToRetype,
        int terrainRetypes,
        int? terrainPointCount)
    {
        if (subDivisionsOnTheirTypes <= 0
            || ForSubDivisionRetypes(subDivisionsToRetype, terrainPointCount) is not null
            || For(ImportStepKind.ImageryDrape, terrainPointCount, terrainRetypes) is not null)
        {
            return null;
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "Next: the imagery drape. The photograph is written into the materials of the terrain and of "
            + "{0:N0} subdivision(s) already on their own types, and committed in one transaction. On the "
            + "one terrain this has been measured on ({1:N0} points), in Revit 2027, that commit took about "
            + "{2:N0} seconds with {3:N0} subdivisions. {4}. A commit cannot report part of itself, so "
            + "Revit will report \"not responding\" until it finishes, and Cancel takes effect when it "
            + "finishes. It has not crashed; leave it alone.",
            subDivisionsOnTheirTypes,
            MeasuredOrderVertexCount,
            MeasuredDrapeCommitSeconds,
            MeasuredDrapeCommitSubDivisions,
            ThisTerrain(terrainPointCount));
    }

    /// <summary>The reassurance for a wait inside Revit's own calls rather than one commit of ours.</summary>
    private const string InsideRevitsOwnCalls =
        "That time is inside Revit's own calls, which cannot report part of themselves: the import window "
        + "names this step and keeps its clock running. Revit will report \"not responding\" until they "
        + "finish, and Cancel takes effect when the step finishes. It has not crashed; leave it alone.";

    /// <summary>This terrain's point count as a sentence, or the admission that it is not known.</summary>
    private static string ThisTerrain(int? terrainPointCount)
        => terrainPointCount is { } count
            ? string.Format(CultureInfo.InvariantCulture, "This terrain has {0:N0} points", count)
            : "This terrain's point count is not known to this run — it was built by an earlier import";

    /// <summary>
    /// What leaving one box out saved a full import, in seconds per Revit version; <c>null</c> where it
    /// was not measured or could not be told from the spread between two full imports. What this is,
    /// against a step's own seconds, is <see cref="StepMeasurement"/>'s.
    /// </summary>
    public readonly record struct ImportSaving(double? Revit2025, double? Revit2026, double? Revit2027)
    {
        /// <summary>The saving in Revit <paramref name="version"/>, or <c>null</c>.</summary>
        public double? In(int version) => ForVersion(version, Revit2025, Revit2026, Revit2027);
    }

    /// <summary>The row of the step <paramref name="layer"/>'s box builds, when that row has a saving.</summary>
    private static StepMeasurement? SlowBoxRow(ImportLayer layer)
        => Enum.GetValues<ImportStepKind>()
            .Where(kind => ImportLayers.Of(kind) == layer)
            .Select(Measured)
            .FirstOrDefault(measured => measured?.Saves is not null);

    /// <summary>
    /// Whether <paramref name="layer"/>'s box starts unticked and warns when ticked: its step's row in
    /// <see cref="Measured"/> has a saving.
    /// </summary>
    public static bool IsSlowBox(ImportLayer layer) => SlowBoxRow(layer) is not null;

    /// <summary>
    /// What the checklist says while <paramref name="layer"/>'s box is ticked, or <c>null</c> for a box
    /// that is not slow.
    /// </summary>
    /// <param name="layer">The ticked box.</param>
    /// <param name="revitVersionNumber">
    /// <c>Application.VersionNumber</c>, the release year as a string. A version that was not timed, or
    /// none, hears the newest measurement and is told this Revit was not timed. A slow box whose row
    /// holds no figure for any version says nothing.
    /// </param>
    /// <remarks>
    /// <para>
    /// The saving where this version has one, said with what else was ticked, because a curator who
    /// ticks one box onto the defaults is not the import that was measured. Where the saving is more
    /// than twice the step's own slowest run, the line says most of it was the steps after it. Where
    /// this version has no saving, the step's own seconds, said as its own step's and with what else was
    /// ticked: alone on the defaults it may be cheaper, since the boxes before it can make it dearer.
    /// </para>
    /// <para>
    /// It picks which measurement is quoted and decides nothing in Revit, so reading the version here
    /// is not the branch on the version number <c>revit/CLAUDE.md</c> forbids.
    /// </para>
    /// </remarks>
    public static string? ForTickedBox(ImportLayer layer, string? revitVersionNumber)
    {
        if (SlowBoxRow(layer) is not { Saves: { } saves } measured)
        {
            return null;
        }

        bool Timed(int version) => saves.In(version) is not null || measured.In(version) is not null;
        bool timed = int.TryParse(revitVersionNumber, NumberStyles.None, CultureInfo.InvariantCulture, out int version)
            && Timed(version);
        if (!timed && !MeasuredVersions.Any(Timed))
        {
            return null;
        }

        int quoted = timed ? version : MeasuredVersions.Last(Timed);

        string figure;
        if (saves.In(quoted) is { } saved)
        {
            bool mostlyLater = measured.In(quoted) is { } own && saved > 2 * own.High;
            figure = string.Format(
                CultureInfo.InvariantCulture,
                "with every other box ticked, it added about {0} minutes to the import in Revit {1}{2}",
                Minutes(saved),
                quoted,
                mostlyLater ? ", most of it by making the steps after it take longer" : string.Empty);
        }
        else
        {
            SecondsRange own = measured.In(quoted)!.Value;
            string low = Minutes(own.Low);
            string high = Minutes(own.High);
            figure = string.Format(
                CultureInfo.InvariantCulture,
                "with every other box ticked, its own step took about {0} minutes in Revit {1}",
                low == high ? low : low + " to " + high,
                quoted);
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}: on the one order this has been measured on, {1}.{2}",
            WindowLabels.LayerName(layer),
            figure,
            timed ? string.Empty : " This Revit has not been timed.");
    }

    /// <summary>Seconds as whole minutes, rounded half away from zero.</summary>
    private static string Minutes(double seconds)
        => Math.Round(seconds / 60.0, MidpointRounding.AwayFromZero).ToString("N0", CultureInfo.InvariantCulture);
}
