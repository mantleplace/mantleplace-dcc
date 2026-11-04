using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using MantlePlace.AutoCad;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.AutoCad.Addin;

/// <summary>What one press did: the plan, the text that says what happened, and whether anything went in.</summary>
internal sealed record OrderImportResult(AutoCadImportPlan Plan, string Summary, bool PlacedAnything);

/// <summary>
/// Places a planned order in a drawing: the shell half of <see cref="AutoCadImportPlanner"/>.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Every number placed is the bundle's.</b> Contour vertices and heights go in as the file holds
/// them and the aerial at its published insertion; the drawing's unit setting is never read and never
/// set, and no coordinate is converted, offset or reprojected (root CLAUDE.md, the boundary that keeps
/// this client thin). The degrees-to-radians step is AutoCAD's API taking an angle, not a placement value.
/// </para>
/// <para>
/// <b>Everything is checked before anything is drawn</b> (HPS-26): each placed part's file is found and
/// its bytes matched to its published digest first, and a mismatch refuses the whole import with nothing
/// in the drawing. The aerial goes in before the contours so the linework draws over the image.
/// </para>
/// <para>
/// The aerial is an external reference, as every AutoCAD raster image is, so its file is extracted to the
/// order's retained cache folder (<see cref="ExtractionLifetime.Retained"/>), which a later import of
/// another order does not touch. The contours file is read and dropped.
/// </para>
/// </remarks>
internal static class OrderImporter
{
    internal static OrderImportResult Import(Document document, string zipPath, bool civil3dLoaded)
    {
        using LocalBundleArchive archive = LocalBundleArchive.Open(zipPath);

        if (archive.ManifestText is not { } manifestText)
        {
            AutoCadImportPlan notABundle = new()
            {
                Refusal = "This file is not a Mantle Place bundle: it holds no Metadata/manifest.json. Nothing was imported.",
            };
            return new OrderImportResult(notABundle, notABundle.Summary(), false);
        }

        Database database = document.Database;
        AutoCadImportPlan plan = AutoCadImportPlanner.Plan(
            manifestText,
            DrawingObservations.Geolocation(database),
            civil3dLoaded);

        if (plan.IsRefused)
        {
            return new OrderImportResult(plan, plan.Summary(), false);
        }

        // Found and verified, all of them, before the drawing is touched.
        Dictionary<AutoCadPart, string> files = [];
        Dictionary<AutoCadPart, string> outcomes = [];
        foreach (PlannedPart part in plan.Parts.Where(p => p.Place))
        {
            if (AutoCadBundleEntries.Resolve(archive.EntryNames, part.Path) is not { } entry)
            {
                outcomes[part.Kind] = AutoCadImportPlanner.FailedSentence(part.Kind, $"the bundle does not hold {part.Path}.");
                continue;
            }

            ExtractionLifetime lifetime = part.Kind == AutoCadPart.Aerial ? ExtractionLifetime.Retained : ExtractionLifetime.Transient;
            try
            {
                files[part.Kind] = archive.Extract(entry, lifetime, part.Sha256);
            }
            catch (InvalidOperationException integrity)
            {
                AutoCadImportPlan refused = new()
                {
                    OrderId = plan.OrderId,
                    JobId = plan.JobId,
                    Frame = plan.Frame,
                    Refusal = integrity.Message,
                };
                return new OrderImportResult(refused, refused.Summary(), false);
            }
        }

        bool placedAnything = false;
        using (document.LockDocument())
        {
            if (files.TryGetValue(AutoCadPart.Aerial, out string? image))
            {
                PlannedPart aerial = plan.Part(AutoCadPart.Aerial);
                outcomes[AutoCadPart.Aerial] = Run(AutoCadPart.Aerial, () => PlaceAerial(database, image, aerial, plan.OrderId), aerial.Sentence, ref placedAnything);
            }

            if (files.TryGetValue(AutoCadPart.Contours, out string? dxf))
            {
                PlannedPart contours = plan.Part(AutoCadPart.Contours);
                outcomes[AutoCadPart.Contours] = Run(AutoCadPart.Contours, () => PlaceContours(database, dxf, contours), null, ref placedAnything);
            }
        }

        return new OrderImportResult(plan, plan.Summary(outcomes), placedAnything);
    }

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
    }

    /// <summary>The contours as lightweight polylines on their layer, each at its published height.</summary>
    private static string PlaceContours(Database database, string dxfPath, PlannedPart part)
    {
        IReadOnlyList<PublishedContour>? contours;
        using (StreamReader reader = File.OpenText(dxfPath))
        {
            if (PublishedContourReader.TryParse(reader, out contours) is { } problem)
            {
                throw new PartRefusedException(problem);
            }
        }

        int placed = 0;
        using Transaction transaction = database.TransactionManager.StartTransaction();
        ObjectId layer = EnsureLayer(database, transaction, part.Layer);
        BlockTableRecord model = (BlockTableRecord)transaction.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(database), OpenMode.ForWrite);

        foreach (PublishedContour contour in contours!)
        {
            if (contour.Vertices.Count < 2)
            {
                continue;
            }

            Polyline polyline = new();
            for (int i = 0; i < contour.Vertices.Count; i++)
            {
                polyline.AddVertexAt(i, new Point2d(contour.Vertices[i].X, contour.Vertices[i].Y), 0, 0, 0);
            }

            polyline.Closed = contour.IsClosed;
            polyline.Elevation = contour.Elevation;
            polyline.LayerId = layer;

            _ = model.AppendEntity(polyline);
            transaction.AddNewlyCreatedDBObject(polyline, true);
            placed++;
        }

        transaction.Commit();
        return AutoCadImportPlanner.ContoursPlacedSentence(part, placed);
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
