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
/// ten-minute freeze arrived with no warning and read as a crash. So it is announced instead. The
/// checklist has since started the three slowest subdivision layers unticked, on a whole-import
/// measurement of every box (<see cref="MeasuredWhenTicked"/>); the planner still takes no toggle of
/// its own, and what is ticked is still built at the cost said here.
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
    /// <remarks>revit/CLAUDE.md's bullet on typed subdivisions records the run.</remarks>
    public const int MeasuredRetypePointCount = 74_852;

    /// <summary>How many subdivisions that measurement retyped, one <c>ChangeTypeId</c> each.</summary>
    /// <remarks>revit/CLAUDE.md's bullet on typed subdivisions records the run.</remarks>
    public const int MeasuredRetypeSubDivisions = 33;

    /// <summary>Rounded seconds those retypes took in a real Revit 2027 import, commit included.</summary>
    /// <remarks>revit/CLAUDE.md's bullet on typed subdivisions records the run.</remarks>
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
    /// subdivision, in Revit 2027 on <see cref="MeasuredSubDivisionTerrainPointCount"/> points.
    /// </summary>
    /// <param name="LowSeconds">The fastest import's seconds a subdivision.</param>
    /// <param name="HighSeconds">The slowest import's, equal to <paramref name="LowSeconds"/> after one import.</param>
    /// <param name="SubDivisions">How many subdivisions each import cut.</param>
    /// <param name="Imports">How many imports it was measured over.</param>
    public readonly record struct TypeAtCutMeasurement(double LowSeconds, double HighSeconds, int SubDivisions, int Imports);

    /// <summary>What typing <paramref name="layer"/>'s cuts measured.</summary>
    /// <remarks>revit/CLAUDE.md's bullet on typed subdivisions records the run.</remarks>
    public static TypeAtCutMeasurement MeasuredTypeAtCut(GroundLayer layer) => layer switch
    {
        GroundLayer.LandCover => new(3.3, 5.0, 19, 4),
        GroundLayer.LandUse => new(1.2, 1.6, 40, 4),
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
        if (newSubDivisions <= 0 || !SubDivisionMaterial.TakesTypeAtCut(cut, drapePlanned))
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
            MeasuredSubDivisionTerrainPointCount,
            perSubDivision,
            GroundLayerWords.For(layer).Label,
            measured.Imports,
            measured.SubDivisions,
            ThisTerrain(terrainPointCount));
    }

    /// <summary>How many subdivisions <see cref="MeasuredDrapeCommitSeconds"/> was measured with.</summary>
    /// <remarks>revit/CLAUDE.md's bullet on typed subdivisions records the run.</remarks>
    public const int MeasuredDrapeCommitSubDivisions = 405;

    /// <summary>
    /// Rounded seconds the drape's commit took in Revit 2027 with that many subdivisions already on
    /// their types, on <see cref="MeasuredSubDivisionTerrainPointCount"/> points.
    /// </summary>
    /// <remarks>revit/CLAUDE.md's bullet on typed subdivisions records the run.</remarks>
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
            MeasuredSubDivisionTerrainPointCount,
            MeasuredDrapeCommitSeconds,
            MeasuredDrapeCommitSubDivisions,
            ThisTerrain(terrainPointCount));
    }

    /// <summary>This terrain's point count as a sentence, or the admission that it is not known.</summary>
    private static string ThisTerrain(int? terrainPointCount)
        => terrainPointCount is { } count
            ? string.Format(CultureInfo.InvariantCulture, "This terrain has {0:N0} points", count)
            : "This terrain's point count is not known to this run — it was built by an earlier import";

    /// <summary>What a slow checklist box was measured to cost in one Revit version, and what kind of figure it is.</summary>
    /// <param name="Seconds">The measured seconds.</param>
    /// <param name="WholeImport">
    /// <c>true</c> when the figure is what leaving the box out saved a full import with every other box
    /// ticked; <c>false</c> when it is the box's own step, because that saving was smaller than the
    /// spread between two full imports and could not be told from it.
    /// </param>
    public readonly record struct TickedBoxMeasurement(int Seconds, bool WholeImport);

    /// <summary>The Revit versions <see cref="MeasuredWhenTicked"/> has figures for, oldest first.</summary>
    public static IReadOnlyList<int> TickedBoxVersions { get; } = [2025, 2026, 2027];

    /// <summary>
    /// What ticking <paramref name="layer"/>'s box was measured to cost in Revit
    /// <paramref name="revitVersion"/>, or <c>null</c> for a box that is not slow, or a version not timed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The boxes listed here are the ones that start unticked for their time
    /// (<see cref="ImportLayers.OnByDefault"/>), so this table decides both which boxes warn and which
    /// start unticked, and the two cannot drift apart.
    /// </para>
    /// <para>
    /// Order <c>4276ef78</c> (405 subdivisions, 344 of them road surfaces), the real importer with no
    /// window, one machine: Revit 2025.4 on 2026-09-30, and Revit 2026.5 and 2027.2 on 2026-09-30 and
    /// 2026-10-01, after the subdivisions took their drape types as they were cut. Each version ran two
    /// full imports with every box ticked, first and last, and one import with only one box unticked.
    /// A whole-import figure is the mean of the two full imports minus that one, so it counts the later
    /// steps the box makes dearer: the land cover's is four to ten times its own step, because the road
    /// surfaces and the land use cut after it took far longer with it. The two full imports differed by
    /// about nine minutes in 2025 and 2026 and eleven in 2027. Leaving out the land use saved 530 s in 2026 and
    /// 520 s in 2027, inside that spread, so those two quote its own step, the mean over the two full
    /// imports.
    /// </para>
    /// <para>
    /// It picks which measurement is quoted and decides nothing in Revit, so reading the version here is
    /// not the branch on the version number <c>revit/CLAUDE.md</c> forbids.
    /// </para>
    /// </remarks>
    public static TickedBoxMeasurement? MeasuredWhenTicked(ImportLayer layer, int revitVersion) => (layer, revitVersion) switch
    {
        (ImportLayer.LandUseSubdivisions, 2025) => new(712, WholeImport: true),
        (ImportLayer.LandUseSubdivisions, 2026) => new(167, WholeImport: false),
        (ImportLayer.LandUseSubdivisions, 2027) => new(171, WholeImport: false),
        (ImportLayer.LandCoverSubdivisions, 2025) => new(1_522, WholeImport: true),
        (ImportLayer.LandCoverSubdivisions, 2026) => new(1_170, WholeImport: true),
        (ImportLayer.LandCoverSubdivisions, 2027) => new(1_164, WholeImport: true),
        (ImportLayer.RoadSubdivisions, 2025) => new(1_169, WholeImport: true),
        (ImportLayer.RoadSubdivisions, 2026) => new(1_888, WholeImport: true),
        (ImportLayer.RoadSubdivisions, 2027) => new(1_900, WholeImport: true),
        _ => null,
    };

    /// <summary>Whether <paramref name="layer"/>'s box is slow enough to start unticked and warn when ticked.</summary>
    public static bool IsSlowBox(ImportLayer layer)
        => TickedBoxVersions.Any(version => MeasuredWhenTicked(layer, version) is not null);

    /// <summary>
    /// What the checklist says while <paramref name="layer"/>'s box is ticked, or <c>null</c> for a box
    /// that is not slow.
    /// </summary>
    /// <param name="layer">The ticked box.</param>
    /// <param name="revitVersionNumber">
    /// <c>Application.VersionNumber</c>, the release year as a string. A version that was not timed, or
    /// none, hears the newest measurement and is told this Revit was not timed.
    /// </param>
    /// <remarks>
    /// The figure is rounded to minutes and said with what it was measured with, because a curator who
    /// ticks one box onto the defaults is not the import that was measured: with every other box
    /// ticked, the land cover made the road surfaces after it several times dearer.
    /// </remarks>
    public static string? ForTickedBox(ImportLayer layer, string? revitVersionNumber)
    {
        if (!IsSlowBox(layer))
        {
            return null;
        }

        bool timed = int.TryParse(revitVersionNumber, NumberStyles.None, CultureInfo.InvariantCulture, out int version)
            && MeasuredWhenTicked(layer, version) is not null;
        int quoted = timed ? version : TickedBoxVersions.Last(candidate => MeasuredWhenTicked(layer, candidate) is not null);
        TickedBoxMeasurement measured = MeasuredWhenTicked(layer, quoted)!.Value;
        double minutes = Math.Round(measured.Seconds / 60.0, MidpointRounding.AwayFromZero);

        string figure = measured.WholeImport
            ? string.Format(
                CultureInfo.InvariantCulture,
                "with every other box ticked, it added about {0:N0} minutes to the import in Revit {1}{2}",
                minutes,
                quoted,
                layer == ImportLayer.LandCoverSubdivisions ? ", most of it by making the subdivisions cut after it take longer" : string.Empty)
            : string.Format(CultureInfo.InvariantCulture, "its own step took about {0:N0} minutes in Revit {1}", minutes, quoted);

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}: on the one order this has been measured on, {1}.{2}",
            WindowLabels.LayerName(layer),
            figure,
            timed ? string.Empty : " This Revit has not been timed.");
    }
}
