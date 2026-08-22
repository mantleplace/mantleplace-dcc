using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>The vertical extent of a parsed point set, in the caller's own unit.</summary>
public readonly record struct TerrainRelief(double MinZ, double MaxZ, int PointCount);

/// <summary>One level the project already contains, as the planner needs to see it.</summary>
/// <param name="Id">Revit's <c>ElementId</c> value, carried as a number so the core stays Revit-free.</param>
public readonly record struct CandidateLevel(long Id, string Name, double Elevation);

/// <summary>How the terrain's base plane is going to be established.</summary>
public enum TerrainBaseStrategy
{
    /// <summary>No level exists at all, so nothing can be built.</summary>
    NoLevelAvailable = 0,

    /// <summary>A level already sits at or below the base plane. Nothing is offset, nothing is created.</summary>
    ExistingLevel,

    /// <summary>The lowest level, pushed down by a negative Height Offset From Level.</summary>
    ExistingLevelWithOffset,

    /// <summary>A level created for the purpose, because neither of the above worked.</summary>
    DedicatedLevel,
}

/// <summary>Where the toposolid's base plane goes, and how it gets there.</summary>
public sealed class TerrainBasePlan
{
    public required TerrainBaseStrategy Strategy { get; init; }

    /// <summary>The level to build on, or <c>0</c> for <see cref="TerrainBaseStrategy.DedicatedLevel"/>.</summary>
    public long LevelId { get; init; }

    public string LevelName { get; init; } = string.Empty;

    /// <summary>The level's own elevation, or the elevation to create one at.</summary>
    public double LevelElevation { get; init; }

    /// <summary>What to write to <c>TOPOSOLID_HEIGHTABOVELEVEL_PARAM</c>. Zero except on the offset arm.</summary>
    public double HeightOffset { get; init; }

    /// <summary>How far below the lowest terrain point the base plane was placed.</summary>
    public double RequiredClearance { get; init; }

    /// <summary>Where the base plane ends up: <see cref="LevelElevation"/> plus <see cref="HeightOffset"/>.</summary>
    public double BasePlane => LevelElevation + HeightOffset;

    /// <summary>One line for the import log, naming the numbers a curator would otherwise have to hunt for.</summary>
    public string Explanation { get; init; } = string.Empty;
}

/// <summary>
/// Chooses the plane a toposolid's underside sits on. Pure.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ This exists because the first real import failed on it. The shim used to take
/// <c>FirstElementIdOf&lt;Level&gt;()</c> — whichever level the collector happened to enumerate
/// first — and hand it straight to <c>Toposolid.Create</c>. Revit's own documentation says that
/// overload's points build the toposolid's <em>top face</em>, so the thickness at each vertex is its
/// Z above the base plane. A coastal AOI publishes points below sea level: the bundle that surfaced
/// this has 33,006 of its 80,940 points below Z = 0. Against a level at 0 every one of them asks for
/// zero or negative thickness, and Revit refuses the whole shape edit with <em>"Slab Shape Edit
/// failed. The Floor or Roof or Toposolid is too thin for its given type."</em> — an error that
/// cannot be ignored, so the import dies with nothing placed.
/// </para>
/// <para>
/// An inland site whose lowest point sits above the level imports cleanly, which is exactly why this
/// survived until a waterfront order was tried.
/// </para>
/// <para>
/// Units are whatever the caller measured in — the arithmetic never converts, so handing it Revit's
/// internal feet throughout is correct by construction, the same contract
/// <see cref="DrapeLayering"/> keeps.
/// </para>
/// </remarks>
public static class TerrainBasePlanner
{
    /// <summary>The name a created level gets, and the name a second import finds it by.</summary>
    public const string DedicatedLevelName = "Mantle Place Terrain Base";

    /// <summary>
    /// How much room the base plane leaves beneath the lowest terrain point.
    /// </summary>
    /// <remarks>
    /// ⛔ The <c>3 ×</c> is not a comfort factor, it is <see cref="DrapeLayering.Split"/>'s own floor
    /// restated: that function refuses unless the remainder clears twice the minimum, i.e. unless the
    /// total is at least three times the host's minimum layer thickness. Reusing the same number here
    /// means a base plane that clears the terrain always leaves a type the imagery drape can still
    /// split. Two separate constants would drift, and the failure would be an import that builds the
    /// terrain and then silently declines the aerial photograph.
    /// </remarks>
    public static double ClearanceFor(double typeTotalThickness, double minimumLayerThickness)
    {
        double byType = double.IsFinite(typeTotalThickness) && typeTotalThickness > 0.0 ? typeTotalThickness : 0.0;
        double byHost = double.IsFinite(minimumLayerThickness) && minimumLayerThickness > 0.0
            ? 3.0 * minimumLayerThickness
            : 0.0;
        return Math.Max(byType, byHost);
    }

    /// <summary>
    /// Picks the base plane for <paramref name="relief"/> against the levels the project has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The preference order is deliberate. An existing level already low enough is best: nothing is
    /// created, nothing is offset, and <c>TOPOSOLID_ELEVATION_AT_BOTTOM</c> still reads as a level a
    /// curator recognises. Among those the <em>highest</em> wins rather than the lowest — a base plane
    /// 200 m under the site would bury the terrain in a solid nobody asked for, and every section
    /// through the project would cut it.
    /// </para>
    /// <para>
    /// Failing that, the lowest existing level takes a negative Height Offset From Level. This is
    /// preferred over creating a level because a created level appears in every elevation and section
    /// view the curator has, forever, for a solid they will never dimension to.
    /// </para>
    /// <para>
    /// A dedicated level is the last arm, reached here only when the project has no level the offset
    /// can hang from, and reached by <see cref="Escalate"/> when Revit refuses the offset.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// The retry after Revit refused <paramref name="refused"/>: put the base plane on a level of our
    /// own, at exactly the elevation the refused plan was aiming for.
    /// </summary>
    /// <remarks>
    /// ⛔ The escalation exists because of one thing nobody can read out of the API: whether Revit
    /// evaluates the too-thin check against the height offset written after <c>Toposolid.Create</c>
    /// returns, or against the raw level at creation time. If it is the latter, the offset arm is
    /// unreachable and only a level at the right elevation works. Rather than bet the branch on the
    /// answer, both arms are reachable and the import log records which one Revit accepted.
    /// </remarks>
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

    /// <summary>Reads back the relief of a point set. Empty input is a zero-count relief, not a throw.</summary>
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
