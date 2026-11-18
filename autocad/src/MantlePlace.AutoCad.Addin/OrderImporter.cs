using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MantlePlace.AutoCad;
using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.AutoCad.Addin;

/// <summary>What an import did: the plan, the text that says what happened, and whether anything went in.</summary>
internal sealed record OrderImportResult(AutoCadImportPlan Plan, string Summary, bool PlacedAnything);

/// <summary>
/// Places a planned order in a drawing, one step at a time: the shell half of <see cref="AutoCadImportPlanner"/>.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Every number placed is the bundle's.</b> Contour vertices and heights go in as the file holds
/// them and the aerial at its published insertion; the drawing's unit setting is never read and never
/// set, and no coordinate is converted, offset or reprojected (root CLAUDE.md, the boundary that keeps
/// this client thin). The degrees-to-radians step is AutoCAD's API taking an angle, not a placement value.
/// </para>
/// <para>
/// <b>Everything is checked before anything is drawn</b> (HPS-26): <see cref="Verify"/>, the run's first
/// step, finds each placed part's file and matches its bytes to its published digest, and a mismatch
/// refuses the whole import with nothing in the drawing. Each part is then one step
/// (<see cref="Place"/>), one transaction, which is what lets the import window's Cancel stop the run
/// between parts and never inside one (<see cref="AutoCadImportRun"/>, <c>HPS-60</c>). The aerial goes in
/// before the contours so the linework draws over the image, and the public-record layers and the site
/// linework go in last, over both.
/// </para>
/// <para>
/// The aerial is an external reference, as every AutoCAD raster image is, so its file is extracted to the
/// order's retained cache folder (<see cref="ExtractionLifetime.Retained"/>), which a later import of
/// another order does not touch. The contours, record and linework drawings are read and dropped, and so
/// is the surface's LandXML once Civil 3D has built the surface from it.
/// </para>
/// <para>
/// <b>Civil 3D is observed through its part alone.</b> When the presence test lets the part load, the part
/// reads the drawing's coordinate zone (never sets it) and builds the surface; when it does not (plain
/// AutoCAD, or Civil 3D's "as AutoCAD" launcher), the plan is plain AutoCAD's and says the surface needs
/// Civil 3D. A part that fails to load is the second case, not a crash.
/// </para>
/// </remarks>
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

    /// <summary>The plan, not refused.</summary>
    internal AutoCadImportPlan Plan { get; }

    /// <summary>Whether the Civil 3D part is on, which is what builds the surface.</summary>
    internal bool Civil3d => _civil3d is not null;

    /// <summary>The order's unit system, linear unit and delivery CRS, for the import window's second line.</summary>
    internal string? DeliveryLine { get; }

    /// <summary>
    /// Opens the zip, observes the drawing and plans the order, drawing nothing; or says why nothing will be
    /// imported (<paramref name="refused"/>, and <c>null</c> returned).
    /// </summary>
    internal static OrderImporter? Prepare(Document document, string zipPath, PresenceVerdict presence, out OrderImportResult? refused)
    {
        // A received export and Import Bundle both open the zip here, so a file that is not a bundle is
        // refused in one set of words whichever way it came, before the drawing is read.
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
#pragma warning disable CA1031 // A zone that cannot be read refuses the import; it never takes AutoCAD down.
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

            // A 3.x bundle from its catalog and frames, a 2.x one from its hosts.autocad block.
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

    /// <summary>
    /// The first step: finds every placed part's file and checks it against its published digest, drawing
    /// nothing. <c>false</c> when a file fails its check, which refuses the whole import; a file the bundle
    /// does not hold fails only its own part.
    /// </summary>
    internal bool Verify()
    {
        foreach (PlannedPart part in Plan.Parts.Where(p => p.Place && (p.Kind != AutoCadPart.Surface || Civil3d)))
        {
            // The records are several files, one per layer; every other part is one.
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

    /// <summary>
    /// One step: places <paramref name="kind"/> in its own transaction, under the drawing's lock. <c>false</c>
    /// when it did not go in; what it said, placed or not, is in the closing report.
    /// </summary>
    internal bool Place(AutoCadPart kind)
    {
        if (_outcomes.ContainsKey(kind))
        {
            // Its file was not in the bundle: the verification already said so.
            return false;
        }

        Database database = _document.Database;
        PlannedPart part = Plan.Part(kind);
        Func<string?>? place = kind switch
        {
            AutoCadPart.Aerial when _files.TryGetValue(kind, out string? image) => () => PlaceAerial(database, image, part, Plan.OrderId),
            AutoCadPart.Surface when _civil3d is not null && _files.TryGetValue(kind, out string? landXml) => () => BuildSurface(database, _civil3d!, landXml, part, Plan.OrderId),
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

    /// <summary>What the import did, for the window's report, the command line and the web.</summary>
    /// <param name="notReached">What each part the run never reached says, in place of the plan's sentence.</param>
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

    /// <summary>Runs one part; a part AutoCAD refuses is reported and the others still go in.</summary>
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
            // What Civil 3D's managed API throws for a file it will not build from.
            return AutoCadImportPlanner.FailedSentence(kind, $"the host refused it ({refused.GetType().Name}: {refused.Message}).");
        }
    }

    /// <summary>
    /// The surface as a Civil 3D TIN surface on its layer, built by Civil 3D from the block's LandXML. The
    /// zone check has already shown the drawing's zone to be the order's, or the drawing to have none.
    /// </summary>
    private static string BuildSurface(Database database, Type civil3d, string landXmlPath, PlannedPart part, string orderId)
    {
        string? landXmlSurface;
        using (StreamReader reader = File.OpenText(landXmlPath))
        {
            if (LandXmlSurface.TryReadName(reader, out landXmlSurface) is { } problem)
            {
                throw new PartRefusedException(problem + ".");
            }
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

    /// <summary>The contours as lightweight polylines on their layer, each at its published height.</summary>
    private static string PlaceContours(Database database, string dxfPath, PlannedPart part)
    {
        IReadOnlyList<PublishedContour> contours = ReadPolylines(dxfPath, "contours");

        using Transaction transaction = database.TransactionManager.StartTransaction();
        int placed = AppendPolylines(database, transaction, contours, _ => part.Layer);
        transaction.Commit();
        return AutoCadImportPlanner.ContoursPlacedSentence(part, placed);
    }

    /// <summary>
    /// Each public-record layer's polylines on that layer's own layer, as its file holds them. Every file is
    /// read before anything is drawn. A file the reader refuses (a <c>POINT</c> entity is the usual one) is left
    /// out on its own, named in what the user is told, and the other layers still go in; only a set with no
    /// readable layer refuses the part.
    /// </summary>
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

    /// <summary>The site linework's polylines under its layer, each on the layer the drawing puts it on.</summary>
    private static string PlaceLinework(Database database, string dxfPath, PlannedPart part)
    {
        IReadOnlyList<PublishedContour> polylines = ReadPolylines(dxfPath, "site linework");

        using Transaction transaction = database.TransactionManager.StartTransaction();
        int placed = AppendPolylines(database, transaction, polylines, p => AutoCadLayers.LineworkLayer(p.Layer));
        transaction.Commit();

        int layers = polylines.Where(p => p.Vertices.Count >= 2).Select(p => AutoCadLayers.LineworkLayer(p.Layer)).Distinct(StringComparer.Ordinal).Count();
        return AutoCadImportPlanner.LineworkPlacedSentence(part, placed, layers);
    }

    /// <summary>A drawing of flat polylines, read whole; a file the reader refuses refuses the part, by name.</summary>
    private static IReadOnlyList<PublishedContour> ReadPolylines(string dxfPath, string drawing)
    {
        using StreamReader reader = File.OpenText(dxfPath);
        return PublishedContourReader.TryParse(reader, drawing, out IReadOnlyList<PublishedContour>? polylines, out _) is { } problem
            ? throw new PartRefusedException(problem)
            : polylines!;
    }

    /// <summary>
    /// Lightweight polylines into model space, vertices and elevation as the file holds them, each on the layer
    /// <paramref name="layerFor"/> names. Returns how many went in; a polyline of fewer than two vertices is no line.
    /// </summary>
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

    /// <summary>The aerial at its published insertion: lower-left corner, ground size, rotation about that corner.</summary>
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

        // AutoCAD's own rotation, about the published corner: the plugin hands it the angle and works
        // out nothing.
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

    /// <summary>An order id as a dictionary key: letters, digits, dash and underscore kept, the rest a dash.</summary>
    private static string SymbolSafe(string text) =>
        string.Concat(text.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-'));

    /// <summary>A part that cannot be placed, with the reason the user is shown.</summary>
    private sealed class PartRefusedException(string message) : Exception(message);
}
