using System.IO;
using Autodesk.Revit.DB;
using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal sealed partial class RevitBundleImporter
{
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
