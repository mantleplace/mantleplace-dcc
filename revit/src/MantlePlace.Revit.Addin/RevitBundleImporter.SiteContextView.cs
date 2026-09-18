using Autodesk.Revit.DB;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

// The site-context step: one 3D view and one view filter, found by name on a re-import.
internal sealed partial class RevitBundleImporter
{
    /// <summary>
    /// Makes the "Mantle Place Site Context" 3D view and the view filter that matches every stamped
    /// element, or finds the ones an earlier import made. What to do with each is
    /// <see cref="SiteContext.Decide"/>'s; this method looks the names up and carries it out.
    /// </summary>
    private void EnsureSiteContextView()
    {
        // A usable view before a template, should a project hold both under the one name.
        View3D? existingView = NamedAs<View3D>(SiteContext.ViewName)
            .OrderBy(candidate => candidate.IsTemplate)
            .FirstOrDefault();
        FilterElement? existingFilter = NamedAs<FilterElement>(SiteContext.FilterName).FirstOrDefault();
        SiteContextDecision decision = SiteContext.Decide(
            existingView is null ? NameHolder.Nobody
                : existingView.IsTemplate ? NameHolder.SomethingElse
                : NameHolder.OneOfOurKind,
            existingFilter switch
            {
                null => NameHolder.Nobody,
                ParameterFilterElement => NameHolder.OneOfOurKind,
                _ => NameHolder.SomethingElse,
            });

        ImportFailureSwallower swallower = new("Making the site context view");
        using Transaction transaction = BeginTransaction("Mantle Place: site context view", swallower);

        View3D? view = decision.View switch
        {
            NamedElementAction.Create => CreateContextView(),
            NamedElementAction.Reuse => existingView,
            _ => null,
        };
        ParameterFilterElement? filter = decision.Filter switch
        {
            NamedElementAction.Create => CreateStampFilter(),
            NamedElementAction.Reuse => existingFilter as ParameterFilterElement,
            _ => null,
        };

        if (decision.ApplyFilterToView && view is not null && filter is not null && !view.IsFilterApplied(filter.Id))
        {
            // Default overrides: nothing changes in the view until the curator switches it, and it
            // is listed in Visibility/Graphics ready for them when they do.
            view.AddFilter(filter.Id);
        }

        if (!CommitAndReport(transaction, swallower))
        {
            return;
        }

        if (decision.Explanation.Length > 0)
        {
            Say(decision.Explanation);
        }

        if (view is not null)
        {
            Say(Describe(decision.View, $"the 3D view \"{SiteContext.ViewName}\"") + " Turn on its sun path to "
                + "check the site location this import set.");
        }

        if (filter is not null)
        {
            Say(Describe(decision.Filter, $"the view filter \"{SiteContext.FilterName}\"") + " It matches every "
                + $"element whose Comments begin with \"{SiteContext.StampPrefix}\" — everything an import "
                + "stamped — so any view can hide, halftone or recolour the site context with it.");
        }
    }

    private View3D CreateContextView()
    {
        ViewFamilyType type = new FilteredElementCollector(_document)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .First(candidate => candidate.ViewFamily == ViewFamily.ThreeDimensional);

        View3D view = View3D.CreateIsometric(_document, type.Id);
        view.Name = SiteContext.ViewName;
        return view;
    }

    /// <summary>
    /// A rule-based filter over every model category whose elements can carry Comments, with one rule:
    /// Comments begins with <see cref="SiteContext.StampPrefix"/>.
    /// </summary>
    /// <remarks>
    /// Every such category, not the ones this import happened to stamp into. A filter scoped to what
    /// one run made would miss the element kinds a later import or a later build of this plugin
    /// stamps, and a filter that is only found and never edited would go on missing them. The rule
    /// is what narrows it to the site context; the categories only have to not get in the way.
    /// </remarks>
    private ParameterFilterElement CreateStampFilter()
    {
        ElementId comments = new(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        List<ElementId> categories = [.. ParameterFilterUtilities.GetAllFilterableCategories()
            .Where(id => Category.GetCategory(_document, id) is { CategoryType: CategoryType.Model })
            .Where(id => ParameterFilterUtilities.GetFilterableParametersInCommon(_document, [id]).Contains(comments))];

        ElementParameterFilter rule = new(ParameterFilterRuleFactory.CreateBeginsWithRule(comments, SiteContext.StampPrefix));
        return ParameterFilterElement.Create(_document, SiteContext.FilterName, categories, rule);
    }

    /// <summary>
    /// Every element of <typeparamref name="T"/> with the name, ignoring case — the comparison that
    /// errs towards finding one, because the cost of missing it is a second view or a failed rename.
    /// </summary>
    private IEnumerable<T> NamedAs<T>(string name)
        where T : Element
        => new FilteredElementCollector(_document)
            .OfClass(typeof(T))
            .Cast<T>()
            .Where(element => string.Equals(element.Name, name, StringComparison.OrdinalIgnoreCase));

    private static string Describe(NamedElementAction action, string what)
        => action == NamedElementAction.Create
            ? $"Made {what}."
            : $"Kept {what} that an earlier import made, as it was.";
}
