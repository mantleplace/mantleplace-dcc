using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// The Revit side of the ODbL container: a selection filter per order and equivalence group, named
/// by <see cref="OdblContent.ContainerName"/>, holding the ids of everything a <c>database</c> step
/// made. What goes in it and what the handover does with it are <see cref="OdblContent"/>'s.
/// </summary>
/// <remarks>
/// A selection filter, because it is the one Revit element that holds elements of any kind under a
/// name a person reads, lists in Visibility/Graphics, takes a graphic override in a view, and is
/// saved with the project. The elements' Comments stamps stay as they are: every identity in the core
/// finds an earlier import's elements by exact stamp (ADR 0004), and a marker added to one would make
/// the next import draw them again.
/// </remarks>
internal static class OdblContainers
{
    /// <summary>The orange the site context view draws ODbL content's lines in: the brand's, so it reads as ours.</summary>
    private static readonly Color Highlight = new(0xF2, 0x7A, 0x1A);

    /// <summary>The line weight (1 to 16) that override uses, heavy enough to read against shaded ground.</summary>
    private const int HighlightWeight = 5;

    /// <summary>
    /// The id of every element carrying a Mantle Place stamp — everything any import wrote. Taken
    /// before and after a step, the difference is what the step made.
    /// </summary>
    internal static HashSet<ElementId> StampedElementIds(Document document)
    {
        ElementId comments = new(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        ElementParameterFilter stamped = new(ParameterFilterRuleFactory.CreateBeginsWithRule(comments, SiteContext.StampPrefix));

        using FilteredElementCollector collector = new(document);
        return [.. collector.WhereElementIsNotElementType().WherePasses(stamped).ToElementIds()];
    }

    /// <summary>Every container the project holds, with the order and group each was made for.</summary>
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

    /// <summary>
    /// Files <paramref name="ids"/> in the container for the order and group, making it first when
    /// there is none. Inside the caller's transaction.
    /// </summary>
    /// <returns>
    /// The container and whether this call made it, or <c>null</c> when the name is held by a filter
    /// of another kind, which Revit will not let two share.
    /// </returns>
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

    /// <summary>
    /// Puts the container on the site context 3D view with an orange line override, so the ODbL
    /// content reads as a different thing from the rest at a glance. Inside the caller's transaction.
    /// </summary>
    /// <returns><c>false</c> when the project has no such view, or it already shows the container.</returns>
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
