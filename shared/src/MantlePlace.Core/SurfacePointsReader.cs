using System.Globalization;

namespace MantlePlace.Core;

public readonly record struct SurfacePoint(double X, double Y, double Z);

public static class SurfacePointsReader
{
    public const int MinimumPoints = 3;

    public static string? TryParse(string csvText, out IReadOnlyList<SurfacePoint> points)
    {
        List<SurfacePoint> parsed = [];
        points = parsed;

        if (string.IsNullOrWhiteSpace(csvText))
        {
            return "The toposurface points file is empty.";
        }

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
