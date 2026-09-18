using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The context view and the view filter: that the filter's prefix covers every stamp this host
/// writes, and that a re-import finds both by name rather than making a second of either.
/// </summary>
internal static class SiteContextTests
{
    private const string Stem = "order-7f3a";
    private const string Build = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    internal static int Run()
    {
        TestRun run = new();

        run.Case("the filter's prefix begins every stamp this host writes", () =>
        {
            // The filter is only as good as this: a stamp kind that does not begin with the prefix
            // is an element the import made that the filter cannot find.
            foreach (string stamp in (string[])
                [
                    TerrainIdentity.Stamp(Stem, Build),
                    TerrainIdentity.Stamp(Stem, null),
                    SiteBoundaryIdentity.Stamp(Stem, "Parcel 12", 3),
                    SiteBoundaryIdentity.Stamp(Stem, null, 1),
                    TreeIdentity.Stamp(Stem, Build, 17),
                ])
            {
                run.True(
                    stamp.StartsWith(SiteContext.StampPrefix, StringComparison.Ordinal),
                    $"\"{stamp}\" begins with \"{SiteContext.StampPrefix}\"");
            }
        });

        run.Case("the prefix is the issue's words", () =>
            run.Equal(SiteContext.StampPrefix, "Mantle Place", "Comments beginning with Mantle Place"));

        run.Case("the view is named as the issue names it", () =>
            run.Equal(SiteContext.ViewName, "Mantle Place Site Context", "found by this name on re-import"));

        run.Case("neither name uses a character Revit refuses in an element name", () =>
        {
            foreach (string name in (string[])[SiteContext.ViewName, SiteContext.FilterName])
            {
                run.True(
                    name.IndexOfAny(['{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~', ':', '\\']) < 0,
                    $"\"{name}\" is a legal Revit name");
            }
        });

        run.Case("a first import creates both, and puts the filter on the view", () =>
        {
            SiteContextDecision decision = SiteContext.Decide(NameHolder.Nobody, NameHolder.Nobody);

            run.True(decision.View == NamedElementAction.Create, "view created");
            run.True(decision.Filter == NamedElementAction.Create, "filter created");
            run.True(decision.ApplyFilterToView, "listed on the view, ready to switch");
            run.Equal(decision.Explanation, string.Empty, "nothing to tell anyone");
        });

        run.Case("a second import creates no second view or filter, and leaves both as the curator left them", () =>
        {
            SiteContextDecision decision = SiteContext.Decide(NameHolder.OneOfOurKind, NameHolder.OneOfOurKind);

            run.True(decision.View == NamedElementAction.Reuse, "view reused");
            run.True(decision.Filter == NamedElementAction.Reuse, "filter reused");
            run.False(
                decision.ApplyFilterToView,
                "a curator who took the filter off the view is not overruled by the next import");
        });

        run.Case("a filter made later is put on the view an earlier import made", () =>
        {
            SiteContextDecision decision = SiteContext.Decide(NameHolder.OneOfOurKind, NameHolder.Nobody);

            run.True(decision.View == NamedElementAction.Reuse, "view reused");
            run.True(decision.Filter == NamedElementAction.Create, "filter created");
            run.True(decision.ApplyFilterToView, "the new filter goes on the old view");
        });

        run.Case("a name held by something else is refused, said, and not renamed away", () =>
        {
            SiteContextDecision decision = SiteContext.Decide(NameHolder.SomethingElse, NameHolder.SomethingElse);

            run.True(decision.View == NamedElementAction.Refuse, "no view");
            run.True(decision.Filter == NamedElementAction.Refuse, "no filter");
            run.False(decision.ApplyFilterToView, "nothing to put on nothing");
            run.Contains(decision.Explanation, SiteContext.ViewName, "the view's name is said");
            run.Contains(decision.Explanation, "view template", "and what holds it");
            run.Contains(decision.Explanation, "selection filter", "and what holds the filter's");
        });

        run.Case("one refusal costs only its own element", () =>
        {
            SiteContextDecision decision = SiteContext.Decide(NameHolder.SomethingElse, NameHolder.Nobody);

            run.True(decision.View == NamedElementAction.Refuse, "no view");
            run.True(decision.Filter == NamedElementAction.Create, "the filter is still made — it is the durable part");
            run.False(decision.ApplyFilterToView, "and there is no view to put it on");
        });

        return run.Report("site context");
    }
}
