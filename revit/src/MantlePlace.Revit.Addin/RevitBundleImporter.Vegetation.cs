// UseWPF switches the SDK to the WindowsDesktop implicit-usings set, which drops System.IO.
using System.IO;
using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// The vegetation step: one trunk-and-crown DirectShape per published tree, in chunks, each stamped.
internal sealed partial class RevitBundleImporter
{
    /// <summary>Trunk height as a fraction of total height — the rest is crown.</summary>
    private const double TrunkHeightFraction = 0.35;

    /// <summary>Trunk radius as a fraction of crown radius.</summary>
    private const double TrunkRadiusFraction = 0.12;

    /// <summary>Crown radius at the apex, as a fraction of its widest — never zero.</summary>
    private const double CrownApexFraction = 0.05;

    /// <summary>
    /// Trees as Planting DirectShapes, dimensioned from the CSV — Forma's "Vegetation" row — created
    /// in chunks, each tree stamped with its row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tree-points file carries <c>height_m</c> and <c>crown_radius_m</c> per tree, so these are
    /// real proxy geometry — a trunk and a tapered crown at the published size — rather than the
    /// markers a points-only layer would justify. Anything Revit refuses to build is counted and
    /// reported; one bad row must not cost the curator the other forty-three.
    /// </para>
    /// <para>
    /// ⛔ <b>One transaction per chunk, and a yield after each commit.</b> The whole layer used to be
    /// one transaction, so the largest bundles' tens of thousands of trees were one uninterruptible
    /// call. Each yield hands Revit its message loop back (<see cref="StagedImport"/>): the import
    /// window moves, and a Cancel stops the step at the next boundary with every committed chunk
    /// kept. Nothing may yield inside a transaction — the commit comes first, in
    /// <see cref="CreateTreeChunk"/>, which is a separate method so that it cannot.
    /// </para>
    /// <para>
    /// A cancelled chunk's trees are kept, so every tree carries its stamp
    /// (<see cref="TreeIdentity"/>) and a re-import of the same build creates only what is missing.
    /// </para>
    /// </remarks>
    private IEnumerable<StepProgress> ImportVegetation(ImportStep step)
    {
        if (step.Frame is not { } frame)
        {
            yield break;
        }

        string csvPath = _archive.Extract(step.EntryName, ImportStepKinds.LifetimeOf(step.Kind), step.ExpectedSha256);
        string? parseError = TreePointsReader.TryParse(File.ReadAllText(csvPath), frame, out IReadOnlyList<SiteTree> trees);
        if (parseError is not null)
        {
            Say(parseError);
            yield break;
        }

        string stem = _archive.Layout.Key.Stem;
        TreeDecision decision = TreeIdentity.Decide(ExistingDirectShapeComments(), stem, step.ExpectedSha256, trees.Count);
        if (decision.Disposition == TreeDisposition.RefuseStale)
        {
            Say(decision.Explanation);
            yield break;
        }

        ElementId category = DirectShapeCategory(BuiltInCategory.OST_Planting);
        IReadOnlyList<int> rows = decision.RowsToCreate;
        int created = 0;
        int unstamped = 0;
        foreach (ImportChunk chunk in ImportChunking.Chunks(rows.Count))
        {
            TreeChunkResult result = CreateTreeChunk(category, trees, rows, chunk, stem, step.ExpectedSha256);
            if (!result.Committed)
            {
                // The swallower already said why, and the rollback took this chunk. The chunks before
                // it stand, stamped; a re-import of this build picks up from here.
                Say($"Stopped the trees after {created:N0} of {rows.Count:N0}: Revit did not accept a chunk.");
                yield break;
            }

            created += result.Created;
            unstamped += result.Unstamped;
            yield return new StepProgress(chunk.Start + chunk.Count, rows.Count);
        }

        string summary = $"Imported {created:N0} tree(s) of {trees.Count:N0} from {step.EntryName}";
        if (decision.AlreadyPresent > 0)
        {
            summary += $"; {decision.AlreadyPresent:N0} from an earlier import of this build were already present and left alone";
        }

        if (unstamped > 0)
        {
            summary += $"; {unstamped:N0} could not be stamped and will not be recognised by a re-import";
        }

        Say(summary + ".");
    }

    /// <summary>What one chunk's transaction did.</summary>
    private readonly record struct TreeChunkResult(bool Committed, int Created, int Unstamped);

    /// <summary>Creates, stamps and commits one chunk of trees.</summary>
    private TreeChunkResult CreateTreeChunk(
        ElementId category,
        IReadOnlyList<SiteTree> trees,
        IReadOnlyList<int> rows,
        ImportChunk chunk,
        string stem,
        string? sha256)
    {
        int created = 0;
        int unstamped = 0;

        ImportFailureSwallower swallower = new("Importing the vegetation");
        using Transaction transaction = BeginTransaction("Mantle Place: vegetation", swallower);

        for (int index = chunk.Start; index < chunk.Start + chunk.Count; index++)
        {
            int row = rows[index];
            if (BuildTreeGeometry(trees[row]) is not { Count: > 0 } geometry
                || TryCreateDirectShape(category, geometry, "Tree") is not { } shape)
            {
                continue;
            }

            created++;

            // Comments is the tree's identity for the NEXT import. A tree it could not stamp is kept
            // — it is real — and cannot be recognised later, which the summary says.
            Parameter? comments = shape.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (comments is null || comments.IsReadOnly || !comments.Set(TreeIdentity.Stamp(stem, sha256, row + 1)))
            {
                unstamped++;
            }
        }

        return CommitAndReport(transaction, swallower)
            ? new TreeChunkResult(true, created, unstamped)
            : new TreeChunkResult(false, 0, 0);
    }

    /// <summary>
    /// The Comments of every DirectShape in the project. Whatever is not a tree stamp is ignored by
    /// <see cref="TreeIdentity"/>, and whatever is not a road stamp by <see cref="RoadIdentity"/>, so
    /// there is no category filter here to get wrong when a Revit without a Planting or Roads
    /// DirectShape category files them under Generic Model.
    /// </summary>
    private List<string?> ExistingDirectShapeComments()
    {
        using FilteredElementCollector collector = new(_document);
        return [.. collector
            .OfClass(typeof(DirectShape))
            .Select(element => element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString())];
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
