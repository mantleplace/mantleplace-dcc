using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// The road-centreline step.
internal sealed partial class RevitBundleImporter
{
    /// <summary>
    /// Road centrelines as DirectShape linework, one element per feature — Forma's "Roads" row.
    /// </summary>
    /// <remarks>
    /// Forma's own conversion writes Model Lines. This writes DirectShape curves instead, and the
    /// reason is the Z: the ETL drapes each centreline over the terrain, so a road is a genuinely
    /// non-planar 3-D polyline, while a Revit <c>ModelCurve</c> must lie in a <c>SketchPlane</c>.
    /// Matching Forma literally would mean one SketchPlane element per straight segment — tens of
    /// thousands of them for forty-five roads — or flattening the roads to one elevation and losing
    /// the drape that makes them useful. A DirectShape holds the whole polyline as one element in
    /// the Roads category, which reads the same in a 3-D view and schedules better.
    /// </remarks>
    private void ImportRoadCentrelines(ImportStep step)
    {
        if (ReadVectorLayer(step, SiteGeometryKinds.Lines, "road centrelines") is not { } features)
        {
            return;
        }

        ElementId category = DirectShapeCategory(BuiltInCategory.OST_Roads);
        int created = 0;

        ImportFailureSwallower swallower = new("Importing the road centrelines");
        using Transaction transaction = BeginTransaction("Mantle Place: road centrelines", swallower);

        foreach (SiteFeature feature in features)
        {
            List<GeometryObject> curves = [];
            for (int index = 1; index < feature.Vertices.Count; index++)
            {
                if (TryVertex(feature.Vertices[index - 1], out XYZ start)
                    && TryVertex(feature.Vertices[index], out XYZ end)
                    && start.DistanceTo(end) > _document.Application.ShortCurveTolerance)
                {
                    curves.Add(Line.CreateBound(start, end));
                }
            }

            if (curves.Count > 0 && TryCreateDirectShape(category, curves, FeatureName(feature, "Road")) is not null)
            {
                created++;
            }
        }

        if (!CommitAndReport(transaction, swallower))
        {
            // The swallower already said why. Reporting the work below as done would be a lie:
            // the rollback took all of it.
            return;
        }

        Say($"Imported {created:N0} road centreline(s) from {step.EntryName}.");
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
}
