using Autodesk.Revit.DB;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal static class OdblContainers
{
    private static readonly Color Highlight = new(0xF2, 0x7A, 0x1A);

    private const int HighlightWeight = 5;

    internal static HashSet<ElementId> StampedElementIds(Document document)
    {
        ElementId comments = new(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        ElementParameterFilter stamped = new(ParameterFilterRuleFactory.CreateBeginsWithRule(comments, SiteContext.StampPrefix));

        using FilteredElementCollector collector = new(document);
        return [.. collector.WhereElementIsNotElementType().WherePasses(stamped).ToElementIds()];
    }

    internal static List<(SelectionFilterElement Filter, ContainerKey Key)> All(Document document)
    {
        List<(SelectionFilterElement, ContainerKey)> found = [];
        foreach (SelectionFilterElement filter in new FilteredElementCollector(document)
            .OfClass(typeof(SelectionFilterElement))
            .Cast<SelectionFilterElement>())
        {
            if (OdblContent.ParseContainerName(filter.Name) is { } key)
            {
                found.Add((filter, key));
            }
        }

        return found;
    }

    internal static (SelectionFilterElement Filter, bool Created)? File(
        Document document,
        string identity,
        string group,
        ICollection<ElementId> ids)
    {
        string name = OdblContent.ContainerName(identity, group);
        FilterElement? holder = new FilteredElementCollector(document)
            .OfClass(typeof(FilterElement))
            .Cast<FilterElement>()
            .FirstOrDefault(filter => string.Equals(filter.Name, name, StringComparison.OrdinalIgnoreCase));

        switch (holder)
        {
            case SelectionFilterElement existing:
                existing.AddSet(ids);
                return (existing, false);
            case null:
                SelectionFilterElement made = SelectionFilterElement.Create(document, name);
                made.AddSet(ids);
                return (made, true);
            default:
                return null;
        }
    }

    internal static bool ShowInSiteContextView(Document document, SelectionFilterElement container)
    {
        View3D? view = new FilteredElementCollector(document)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .FirstOrDefault(candidate => !candidate.IsTemplate
                && string.Equals(candidate.Name, SiteContext.ViewName, StringComparison.OrdinalIgnoreCase));
        if (view is null || view.IsFilterApplied(container.Id))
        {
            return false;
        }

        view.AddFilter(container.Id);
        OverrideGraphicSettings overrides = new();
        overrides.SetProjectionLineColor(Highlight);
        overrides.SetProjectionLineWeight(HighlightWeight);
        view.SetFilterOverrides(container.Id, overrides);
        return true;
    }
}
