namespace MantlePlace.Core;

public readonly record struct ContourPoint(double EastM, double NorthM);

public sealed class PlacedContour
{
    public required string ElevationText { get; init; }

    public required double ZMetres { get; init; }

    public required IReadOnlyList<IReadOnlyList<ContourPoint>> Pieces { get; init; }
}

public sealed class ContourPlacement
{
    public required IReadOnlyList<PlacedContour> Contours { get; init; }

    public int OutsideWindow { get; init; }

    public int TooShort { get; init; }
}

public static class PublishedContours
{
    private const string NamePrefix = "Published contour ";

    public static string ElementName(string elevationText, LinearUnit verticalUnit)
    {
        ArgumentNullException.ThrowIfNull(elevationText);
        string unit = LinearUnits.ToManifestToken(verticalUnit == LinearUnit.Unspecified ? LinearUnit.Metre : verticalUnit);
        return NamePrefix + elevationText + " " + unit;
    }

    public static ContourPlacement? TryPlace(
        IReadOnlyList<PublishedContour> contours,
        SiteFrame frame,
        LayerFrame horizontal,
        LinearUnit verticalUnit,
        SurfaceCropWindow? window,
        double shortCurveToleranceM,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(contours);
        ArgumentNullException.ThrowIfNull(frame);
        reason = null;

        double metresPerVerticalUnit = LinearUnits.MetresPerUnit(verticalUnit);
        List<PlacedContour> placed = [];
        int outsideWindow = 0;
        int tooShort = 0;

        foreach (PublishedContour contour in contours)
        {
            List<ContourPoint> local = new(contour.Vertices.Count + 1);
            foreach (ContourVertex vertex in contour.Vertices)
            {
                if (!frame.TryPlace(horizontal, vertex.X, vertex.Y, out double east, out double north))
                {
                    reason = "This plugin cannot place the published contours in this bundle's frame: their "
                        + "coordinates are in a unit or frame the published origin does not measure.";
                    return null;
                }

                local.Add(new ContourPoint(east, north));
            }

            if (contour.IsClosed && local.Count > 1 && local[0] != local[^1])
            {
                local.Add(local[0]);
            }

            List<List<ContourPoint>> clipped = window is { } crop
                ? Clip(local, crop, contour.IsClosed)
                : local.Count > 0 ? [local] : [];

            if (clipped.Count == 0 && local.Count > 1)
            {
                outsideWindow++;
                continue;
            }

            List<IReadOnlyList<ContourPoint>> pieces = [];
            foreach (List<ContourPoint> piece in clipped)
            {
                if (Thin(piece, shortCurveToleranceM) is { } thinned)
                {
                    pieces.Add(thinned);
                }
            }

            if (pieces.Count == 0)
            {
                tooShort++;
                continue;
            }

            placed.Add(new PlacedContour
            {
                ElevationText = contour.ElevationText,
                ZMetres = contour.Elevation * metresPerVerticalUnit,
                Pieces = pieces,
            });
        }

        return new ContourPlacement { Contours = placed, OutsideWindow = outsideWindow, TooShort = tooShort };
    }

    private static List<List<ContourPoint>> Clip(List<ContourPoint> line, SurfaceCropWindow window, bool closed)
    {
        List<List<ContourPoint>> pieces = [];
        List<ContourPoint>? current = null;

        for (int index = 1; index < line.Count; index++)
        {
            if (!TryClipSegment(line[index - 1], line[index], window, out ContourPoint start, out ContourPoint end, out bool entered, out bool left))
            {
                current = null;
                continue;
            }

            if (current is null || entered)
            {
                current = [start];
                pieces.Add(current);
            }

            current.Add(end);

            if (left)
            {
                current = null;
            }
        }

        if (closed && pieces.Count > 1 && window.Contains(line[0].EastM, line[0].NorthM, 0.0))
        {
            List<ContourPoint> first = pieces[0];
            List<ContourPoint> last = pieces[^1];
            last.AddRange(first.Skip(1));
            pieces.RemoveAt(0);
        }

        return pieces;
    }

    private static bool TryClipSegment(
        ContourPoint a,
        ContourPoint b,
        SurfaceCropWindow window,
        out ContourPoint start,
        out ContourPoint end,
        out bool entered,
        out bool left)
    {
        start = a;
        end = b;
        entered = false;
        left = false;

        double dx = b.EastM - a.EastM;
        double dy = b.NorthM - a.NorthM;
        double t0 = 0.0;
        double t1 = 1.0;

        if (!Narrow(-dx, a.EastM - window.WestM, ref t0, ref t1)
            || !Narrow(dx, window.EastM - a.EastM, ref t0, ref t1)
            || !Narrow(-dy, a.NorthM - window.SouthM, ref t0, ref t1)
            || !Narrow(dy, window.NorthM - a.NorthM, ref t0, ref t1))
        {
            return false;
        }

        if (t0 > 0.0)
        {
            entered = true;
            start = OnEdge(a.EastM + (t0 * dx), a.NorthM + (t0 * dy), window);
        }

        if (t1 < 1.0)
        {
            left = true;
            end = OnEdge(a.EastM + (t1 * dx), a.NorthM + (t1 * dy), window);
        }

        return true;
    }

    private static ContourPoint OnEdge(double east, double north, SurfaceCropWindow window)
        => new(Math.Clamp(east, window.WestM, window.EastM), Math.Clamp(north, window.SouthM, window.NorthM));

    private static bool Narrow(double p, double q, ref double t0, ref double t1)
    {
        if (p == 0.0)
        {
            return q >= 0.0;
        }

        double t = q / p;
        if (p < 0.0)
        {
            if (t > t1)
            {
                return false;
            }

            t0 = Math.Max(t0, t);
        }
        else
        {
            if (t < t0)
            {
                return false;
            }

            t1 = Math.Min(t1, t);
        }

        return true;
    }

    private static List<ContourPoint>? Thin(List<ContourPoint> piece, double tolerance)
    {
        if (piece.Count < 2)
        {
            return null;
        }

        List<ContourPoint> kept = [piece[0]];
        for (int index = 1; index < piece.Count - 1; index++)
        {
            if (Distance(kept[^1], piece[index]) > tolerance)
            {
                kept.Add(piece[index]);
            }
        }

        ContourPoint last = piece[^1];
        while (kept.Count > 1 && Distance(kept[^1], last) <= tolerance)
        {
            kept.RemoveAt(kept.Count - 1);
        }

        if (Distance(kept[^1], last) <= tolerance)
        {
            return null;
        }

        kept.Add(last);
        return kept;
    }

    private static double Distance(ContourPoint a, ContourPoint b)
        => Math.Sqrt(((a.EastM - b.EastM) * (a.EastM - b.EastM)) + ((a.NorthM - b.NorthM) * (a.NorthM - b.NorthM)));
}
