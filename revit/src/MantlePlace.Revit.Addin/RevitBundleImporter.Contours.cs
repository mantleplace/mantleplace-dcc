using System.IO;
using Autodesk.Revit.DB;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal sealed partial class RevitBundleImporter
{
    private const string PublishedContoursSubcategory = "Published Contours";

    private void ImportPublishedContours(ImportStep step)
    {
        if (step.Frame is not { } frame || step.Layer is not { } layer || HeightsRefused(step))
        {
            return;
        }

        string stem = _archive.Layout.Key.Stem;
        ContourDecision decision = ContourIdentity.Decide(ExistingDirectShapeComments(), stem, step.ExpectedSha256);
        switch (decision.Disposition)
        {
            case ContourDisposition.RefuseStale:
                Say(decision.Explanation);
                return;
            case ContourDisposition.Reuse:
                Say($"The published contours of this build are already in the project ({decision.AlreadyPresent:N0} "
                    + "element(s)), so none were drawn again.");
                return;
            default:
                break;
        }

        string path = _archive.Extract(step.EntryName, step.Lifetime, step.ExpectedSha256);
        IReadOnlyList<PublishedContour>? contours;
        int skippedLabels;
        using (StreamReader reader = File.OpenText(path))
        {
            if (PublishedContourReader.TryParse(reader, out contours, out skippedLabels) is { } readError)
            {
                Say(readError);
                return;
            }
        }

        if (PublishedContours.TryPlace(contours!, frame, layer, step.VerticalUnits, step.Crop, ShortCurveToleranceM, out string? placeError)
            is not { } placement)
        {
            Say(placeError ?? "The published contours could not be placed.");
            return;
        }

        if (step.Crop is null)
        {
            Say("The published contours were not clipped to the area you ordered: "
                + (step.CropUnavailable ?? "no crop window could be made for this bundle's frame")
                + ". They were left unclipped, as the terrain was.");
        }

        ElementId category = DirectShapeCategory(BuiltInCategory.OST_Topography);
        string contourStamp = ContourIdentity.Stamp(stem, step.ExpectedSha256);
        int created = 0;
        int refused = 0;
        int tooShort = placement.TooShort;
        int unstamped = 0;

        ImportFailureSwallower swallower = new("Drawing the published contours");
        using Transaction transaction = BeginTransaction("Mantle Place: published contours", swallower);

        ElementId? style = PublishedContoursStyle(category);

        foreach (PlacedContour contour in placement.Contours)
        {
            double z = MetresToInternal(contour.ZMetres);
            List<GeometryObject> curves = [];
            foreach (IReadOnlyList<ContourPoint> piece in contour.Pieces)
            {
                XYZ start = SitePoint(piece[0].EastM, piece[0].NorthM, z);
                for (int index = 1; index < piece.Count; index++)
                {
                    XYZ end = SitePoint(piece[index].EastM, piece[index].NorthM, z);
                    if (start.DistanceTo(end) <= _document.Application.ShortCurveTolerance)
                    {
                        continue;
                    }

                    Line line = Line.CreateBound(start, end);
                    if (style is not null)
                    {
                        line.SetGraphicsStyleId(style);
                    }

                    curves.Add(line);
                    start = end;
                }
            }

            if (curves.Count == 0)
            {
                tooShort++;
                continue;
            }

            string name = PublishedContours.ElementName(contour.ElevationText, step.VerticalUnits);
            if (TryCreateDirectShape(category, curves, name) is not { } shape)
            {
                refused++;
                continue;
            }

            created++;

            Parameter? comments = shape.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (comments is null || comments.IsReadOnly || !comments.Set(contourStamp))
            {
                unstamped++;
            }
        }

        if (!CommitAndReport(transaction, swallower))
        {
            return;
        }

        string summary = $"Drew {created:N0} published contour(s) from {step.EntryName}";
        if (placement.OutsideWindow > 0)
        {
            summary += $"; {placement.OutsideWindow:N0} lay wholly outside the terrain's crop window";
        }

        if (skippedLabels > 0)
        {
            summary += $"; {skippedLabels:N0} contour label(s) in the file were skipped, since this plugin draws the lines only";
        }

        if (tooShort > 0)
        {
            summary += $"; {tooShort:N0} were too short for Revit to draw";
        }

        if (refused > 0)
        {
            summary += $"; Revit refused {refused:N0}";
        }

        if (unstamped > 0)
        {
            summary += $"; {unstamped:N0} could not be stamped and will not be recognised by a re-import";
        }

        if (style is null)
        {
            summary += $"; the \"{PublishedContoursSubcategory}\" subcategory could not be made, so they are on their category's own lines";
        }

        Say(summary + ". The toposolid's own contours are unchanged; to hide them, edit Contour Display in the "
            + "toposolid's type properties.");
    }

    private ElementId? PublishedContoursStyle(ElementId categoryId)
    {
        if (Category.GetCategory(_document, categoryId) is not { } parent)
        {
            return null;
        }

        try
        {
            Category subcategory = parent.SubCategories.Contains(PublishedContoursSubcategory)
                ? parent.SubCategories.get_Item(PublishedContoursSubcategory)
                : _document.Settings.Categories.NewSubcategory(parent, PublishedContoursSubcategory);

            return subcategory.GetGraphicsStyle(GraphicsStyleType.Projection)?.Id;
        }
        catch (Exception ex) when (ex is Autodesk.Revit.Exceptions.ApplicationException)
        {
            return null;
        }
    }
}
