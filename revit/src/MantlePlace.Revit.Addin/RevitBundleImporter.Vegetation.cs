// UseWPF switches the SDK to the WindowsDesktop implicit-usings set, which drops System.IO.
using System.IO;
using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// The vegetation step: one trunk-and-crown DirectShape per published tree.
internal sealed partial class RevitBundleImporter
{
    /// <summary>Trunk height as a fraction of total height — the rest is crown.</summary>
    private const double TrunkHeightFraction = 0.35;

    /// <summary>Trunk radius as a fraction of crown radius.</summary>
    private const double TrunkRadiusFraction = 0.12;

    /// <summary>Crown radius at the apex, as a fraction of its widest — never zero.</summary>
    private const double CrownApexFraction = 0.05;

    /// <summary>
    /// Trees as Generic Model DirectShapes, dimensioned from the CSV — Forma's "Vegetation" row.
    /// </summary>
    /// <remarks>
    /// The tree-points file carries <c>height_m</c> and <c>crown_radius_m</c> per tree, so these are
    /// real proxy geometry — a trunk and a tapered crown at the published size — rather than the
    /// markers a points-only layer would justify. Anything Revit refuses to build is counted and
    /// reported; one bad row must not cost the curator the other forty-three.
    /// </remarks>
    private void ImportVegetation(ImportStep step)
    {
        if (step.Frame is not { } frame)
        {
            return;
        }

        string csvPath = _archive.Extract(step.EntryName, ImportStepKinds.LifetimeOf(step.Kind), step.ExpectedSha256);
        string? parseError = TreePointsReader.TryParse(File.ReadAllText(csvPath), frame, out IReadOnlyList<SiteTree> trees);
        if (parseError is not null)
        {
            Say(parseError);
            return;
        }

        ElementId category = DirectShapeCategory(BuiltInCategory.OST_Planting);
        int created = 0;

        ImportFailureSwallower swallower = new("Importing the vegetation");
        using Transaction transaction = BeginTransaction("Mantle Place: vegetation", swallower);

        foreach (SiteTree tree in trees)
        {
            if (BuildTreeGeometry(tree) is { Count: > 0 } geometry
                && TryCreateDirectShape(category, geometry, "Tree"))
            {
                created++;
            }
        }

        if (!CommitAndReport(transaction, swallower))
        {
            // The swallower already said why. Reporting the work below as done would be a lie:
            // the rollback took all of it.
            return;
        }

        Say($"Imported {created:N0} tree(s) of {trees.Count:N0} from {step.EntryName}.");
    }

    /// <summary>
    /// A trunk and a tapered crown, both at the published dimensions.
    /// </summary>
    /// <remarks>
    /// Two extrusion-family primitives rather than one revolve: a blend between two circles is a
    /// truncated cone whose behaviour is unambiguous, where a revolved silhouette depends on which
    /// of the frame's axes Revit reads as the axis of revolution. The crown's top radius is a small
    /// fraction of its base rather than zero, because a degenerate loop is not a curve loop.
    /// </remarks>
    private static List<GeometryObject>? BuildTreeGeometry(SiteTree tree)
    {
        double height = MetresToInternal(tree.HeightM);
        double crownRadius = MetresToInternal(tree.CrownRadiusM);
        if (height <= 0.0 || crownRadius <= 0.0)
        {
            return null;
        }

        double east = MetresToInternal(tree.EastM);
        double north = MetresToInternal(tree.NorthM);
        double ground = MetresToInternal(tree.GroundElevationM);

        double trunkHeight = height * TrunkHeightFraction;
        double trunkRadius = crownRadius * TrunkRadiusFraction;

        try
        {
            Solid trunk = GeometryCreationUtilities.CreateExtrusionGeometry(
                [Circle(new XYZ(east, north, ground), trunkRadius)],
                XYZ.BasisZ,
                trunkHeight);

            Solid crown = GeometryCreationUtilities.CreateBlendGeometry(
                Circle(new XYZ(east, north, ground + trunkHeight), crownRadius),
                Circle(new XYZ(east, north, ground + height), crownRadius * CrownApexFraction),
                null);

            return [trunk, crown];
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
        {
            return null;
        }
    }

    /// <summary>A horizontal circle as two half-arcs — the shape Revit's curve loops want.</summary>
    private static CurveLoop Circle(XYZ centre, double radius) => CurveLoop.Create(
    [
        Arc.Create(centre, radius, 0.0, Math.PI, XYZ.BasisX, XYZ.BasisY),
        Arc.Create(centre, radius, Math.PI, 2.0 * Math.PI, XYZ.BasisX, XYZ.BasisY),
    ]);
}
