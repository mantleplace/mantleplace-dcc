using System.Xml.Linq;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Where a later bundle goes against the shared coordinates the project already holds: the anchor
/// and offset decision, and every refusal.
/// </summary>
/// <remarks>
/// The numbers are UTM 13N tiles 1,000 m apart, the shape of an adjacent order. The decision is
/// asserted here, headlessly (<c>HPS-02</c>); the add-in only applies what it says.
/// </remarks>
internal static class AdjacentBundleTests
{
    private const int Utm13North = 32613;

    private static SurveyPointPlacement Incoming(
        double easting,
        double northing,
        int? epsg = Utm13North,
        LinearUnit unit = LinearUnit.Metre,
        double angleRadians = 0.0)
        => new()
        {
            Origin = new GeoOrigin { Easting = easting, Northing = northing, Epsg = epsg, LinearUnit = unit },
            AngleRadians = angleRadians,
        };

    private static ProjectAnchor Anchor(
        double easting = 500_000.0,
        double northing = 4_400_000.0,
        int? epsg = Utm13North,
        double angleRadians = 0.0)
        => new(epsg, easting, northing, angleRadians);

    internal static int Run()
    {
        TestRun run = new();

        run.Case("no anchor: the first bundle sets the project position, as it always did", () =>
        {
            AnchorDecision decision = AdjacentBundle.Decide(null, Incoming(500_000.0, 4_400_000.0));

            run.Equal(decision.Kind, AnchorDecisionKind.FirstBundle, "kind");
            run.False(decision.IsRefusal, "not a refusal");
            run.False(decision.MovesGeometry, "the geometry stays about the origin");
        });

        run.Case("a bundle with no survey point leaves the project alone", () =>
        {
            AnchorDecision decision = AdjacentBundle.Decide(Anchor(), null);

            run.Equal(decision.Kind, AnchorDecisionKind.NoSurveyPoint, "kind");
            run.False(decision.IsRefusal, "not a refusal");
            run.False(decision.MovesGeometry, "no move");
        });

        run.Case("an adjacent bundle is offset by the difference of the two published points", () =>
        {
            AnchorDecision decision = AdjacentBundle.Decide(Anchor(), Incoming(501_000.0, 4_400_000.0));

            run.Equal(decision.Kind, AnchorDecisionKind.Offset, "kind");
            run.Within(decision.OffsetEastM, 1000.0, 1e-9, "east");
            run.Within(decision.OffsetNorthM, 0.0, 1e-9, "north");
            run.True(decision.MovesGeometry, "moves");
        });

        run.Case("a third bundle is offset against the anchor, not against the second", () =>
        {
            // The anchor stays the first bundle's point however many follow it, so a third tile lands
            // against the first two without either of them being read back.
            AnchorDecision second = AdjacentBundle.Decide(Anchor(), Incoming(501_000.0, 4_400_000.0));
            AnchorDecision third = AdjacentBundle.Decide(Anchor(), Incoming(500_000.0, 4_401_000.0));

            run.Within(second.OffsetEastM, 1000.0, 1e-9, "second east");
            run.Within(third.OffsetEastM, 0.0, 1e-9, "third east");
            run.Within(third.OffsetNorthM, 1000.0, 1e-9, "third north");
        });

        run.Case("a bundle west and south of the anchor is offset negatively", () =>
        {
            AnchorDecision decision = AdjacentBundle.Decide(Anchor(), Incoming(499_000.0, 4_398_500.0));

            run.Within(decision.OffsetEastM, -1000.0, 1e-9, "east");
            run.Within(decision.OffsetNorthM, -1500.0, 1e-9, "north");
        });

        run.Case("the offset is converted with the unit each side states", () =>
        {
            // The anchor was read back from Revit in metres. The bundle publishes US survey feet:
            // 1 ftUS is exactly 1200/3937 m, so 3,280.8333... ftUS is 1,000 m.
            double metresPerFoot = 1200.0 / 3937.0;
            double eastFeet = 501_000.0 / metresPerFoot;
            double northFeet = 4_400_000.0 / metresPerFoot;
            AnchorDecision decision = AdjacentBundle.Decide(
                Anchor(),
                Incoming(eastFeet, northFeet, unit: LinearUnit.UsSurveyFoot));

            run.Equal(decision.Kind, AnchorDecisionKind.Offset, "kind");
            run.Within(decision.OffsetEastM, 1000.0, 1e-6, "east");
            run.Within(decision.OffsetNorthM, 0.0, 1e-6, "north");
        });

        run.Case("an unstated unit is metric, as everywhere else in this host", () =>
        {
            AnchorDecision decision = AdjacentBundle.Decide(
                Anchor(),
                Incoming(501_000.0, 4_400_000.0, unit: LinearUnit.Unspecified));

            run.Within(decision.OffsetEastM, 1000.0, 1e-9, "east");
        });

        run.Case("the same point is an offset of zero: a re-import of the anchor bundle itself", () =>
        {
            AnchorDecision decision = AdjacentBundle.Decide(Anchor(), Incoming(500_000.0, 4_400_000.0));

            run.Equal(decision.Kind, AnchorDecisionKind.Offset, "kind");
            run.False(decision.IsRefusal, "not a refusal");
            run.False(decision.MovesGeometry, "nothing to move");
        });

        run.Case("height is never offset: every artifact's Z is already absolute", () =>
        {
            // The decision has no vertical component, so none can be applied twice.
            run.True(typeof(AnchorDecision).GetProperty("OffsetElevationM") is null, "no elevation offset");
        });

        run.Case("a different EPSG code is refused, naming both", () =>
        {
            AnchorDecision decision = AdjacentBundle.Decide(Anchor(), Incoming(501_000.0, 4_400_000.0, epsg: 32614));

            run.Equal(decision.Kind, AnchorDecisionKind.Refused, "kind");
            run.Equal(decision.Refusal, AnchorRefusal.DifferentCrs, "reason");
            run.Contains(decision.Message, "32613", "names the project's code");
            run.Contains(decision.Message, "32614", "names the bundle's code");
            run.Contains(decision.Message, "unchanged", "says the project is untouched");
            run.False(decision.MovesGeometry, "no move");
        });

        run.Case("a different published angle is refused", () =>
        {
            AnchorDecision decision = AdjacentBundle.Decide(
                Anchor(angleRadians: 0.01),
                Incoming(501_000.0, 4_400_000.0, angleRadians: 0.02));

            run.Equal(decision.Refusal, AnchorRefusal.DifferentAngle, "reason");
            run.Contains(decision.Message, "angle", "says why");
            run.Contains(decision.Message, "unchanged", "says the project is untouched");
        });

        run.Case("an angle that differs by less than the tolerance is the same angle", () =>
        {
            AnchorDecision decision = AdjacentBundle.Decide(
                Anchor(),
                Incoming(501_000.0, 4_400_000.0, angleRadians: AdjacentBundle.AngleToleranceRadians / 2.0));

            run.Equal(decision.Kind, AnchorDecisionKind.Offset, "kind");
        });

        run.Case("two bundles on the same non-zero angle are refused: the offset would need a rotation", () =>
        {
            AnchorDecision decision = AdjacentBundle.Decide(
                Anchor(angleRadians: 0.05),
                Incoming(501_000.0, 4_400_000.0, angleRadians: 0.05));

            run.Equal(decision.Refusal, AnchorRefusal.RotatedGrid, "reason");
            run.Contains(decision.Message, "rotat", "says why");
        });

        run.Case("a bundle farther from the anchor than the limit is refused", () =>
        {
            double justOver = AdjacentBundle.MaxOffsetM + 1.0;
            AnchorDecision decision = AdjacentBundle.Decide(Anchor(), Incoming(500_000.0 + justOver, 4_400_000.0));

            run.Equal(decision.Refusal, AnchorRefusal.TooFar, "reason");
            run.Contains(decision.Message, "unchanged", "says the project is untouched");
        });

        run.Case("the limit is on the distance, not on each axis", () =>
        {
            // 0.8 of the limit on both axes is 1.13 of it on the diagonal.
            double each = AdjacentBundle.MaxOffsetM * 0.8;
            AnchorDecision decision = AdjacentBundle.Decide(Anchor(), Incoming(500_000.0 + each, 4_400_000.0 + each));

            run.Equal(decision.Refusal, AnchorRefusal.TooFar, "reason");
        });

        run.Case("a bundle exactly at the limit is taken", () =>
        {
            AnchorDecision decision = AdjacentBundle.Decide(
                Anchor(),
                Incoming(500_000.0 + AdjacentBundle.MaxOffsetM, 4_400_000.0));

            run.Equal(decision.Kind, AnchorDecisionKind.Offset, "kind");
        });

        run.Case("a legacy project that never recorded its EPSG: the same point is taken", () =>
        {
            AnchorDecision decision = AdjacentBundle.Decide(Anchor(epsg: null), Incoming(500_000.0, 4_400_000.0));

            run.Equal(decision.Kind, AnchorDecisionKind.Offset, "kind");
            run.False(decision.MovesGeometry, "nothing to move");
        });

        run.Case("a legacy project that never recorded its EPSG: another point is refused, not guessed", () =>
        {
            AnchorDecision decision = AdjacentBundle.Decide(Anchor(epsg: null), Incoming(501_000.0, 4_400_000.0));

            run.Equal(decision.Refusal, AnchorRefusal.UnknownCrs, "reason");
            run.Contains(decision.Message, "unchanged", "says the project is untouched");
        });

        run.Case("every refusal is one line that says the project was left as it was", () =>
        {
            AnchorDecision[] refusals =
            [
                AdjacentBundle.Decide(Anchor(), Incoming(501_000.0, 4_400_000.0, epsg: 2231)),
                AdjacentBundle.Decide(Anchor(angleRadians: 0.1), Incoming(501_000.0, 4_400_000.0)),
                AdjacentBundle.Decide(Anchor(angleRadians: 0.1), Incoming(501_000.0, 4_400_000.0, angleRadians: 0.1)),
                AdjacentBundle.Decide(Anchor(), Incoming(900_000.0, 4_400_000.0)),
                AdjacentBundle.Decide(Anchor(epsg: null), Incoming(501_000.0, 4_400_000.0)),
            ];

            foreach (AnchorDecision refusal in refusals)
            {
                run.True(refusal.IsRefusal, $"{refusal.Refusal} is a refusal");
                run.False(string.IsNullOrWhiteSpace(refusal.Message), $"{refusal.Refusal} has a message");
                run.False(refusal.Message.Contains('\n'), $"{refusal.Refusal} is one line");
                run.Contains(refusal.Message, "unchanged", $"{refusal.Refusal} leaves the project alone");
            }

            run.Equal(refusals.Select(refusal => refusal.Refusal).Distinct().Count(), 5, "five distinct reasons");
        });

        run.Case("an offset bundle's drape is written for its corner in the document, not in its own frame", () =>
        {
            // Image corner (-709.5, -706.5) in the bundle's frame; the bundle stands 1,419 m east. Under
            // smooth shading the offset is the image corner minus the element's corner, both read in the
            // document, so it must equal the unshifted offset when the element moved with the image.
            DrapePlacement placement = new()
            {
                LeftM = -709.5,
                BottomM = -706.5,
                RightM = 709.5,
                TopM = 706.5,
                PixelSize = new ImageSize(4730, 4710),
                ExtentFromDrapeBlock = true,
            };
            DrapePlacement moved = placement.Shifted(1419.0, 0.0);
            DrapeOffset atOrigin = DrapeAnchor.For(placement, smoothShading: true, -708.5, -704.8);
            DrapeOffset offset = DrapeAnchor.For(moved, smoothShading: true, -708.5 + 1419.0, -704.8);

            run.Within(offset.Xm, atOrigin.Xm, 1e-9, "x");
            run.Within(offset.Ym, atOrigin.Ym, 1e-9, "y");
            run.Within(moved.LeftM, 709.5, 1e-9, "left");
            run.Within(moved.WidthM, placement.WidthM, 1e-9, "the scale is unchanged");
        });

        run.Case("the anchor record's schema has a GUID of its own and the add-in's vendor", () =>
        {
            run.True(ProjectAnchorStorage.SchemaGuid != ProvenanceStorage.SchemaGuid, "not the provenance schema");

            string? dir = RepoTree.Find(
                "revit/src/MantlePlace.Revit.Addin",
                candidate => File.Exists(Path.Combine(candidate, "MantlePlace.addin")));
            if (dir is null)
            {
                run.Fail("could not find the add-in manifest from the test assembly");
                return;
            }

            string? vendor = XDocument.Load(Path.Combine(dir, "MantlePlace.addin"))
                .Descendants("VendorId")
                .FirstOrDefault()?.Value;
            run.Equal(ProjectAnchorStorage.VendorId, vendor, "one vendor id");
        });

        return run.Report("adjacent bundles");
    }
}
