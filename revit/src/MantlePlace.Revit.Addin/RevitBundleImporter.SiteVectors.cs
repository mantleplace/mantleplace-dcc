// UseWPF switches the SDK to the WindowsDesktop implicit-usings set, which drops System.IO.
using System.IO;
using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// What more than one vector step shares — roads, site boundaries, vegetation: reading a layer,
// naming a feature, and turning geometry into a DirectShape.
internal sealed partial class RevitBundleImporter
{
    /// <summary>
    /// Extracts and parses one GeoJSON layer, or logs why it could not be read.
    /// </summary>
    /// <returns><c>null</c> when there is nothing to build.</returns>
    private IReadOnlyList<SiteFeature>? ReadVectorLayer(ImportStep step, SiteGeometryKinds accept, string label)
    {
        // The planner states the route for every vector step it plans: lon/lat for the shared set,
        // subtraction or offsets for this host's own copy.
        if (step.Frame is not { } frame || step.Layer is not { } layer)
        {
            return null;
        }

        string path = _archive.Extract(step.EntryName, ImportStepKinds.LifetimeOf(step.Kind), step.ExpectedSha256);
        string? parseError = SiteVectorReader.TryParse(
            File.ReadAllText(path),
            frame,
            layer,
            accept,
            label,
            out IReadOnlyList<SiteFeature> features);

        if (parseError is not null)
        {
            Say(parseError);
            return null;
        }

        if (features.Count == 0)
        {
            Say($"The {label} layer ({step.EntryName}) carries nothing this plugin could place.");
            return null;
        }

        return features;
    }

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
