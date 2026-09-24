using System.IO;
using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// The published-contours step.
internal sealed partial class RevitBundleImporter
{
    /// <summary>The subcategory every published contour's lines are drawn on.</summary>
    private const string PublishedContoursSubcategory = "Published Contours";

    /// <summary>
    /// The bundle's published contours, one DirectShape per contour — see
    /// <c>docs/adr/0013-revit-published-contours-are-directshapes.md</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything but the element calls is decided in Core: <see cref="PublishedContourReader"/> reads
    /// the file, <see cref="PublishedContours"/> places, clips and thins it, and
    /// <see cref="ContourIdentity"/> decides whether this build's layer is already here. The step
    /// commits in one transaction, because the layer's stamp has no row to resume from.
    /// </para>
    /// <para>
    /// ⛔ The toposolid's own contour display is never touched: it is the curator's setting. The
    /// summary says where it lives, for a curator who wants only one set.
    /// </para>
    /// </remarks>
    private void ImportPublishedContours(ImportStep step)
    {
        if (step.Frame is not { } frame || step.Layer is not { } layer)
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

        string path = _archive.Extract(step.EntryName, ImportStepKinds.LifetimeOf(step.Kind), step.ExpectedSha256);
        IReadOnlyList<PublishedContour>? contours;
        using (StreamReader reader = File.OpenText(path))
        {
            if (PublishedContourReader.TryParse(reader, out contours) is { } readError)
            {
                Say(readError);
                return;
            }
        }

        // Revit states its tolerance in its internal unit; Core works in metres.
        double toleranceM = InternalToMetres(_document.Application.ShortCurveTolerance);
        if (PublishedContours.TryPlace(contours!, frame, layer, step.VerticalUnits, step.Crop, toleranceM, out string? placeError)
            is not { } placement)
        {
            Say(placeError ?? "The published contours could not be placed.");
            return;
        }

        if (step.Crop is null)
        {
            // SurfaceCrop.For is null for a bundle with no bbox and for a frame it cannot project the
            // bbox into — every State Plane delivery — and the terrain is left uncropped in both.
            Say("No crop window could be made for this bundle's frame, so the published contours were left "
                + "unclipped, as the terrain was.");
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
                // Core has already skipped every vertex within tolerance of the last one kept. This
                // repeats that rule against the unit round-trip — from the last point DRAWN, so the
                // line stays continuous — since a Line under tolerance throws rather than failing
                // quietly.
                XYZ start = new(MetresToInternal(piece[0].EastM), MetresToInternal(piece[0].NorthM), z);
                for (int index = 1; index < piece.Count; index++)
                {
                    XYZ end = new(MetresToInternal(piece[index].EastM), MetresToInternal(piece[index].NorthM), z);
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
            // The swallower already said why, and the rollback took everything below with it.
            return;
        }

        string summary = $"Drew {created:N0} published contour(s) from {step.EntryName}";
        if (placement.OutsideWindow > 0)
        {
            summary += $"; {placement.OutsideWindow:N0} lay wholly outside the terrain's crop window";
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

    /// <summary>
    /// The projection style of the "Published Contours" subcategory, made if the project lacks it, or
    /// <c>null</c> when the category takes no subcategory.
    /// </summary>
    /// <remarks>
    /// An existing subcategory is used as it is: its colour and weight are the curator's, set in
    /// Object Styles, and a re-import must not reset them.
    /// </remarks>
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
