using System.Globalization;

namespace MantlePlace.Core;

public static class SlowStepNotice
{
    public const int MeasuredPointCount = 80_372;

    public const int MeasuredImageryDrapeMinutes = 7;

    public const double AnnouncedFromSeconds = 30;

    public readonly record struct SecondsRange(double Low, double High);

    public static IReadOnlyList<int> MeasuredVersions { get; } = [2025, 2026, 2027];

    private static T? ForVersion<T>(int version, T? revit2025, T? revit2026, T? revit2027)
        where T : struct
    {
        int index = MeasuredVersions.ToList().IndexOf(version);
        return index < 0 ? null : new[] { revit2025, revit2026, revit2027 }[index];
    }

    public sealed record StepMeasurement(SecondsRange? Revit2025, SecondsRange? Revit2026, SecondsRange? Revit2027)
    {
        public ImportSaving? Saves { get; init; }

        public UnitCost? PerUnit { get; init; }

        public UnitCost? PerCatalogCount { get; init; }

        public UnitCost? CostPer(CostUnit unit) => new[] { PerUnit, PerCatalogCount }.FirstOrDefault(cost => cost?.Unit == unit);

        public SecondsRange? In(int version) => ForVersion(version, Revit2025, Revit2026, Revit2027);

        public double Slowest => MeasuredVersions
            .Select(In)
            .Where(range => range is not null)
            .Select(range => range!.Value.High)
            .DefaultIfEmpty(0)
            .Max();

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

    public sealed record UnitCost(CostUnit Unit, SecondsRange? Revit2025, SecondsRange? Revit2026, SecondsRange? Revit2027)
    {
        public bool IsFloor { get; init; }

        public SecondsRange? In(int version) => ForVersion(version, Revit2025, Revit2026, Revit2027);
    }

    public sealed record LevelEstimate(SecondsRange Seconds, int Version, bool Timed)
    {
        public bool IsFloor { get; init; }

        public string Approximately => IsFloor ? "at least" : "about";
    }

    public static LevelEstimate? EstimateLevel(ImportStepKind kind, CostDriver? cost, string? revitVersionNumber)
    {
        if (cost is not { Unit: not CostUnit.Unknown } || Measured(kind)?.CostPer(cost.Unit) is not { } perUnit)
        {
            return null;
        }

        if (QuotedVersion(revitVersionNumber, year => perUnit.In(year) is not null) is not (int quoted, bool timed))
        {
            return null;
        }

        SecondsRange each = perUnit.In(quoted)!.Value;
        return new LevelEstimate(new SecondsRange(cost.Count * each.Low, cost.Count * each.High), quoted, timed) { IsFloor = perUnit.IsFloor };
    }

    public const double BudgetSeconds = 300;

    public static bool BreaksBudget(LevelEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        return estimate.Seconds.High > BudgetSeconds;
    }

    public static CostDriver? CostOfCatalogCount(ImportStepKind kind, long? count)
        => count is { } stated && Measured(kind) is { } measured && (measured.PerCatalogCount ?? measured.PerUnit) is { } perUnit
            ? new CostDriver(perUnit.Unit, perUnit.Unit.ToString().ToLowerInvariant(), stated)
            : null;

    public static StepMeasurement? Measured(ImportStepKind kind) => kind switch
    {
        ImportStepKind.ToposurfaceFromPointsFile or ImportStepKind.ToposurfaceFromSurfaceTin
            => new(new(25.9, 41.9), new(57.3, 58.2), new(59.6, 79.1)),
        ImportStepKind.PublishedContours => new(new(2.1, 3.1), new(2.2, 2.2), new(2.2, 3.3)),
        ImportStepKind.ContextBuildings => new(new(33.6, 57.4), new(32.4, 41.3), new(35.9, 59.2))
        {
            PerUnit = new(CostUnit.Elements, new(0.0255, 0.0436), new(0.0245, 0.289), new(0.0211, 0.0452)),
        },
        ImportStepKind.LinkSiteIfc => new(new(41.1, 71.2), new(39.3, 39.5), new(43.8, 44.6)),
        ImportStepKind.SetSharedCoordinates or ImportStepKind.SetSiteLocation or ImportStepKind.RoadCentrelines
            => new(new(1.6, 3.1), new(1.9, 1.9), new(1.6, 2.8)),
        ImportStepKind.Water => new(new(2.1, 2.8), new(5.7, 11.4), new(7.7, 11.1)),
        ImportStepKind.RoadPolygons => new(new(870, 1_112), new(1_318.6, 1_754.6), new(1_337.0, 1_781.7))
        {
            PerUnit = new(CostUnit.Cuts, new(0.65, 1.67), new(1.90, 2.49), new(1.85, 2.46)),
            PerCatalogCount = new(CostUnit.RoadSurfaces, new(83.3, 83.3), new(103.5, 103.5), new(37.3, 37.3)) { IsFloor = true },
        },
        ImportStepKind.Vegetation => new(new(206, 415), new(266, 275), new(231, 338))
        {
            PerUnit = new(CostUnit.Elements, new(0.00328, 0.00647), new(0.00334, 0.00565), new(0.00332, 0.00580)),
        },
        ImportStepKind.AttributionAndProvenance => new(new(0.1, 0.6), new(0.1, 0.1), new(0.1, 0.2)),
        ImportStepKind.SiteContextView => new(new(0.5, 0.6), new(0.8, 1.0), new(0.8, 1.0)),
        ImportStepKind.ImageryDrape => new(new(25.6, 61.6), null, null),
        ImportStepKind.FloodZones => new(new(32.9, 32.9), new(31.7, 31.7), new(43.5, 43.5)),
        ImportStepKind.SteepGround => new(new(26.4, 26.8), new(25.2, 26.3), new(27.3, 27.5)),
        ImportStepKind.LandUse => new(new(1.5, 2.1), new(1.6, 2.0), new(1.5, 2.2)),
        ImportStepKind.LandCover => new(new(0.3, 1.2), new(0.6, 1.2), new(0.3, 1.3)),

        _ => null,
    };

    public static bool IsAnnounced(ImportStepKind kind) => Measured(kind) is { } measured && measured.Slowest >= AnnouncedFromSeconds;

    public static string? For(ImportStepKind kind, int? terrainPointCount, int plannedWorkItems)
    {
        if (plannedWorkItems <= 0 || !IsAnnounced(kind) || Measured(kind) is not { } measured)
        {
            return null;
        }

        string count = plannedWorkItems.ToString("N0", CultureInfo.InvariantCulture);
        return kind switch
        {
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

            _ => string.Format(
                CultureInfo.InvariantCulture,
                "Next: {0}. On the one order this has been measured on, this step took {1}. {2}",
                WindowLabels.StepName(kind),
                measured.Describe(),
                InsideRevitsOwnCalls),
        };
    }

    public static readonly StepMeasurement MeasuredSmoothShadingWith405 = new(new(3.8, 3.8), new(110.7, 110.7), new(98.2, 98.2));

    public static readonly StepMeasurement MeasuredSmoothShadingWith61 = new(new(1.5, 1.5), null, new(14.1, 14.1));

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

    public static string? ForTerrain(ImportStepKind kind, bool onImageryType, int pointCount)
        => onImageryType ? For(kind, pointCount, 1) : null;

    public static string? ForSiteModel(bool convertedFileExists)
        => convertedFileExists ? null : For(ImportStepKind.LinkSiteIfc, null, 1);

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

        return making is null || !IsAnnounced(ImportStepKind.SiteContextView) || Measured(ImportStepKind.SiteContextView) is not { } measured
            ? null
            : SiteContextViewNotice(making, measured);
    }

    public static string? ForHazardLayer(ImportStepKind kind, bool createsPlan, int regions)
        => createsPlan ? For(kind, null, regions) : null;

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

    private static string SiteContextViewNotice(string making, StepMeasurement measured)
        => string.Format(
            CultureInfo.InvariantCulture,
            "Next: the site context view, making {0}: the view is a 3D view, and the filter finds what an import "
            + "stamped across every model category. On the bundles this has been measured on, making both took "
            + "{1}. {2}",
            making,
            measured.Describe(),
            InsideRevitsOwnCalls);

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

    private const string InsideOneCommit =
        "That cost is inside one commit, and a commit cannot report part of itself or be interrupted: "
        + "the import window names this step and keeps its clock running, but cannot show how far the "
        + "commit has got. Revit will report \"not responding\" until it finishes, and Cancel takes "
        + "effect when it finishes. It has not crashed; leave it alone.";

    public const int MeasuredOrderVertexCount = 75_314;

    public const int MeasuredOrderBuildings = 1_319;

    public const int MeasuredOrderPlantings = 19_755;

    public const int MeasuredOrderSubDivisions = 405;

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

    public const int MeasuredRetypePointCount = 74_852;

    public const int MeasuredRetypeSubDivisions = 33;

    public const int MeasuredRetypeSeconds = 190;

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

    public readonly record struct TypeAtCutMeasurement(double LowSeconds, double HighSeconds, int SubDivisions, int Imports);

    public static TypeAtCutMeasurement MeasuredTypeAtCut(GroundLayer layer) => layer switch
    {
        GroundLayer.Water => new(1.0, 1.3, 2, 4),
        GroundLayer.RoadSurface => new(1.2, 1.2, 344, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, "a ground layer with no typing measurement"),
    };

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

    public const int MeasuredDrapeCommitSubDivisions = 405;

    public const int MeasuredDrapeCommitSeconds = 65;

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

    private const string InsideRevitsOwnCalls =
        "That time is inside Revit's own calls, which cannot report part of themselves: the import window "
        + "names this step and keeps its clock running. Revit will report \"not responding\" until they "
        + "finish, and Cancel takes effect when the step finishes. It has not crashed; leave it alone.";

    private static string ThisTerrain(int? terrainPointCount)
        => terrainPointCount is { } count
            ? string.Format(CultureInfo.InvariantCulture, "This terrain has {0:N0} points", count)
            : "This terrain's point count is not known to this run — it was built by an earlier import";

    public readonly record struct ImportSaving(double? Revit2025, double? Revit2026, double? Revit2027)
    {
        public double? In(int version) => ForVersion(version, Revit2025, Revit2026, Revit2027);
    }

    private static StepMeasurement? SlowBoxRow(ImportLayer layer)
        => Enum.GetValues<ImportStepKind>()
            .Where(kind => ImportLayers.Of(kind) == layer)
            .Select(Measured)
            .FirstOrDefault(measured => measured?.Saves is not null);

    public static bool IsSlowBox(ImportLayer layer) => SlowBoxRow(layer) is not null;

    public static string? ForTickedBox(ImportLayer layer, string? revitVersionNumber)
    {
        if (SlowBoxRow(layer) is not { Saves: { } saves } measured)
        {
            return null;
        }

        if (QuotedVersion(revitVersionNumber, version => saves.In(version) is not null || measured.In(version) is not null)
            is not (int quoted, bool timed))
        {
            return null;
        }

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

    private static string Minutes(double seconds)
        => Math.Round(seconds / 60.0, MidpointRounding.AwayFromZero).ToString("N0", CultureInfo.InvariantCulture);

    private static (int Quoted, bool Timed)? QuotedVersion(string? revitVersionNumber, Func<int, bool> wasTimed)
    {
        if (int.TryParse(revitVersionNumber, NumberStyles.None, CultureInfo.InvariantCulture, out int version) && wasTimed(version))
        {
            return (version, true);
        }

        return MeasuredVersions.Any(wasTimed) ? (MeasuredVersions.Last(wasTimed), false) : null;
    }
}
