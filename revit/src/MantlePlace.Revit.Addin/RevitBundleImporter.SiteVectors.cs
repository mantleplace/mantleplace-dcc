// UseWPF switches the SDK to the WindowsDesktop implicit-usings set, which drops System.IO.
using System.IO;
using Autodesk.Revit.DB;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// What more than one vector step shares — roads, polygon layers, vegetation: reading a layer,
// naming a feature, and turning geometry into a DirectShape.
internal sealed partial class RevitBundleImporter
{
    /// <summary>
    /// Extracts and parses one vector layer, from its table of the shared carrier or from its GeoJSON
    /// file, or logs why it could not be read.
    /// </summary>
    /// <remarks>
    /// A layer the planner sent to the carrier that the carrier cannot give — the file will not open,
    /// has no table for it, or is stored in a CRS other than this project's — is read from the same
    /// layer's GeoJSON file where the plan holds one, and says so. A carrier is never placed in a frame
    /// it cannot be shown to be in (<c>HPS-53</c>); a layer the same bundle can place by its other file
    /// is not lost to it.
    /// </remarks>
    /// <returns><c>null</c> when there is nothing to build.</returns>
    private IReadOnlyList<SiteFeature>? ReadVectorLayer(ImportStep step, SiteGeometryKinds accept, string label)
    {
        ImportStep source = step;
        IReadOnlyList<SiteFeature> features;
        string? error;

        if (step.Table is not null)
        {
            error = TryReadCarrier(step, accept, label, out features);
            if (error is not null && step.GeoJsonFallback is { } fallback)
            {
                Say($"{error} Reading the {label} from the bundle's GeoJSON copy instead.");
                source = fallback;
                error = TryReadGeoJson(fallback, accept, label, out features);
            }
        }
        else
        {
            error = TryReadGeoJson(step, accept, label, out features);
        }

        if (error is not null)
        {
            Say(error);
            return null;
        }

        if (features.Count == 0)
        {
            Say($"The {label} layer ({source.EntryName}) carries nothing this plugin could place.");
            return null;
        }

        return features;
    }

    // The planner states the route for every vector step it plans: lon/lat for the shared set,
    // subtraction or offsets for this host's own copy.
    private string? TryReadGeoJson(ImportStep step, SiteGeometryKinds accept, string label, out IReadOnlyList<SiteFeature> features)
    {
        features = [];
        if (step.Frame is not { } frame || step.Layer is not { } layer)
        {
            return $"The {label} could not be placed: the plan states no frame for them.";
        }

        string path = _archive.Extract(step.EntryName, step.Lifetime, step.ExpectedSha256);
        return SiteVectorReader.TryParse(
            File.ReadAllText(path),
            frame,
            layer,
            accept,
            label,
            out features,
            levelField: LevelField(step));
    }

    private string? TryReadCarrier(ImportStep step, SiteGeometryKinds accept, string label, out IReadOnlyList<SiteFeature> features)
    {
        features = [];
        if (step.Table is not { } table)
        {
            return $"The {label} could not be read from the shared vector file: the plan names no table for them.";
        }

        if (step.Frame is not { } frame || step.Layer is not { } layer)
        {
            return $"The {label} could not be placed: the plan states no frame for them.";
        }

        string path = _archive.Extract(step.EntryName, step.Lifetime, step.ExpectedSha256);
        string? opened = GeoPackageFile.TryReadTable(path, table, label, out GeoPackageTable? rows, step.SoleTable);
        return opened is not null || rows is null
            ? opened ?? $"The {label} could not be read from the shared vector file."
            : SiteGeoPackageReader.TryRead(rows, frame, layer, accept, label, out features, LevelField(step));
    }

    private static string? LevelField(ImportStep step)
        => step.Level is { Kind: FidelityLevelKind.FieldCut } level ? level.LowestLevelField : null;

    /// <summary>
    /// Creates one DirectShape, or reports nothing and returns <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Per-element rather than per-layer, because Revit rejects individual shapes — a self-touching
    /// polyline, a crown whose radius rounds to nothing — and losing one road must not abort the
    /// transaction that holds the other forty-four.
    /// </remarks>
    private DirectShape? TryCreateDirectShape(ElementId category, IList<GeometryObject> geometry, string name)
    {
        try
        {
            DirectShape shape = DirectShape.CreateElement(_document, category);
            shape.SetShape(geometry);
            shape.Name = name;
            return shape;
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
        {
            return null;
        }
    }

    /// <summary>
    /// The preferred category, or Generic Model where this Revit does not allow a DirectShape in it.
    /// </summary>
    /// <remarks>
    /// Generic Model is the fallback because it is the category Forma itself uses for context
    /// geometry, and because a DirectShape that cannot be created at all is worse than one filed a
    /// category away from where a curator would look for it.
    /// </remarks>
    private ElementId DirectShapeCategory(BuiltInCategory preferred)
    {
        ElementId category = new(preferred);
        return DirectShape.IsValidCategoryId(category, _document)
            ? category
            : new ElementId(BuiltInCategory.OST_GenericModel);
    }

    private static string FeatureName(SiteFeature feature, string fallback)
        => feature.Name.Length > 0 ? feature.Name : fallback;
}
