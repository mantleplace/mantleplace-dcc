namespace MantlePlace.Revit.Core;

/// <summary>Who already holds a name the site context step wants.</summary>
public enum NameHolder
{
    /// <summary>Nothing in the project has the name.</summary>
    Nobody,

    /// <summary>
    /// An element of the kind the step would make: a 3D view that is not a template, or a
    /// rule-based view filter.
    /// </summary>
    OneOfOurKind,

    /// <summary>
    /// Something else Revit will not let two of share the name with: a 3D view template, or a
    /// selection filter.
    /// </summary>
    SomethingElse,
}

/// <summary>What the site context step does with one of its two elements.</summary>
public enum NamedElementAction
{
    /// <summary>Make it, under its name.</summary>
    Create,

    /// <summary>Use the one found by name, as the curator left it.</summary>
    Reuse,

    /// <summary>Make nothing, and say why.</summary>
    Refuse,
}

/// <summary>What the site context step does, decided before the shim touches the document.</summary>
public sealed class SiteContextDecision
{
    public required NamedElementAction View { get; init; }

    public required NamedElementAction Filter { get; init; }

    /// <summary>
    /// Whether to put the filter on the view — with no override, so it changes nothing until the
    /// curator switches it, and is there in Visibility/Graphics when they do.
    /// </summary>
    public required bool ApplyFilterToView { get; init; }

    /// <summary>What the log says about a refusal. Empty when nothing was refused.</summary>
    public required string Explanation { get; init; }
}

/// <summary>
/// The view and the filter an import leaves behind so a curator can find what it made. Pure.
/// </summary>
/// <remarks>
/// <para>
/// The filter is the durable part. It matches every element whose Comments begin with
/// <see cref="StampPrefix"/>, which is every element any import stamped — the terrain, the
/// subdivisions, the roads, the trees, and each element kind a later change stamps, provided its
/// stamp begins the same way. The headless suite holds every stamp this host writes to that prefix.
/// From the filter a curator builds a plan, a sun study or a render view of the context alone in a
/// minute.
/// </para>
/// <para>
/// Found by name on a re-import, never made twice and never edited: a curator who has changed the
/// view or the filter is not overruled by the next import. That is ADR 0004's reuse, applied to two
/// elements that carry no build — the view and the filter are the same whatever bundle made them.
/// </para>
/// </remarks>
public static class SiteContext
{
    /// <summary>What every stamp begins with, and what the view filter's one rule matches.</summary>
    public const string StampPrefix = "Mantle Place";

    /// <summary>The 3D view's name, which is how a re-import finds it.</summary>
    public const string ViewName = "Mantle Place Site Context";

    /// <summary>The view filter's name, which is how a re-import finds it.</summary>
    /// <remarks>
    /// The view's name as well, on purpose: they are one feature to the curator. That Revit lets a
    /// view and a filter share a name is unverified until a real import proves it — Revit refuses a
    /// duplicate "filter element name", which reads as a filter-only namespace.
    /// </remarks>
    public const string FilterName = "Mantle Place Site Context";

    /// <summary>What to do with the view and the filter, given who holds their names.</summary>
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

            // Only when this run made one of the two. On a re-import that found both, a filter that
            // is not on the view is one the curator took off.
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
