using System.Globalization;

namespace MantlePlace.Core;

public readonly record struct TerrainRelief(double MinZ, double MaxZ, int PointCount);

public readonly record struct CandidateLevel(long Id, string Name, double Elevation);

public enum TerrainBaseStrategy
{
    NoLevelAvailable = 0,

    ExistingLevel,

    ExistingLevelWithOffset,

    DedicatedLevel,
}

public sealed class TerrainBasePlan
{
    public required TerrainBaseStrategy Strategy { get; init; }

    public long LevelId { get; init; }

    public string LevelName { get; init; } = string.Empty;

    public double LevelElevation { get; init; }

    public double HeightOffset { get; init; }

    public double RequiredClearance { get; init; }

    public double BasePlane => LevelElevation + HeightOffset;

    public string Explanation { get; init; } = string.Empty;
}

public static class TerrainBasePlanner
{
    public const string DedicatedLevelName = "Mantle Place Terrain Base";

    public static double ClearanceFor(double typeTotalThickness, double minimumLayerThickness)
    {
        double byType = double.IsFinite(typeTotalThickness) && typeTotalThickness > 0.0 ? typeTotalThickness : 0.0;
        double byHost = double.IsFinite(minimumLayerThickness) && minimumLayerThickness > 0.0
            ? 3.0 * minimumLayerThickness
            : 0.0;
        return Math.Max(byType, byHost);
    }

    public static TerrainBasePlan Decide(
        IReadOnlyList<CandidateLevel> levels,
        TerrainRelief relief,
        double typeTotalThickness,
        double minimumLayerThickness)
    {
        ArgumentNullException.ThrowIfNull(levels);

        double clearance = ClearanceFor(typeTotalThickness, minimumLayerThickness);
        double target = relief.MinZ - clearance;

        CandidateLevel? best = null;
        CandidateLevel? lowest = null;
        foreach (CandidateLevel level in levels)
        {
            if (!double.IsFinite(level.Elevation))
            {
                continue;
            }

            if (lowest is not { } currentLowest || level.Elevation < currentLowest.Elevation)
            {
                lowest = level;
            }

            if (level.Elevation <= target && (best is not { } currentBest || level.Elevation > currentBest.Elevation))
            {
                best = level;
            }
        }

        if (best is { } onIt)
        {
            return new TerrainBasePlan
            {
                Strategy = TerrainBaseStrategy.ExistingLevel,
                LevelId = onIt.Id,
                LevelName = onIt.Name,
                LevelElevation = onIt.Elevation,
                HeightOffset = 0.0,
                RequiredClearance = clearance,
                Explanation = $"Terrain base: level \"{onIt.Name}\" at {Round(onIt.Elevation)}, which "
                    + $"already clears the lowest terrain point ({Round(relief.MinZ)}) by at least "
                    + $"{Round(clearance)}.",
            };
        }

        if (lowest is { } bottom)
        {
            double offset = target - bottom.Elevation;
            return new TerrainBasePlan
            {
                Strategy = TerrainBaseStrategy.ExistingLevelWithOffset,
                LevelId = bottom.Id,
                LevelName = bottom.Name,
                LevelElevation = bottom.Elevation,
                HeightOffset = offset,
                RequiredClearance = clearance,
                Explanation = $"Terrain base: level \"{bottom.Name}\" at {Round(bottom.Elevation)} with a "
                    + $"height offset of {Round(offset)}, putting the underside at {Round(target)} — "
                    + $"below the lowest terrain point ({Round(relief.MinZ)}) by {Round(clearance)}.",
            };
        }

        return new TerrainBasePlan
        {
            Strategy = TerrainBaseStrategy.NoLevelAvailable,
            LevelElevation = target,
            RequiredClearance = clearance,
            Explanation = "This project has no level, so the terrain has nothing to sit on.",
        };
    }

    public static TerrainBasePlan Escalate(TerrainBasePlan refused, TerrainRelief relief)
    {
        ArgumentNullException.ThrowIfNull(refused);

        double target = relief.MinZ - refused.RequiredClearance;
        return new TerrainBasePlan
        {
            Strategy = TerrainBaseStrategy.DedicatedLevel,
            LevelId = 0,
            LevelName = DedicatedLevelName,
            LevelElevation = target,
            HeightOffset = 0.0,
            RequiredClearance = refused.RequiredClearance,
            Explanation = "The first base did not satisfy Revit's minimum thickness; retried on a level "
                + $"named \"{DedicatedLevelName}\" at {Round(target)}.",
        };
    }

    public static TerrainRelief ReliefOf(IReadOnlyList<SurfacePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);

        if (points.Count == 0)
        {
            return new TerrainRelief(0.0, 0.0, 0);
        }

        double min = double.PositiveInfinity;
        double max = double.NegativeInfinity;
        foreach (SurfacePoint point in points)
        {
            if (point.Z < min)
            {
                min = point.Z;
            }

            if (point.Z > max)
            {
                max = point.Z;
            }
        }

        return new TerrainRelief(min, max, points.Count);
    }

    private static string Round(double value)
        => value.ToString("0.###", CultureInfo.InvariantCulture);
}
