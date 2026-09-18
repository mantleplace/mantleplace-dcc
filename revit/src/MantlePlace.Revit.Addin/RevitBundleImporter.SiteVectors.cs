// UseWPF switches the SDK to the WindowsDesktop implicit-usings set, which drops System.IO.
using System.IO;
using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// What the three vector steps share — roads, site boundaries and vegetation: reading a layer, and
// turning a feature into a DirectShape.
internal sealed partial class RevitBundleImporter
{
    /// <summary>
    /// Extracts and parses one GeoJSON layer, or logs why it could not be read.
    /// </summary>
    /// <returns><c>null</c> when there is nothing to build.</returns>
    private IReadOnlyList<SiteFeature>? ReadVectorLayer(ImportStep step, SiteGeometryKinds accept, string label)
    {
        if (step.Frame is not { } frame)
        {
            return null;
        }

        string path = _archive.Extract(step.EntryName, ImportStepKinds.LifetimeOf(step.Kind), step.ExpectedSha256);
        string? parseError = SiteVectorReader.TryParse(
            File.ReadAllText(path),
            frame,
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
    /// Creates one DirectShape, or reports nothing and returns false.
    /// </summary>
    /// <remarks>
    /// Per-element rather than per-layer, because Revit rejects individual shapes — a self-touching
    /// polyline, a crown whose radius rounds to nothing — and losing one road must not abort the
    /// transaction that holds the other forty-four.
    /// </remarks>
    private bool TryCreateDirectShape(ElementId category, IList<GeometryObject> geometry, string name)
    {
        try
        {
            DirectShape shape = DirectShape.CreateElement(_document, category);
            shape.SetShape(geometry);
            shape.Name = name;
            return true;
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
        {
            return false;
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

    /// <summary>A vertex as a Revit point, or false when the layer published no elevation for it.</summary>
    /// <remarks>
    /// Z is ABSOLUTE orthometric height, the same reading the toposurface points take, so a vertex
    /// with no Z has no elevation this host may invent — dropping it is the <c>HPS-20</c> reading and
    /// zero would put the road two kilometres below the site.
    /// </remarks>
    private static bool TryVertex(SiteVertex vertex, out XYZ point)
    {
        point = XYZ.Zero;
        if (vertex.ElevationM is not { } elevation)
        {
            return false;
        }

        point = new XYZ(MetresToInternal(vertex.EastM), MetresToInternal(vertex.NorthM), MetresToInternal(elevation));
        return true;
    }

    private static string FeatureName(SiteFeature feature, string fallback)
        => feature.Name.Length > 0 ? feature.Name : fallback;
}
