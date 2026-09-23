// UseWPF switches the SDK to the WindowsDesktop implicit-usings set, which drops System.IO.
using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// The vegetation step: one Mantle Place Tree instance per published tree, in chunks, each stamped —
// or, when that family cannot be had, the trunk-and-crown DirectShape it replaced.
internal sealed partial class RevitBundleImporter
{
    /// <summary>The embedded family, as <c>MantlePlace.Revit.Addin.csproj</c> names the resource.</summary>
    private const string TreeFamilyResource = "MantlePlace.Revit.Addin.Families.MantlePlaceTree.rfa";

    /// <summary>
    /// Trees as instances of the <see cref="TreeFamily"/> Planting family, sized per instance from the
    /// CSV, created in chunks, each tree stamped with its row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tree-points file carries <c>height_m</c> and <c>crown_radius_m</c> per tree, and both are
    /// written to the instance verbatim — <see cref="TreeFamily.HeightParameter"/> and
    /// <see cref="TreeFamily.CrownRadiusParameter"/> — so a tree is a Planting element a curator can
    /// schedule, filter, resize and hand to a renderer, rather than anonymous geometry. When the family
    /// cannot be loaded, <see cref="TreeFamilyChoice"/> falls back to the DirectShapes the step built
    /// before it, at the same size and place, and the log says so. Anything Revit refuses to build is
    /// counted and reported; one bad row must not cost the curator the other forty-three.
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
    /// (<see cref="TreeIdentity"/>) and a re-import of the same build creates only what is missing —
    /// whichever path built the trees already there.
    /// </para>
    /// </remarks>
    private IEnumerable<StepProgress> ImportVegetation(ImportStep step)
    {
        if (step.Frame is not { } frame)
        {
            yield break;
        }

        string csvPath = _archive.Extract(step.EntryName, ImportStepKinds.LifetimeOf(step.Kind), step.ExpectedSha256);
        // The vocabulary rides on the step because the manifest owns what the CSV's foliage values
        // mean. Nothing here reads a point's foliage type yet — one Planting family is what this
        // build ships — so every point is placed as a tree, exactly as it was before the column
        // existed. The shrub family, and what the parse's foliage notes and counts have to say, land with it.
        TreePointsParse parse = TreePointsReader.Parse(
            File.ReadAllText(csvPath),
            frame,
            step.FoliageTypeVocabulary,
            step.Units);
        if (parse.Failure is not null)
        {
            Say(parse.Failure);
            yield break;
        }

        IReadOnlyList<SiteTreePoint> trees = parse.Points;

        string stem = _archive.Layout.Key.Stem;
        TreeDecision decision = TreeIdentity.Decide(ExistingTreeComments(), stem, step.ExpectedSha256, trees.Count);
        if (decision.Disposition == TreeDisposition.RefuseStale)
        {
            Say(decision.Explanation);
            yield break;
        }

        // A tree Revit cannot build is left out before the chunks are cut, so it cannot take a chunk's
        // transaction down with it; it is unstamped, so a re-import meets it again and says so again.
        List<int> rows = [.. decision.RowsToCreate.Where(row => TreeFamily.Fits(trees[row]))];
        int unbuildable = decision.RowsToCreate.Count - rows.Count;

        TreeGeometry geometry = rows.Count > 0 ? PrepareTreeGeometry() : TreeGeometry.None;
        int created = 0;
        int unstamped = 0;
        int unsized = 0;
        foreach (ImportChunk chunk in ImportChunking.Chunks(rows.Count))
        {
            TreeChunkResult result = CreateTreeChunk(geometry, trees, rows, chunk, stem, step.ExpectedSha256);
            if (!result.Committed)
            {
                // The swallower already said why, and the rollback took this chunk. The chunks before
                // it stand, stamped; a re-import of this build picks up from here.
                Say($"Stopped the trees after {created:N0} of {rows.Count:N0}: Revit did not accept a chunk.");
                yield break;
            }

            created += result.Created;
            unstamped += result.Unstamped;
            unsized += result.Unsized;
            yield return new StepProgress(chunk.Start + chunk.Count, rows.Count);
        }

        // Which path built them is only worth saying about trees that were built.
        string summary = $"Imported {created:N0} tree(s) of {trees.Count:N0} from {step.EntryName}"
            + (created == 0 ? string.Empty
                : geometry.IsFamily ? $" as \"{TreeFamily.FamilyName}\" instances" : " as DirectShapes");
        // Rows the parse left out are said first: they never reached the "of" count, so without
        // these clauses a half-blank ground_z column reads as a whole layer.
        if (parse.RowsWithoutGround > 0)
        {
            summary += $"; {parse.RowsWithoutGround:N0} had no ground elevation in the file and were left out";
        }

        if (parse.UnreadableRows > 0)
        {
            summary += $"; {parse.UnreadableRows:N0} could not be read and were left out";
        }

        if (decision.AlreadyPresent > 0)
        {
            summary += $"; {decision.AlreadyPresent:N0} from an earlier import of this build were already present and left alone";
        }

        if (unbuildable > 0)
        {
            summary += $"; {unbuildable:N0} had a height or crown too small for Revit to build and were left out";
        }

        if (unsized > 0)
        {
            summary += $"; {unsized:N0} would not take their published size or elevation and stand at the family's default";
        }

        if (unstamped > 0)
        {
            summary += $"; {unstamped:N0} could not be stamped and will not be recognised by a re-import";
        }

        Say(summary + ".");
    }

    /// <summary>What the chunks build with: a family type on a level, or DirectShapes on a category.</summary>
    private readonly record struct TreeGeometry(FamilySymbol? Symbol, Level? Level, ElementId Category)
    {
        internal static TreeGeometry None => new(null, null, ElementId.InvalidElementId);

        /// <summary>Family instances; otherwise DirectShapes on <see cref="Category"/>.</summary>
        internal bool IsFamily => Symbol is not null && Level is not null;
    }

    /// <summary>What one chunk's transaction did.</summary>
    private readonly record struct TreeChunkResult(bool Committed, int Created, int Unstamped, int Unsized);

    /// <summary>
    /// The tree family and the level to host it — loaded into the project if it is not there yet — or
    /// the DirectShape fallback, with the reason said.
    /// </summary>
    /// <remarks>
    /// A family already in the project under the name is used as it stands and never reloaded over:
    /// a curator may have given its type a render substitution or a material, and a reload is how
    /// that work would be lost. The cost is that a later build's family never replaces an earlier one
    /// in a project that has it; the curator reloads it by hand, which the README says.
    /// <see cref="TreeFamilyChoice"/> still checks it carries the parameters
    /// this step writes, because a family of that name is not proof it is this one.
    /// </remarks>
    private TreeGeometry PrepareTreeGeometry()
    {
        Family? family = TreeInstances.Find(_document);
        string? failure = family is null ? LoadTreeFamily(out family) : null;

        List<TreeFamilyParameter> parameters = [];
        if (family is not null)
        {
            try
            {
                parameters = TreeInstances.ParametersOf(_document, family);
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
            {
                failure = $"its definition could not be read: {ex.Message}";
            }
        }

        ISet<ElementId> types = family?.GetFamilySymbolIds() ?? new HashSet<ElementId>();
        TreeFamilyDecision decision = TreeFamilyChoice.Decide(failure, types.Count, parameters, CollectLevels());
        if (!decision.UseFamily)
        {
            Say(decision.Explanation);
            return new TreeGeometry(null, null, DirectShapeCategory(BuiltInCategory.OST_Planting));
        }

        return new TreeGeometry(
            (FamilySymbol)_document.GetElement(types.First()),
            (Level)_document.GetElement(new ElementId(decision.LevelId)),
            ElementId.InvalidElementId);
    }

    /// <summary>Loads the family this assembly carries. Returns why not, or <c>null</c> on success.</summary>
    /// <remarks>
    /// The <c>.rfa</c> travels inside the assembly, so an add-in that loaded has its family and no
    /// installer or package step can drop it. <see cref="FamilyFileStore"/> puts it on disk under the
    /// family's name for this build; only the load itself is Revit's.
    /// </remarks>
    private string? LoadTreeFamily(out Family? family)
    {
        family = null;
        try
        {
            Assembly assembly = typeof(RevitBundleImporter).Assembly;
            using Stream? resource = assembly.GetManifestResourceStream(TreeFamilyResource);
            if (resource is null)
            {
                return "this build of the add-in does not carry it";
            }

            using MemoryStream bytes = new();
            resource.CopyTo(bytes);
            string path = FamilyFileStore.Materialise(
                FamilyFileStore.DefaultRoot,
                assembly.ManifestModule.ModuleVersionId.ToString("N"),
                TreeFamily.FileName,
                bytes.ToArray());

            ImportFailureSwallower swallower = new("Loading the tree family");
            using Transaction transaction = BeginTransaction("Mantle Place: tree family", swallower);
            bool loaded = _document.LoadFamily(path, out family);
            if (!CommitAndReport(transaction, swallower) || !loaded || family is null)
            {
                family = null;
                return "Revit did not accept the file";
            }

            return null;
        }
        catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or Autodesk.Revit.Exceptions.ApplicationException)
        {
            family = null;
            return ex.Message;
        }
    }

    /// <summary>Creates, stamps and commits one chunk of trees.</summary>
    private TreeChunkResult CreateTreeChunk(
        TreeGeometry geometry,
        IReadOnlyList<SiteTreePoint> trees,
        IReadOnlyList<int> rows,
        ImportChunk chunk,
        string stem,
        string? sha256)
    {
        int created = 0;
        int unstamped = 0;
        int unsized = 0;

        ImportFailureSwallower swallower = new("Importing the vegetation");
        using Transaction transaction = BeginTransaction("Mantle Place: vegetation", swallower);

        if (geometry.Symbol is { IsActive: false } inactive)
        {
            inactive.Activate();
        }

        for (int index = chunk.Start; index < chunk.Start + chunk.Count; index++)
        {
            int row = rows[index];
            if (CreateTree(geometry, trees[row], out bool sized) is not { } tree)
            {
                continue;
            }

            created++;
            unsized += sized ? 0 : 1;

            // Comments is the tree's identity for the NEXT import. A tree it could not stamp is kept
            // — it is real — and cannot be recognised later, which the summary says.
            Parameter? comments = tree.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (comments is null || comments.IsReadOnly || !comments.Set(TreeIdentity.Stamp(stem, sha256, row + 1)))
            {
                unstamped++;
            }
        }

        return CommitAndReport(transaction, swallower)
            ? new TreeChunkResult(true, created, unstamped, unsized)
            : new TreeChunkResult(false, 0, 0, 0);
    }

    /// <summary>One tree by whichever path this step is on, or <c>null</c> when Revit refused it.</summary>
    private Element? CreateTree(TreeGeometry geometry, SiteTreePoint tree, out bool sized)
    {
        sized = true;
        if (geometry.IsFamily)
        {
            try
            {
                return TreeInstances.Place(_document, geometry.Symbol!, geometry.Level!, tree, out sized);
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
            {
                return null;
            }
        }

        return BuildTreeGeometry(tree) is { Count: > 0 } shape
            ? TryCreateDirectShape(geometry.Category, shape, "Tree")
            : null;
    }

    /// <summary>
    /// The Comments of every element that might be a tree: the Planting family instances, and every
    /// DirectShape for the trees the fallback built.
    /// </summary>
    private List<string?> ExistingTreeComments()
    {
        using FilteredElementCollector instances = new(_document);
        return [.. ExistingDirectShapeComments().Concat(instances
            .OfClass(typeof(FamilyInstance))
            .OfCategory(BuiltInCategory.OST_Planting)
            .Select(element => element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString()))];
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
    /// The fallback: a trunk and a tapered crown, both at the published dimensions, in the proportions
    /// the family's own formulas use.
    /// </summary>
    /// <remarks>
    /// Two extrusion-family primitives rather than one revolve: a blend between two circles is a
    /// truncated cone whose behaviour is unambiguous, where a revolved silhouette depends on which
    /// of the frame's axes Revit reads as the axis of revolution. The crown's top radius is a small
    /// fraction of its base rather than zero, because a degenerate loop is not a curve loop.
    /// </remarks>
    private static List<GeometryObject>? BuildTreeGeometry(SiteTreePoint tree)
    {
        double height = MetresToInternal(tree.HeightM);
        double crownRadius = MetresToInternal(tree.CrownRadiusM);
        double east = MetresToInternal(tree.EastM);
        double north = MetresToInternal(tree.NorthM);
        double ground = MetresToInternal(tree.GroundElevationM);

        double trunkHeight = height * TreeFamily.TrunkHeightFraction;
        double trunkRadius = crownRadius * TreeFamily.TrunkRadiusFraction;

        try
        {
            Solid trunk = GeometryCreationUtilities.CreateExtrusionGeometry(
                [Circle(new XYZ(east, north, ground), trunkRadius)],
                XYZ.BasisZ,
                trunkHeight);

            Solid crown = GeometryCreationUtilities.CreateBlendGeometry(
                Circle(new XYZ(east, north, ground + trunkHeight), crownRadius),
                Circle(new XYZ(east, north, ground + height), crownRadius * TreeFamily.CrownApexFraction),
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
