using Autodesk.Revit.DB;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal sealed partial class RevitBundleImporter
{
    private void EnsureSiteContextView()
    {
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

        Announce(SlowStepNotice.ForSiteContextView(decision));

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
            view.AddFilter(filter.Id);
        }

        int containersShown = view is null
            ? 0
            : OdblContainers.All(_document).Count(found => OdblContainers.ShowInSiteContextView(_document, found.Filter));

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

        if (containersShown > 0)
        {
            Say($"The \"{SiteContext.ViewName}\" view draws the ODbL content already filed in orange.");
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

    private ParameterFilterElement CreateStampFilter()
    {
        ElementId comments = new(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
        List<ElementId> categories = [.. ParameterFilterUtilities.GetAllFilterableCategories()
            .Where(id => Category.GetCategory(_document, id) is { CategoryType: CategoryType.Model })
            .Where(id => ParameterFilterUtilities.GetFilterableParametersInCommon(_document, [id]).Contains(comments))];

        ElementParameterFilter rule = new(ParameterFilterRuleFactory.CreateBeginsWithRule(comments, SiteContext.StampPrefix));
        return ParameterFilterElement.Create(_document, SiteContext.FilterName, categories, rule);
    }

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
