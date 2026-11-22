using Autodesk.Revit.DB;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal sealed partial class RevitBundleImporter
{
    private void ImportRoadCentrelines(ImportStep step)
    {
        if (HeightsRefused(step))
        {
            return;
        }

        if (ReadVectorLayer(step, SiteVectorLayers.For(step.Kind).DrawnFrom, SiteVectorLayers.For(step.Kind).Label) is not { } features)
        {
            return;
        }

        string stem = _archive.Layout.Key.Stem;
        RoadDecision decision = RoadIdentity.Decide(ExistingDirectShapeComments(), stem, features.Count);

        ElementId category = DirectShapeCategory(BuiltInCategory.OST_Roads);
        int created = 0;
        int unstamped = 0;

        ImportFailureSwallower swallower = new("Importing the road centrelines");
        using Transaction transaction = BeginTransaction("Mantle Place: road centrelines", swallower);

        foreach (int row in decision.RowsToCreate)
        {
            SiteFeature feature = features[row];
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

            if (curves.Count == 0 || TryCreateDirectShape(category, curves, FeatureName(feature, "Road")) is not { } shape)
            {
                continue;
            }

            created++;

            Parameter? comments = shape.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (comments is null || comments.IsReadOnly || !comments.Set(RoadIdentity.Stamp(stem, row + 1)))
            {
                unstamped++;
            }
        }

        if (!CommitAndReport(transaction, swallower))
        {
            return;
        }

        string summary = $"Imported {created:N0} road centreline(s) from {step.EntryName}";
        if (decision.AlreadyPresent > 0)
        {
            summary += $"; {decision.AlreadyPresent:N0} from an earlier import of this bundle were already present and left alone";
        }

        if (unstamped > 0)
        {
            summary += $"; {unstamped:N0} could not be stamped and will not be recognised by a re-import";
        }

        Say(summary + ".");
    }

    private bool TryVertex(SiteVertex vertex, out XYZ point)
    {
        point = XYZ.Zero;
        if (vertex.ElevationM is not { } elevation)
        {
            return false;
        }

        point = SitePoint(vertex.EastM, vertex.NorthM, MetresToInternal(elevation));
        return true;
    }
}
