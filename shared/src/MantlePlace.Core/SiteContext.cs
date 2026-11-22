namespace MantlePlace.Core;

public enum NameHolder
{
    Nobody,

    OneOfOurKind,

    SomethingElse,
}

public enum NamedElementAction
{
    Create,

    Reuse,

    Refuse,
}

public sealed class SiteContextDecision
{
    public required NamedElementAction View { get; init; }

    public required NamedElementAction Filter { get; init; }

    public required bool ApplyFilterToView { get; init; }

    public required string Explanation { get; init; }
}

public static class SiteContext
{
    public const string StampPrefix = "Mantle Place";

    public const string ViewName = "Mantle Place Site Context";

    public const string FilterName = "Mantle Place Site Context";

    public static SiteContextDecision Decide(NameHolder view, NameHolder filter)
    {
        NamedElementAction viewAction = ActionFor(view);
        NamedElementAction filterAction = ActionFor(filter);

        List<string> refusals = [];
        if (viewAction == NamedElementAction.Refuse)
        {
            refusals.Add(
                $"A 3D view template is already named \"{ViewName}\", so no context view was made; "
                + "rename the template and import again to get one.");
        }

        if (filterAction == NamedElementAction.Refuse)
        {
            refusals.Add(
                $"A selection filter is already named \"{FilterName}\", so no view filter was made; "
                + "rename it and import again to get one.");
        }

        return new SiteContextDecision
        {
            View = viewAction,
            Filter = filterAction,

            ApplyFilterToView = viewAction != NamedElementAction.Refuse
                && filterAction != NamedElementAction.Refuse
                && (viewAction == NamedElementAction.Create || filterAction == NamedElementAction.Create),
            Explanation = string.Join(" ", refusals),
        };
    }

    private static NamedElementAction ActionFor(NameHolder holder) => holder switch
    {
        NameHolder.Nobody => NamedElementAction.Create,
        NameHolder.OneOfOurKind => NamedElementAction.Reuse,
        _ => NamedElementAction.Refuse,
    };
}
