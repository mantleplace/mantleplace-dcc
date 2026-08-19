using System.Globalization;

namespace MantlePlace.Revit.Core;

/// <summary>One point from the toposurface points file, in the file's own units.</summary>
public readonly record struct SurfacePoint(double X, double Y, double Z);

/// <summary>
/// Parses <c>Surface/SurfacePoints.csv</c> — a headerless <c>X,Y,Z</c> file. Pure.
/// </summary>
/// <remarks>
/// The file's X/Y are east/north offsets from the AOI centroid and its Z is a real orthometric
/// elevation, so the surface lands near the project origin rather than at a ~500 000 m easting
/// where Revit's precision warnings start. Nothing here rescales or re-origins anything: the ETL
/// already did that, and re-deriving it would be a second implementation of the pipeline (HPS-33).
/// </remarks>
public static class SurfacePointsReader
{
    /// <summary>
    /// Fewest points <c>Toposolid.Create</c> accepts; below this it throws <c>ArgumentException</c>.
    /// </summary>
    /// <remarks>
    /// A Revit API constant living in the pure core looks misplaced until you ask where the check
    /// can be asserted. In the shim it fired after <see cref="BundleImportPlanner"/> had already
    /// reported <c>CanImport</c> — a plan the executor then declined — and no headless test could
    /// reach it (<c>HPS-02</c>). It is a property of the FILE, so it is the reader's to enforce.
    /// </remarks>
    public const int MinimumPoints = 3;

    /// <summary>
    /// Parses the whole file. A row that is not exactly three parseable numbers fails the read
    /// rather than being dropped — the same shape the ETL's own <c>dcc.points_csv_bounds</c> check
    /// asserts on the producing side, and a silently-skipped row is a hole in the terrain.
    /// </summary>
    /// <returns><c>null</c> on success, or a user-facing reason the file could not be read.</returns>
    public static string? TryParse(string csvText, out IReadOnlyList<SurfacePoint> points)
    {
        List<SurfacePoint> parsed = [];
        points = parsed;

        if (string.IsNullOrWhiteSpace(csvText))
        {
            return "The toposurface points file is empty.";
        }

        // Hoisted: one buffer for the whole file, not one per row (CA2014).
        Span<Range> fields = stackalloc Range[4];

        int lineNumber = 0;
        foreach (ReadOnlySpan<char> rawLine in csvText.AsSpan().EnumerateLines())
        {
            lineNumber++;
            ReadOnlySpan<char> line = rawLine.Trim();
            if (line.IsEmpty)
            {
                continue;
            }

            int count = line.Split(fields, ',');
            if (count != 3)
            {
                return $"The toposurface points file is malformed: line {lineNumber} has {count} "
                    + "comma-separated values, expected exactly 3 (X,Y,Z).";
            }

            if (!TryNumber(line[fields[0]], out double x)
                || !TryNumber(line[fields[1]], out double y)
                || !TryNumber(line[fields[2]], out double z))
            {
                return $"The toposurface points file is malformed: line {lineNumber} is not three numbers.";
            }

            parsed.Add(new SurfacePoint(x, y, z));
        }

        if (parsed.Count == 0)
        {
            return "The toposurface points file contains no points.";
        }

        if (parsed.Count < MinimumPoints)
        {
            return $"The toposurface points file has only {parsed.Count} "
                + (parsed.Count == 1 ? "point" : "points")
                + $"; Revit needs at least {MinimumPoints} to build a surface.";
        }

        return null;
    }

    private static bool TryNumber(ReadOnlySpan<char> field, out double value)
        => double.TryParse(field.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
