using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MantlePlace.AutoCad;
using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.AutoCad.Addin;

internal sealed record OrderImportResult(AutoCadImportPlan Plan, string Summary, bool PlacedAnything);

internal sealed class OrderImporter : IDisposable
{
    private readonly Document _document;
    private readonly LocalBundleArchive _archive;
    private readonly Type? _civil3d;
    private readonly Dictionary<AutoCadPart, string> _files = [];
    private readonly List<(PlannedFile File, string Path)> _recordFiles = [];
    private readonly Dictionary<AutoCadPart, string> _outcomes = [];
    private string? _integrityRefusal;
    private bool _placedAnything;

    private OrderImporter(Document document, LocalBundleArchive archive, Type? civil3d, AutoCadImportPlan plan, string? deliveryLine)
    {
        _document = document;
        _archive = archive;
        _civil3d = civil3d;
        Plan = plan;
        DeliveryLine = deliveryLine;
    }

    internal AutoCadImportPlan Plan { get; }

    internal bool Civil3d => _civil3d is not null;

    internal string? DeliveryLine { get; }

    internal static OrderImporter? Prepare(Document document, string zipPath, PresenceVerdict presence, out OrderImportResult? refused)
    {
        if (!LocalBundleImport.TryOpen(zipPath, out LocalBundleArchive? opened, out string? refusal))
        {
            refused = Refused(new AutoCadImportPlan { Refusal = refusal });
            return null;
        }

        bool handedOver = false;
        try
        {
            Database database = document.Database;
            Type? civil3d = Civil3dPartLoader.Load(presence, out _);
            DrawingZone? zone = null;
            if (civil3d is not null)
            {
                try
                {
                    zone = DrawingObservations.Zone(Civil3dPartLoader.ReadZoneCode(civil3d, database));
                }
#pragma warning disable CA1031
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    refused = Refused(new AutoCadImportPlan
                    {
                        Refusal = $"Civil 3D could not say this drawing's coordinate zone ({ex.Message}), so it cannot be shown to be the order's. Nothing was imported.",
                    });
                    return null;
                }
            }

            AutoCadBlockRead read = AutoCadBlockReader.Read(opened.Manifest, opened.ManifestText);
            AutoCadImportPlan plan = AutoCadImportPlanner.Plan(read, DrawingObservations.Geolocation(database), zone);
            if (plan.IsRefused)
            {
                refused = Refused(plan);
                return null;
            }

            refused = null;
            handedOver = true;
            return new OrderImporter(document, opened, civil3d, plan, DeliveryHeader.Describe(read.Manifest.Delivery));
        }
        finally
        {
            if (!handedOver)
            {
                opened.Dispose();
            }
        }
    }

    internal bool Verify()
    {
        foreach (PlannedPart part in Plan.Parts.Where(p => p.Place && (p.Kind != AutoCadPart.Surface || Civil3d)))
        {
            IEnumerable<(string Path, string Sha256, PlannedFile? File)> wanted = part.Kind == AutoCadPart.Records
                ? part.Files.Select(f => (f.Path, f.Sha256, (PlannedFile?)f))
                : [(part.Path, part.Sha256, null)];

            List<(PlannedFile File, string Path)> extracted = [];
            foreach ((string path, string sha256, PlannedFile? file) in wanted)
            {
                if (AutoCadBundleEntries.Resolve(_archive.EntryNames, path) is not { } entry)
                {
                    _outcomes[part.Kind] = AutoCadImportPlanner.FailedSentence(part.Kind, $"the bundle does not hold {path}.");
                    extracted.Clear();
                    break;
                }

                ExtractionLifetime lifetime = part.Kind == AutoCadPart.Aerial ? ExtractionLifetime.Retained : ExtractionLifetime.Transient;
                string local;
                try
                {
                    local = _archive.Extract(entry, lifetime, sha256);
                }
                catch (InvalidOperationException integrity)
                {
                    _integrityRefusal = integrity.Message;
                    return false;
                }

                if (file is null)
                {
                    _files[part.Kind] = local;
                }
                else
                {
                    extracted.Add((file, local));
                }
            }

            _recordFiles.AddRange(extracted);
        }

        return true;
    }

    internal bool Place(AutoCadPart kind)
    {
        if (_outcomes.ContainsKey(kind))
        {
            return false;
        }

        Database database = _document.Database;
        PlannedPart part = Plan.Part(kind);
        Func<string?>? place = kind switch
        {
            AutoCadPart.Aerial when _files.TryGetValue(kind, out string? image) => () => PlaceAerial(database, image, part, Plan.OrderId),
            AutoCadPart.Surface when _civil3d is not null && _files.TryGetValue(kind, out string? landXml) => () => BuildSurface(database, _civil3d!, landXml, part, Plan),
            AutoCadPart.Contours when _files.TryGetValue(kind, out string? dxf) => () => PlaceContours(database, dxf, part),
            AutoCadPart.Records when _recordFiles.Count > 0 => () => PlaceRecords(database, _recordFiles, part),
            AutoCadPart.Linework when _files.TryGetValue(kind, out string? linework) => () => PlaceLinework(database, linework, part),
            _ => null,
        };

        if (place is null)
        {
            return false;
        }

        using (_document.LockDocument())
        {
            bool placed = false;
            _outcomes[kind] = Run(kind, place, kind == AutoCadPart.Aerial ? part.Sentence : null, ref placed);
            _placedAnything |= placed;
            return placed;
        }
    }

    internal OrderImportResult Result(IReadOnlyDictionary<AutoCadPart, string>? notReached)
    {
        if (_integrityRefusal is not null)
        {
            return Refused(new AutoCadImportPlan { OrderId = Plan.OrderId, JobId = Plan.JobId, Frame = Plan.Frame, Refusal = _integrityRefusal });
        }

        Dictionary<AutoCadPart, string> said = new(_outcomes);
        foreach ((AutoCadPart kind, string sentence) in notReached ?? new Dictionary<AutoCadPart, string>())
        {
            _ = said.TryAdd(kind, sentence);
        }

        return new OrderImportResult(Plan, Plan.Summary(said), _placedAnything);
    }

    public void Dispose() => _archive.Dispose();

    private static OrderImportResult Refused(AutoCadImportPlan plan) => new(plan, plan.Summary(), false);

    private static string Run(AutoCadPart kind, Func<string?> place, string? placedSentence, ref bool placedAnything)
    {
        try
        {
            string? said = place();
            placedAnything = true;
            return said ?? placedSentence ?? string.Empty;
        }
        catch (PartRefusedException refused)
        {
            return AutoCadImportPlanner.FailedSentence(kind, refused.Message);
        }
        catch (Autodesk.AutoCAD.Runtime.Exception host)
        {
            return AutoCadImportPlanner.FailedSentence(kind, $"AutoCAD refused it ({host.ErrorStatus}: {host.Message}).");
        }
        catch (IOException io)
        {
            return AutoCadImportPlanner.FailedSentence(kind, $"its file could not be read ({io.Message}).");
        }
        catch (Exception refused) when (refused is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return AutoCadImportPlanner.FailedSentence(kind, $"the host refused it ({refused.GetType().Name}: {refused.Message}).");
        }
    }

    private static string BuildSurface(Database database, Type civil3d, string landXmlPath, PlannedPart part, AutoCadImportPlan plan)
    {
        string orderId = plan.OrderId;
        string? landXmlSurface;
        using (StreamReader reader = File.OpenText(landXmlPath))
        {
            if (LandXmlSurface.TryReadName(reader, plan, out landXmlSurface) is { } problem)
            {
                throw new PartRefusedException(problem + ".");
            }
        }

        if (LandXmlSurface.DrawingUnitsProblem(plan, Civil3dPartLoader.ReadDrawingUnits(civil3d, database)) is { } unitsProblem)
        {
            throw new PartRefusedException(unitsProblem + ".");
        }

        using (Transaction transaction = database.TransactionManager.StartTransaction())
        {
            _ = EnsureLayer(database, transaction, part.Layer);
            transaction.Commit();
        }

        (string name, int points) = Civil3dPartLoader.BuildSurfaceFromLandXml(civil3d, database, AutoCadImportPlanner.SurfaceName(orderId), landXmlPath, landXmlSurface!, part.Layer);
        if (points == 0)
        {
            throw new PartRefusedException($"Civil 3D built {name} from {part.Path} and read no points into it.");
        }

        return AutoCadImportPlanner.SurfacePlacedSentence(part, name, points);
    }

    private static string PlaceContours(Database database, string dxfPath, PlannedPart part)
    {
        IReadOnlyList<PublishedContour> contours = ReadPolylines(dxfPath, "contours");

        using Transaction transaction = database.TransactionManager.StartTransaction();
        int placed = AppendPolylines(database, transaction, contours, _ => part.Layer);
        transaction.Commit();
        return AutoCadImportPlanner.ContoursPlacedSentence(part, placed);
    }

    private static string PlaceRecords(Database database, IReadOnlyList<(PlannedFile File, string Path)> files, PlannedPart part)
    {
        AutoCadRecordSet set = AutoCadRecordSet.Read([.. files.Select(f => f.File)], f => File.OpenText(files.First(x => ReferenceEquals(x.File, f)).Path));

        if (set.Layers.Count == 0)
        {
            throw new PartRefusedException(set.RefusalSentence());
        }

        int placed = 0;
        using Transaction transaction = database.TransactionManager.StartTransaction();
        foreach (ReadRecordLayer layer in set.Layers)
        {
            placed += AppendPolylines(database, transaction, layer.Polylines, _ => layer.File.Layer);
        }

        transaction.Commit();
        return AutoCadImportPlanner.RecordsPlacedSentence(part, placed, set.Refused);
    }

    private static string PlaceLinework(Database database, string dxfPath, PlannedPart part)
    {
        IReadOnlyList<PublishedContour> polylines = ReadPolylines(dxfPath, "site linework");

        using Transaction transaction = database.TransactionManager.StartTransaction();
        int placed = AppendPolylines(database, transaction, polylines, p => AutoCadLayers.LineworkLayer(p.Layer));
        transaction.Commit();

        int layers = polylines.Where(p => p.Vertices.Count >= 2).Select(p => AutoCadLayers.LineworkLayer(p.Layer)).Distinct(StringComparer.Ordinal).Count();
        return AutoCadImportPlanner.LineworkPlacedSentence(part, placed, layers);
    }

    private static IReadOnlyList<PublishedContour> ReadPolylines(string dxfPath, string drawing)
    {
        using StreamReader reader = File.OpenText(dxfPath);
        return PublishedContourReader.TryParse(reader, drawing, out IReadOnlyList<PublishedContour>? polylines, out _) is { } problem
            ? throw new PartRefusedException(problem)
            : polylines!;
    }

    private static int AppendPolylines(Database database, Transaction transaction, IReadOnlyList<PublishedContour> polylines, Func<PublishedContour, string> layerFor)
    {
        BlockTableRecord model = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
        Dictionary<string, ObjectId> layers = new(StringComparer.Ordinal);

        int placed = 0;
        foreach (PublishedContour source in polylines)
        {
            if (source.Vertices.Count < 2)
            {
                continue;
            }

            string name = layerFor(source);
            if (!layers.TryGetValue(name, out ObjectId layer))
            {
                layer = EnsureLayer(database, transaction, name);
                layers[name] = layer;
            }

            Polyline polyline = new();
            for (int i = 0; i < source.Vertices.Count; i++)
            {
                polyline.AddVertexAt(i, new Point2d(source.Vertices[i].X, source.Vertices[i].Y), 0, 0, 0);
            }

            polyline.Closed = source.IsClosed;
            polyline.Elevation = source.Elevation;
            polyline.LayerId = layer;

            _ = model.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
            placed++;
        }

        return placed;
    }

    private static string? PlaceAerial(Database database, string imagePath, PlannedPart part, string orderId)
    {
        AerialInsertion insertion = part.Insertion ?? throw new PartRefusedException("the plan carries no insertion for it.");

        using Transaction transaction = database.TransactionManager.StartTransaction();
        ObjectId layer = EnsureLayer(database, transaction, part.Layer);

        ObjectId dictionaryId = RasterImageDef.GetImageDictionary(database);
        if (dictionaryId.IsNull)
        {
            dictionaryId = RasterImageDef.CreateImageDictionary(database);
        }

        DBDictionary images = (DBDictionary)transaction.GetObject(dictionaryId, OpenMode.ForWrite);
        string name = UniqueName(images, $"MantlePlace-Aerial-{SymbolSafe(orderId)}");

        RasterImageDef definition = new() { SourceFileName = imagePath };
        definition.Load();
        ObjectId definitionId = images.SetAt(name, definition);
        transaction.AddNewlyCreatedDBObject(definition, true);

        Point3d corner = new(insertion.X, insertion.Y, 0);
        RasterImage image = new()
        {
            ImageDefId = definitionId,
            LayerId = layer,
            Orientation = new CoordinateSystem3d(corner, new Vector3d(insertion.Width, 0, 0), new Vector3d(0, insertion.Height, 0)),
            ShowImage = true,
        };

        if (insertion.RotationDeg != 0)
        {
            image.TransformBy(Matrix3d.Rotation(insertion.RotationDeg * Math.PI / 180.0, Vector3d.ZAxis, corner));
        }

        BlockTableRecord model = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);
        _ = model.AppendEntity(image);
        transaction.AddNewlyCreatedDBObject(image, true);

        RasterImage.EnableReactors(true);
        image.AssociateRasterDef(definition);

        transaction.Commit();
        return null;
    }

    private static ObjectId EnsureLayer(Database database, Transaction transaction, string name)
    {
        LayerTable layers = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        if (layers.Has(name))
        {
            return layers[name];
        }

        layers.UpgradeOpen();
        LayerTableRecord record = new() { Name = name };
        ObjectId id = layers.Add(record);
        transaction.AddNewlyCreatedDBObject(record, true);
        return id;
    }

    private static string UniqueName(DBDictionary dictionary, string stem)
    {
        string name = stem;
        for (int n = 2; dictionary.Contains(name); n++)
        {
            name = $"{stem}-{n}";
        }

        return name;
    }

    private static string SymbolSafe(string text) =>
        string.Concat(text.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-'));

    private sealed class PartRefusedException(string message) : Exception(message);
}
