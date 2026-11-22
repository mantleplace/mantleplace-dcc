using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;
using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal sealed partial class RevitBundleImporter
{
    private const string TreeFamilyResource = "MantlePlace.Revit.Addin.Families.MantlePlaceTree.rfa";

    private const string ShrubFamilyResource = "MantlePlace.Revit.Addin.Families.MantlePlaceShrub.rfa";

    private IEnumerable<StepProgress> ImportVegetation(ImportStep step)
    {
        if (step.Frame is not { } frame || HeightsRefused(step))
        {
            yield break;
        }

        string csvPath = _archive.Extract(step.EntryName, step.Lifetime, step.ExpectedSha256);
        TreePointsParse parse = TreePointsReader.Parse(
            File.ReadAllText(csvPath),
            frame,
            step.FoliageTypeVocabulary,
            step.Units,
            rowLimit: step.Level is { Kind: FidelityLevelKind.RowCut } level ? level.Rows : null,
            layer: step.Layer,
            groundBaseM: step.HeightBaseM ?? 0.0);
        if (parse.Failure is not null)
        {
            Say(parse.Failure);
            yield break;
        }

        foreach (string note in parse.Notes)
        {
            Say(note);
        }

        IReadOnlyList<SiteTreePoint> points = parse.Points;

        string stem = _archive.Layout.Key.Stem;
        List<ExistingTreePoint> existing = ExistingTreePoints();
        TreeDecision decision = TreeIdentity.Decide(
            existing.Select(element => element.Comments), stem, step.ExpectedSha256, points.Count, step.LevelFiles);
        if (decision.Disposition != TreeDisposition.Create)
        {
            Say(decision.Explanation);
            yield break;
        }

        List<int> rows = [.. decision.RowsToCreate.Where(row => PlantingFamilies.Fits(points[row]))];
        int unbuildable = decision.RowsToCreate.Count - rows.Count;

        PlantingGeometry geometry = new(
            rows.Any(row => points[row].FoliageType == FoliageType.Tree) ? PrepareGeometry(FoliageType.Tree) : TreeGeometry.None,
            rows.Any(row => points[row].FoliageType == FoliageType.Shrub) ? PrepareGeometry(FoliageType.Shrub) : TreeGeometry.None);

        Announce(SlowStepNotice.For(step.Kind, _terrainVertexCount, rows.Count));

        TreeChunkResult total = default;
        foreach (ImportChunk chunk in ImportChunking.Chunks(rows.Count))
        {
            TreeChunkResult result = CreateTreeChunk(geometry, points, rows, chunk, stem, step.ExpectedSha256);
            if (!result.Committed)
            {
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
            RowsWithoutGround = parse.RowsWithoutGround,
            UnreadableRows = parse.UnreadableRows,
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

        string levelSentence = FidelityCuts.RowCutSentence(step.Level, decision.KeptAboveLevel);
        if (levelSentence.Length > 0)
        {
            Say(levelSentence);
        }

        string mismatches = TreeIdentity.FoliageMismatchNote(
            TreeIdentity.FoliageMismatches(existing, stem, step.ExpectedSha256, points),
            stem,
            step.ExpectedSha256);
        if (mismatches.Length > 0)
        {
            Say(mismatches);
        }
    }

    private readonly record struct TreeGeometry(FamilySymbol? Symbol, Level? Level, ElementId Category)
    {
        internal static TreeGeometry None => new(null, null, ElementId.InvalidElementId);

        internal bool IsFamily => Symbol is not null && Level is not null;
    }

    private readonly record struct PlantingGeometry(TreeGeometry Tree, TreeGeometry Shrub)
    {
        internal TreeGeometry For(FoliageType foliage) => foliage == FoliageType.Shrub ? Shrub : Tree;
    }

    private readonly record struct TreeChunkResult(bool Committed, int Trees, int Shrubs, int Unstamped, int Unsized)
    {
        internal TreeChunkResult Add(TreeChunkResult other) => new(
            true, Trees + other.Trees, Shrubs + other.Shrubs, Unstamped + other.Unstamped, Unsized + other.Unsized);
    }

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
                return "this build of the plugin does not carry it";
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

    private Element? CreateTreePoint(TreeGeometry geometry, SiteTreePoint point, out bool sized)
    {
        sized = true;
        if (geometry.IsFamily)
        {
            try
            {
                return TreeInstances.Place(_document, geometry.Symbol!, geometry.Level!, point, out sized, Anchoring.OffsetEastM, Anchoring.OffsetNorthM);
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

    private List<GeometryObject>? BuildTreeGeometry(SiteTreePoint tree)
    {
        double height = MetresToInternal(tree.HeightM);
        double crownRadius = MetresToInternal(tree.CrownRadiusM);
        double east = MetresToInternal(tree.EastM + Anchoring.OffsetEastM);
        double north = MetresToInternal(tree.NorthM + Anchoring.OffsetNorthM);
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

    private List<GeometryObject>? BuildShrubGeometry(SiteTreePoint shrub)
    {
        double height = MetresToInternal(shrub.HeightM);
        double crownRadius = MetresToInternal(shrub.CrownRadiusM);
        double east = MetresToInternal(shrub.EastM + Anchoring.OffsetEastM);
        double north = MetresToInternal(shrub.NorthM + Anchoring.OffsetNorthM);
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

    private static CurveLoop Circle(XYZ centre, double radius) => CurveLoop.Create(
    [
        Arc.Create(centre, radius, 0.0, Math.PI, XYZ.BasisX, XYZ.BasisY),
        Arc.Create(centre, radius, Math.PI, 2.0 * Math.PI, XYZ.BasisX, XYZ.BasisY),
    ]);
}
