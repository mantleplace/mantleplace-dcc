namespace MantlePlace.Revit.Core;

/// <summary>A point of a placed contour, in frame-local metres.</summary>
public readonly record struct ContourPoint(double EastM, double NorthM);

/// <summary>One published contour ready to draw: one element, holding one or more runs of line.</summary>
public sealed class PlacedContour
{
    /// <summary>The elevation as the file wrote it, for <see cref="PublishedContours.ElementName"/>.</summary>
    public required string ElevationText { get; init; }

    /// <summary>The absolute height every point of the contour sits at, in metres.</summary>
    public required double ZMetres { get; init; }

    /// <summary>
    /// The runs of line the crop window left, each at least two points. A contour the window cuts
    /// stays one contour in several pieces, so it stays one element.
    /// </summary>
    public required IReadOnlyList<IReadOnlyList<ContourPoint>> Pieces { get; init; }
}

/// <summary>What placing a contours file produced, and what it left out and why.</summary>
public sealed class ContourPlacement
{
    public required IReadOnlyList<PlacedContour> Contours { get; init; }

    /// <summary>Contours with nothing inside the crop window.</summary>
    public int OutsideWindow { get; init; }

    /// <summary>Contours with no two points further apart than Revit's short-curve tolerance.</summary>
    public int TooShort { get; init; }
}

/// <summary>
/// Brings published contours into the site's frame, clips them at the terrain's crop window, and
/// drops the vertices Revit could not draw a segment between (ADR 0013). Pure.
/// </summary>
/// <remarks>
/// <para>
/// <b>X/Y</b> go through <see cref="SiteFrame.TryPlace"/> in the frame the host block's
/// <c>file_frame</c> states, and nothing else (<c>HPS-33</c>). <b>Z</b> is the published elevation
/// converted by the pointer's vertical unit, which is stated separately from the horizontal one and
/// may differ from it. ⛔ Nothing is added to Z: the lines will not sit exactly on a toposolid Revit
/// re-triangulated, and an offset to lift them clear would be a placement value computed here.
/// </para>
/// <para>
/// <b>Clipping</b> is exact at the window's edges, so the published contours end where the terrain
/// ends. The crossing point is interpolated in plan only; a contour's Z is one value.
/// </para>
/// <para>
/// <b>Short segments.</b> Revit refuses a line shorter than its short-curve tolerance. The roads
/// drop such a segment and leave a gap; a contour is far denser, so here a vertex within tolerance
/// of the last one <em>kept</em> is skipped instead, and the line stays continuous. That drops a
/// vertex and derives none. The last vertex of a piece is always kept, so a clipped end stays on the
/// edge.
/// </para>
/// </remarks>
public static class PublishedContours
{
    private const string NamePrefix = "Published contour ";

    /// <summary>
    /// The element name: the elevation exactly as the file published it, then its unit.
    /// </summary>
    /// <remarks>
    /// Not an elevation label drawn in a view — those are a follow-up. It is what Properties and a
    /// schedule show, so a curator can find one contour without them.
    /// </remarks>
    public static string ElementName(string elevationText, LinearUnit verticalUnit)
    {
        ArgumentNullException.ThrowIfNull(elevationText);
        string unit = LinearUnits.ToManifestToken(verticalUnit == LinearUnit.Unspecified ? LinearUnit.Metre : verticalUnit);
        return NamePrefix + elevationText + " " + unit;
    }

    /// <summary>
    /// Places every contour, or returns <c>null</c> with a stated reason when the frame cannot place
    /// the file at all.
    /// </summary>
    /// <param name="horizontal">The file's X/Y, as the host block's <c>file_frame</c> and the pointer's unit state them.</param>
    /// <param name="verticalUnit">The pointer's vertical unit.</param>
    /// <param name="window">The terrain's crop window, or <c>null</c> to leave the contours unclipped, as the terrain is then.</param>
    /// <param name="shortCurveToleranceM">Revit's short-curve tolerance, in metres.</param>
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

    /// <summary>
    /// The runs of <paramref name="line"/> inside <paramref name="window"/>, cut exactly at its edges.
    /// </summary>
    /// <remarks>
    /// A closed line that starts inside and is cut comes out with its first and last runs meeting at
    /// the start vertex; they are one run of line on the ground, so they are joined.
    /// </remarks>
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

    /// <summary>Liang–Barsky: the part of one segment inside the window, if any.</summary>
    /// <param name="entered">Whether the kept part begins on an edge rather than at <paramref name="a"/>.</param>
    /// <param name="left">Whether the kept part ends on an edge rather than at <paramref name="b"/>.</param>
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

    /// <summary>
    /// A crossing point held inside the window, so a rounding error in <c>a + t·d</c> cannot leave a
    /// clipped end a hair past the edge it was cut at.
    /// </summary>
    private static ContourPoint OnEdge(double east, double north, SurfaceCropWindow window)
        => new(Math.Clamp(east, window.WestM, window.EastM), Math.Clamp(north, window.SouthM, window.NorthM));

    private static bool Narrow(double p, double q, ref double t0, ref double t1)
    {
        if (p == 0.0)
        {
            // Parallel to this edge: inside it or wholly outside.
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

    /// <summary>
    /// The piece with every vertex within tolerance of the last kept one skipped, or <c>null</c> when
    /// fewer than two points would remain.
    /// </summary>
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

        // The true end displaces any kept vertex too close to it, so a clipped end stays on the edge.
        // The first vertex is never displaced, for the same reason at the other end.
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
