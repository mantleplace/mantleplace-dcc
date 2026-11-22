using System.Globalization;

namespace MantlePlace.Core;

public enum ImportFailureKind
{
    Unknown = 0,

    ToposolidFloorOverlap,

    ToposolidOverlap,

    ToposolidSubregionOverlap,

    SlabShapeVerticesCoincident,

    SlabShapeVerticesDeleted,

    ToposolidSlopeExceedsThreshold,

    SlabShapeTooThin,

    SlabShapeEditFailed,

    CannotKeepElementsJoined,

    InaccurateSketchLine,

    IfcPartiallySupported,

    SubDivisionRefused,
}

public enum ImportFailureAction
{
    Swallow,

    Resolve,

    RollBack,

    DeleteNamedElements,
}

public static class ImportFailurePolicy
{
    public static ImportFailureAction Decide(
        ImportFailureKind kind,
        bool isError,
        bool hasResolutions,
        bool namesOnlyOwnNewElements = false)
    {
        if (!isError)
        {
            return ImportFailureAction.Swallow;
        }

        if (namesOnlyOwnNewElements && IsDeletable(kind))
        {
            return ImportFailureAction.DeleteNamedElements;
        }

        return hasResolutions && IsResolvable(kind)
            ? ImportFailureAction.Resolve
            : ImportFailureAction.RollBack;
    }

    private static bool IsResolvable(ImportFailureKind kind)
        => kind == ImportFailureKind.CannotKeepElementsJoined;

    private static bool IsDeletable(ImportFailureKind kind)
        => kind == ImportFailureKind.SubDivisionRefused;

    public static string Explain(ImportFailureKind kind, int count)
    {
        string places = count == 1 ? "1 place" : string.Create(
            CultureInfo.InvariantCulture, $"{count} places");

        return kind switch
        {
            ImportFailureKind.ToposolidFloorOverlap =>
                $"The terrain overlaps floors already in this project, in {places}. That is expected "
                + "when you import site context into a project that already contains a building — "
                + "nothing in those floors was changed.",

            ImportFailureKind.ToposolidOverlap =>
                $"The terrain overlaps another toposolid in {places}. If you have imported this area "
                + "before, the older terrain is still there.",

            ImportFailureKind.ToposolidSubregionOverlap =>
                $"Subdivisions overlap each other in {places}. The published polygons genuinely do "
                + "overlap, within a layer and between layers alike, and nothing was clipped or left "
                + "out: Revit kept them all.",

            ImportFailureKind.SlabShapeVerticesCoincident =>
                $"Revit found {places} where two terrain points share the same position in plan and "
                + "kept one of each. The surface is unchanged.",

            ImportFailureKind.SlabShapeVerticesDeleted =>
                $"Revit dropped terrain points it could not use, in {places}.",

            ImportFailureKind.ToposolidSlopeExceedsThreshold =>
                $"The terrain is steeper than Revit's slope warning threshold in {places}. Real "
                + "ground can be; nothing was flattened.",

            ImportFailureKind.InaccurateSketchLine =>
                $"Revit noted {places} where a sketch line sits fractionally off axis. Published polygons "
                + "follow real ground, so almost none of their edges are square; nothing was moved.",

            ImportFailureKind.IfcPartiallySupported =>
                "Revit's IFC importer reports that IFC4 is only partially supported. The site model "
                + "is IFC4 by design and links as context geometry; what Revit does not read from it "
                + "is detail this import does not rely on.",

            _ => $"Revit raised {places} of \"{kind}\" while building the terrain.",
        };
    }

    public static string ExplainRollBack(string stepLabel, string revitText, string failureId)
        => $"{stepLabel} was rolled back: Revit refused it with \"{Quoted(revitText)}\" (id {failureId}). "
            + "Nothing from this step was left in the project.";

    public static string ExplainRefusalNamedFeature(string failureId, int position, string name, string elementId)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"  Revit's error {failureId} named element {elementId}, the subdivision cut from feature {position:N0} "
            + $"(\"{name}\") in the layer.");

    public static string ExplainRefusalNamedOther(string failureId, string elementId)
        => $"  Revit's error {failureId} named element {elementId}, which is not one of this step's cuts.";

    public static string ExplainRefusalNamedNothing(string failureId)
        => $"  Revit's error {failureId} named no element.";

    public static string ExplainRefusedSubDivision(int position, string name, string revitText, string failureId, string elementId)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"Revit refused the subdivision for feature {position:N0} (\"{name}\") when the step committed, with "
            + $"\"{Quoted(revitText)}\" (id {failureId}, element {elementId}). That feature was left out as published; "
            + $"the rest of the layer was kept.");

    public static string ExplainSiteModelRefused(string entryName, string revitText, string failureId, string lost)
        => $"Revit's own IFC conversion of the site model ({entryName}) was refused with \"{Quoted(revitText)}\" "
            + $"(id {failureId}), which the import could not get past, so {lost}. Nothing from the conversion was left "
            + "in the project.";

    private static string Quoted(string revitText)
        => string.IsNullOrWhiteSpace(revitText) ? "no reason given" : revitText.Trim();

    public static string ExplainResolved(ImportFailureKind kind, string resolutionCaption, int count)
    {
        string places = count == 1 ? "1 place" : string.Create(CultureInfo.InvariantCulture, $"{count} places");
        string caption = string.IsNullOrWhiteSpace(resolutionCaption) ? "its default resolution" : $"\"{resolutionCaption.Trim()}\"";

        return kind == ImportFailureKind.CannotKeepElementsJoined
            ? $"Revit could not keep some elements joined in {places} and unjoined them ({caption}). "
                + "Nothing was deleted — the elements simply stop sharing geometry."
            : $"Revit resolved \"{kind}\" in {places} with {caption}.";
    }

    public static string ExplainUnknownWarning(string failureId, string revitText, int count)
    {
        string places = count == 1 ? "once" : string.Create(CultureInfo.InvariantCulture, $"{count} times");
        string quoted = string.IsNullOrWhiteSpace(revitText) ? failureId : revitText.Trim();
        return $"Revit raised a warning this plugin does not recognise, {places}: \"{quoted}\" "
            + $"(id {failureId}). It was allowed through; nothing was rolled back.";
    }
}
