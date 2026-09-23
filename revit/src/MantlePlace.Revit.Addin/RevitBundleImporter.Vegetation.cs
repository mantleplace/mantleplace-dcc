// UseWPF switches the SDK to the WindowsDesktop implicit-usings set, which drops System.IO.
using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// The planting step: one instance per published tree point, of the Planting family its foliage type
// names, in chunks, each stamped — or, when that family cannot be had, the DirectShape it replaced.
internal sealed partial class RevitBundleImporter
{
    /// <summary>The embedded tree family, as <c>MantlePlace.Revit.Addin.csproj</c> names the resource.</summary>
    private const string TreeFamilyResource = "MantlePlace.Revit.Addin.Families.MantlePlaceTree.rfa";

    /// <summary>The embedded shrub family, as <c>MantlePlace.Revit.Addin.csproj</c> names the resource.</summary>
    private const string ShrubFamilyResource = "MantlePlace.Revit.Addin.Families.MantlePlaceShrub.rfa";

    /// <summary>
    /// Tree points as instances of the Planting family their published foliage type names —
    /// <see cref="TreeFamily"/> or <see cref="ShrubFamily"/> — sized per instance from the CSV, created
    /// in chunks, each stamped with its row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The tree-points file carries <c>height_m</c> and <c>crown_radius_m</c> per point, and both are
    /// written to the instance verbatim — the family's height parameter and
    /// <see cref="TreeFamily.CrownRadiusParameter"/> — so a tree point is a Planting element a curator
    /// can schedule, filter, resize and hand to a renderer, rather than anonymous geometry. When a
    /// family cannot be loaded, <see cref="TreeFamilyChoice"/> falls back to the DirectShapes the step
    /// built before it, at the same size and place, and the log says so. Anything Revit refuses to
    /// build is counted and reported; one bad row must not cost the curator the other forty-three.
    /// </para>
    /// <para>
    /// <b>Each family decides its own fallback.</b> A shrub family that will not load builds the shrubs
    /// as DirectShapes and leaves the trees where their own family put them, and the shrub family is
    /// loaded only when at least one shrub will be created. Rows stay in file order across both, so a
    /// chunk that fails leaves the same clean prefix of rows behind whichever families it held.
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
    /// A cancelled chunk's points are kept, so every point carries its stamp
    /// (<see cref="TreeIdentity"/>) and a re-import of the same build creates only what is missing —
    /// whichever path, and whichever plugin, built the points already there.
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
        // mean; the parse maps them, and nothing reads a point's size to decide one.
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

        // Said once each, about the file as a whole, before anything is built.
        foreach (string note in parse.Notes)
        {
            Say(note);
        }

        IReadOnlyList<SiteTreePoint> points = parse.Points;

        string stem = _archive.Layout.Key.Stem;
        List<ExistingTreePoint> existing = ExistingTreePoints();
        TreeDecision decision = TreeIdentity.Decide(
            existing.Select(element => element.Comments), stem, step.ExpectedSha256, points.Count);
        if (decision.Disposition == TreeDisposition.RefuseStale)
        {
            Say(decision.Explanation);
            yield break;
        }

        // A point Revit cannot build is left out before the chunks are cut, so it cannot take a chunk's
        // transaction down with it; it is unstamped, so a re-import meets it again and says so again.
        List<int> rows = [.. decision.RowsToCreate.Where(row => PlantingFamilies.Fits(points[row]))];
        int unbuildable = decision.RowsToCreate.Count - rows.Count;

        // A family is prepared only if a row will use it, so a bundle with no shrubs never loads the
        // shrub family, and each decides its own fallback.
        PlantingGeometry geometry = new(
            rows.Any(row => points[row].FoliageType == FoliageType.Tree) ? PrepareGeometry(FoliageType.Tree) : TreeGeometry.None,
            rows.Any(row => points[row].FoliageType == FoliageType.Shrub) ? PrepareGeometry(FoliageType.Shrub) : TreeGeometry.None);

        TreeChunkResult total = default;
        foreach (ImportChunk chunk in ImportChunking.Chunks(rows.Count))
        {
            TreeChunkResult result = CreateTreeChunk(geometry, points, rows, chunk, stem, step.ExpectedSha256);
            if (!result.Committed)
            {
                // The swallower already said why, and the rollback took this chunk. The chunks before
                // it stand, stamped; a re-import of this build picks up from here.
                Say($"Stopped the tree points after {total.Trees + total.Shrubs:N0} of {rows.Count:N0}: Revit did not accept a chunk.");
                yield break;
            }

            total = total.Add(result);
            yield return new StepProgress(chunk.Start + chunk.Count, rows.Count);
        }

        Say(PlantingSummary.Sentence(new PlantingTally
        {
            EntryName = step.EntryName,
            PointCount = points.Count,
            HasVocabulary = step.FoliageTypeVocabulary is not null,
            TreesCreated = total.Trees,
            ShrubsCreated = total.Shrubs,
            TreesAsFamily = geometry.Tree.IsFamily,
            ShrubsAsFamily = geometry.Shrub.IsFamily,
            AlreadyPresent = decision.AlreadyPresent,
            Unbuildable = unbuildable,
            Unsized = total.Unsized,
            Unstamped = total.Unstamped,
            EmptyFoliageCells = parse.EmptyFoliageCells,
            UnknownFoliageValues = parse.UnknownFoliageValues,
        }));

        // Informational: the reused rows are left as they are, and the checklist stays green.
        string mismatches = TreeIdentity.FoliageMismatchNote(
            TreeIdentity.FoliageMismatches(existing, stem, step.ExpectedSha256, points),
            stem,
            step.ExpectedSha256);
        if (mismatches.Length > 0)
        {
            Say(mismatches);
        }
    }

    /// <summary>What one family's rows build with: a family type on a level, or DirectShapes on a category.</summary>
    private readonly record struct TreeGeometry(FamilySymbol? Symbol, Level? Level, ElementId Category)
    {
        internal static TreeGeometry None => new(null, null, ElementId.InvalidElementId);

        /// <summary>Family instances; otherwise DirectShapes on <see cref="Category"/>.</summary>
        internal bool IsFamily => Symbol is not null && Level is not null;
    }

    /// <summary>Each foliage type's geometry, decided separately.</summary>
    private readonly record struct PlantingGeometry(TreeGeometry Tree, TreeGeometry Shrub)
    {
        internal TreeGeometry For(FoliageType foliage) => foliage == FoliageType.Shrub ? Shrub : Tree;
    }

    /// <summary>What one chunk's transaction did, or, summed, what the whole step did.</summary>
    private readonly record struct TreeChunkResult(bool Committed, int Trees, int Shrubs, int Unstamped, int Unsized)
    {
        internal TreeChunkResult Add(TreeChunkResult other) => new(
            true, Trees + other.Trees, Shrubs + other.Shrubs, Unstamped + other.Unstamped, Unsized + other.Unsized);
    }

    /// <summary>
    /// One foliage type's family and the level to host it — loaded into the project if it is not
    /// there yet — or the DirectShape fallback, with the reason said.
    /// </summary>
    /// <remarks>
    /// A family already in the project under the name is used as it stands and never reloaded over:
    /// a curator may have given its type a render substitution or a material, and a reload is how
    /// that work would be lost. The cost is that a later build's family never replaces an earlier one
    /// in a project that has it; the curator reloads it by hand, which the README says.
    /// <see cref="TreeFamilyChoice"/> still checks it carries the parameters
    /// this step writes, because a family of that name is not proof it is this one.
    /// </remarks>
    private TreeGeometry PrepareGeometry(FoliageType foliage)
    {
        Family? family = TreeInstances.Find(_document, PlantingFamilies.FamilyName(foliage));
        string? failure = family is null ? LoadFamily(foliage, out family) : null;

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
        TreeFamilyDecision decision = TreeFamilyChoice.Decide(foliage, failure, types.Count, parameters, CollectLevels());
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

    /// <summary>
    /// Loads the family this assembly carries for a foliage type. Returns why not, or <c>null</c> on
    /// success.
    /// </summary>
    /// <remarks>
    /// The <c>.rfa</c> travels inside the assembly, so an add-in that loaded has its family and no
    /// installer or package step can drop it. <see cref="FamilyFileStore"/> puts it on disk under the
    /// family's name for this build; only the load itself is Revit's.
    /// </remarks>
    private string? LoadFamily(FoliageType foliage, out Family? family)
    {
        family = null;
        bool shrub = foliage == FoliageType.Shrub;
        try
        {
            Assembly assembly = typeof(RevitBundleImporter).Assembly;
            using Stream? resource = assembly.GetManifestResourceStream(shrub ? ShrubFamilyResource : TreeFamilyResource);
            if (resource is null)
            {
                return "this build of the add-in does not carry it";
            }

            using MemoryStream bytes = new();
            resource.CopyTo(bytes);
            string path = FamilyFileStore.Materialise(
                FamilyFileStore.DefaultRoot,
                assembly.ManifestModule.ModuleVersionId.ToString("N"),
                shrub ? ShrubFamily.FileName : TreeFamily.FileName,
                bytes.ToArray());

            string what = shrub ? "shrub" : "tree";
            ImportFailureSwallower swallower = new($"Loading the {what} family");
            using Transaction transaction = BeginTransaction($"Mantle Place: {what} family", swallower);
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

    /// <summary>Creates, stamps and commits one chunk of tree points, of either foliage type.</summary>
    private TreeChunkResult CreateTreeChunk(
        PlantingGeometry geometry,
        IReadOnlyList<SiteTreePoint> points,
        IReadOnlyList<int> rows,
        ImportChunk chunk,
        string stem,
        string? sha256)
    {
        int trees = 0;
        int shrubs = 0;
        int unstamped = 0;
        int unsized = 0;

        ImportFailureSwallower swallower = new("Importing the vegetation");
        using Transaction transaction = BeginTransaction("Mantle Place: vegetation", swallower);

        foreach (FamilySymbol? symbol in (FamilySymbol?[])[geometry.Tree.Symbol, geometry.Shrub.Symbol])
        {
            if (symbol is { IsActive: false } inactive)
            {
                inactive.Activate();
            }
        }

        for (int index = chunk.Start; index < chunk.Start + chunk.Count; index++)
        {
            int row = rows[index];
            SiteTreePoint point = points[row];
            if (CreateTreePoint(geometry.For(point.FoliageType), point, out bool sized) is not { } element)
            {
                continue;
            }

            bool isShrub = point.FoliageType == FoliageType.Shrub;
            trees += isShrub ? 0 : 1;
            shrubs += isShrub ? 1 : 0;
            unsized += sized ? 0 : 1;

            // Comments is the point's identity for the NEXT import. A point it could not stamp is kept
            // — it is real — and cannot be recognised later, which the summary says.
            Parameter? comments = element.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (comments is null || comments.IsReadOnly || !comments.Set(TreeIdentity.Stamp(stem, sha256, row + 1)))
            {
                unstamped++;
            }
        }

        return CommitAndReport(transaction, swallower)
            ? new TreeChunkResult(true, trees, shrubs, unstamped, unsized)
            : new TreeChunkResult(false, 0, 0, 0, 0);
    }

    /// <summary>
    /// One tree point by whichever path its family is on, or <c>null</c> when Revit refused it.
    /// </summary>
    private Element? CreateTreePoint(TreeGeometry geometry, SiteTreePoint point, out bool sized)
    {
        sized = true;
        if (geometry.IsFamily)
        {
            try
            {
                return TreeInstances.Place(_document, geometry.Symbol!, geometry.Level!, point, out sized);
            }
            catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
            {
                return null;
            }
        }

        List<GeometryObject>? shape = point.FoliageType == FoliageType.Shrub
            ? BuildShrubGeometry(point)
            : BuildTreeGeometry(point);
        return shape is { Count: > 0 }
            ? TryCreateDirectShape(geometry.Category, shape, PlantingFamilies.DirectShapeName(point.FoliageType))
            : null;
    }

    /// <summary>
    /// Every element that might be a tree point: the Planting family instances, with their family's
    /// name, and every DirectShape — which has none — for the points a fallback built.
    /// </summary>
    private List<ExistingTreePoint> ExistingTreePoints()
    {
        using FilteredElementCollector instances = new(_document);
        return [.. ExistingDirectShapeComments()
            .Select(comments => new ExistingTreePoint(comments, null))
            .Concat(instances
                .OfClass(typeof(FamilyInstance))
                .OfCategory(BuiltInCategory.OST_Planting)
                .Cast<FamilyInstance>()
                .Select(instance => new ExistingTreePoint(
                    instance.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString(),
                    instance.Symbol?.Family?.Name)))];
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
    /// The tree fallback: a trunk and a tapered crown, both at the published dimensions, in the
    /// proportions the family's own formulas use.
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

    /// <summary>
    /// The shrub fallback: the family's dome as two stacked blends, at the published dimensions, in
    /// <see cref="ShrubFamily"/>'s proportions.
    /// </summary>
    private static List<GeometryObject>? BuildShrubGeometry(SiteTreePoint shrub)
    {
        double height = MetresToInternal(shrub.HeightM);
        double crownRadius = MetresToInternal(shrub.CrownRadiusM);
        double east = MetresToInternal(shrub.EastM);
        double north = MetresToInternal(shrub.NorthM);
        double ground = MetresToInternal(shrub.GroundElevationM);
        double waist = ground + (height * ShrubFamily.WaistHeightFraction);

        try
        {
            Solid lower = GeometryCreationUtilities.CreateBlendGeometry(
                Circle(new XYZ(east, north, ground), crownRadius * ShrubFamily.BaseRadiusFraction),
                Circle(new XYZ(east, north, waist), crownRadius),
                null);

            Solid upper = GeometryCreationUtilities.CreateBlendGeometry(
                Circle(new XYZ(east, north, waist), crownRadius),
                Circle(new XYZ(east, north, ground + height), crownRadius * ShrubFamily.ApexRadiusFraction),
                null);

            return [lower, upper];
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
