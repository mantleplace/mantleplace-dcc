using System.Text;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Published contours (ADR 0013): reading the <c>LWPOLYLINE</c>s of the contours DXF, bringing them
/// into the site's frame, clipping them at the terrain's crop window, dropping the vertices Revit
/// could not draw a segment between, and the layer's build-scoped identity.
/// </summary>
/// <remarks>
/// The DXF shape pinned here is the one two real bundles carry: <c>LWPOLYLINE</c> only, in
/// <c>ENTITIES</c>, elevation in group 38, absolute projected X/Y in groups 10/20.
/// </remarks>
internal static class PublishedContourTests
{
    private const string Stem = "order-7f3a";
    private const string Build = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Rebuild = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    private static readonly SiteFrame MetricFrame = new()
    {
        Origin = new GeoOrigin
        {
            Epsg = 32610,
            Easting = 545888.5,
            Northing = 4187221.5,
            LinearUnit = LinearUnit.Metre,
        },
    };

    private static readonly SiteFrame UsFootFrame = new()
    {
        Origin = new GeoOrigin
        {
            Epsg = 2227,
            Easting = 6000000.0,
            Northing = 2000000.0,
            LinearUnit = LinearUnit.UsSurveyFoot,
        },
    };

    private static readonly LayerFrame MetricAbsolute = new(LayerCoordinates.AbsoluteProjected, LinearUnit.Metre);

    internal static int Run()
    {
        TestRun run = new();

        RunReaderCases(run);
        RunPlacementCases(run);
        RunClipCases(run);
        RunShortSegmentCases(run);
        RunIdentityCases(run);

        return run.Report("published contours");
    }

    private static void RunReaderCases(TestRun run)
    {
        run.Case("an LWPOLYLINE is one contour, its elevation text kept as published", () =>
        {
            string dxf = Entities(Polyline("12.50", closed: false, (1, 2), (3, 4), (5, 6)));

            run.Equal(PublishedContourReader.TryParse(new StringReader(dxf), out IReadOnlyList<PublishedContour>? contours), null, "read");
            run.Equal(contours!.Count, 1, "one contour");
            run.Equal(contours[0].ElevationText, "12.50", "verbatim, not reformatted");
            run.Within(contours[0].Elevation, 12.5, 0.0, "elevation");
            run.False(contours[0].IsClosed, "open");
            run.Equal(contours[0].Vertices.Count, 3, "three vertices");
            run.Within(contours[0].Vertices[2].X, 5, 0.0, "x from group 10");
            run.Within(contours[0].Vertices[2].Y, 6, 0.0, "y from group 20");
        });

        run.Case("flag 1 in group 70 marks a closed contour", () =>
        {
            string dxf = Entities(Polyline("7", closed: true, (0, 0), (1, 0), (1, 1)));

            run.Equal(PublishedContourReader.TryParse(new StringReader(dxf), out IReadOnlyList<PublishedContour>? contours), null, "read");
            run.True(contours![0].IsClosed, "closed");
        });

        run.Case("a missing group 38 is elevation zero, as the DXF reference defaults it", () =>
        {
            // A coastal site has a zero contour, and an emitter may leave a default out.
            string dxf = Entities(Polyline(null, closed: false, (0, 0), (1, 0)));

            run.Equal(PublishedContourReader.TryParse(new StringReader(dxf), out IReadOnlyList<PublishedContour>? contours), null, "read");
            run.Equal(contours![0].ElevationText, "0", "the default");
            run.Within(contours[0].Elevation, 0.0, 0.0, "zero");
        });

        run.Case("an LWPOLYLINE outside ENTITIES is not a contour", () =>
        {
            // ⛔ Geometry in BLOCKS is a definition placed by an INSERT with its own transform.
            StringBuilder dxf = new();
            dxf.Append("0\nSECTION\n2\nBLOCKS\n");
            dxf.Append(Polyline("1", closed: false, (0, 0), (1, 1)));
            dxf.Append("0\nENDSEC\n");
            dxf.Append(Entities(Polyline("2", closed: false, (0, 0), (1, 1))));

            run.Equal(PublishedContourReader.TryParse(new StringReader(dxf.ToString()), out IReadOnlyList<PublishedContour>? contours), null, "read");
            run.Equal(contours!.Count, 1, "only the ENTITIES one");
            run.Equal(contours[0].ElevationText, "2", "and it is that one");
        });

        run.Case("any other entity in ENTITIES refuses the file by name", () =>
        {
            string dxf = Entities(
                Polyline("1", closed: false, (0, 0), (1, 1)),
                "0\nLINE\n8\n0\n10\n0\n20\n0\n30\n0\n11\n1\n21\n1\n31\n0\n");

            string? reason = PublishedContourReader.TryParse(new StringReader(dxf), out IReadOnlyList<PublishedContour>? contours);
            run.Contains(reason, "LINE", "names the entity");
            run.True(contours is null, "nothing half-read");
        });

        run.Case("a bulge refuses the file, because an arc is not a polyline segment", () =>
        {
            string dxf = Entities("0\nLWPOLYLINE\n90\n2\n70\n0\n38\n1\n10\n0\n20\n0\n42\n0.5\n10\n1\n20\n1\n");

            run.Contains(PublishedContourReader.TryParse(new StringReader(dxf), out _), "arc", "refused by name");
        });

        run.Case("a zero bulge is a straight segment and reads", () =>
        {
            string dxf = Entities("0\nLWPOLYLINE\n90\n2\n70\n0\n38\n1\n10\n0\n20\n0\n42\n0.0\n10\n1\n20\n1\n");

            run.Equal(PublishedContourReader.TryParse(new StringReader(dxf), out _), null, "read");
        });

        run.Case("an extrusion other than +Z refuses the file", () =>
        {
            // LWPOLYLINE vertices are in the entity's OCS. Only a +Z extrusion makes them world X/Y.
            string dxf = Entities("0\nLWPOLYLINE\n90\n2\n70\n0\n38\n1\n10\n0\n20\n0\n10\n1\n20\n1\n210\n0\n220\n0\n230\n-1\n");

            run.Contains(PublishedContourReader.TryParse(new StringReader(dxf), out _), "extrusion", "refused by name");
        });

        run.Case("a +Z extrusion written out is world coordinates and reads", () =>
        {
            string dxf = Entities("0\nLWPOLYLINE\n90\n2\n70\n0\n38\n1\n10\n0\n20\n0\n10\n1\n20\n1\n210\n0.0\n220\n0.0\n230\n1.0\n");

            run.Equal(PublishedContourReader.TryParse(new StringReader(dxf), out _), null, "read");
        });

        run.Case("a vertex count that disagrees with group 90 refuses the file", () =>
        {
            string dxf = Entities("0\nLWPOLYLINE\n90\n3\n70\n0\n38\n1\n10\n0\n20\n0\n10\n1\n20\n1\n");

            run.Contains(PublishedContourReader.TryParse(new StringReader(dxf), out _), "malformed", "refused");
        });

        run.Case("a vertex with no Y refuses the file", () =>
        {
            string dxf = Entities("0\nLWPOLYLINE\n90\n2\n70\n0\n38\n1\n10\n0\n20\n0\n10\n1\n");

            run.Contains(PublishedContourReader.TryParse(new StringReader(dxf), out _), "malformed", "refused");
        });

        run.Case("a non-numeric ordinate refuses the file", () =>
        {
            string dxf = Entities("0\nLWPOLYLINE\n90\n2\n70\n0\n38\n1\n10\n0\n20\nnope\n10\n1\n20\n1\n");

            run.Contains(PublishedContourReader.TryParse(new StringReader(dxf), out _), "not a finite number", "refused");
        });

        run.Case("a file with no contours is refused rather than drawing nothing", () =>
        {
            run.Contains(PublishedContourReader.TryParse(new StringReader(Entities()), out _), "no LWPOLYLINE", "refused");
        });
    }

    private static void RunPlacementCases(TestRun run)
    {
        run.Case("X/Y are reduced by the published origin and Z is only converted", () =>
        {
            PublishedContour contour = Contour("120.5", 120.5, closed: false, (545890.5, 4187220.5), (545900.5, 4187230.5));

            ContourPlacement? placed = Place([contour], MetricFrame, MetricAbsolute, LinearUnit.Metre, window: null, out string? reason);

            run.Equal(reason, null, "placed");
            PlacedContour only = placed!.Contours[0];
            run.Within(only.Pieces[0][0].EastM, 2.0, 1e-9, "east");
            run.Within(only.Pieces[0][0].NorthM, -1.0, 1e-9, "north");
            run.Within(only.ZMetres, 120.5, 0.0, "absolute height, no offset");
        });

        run.Case("a foot vertical unit converts Z while X/Y keep the horizontal unit", () =>
        {
            // The local_ft shape: X/Y in one unit, Z in another. Each is taken from the manifest.
            PublishedContour contour = Contour("100", 100, closed: false, (545890.5, 4187220.5), (545900.5, 4187230.5));

            ContourPlacement? placed = Place([contour], MetricFrame, MetricAbsolute, LinearUnit.UsSurveyFoot, window: null, out _);

            run.Within(placed!.Contours[0].ZMetres, 100 * 1200.0 / 3937.0, 1e-12, "US survey feet to metres");
            run.Within(placed.Contours[0].Pieces[0][1].EastM, 12.0, 1e-9, "east stays metric");
        });

        run.Case("a State Plane foot delivery lands in metres", () =>
        {
            PublishedContour contour = Contour("50", 50, closed: false, (6000100.0, 2000000.0), (6000200.0, 2000000.0));

            ContourPlacement? placed = Place(
                [contour], UsFootFrame, new LayerFrame(LayerCoordinates.AbsoluteProjected, LinearUnit.UsSurveyFoot),
                LinearUnit.UsSurveyFoot, window: null, out _);

            run.Within(placed!.Contours[0].Pieces[0][0].EastM, 100 * 1200.0 / 3937.0, 1e-9, "100 ftUS east");
        });

        run.Case("absolute X/Y in a unit other than the origin's is refused, never rescaled", () =>
        {
            PublishedContour contour = Contour("1", 1, closed: false, (545890.5, 4187220.5), (545900.5, 4187230.5));

            ContourPlacement? placed = Place(
                [contour], MetricFrame, new LayerFrame(LayerCoordinates.AbsoluteProjected, LinearUnit.InternationalFoot),
                LinearUnit.Metre, window: null, out string? reason);

            run.True(placed is null, "nothing placed");
            run.Contains(reason, "contours", "a stated reason");
        });

        run.Case("local offsets are converted and not subtracted", () =>
        {
            PublishedContour contour = Contour("1", 1, closed: false, (10, 20), (30, 40));

            ContourPlacement? placed = Place(
                [contour], MetricFrame, new LayerFrame(LayerCoordinates.LocalOffsets, LinearUnit.InternationalFoot),
                LinearUnit.Metre, window: null, out _);

            run.Within(placed!.Contours[0].Pieces[0][0].EastM, 3.048, 1e-12, "10 ft east");
        });

        run.Case("the element name carries the published elevation text and its unit", () =>
        {
            run.Equal(PublishedContours.ElementName("120.50", LinearUnit.Metre), "Published contour 120.50 m", "metric");
            run.Equal(PublishedContours.ElementName("400", LinearUnit.UsSurveyFoot), "Published contour 400 ftUS", "US survey foot");
            run.Equal(PublishedContours.ElementName("400", LinearUnit.InternationalFoot), "Published contour 400 ft", "international foot");
            run.Equal(PublishedContours.ElementName("3", LinearUnit.Unspecified), "Published contour 3 m", "unstated is metric");
        });
    }

    private static void RunClipCases(TestRun run)
    {
        SurfaceCropWindow window = new(WestM: 0, SouthM: 0, EastM: 10, NorthM: 10);

        run.Case("a contour inside the window is untouched", () =>
        {
            PlacedContour only = PlaceLocal(window, closed: false, (1, 1), (5, 5), (9, 1))!.Contours[0];

            run.Equal(only.Pieces.Count, 1, "one piece");
            run.Equal(only.Pieces[0].Count, 3, "every vertex");
        });

        run.Case("a contour crossing the edge ends exactly on it", () =>
        {
            PlacedContour only = PlaceLocal(window, closed: false, (5, 5), (15, 5))!.Contours[0];

            run.Equal(only.Pieces[0].Count, 2, "two vertices");
            run.Within(only.Pieces[0][1].EastM, 10.0, 1e-12, "on the east edge");
            run.Within(only.Pieces[0][1].NorthM, 5.0, 1e-12, "at the crossing");
        });

        run.Case("a contour that leaves and re-enters is one contour in two pieces", () =>
        {
            ContourPlacement placed = PlaceLocal(window, closed: false, (5, 5), (15, 5), (15, 8), (5, 8))!;

            run.Equal(placed.Contours.Count, 1, "still one contour, so one element");
            run.Equal(placed.Contours[0].Pieces.Count, 2, "two pieces");
            run.Within(placed.Contours[0].Pieces[1][0].EastM, 10.0, 1e-12, "the second piece starts on the edge");
        });

        run.Case("a segment passing straight through the window is kept between its two crossings", () =>
        {
            PlacedContour only = PlaceLocal(window, closed: false, (-5, 5), (15, 5))!.Contours[0];

            run.Equal(only.Pieces[0].Count, 2, "two vertices");
            run.Within(only.Pieces[0][0].EastM, 0.0, 1e-12, "enters on the west edge");
            run.Within(only.Pieces[0][1].EastM, 10.0, 1e-12, "leaves on the east edge");
        });

        run.Case("a contour wholly outside the window is counted and not placed", () =>
        {
            ContourPlacement placed = PlaceLocal(window, closed: false, (20, 20), (30, 30))!;

            run.Equal(placed.Contours.Count, 0, "nothing placed");
            run.Equal(placed.OutsideWindow, 1, "counted");
        });

        run.Case("a closed contour inside the window closes on its first vertex", () =>
        {
            PlacedContour only = PlaceLocal(window, closed: true, (2, 2), (8, 2), (8, 8), (2, 8))!.Contours[0];

            run.Equal(only.Pieces.Count, 1, "one piece");
            run.Equal(only.Pieces[0].Count, 5, "four vertices and the closing one");
            run.Within(only.Pieces[0][4].EastM, 2.0, 0.0, "back to the start");
            run.Within(only.Pieces[0][4].NorthM, 2.0, 0.0, "back to the start");
        });

        run.Case("a closed contour cut once is one piece joined across its start", () =>
        {
            // Start inside, run out through the east edge and back in: the piece before the
            // crossing and the piece after it are one run of line on the ground.
            PlacedContour only = PlaceLocal(window, closed: true, (5, 2), (15, 2), (15, 8), (5, 8))!.Contours[0];

            run.Equal(only.Pieces.Count, 1, "one piece, not two");
            run.Within(only.Pieces[0][0].EastM, 10.0, 1e-12, "starts where it re-enters");
            run.Within(only.Pieces[0][^1].EastM, 10.0, 1e-12, "ends where it leaves");
            run.Equal(only.Pieces[0].Count, 4, "(10,8), (5,8), (5,2), (10,2): the start vertex once");
        });

        run.Case("with no window the contours are left unclipped", () =>
        {
            ContourPlacement placed = PlaceLocal(null, closed: false, (-100, -100), (100, 100))!;

            run.Equal(placed.Contours.Count, 1, "placed");
            run.Within(placed.Contours[0].Pieces[0][0].EastM, -100.0, 0.0, "as published");
        });
    }

    private static void RunShortSegmentCases(TestRun run)
    {
        const double Tolerance = 0.01;

        run.Case("a vertex within tolerance of the last kept one is skipped, and the line stays continuous", () =>
        {
            PlacedContour only = PlaceLocal(null, Tolerance, closed: false, (0, 0), (0.004, 0), (0.008, 0), (1, 0))!.Contours[0];

            run.Equal(only.Pieces[0].Count, 2, "the two near ones skipped");
            run.Within(only.Pieces[0][1].EastM, 1.0, 0.0, "and the next segment starts from the kept vertex");
        });

        run.Case("a run of short steps is measured from the last kept vertex, not the previous one", () =>
        {
            // Each step is under tolerance, but the fourth vertex is past it from the first.
            PlacedContour only = PlaceLocal(null, Tolerance, closed: false, (0, 0), (0.006, 0), (0.012, 0), (0.018, 0), (0.024, 0))!.Contours[0];

            run.Equal(only.Pieces[0].Count, 3, "0, 0.012, 0.024");
            run.Within(only.Pieces[0][1].EastM, 0.012, 1e-12, "the first vertex past tolerance");
        });

        run.Case("the last vertex is always kept, so a clipped end stays on the edge", () =>
        {
            PlacedContour only = PlaceLocal(null, Tolerance, closed: false, (0, 0), (1, 0), (1.005, 0))!.Contours[0];

            run.Equal(only.Pieces[0].Count, 2, "the near one replaced");
            run.Within(only.Pieces[0][1].EastM, 1.005, 0.0, "by the true end");
        });

        run.Case("a contour shorter than tolerance is counted and not placed", () =>
        {
            ContourPlacement placed = PlaceLocal(null, Tolerance, closed: false, (0, 0), (0.005, 0))!;

            run.Equal(placed.Contours.Count, 0, "nothing placed");
            run.Equal(placed.TooShort, 1, "counted");
        });

        run.Case("a contour with a single vertex is counted as too short", () =>
        {
            ContourPlacement placed = PlaceLocal(null, Tolerance, closed: false, (3, 3))!;

            run.Equal(placed.TooShort, 1, "counted");
        });
    }

    private static void RunIdentityCases(TestRun run)
    {
        run.Case("the stamp names the order and the contours file's build", () =>
            run.Equal(
                ContourIdentity.Stamp(Stem, Build),
                "Mantle Place Contours order-7f3a/0123456789ab",
                "stem, twelve-character build token"));

        run.Case("a first import creates the layer", () =>
            run.Equal(ContourIdentity.Decide([], Stem, Build).Disposition, ContourDisposition.Create, "create"));

        run.Case("this build's contours are reused", () =>
        {
            string stamp = ContourIdentity.Stamp(Stem, Build);
            ContourDecision decision = ContourIdentity.Decide([stamp, stamp, "something else"], Stem, Build);

            run.Equal(decision.Disposition, ContourDisposition.Reuse, "reuse");
            run.Equal(decision.AlreadyPresent, 2, "counted");
        });

        run.Case("an earlier build's contours refuse the step and name the prefix to delete", () =>
        {
            ContourDecision decision = ContourIdentity.Decide([ContourIdentity.Stamp(Stem, Rebuild)], Stem, Build);

            run.Equal(decision.Disposition, ContourDisposition.RefuseStale, "refused");
            run.Contains(decision.Explanation, "\"Mantle Place Contours order-7f3a/\"", "names the prefix");
        });

        run.Case("a stem this one is a prefix of is another order's", () =>
        {
            ContourDecision decision = ContourIdentity.Decide([ContourIdentity.Stamp(Stem + "9", Rebuild)], Stem, Build);

            run.Equal(decision.Disposition, ContourDisposition.Create, "not ours, so not stale");
        });
    }

    private static ContourPlacement? PlaceLocal(SurfaceCropWindow? window, bool closed, params (double X, double Y)[] vertices)
        => PlaceLocal(window, 1e-6, closed, vertices);

    private static ContourPlacement? PlaceLocal(SurfaceCropWindow? window, double tolerance, bool closed, params (double X, double Y)[] vertices)
        => PublishedContours.TryPlace(
            [Contour("1", 1, closed, vertices)],
            MetricFrame,
            new LayerFrame(LayerCoordinates.LocalOffsets, LinearUnit.Metre),
            LinearUnit.Metre,
            window,
            tolerance,
            out _);

    private static ContourPlacement? Place(
        IReadOnlyList<PublishedContour> contours,
        SiteFrame frame,
        LayerFrame horizontal,
        LinearUnit vertical,
        SurfaceCropWindow? window,
        out string? reason)
        => PublishedContours.TryPlace(contours, frame, horizontal, vertical, window, 1e-6, out reason);

    private static PublishedContour Contour(string text, double elevation, bool closed, params (double X, double Y)[] vertices)
        => new()
        {
            ElevationText = text,
            Elevation = elevation,
            IsClosed = closed,
            Vertices = [.. vertices.Select(v => new ContourVertex(v.X, v.Y))],
        };

    private static string Entities(params string[] entities)
        => "0\nSECTION\n2\nENTITIES\n" + string.Concat(entities) + "0\nENDSEC\n0\nEOF\n";

    private static string Polyline(string? elevation, bool closed, params (double X, double Y)[] vertices)
    {
        StringBuilder entity = new();
        entity.Append("0\nLWPOLYLINE\n8\nContours\n");
        entity.Append("90\n").Append(vertices.Length).Append('\n');
        entity.Append("70\n").Append(closed ? 1 : 0).Append('\n');
        if (elevation is not null)
        {
            entity.Append("38\n").Append(elevation).Append('\n');
        }

        foreach ((double x, double y) in vertices)
        {
            entity.Append("10\n").Append(x.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
            entity.Append("20\n").Append(y.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        }

        return entity.ToString();
    }
}
