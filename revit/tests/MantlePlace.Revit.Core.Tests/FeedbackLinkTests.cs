using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The link Send Feedback and Report a problem open (<c>HPS-56</c>).
/// </summary>
/// <remarks>
/// The shim only opens the string this returns and is never built in CI (<c>HPS-02</c>), so every
/// rule the standard puts on the link is pinned here: which parameters exist, a malformed version
/// being left out rather than sent raw, <c>order</c> being absent unless a UUID was given, and nothing
/// else ever appearing in the query.
/// </remarks>
internal static class FeedbackLinkTests
{
    private const string Site = "https://mantle.place";
    private const string Order = "3f2b8c1e-9d4a-4e7b-8a61-0c5d2e9f7a14";

    internal static int Run()
    {
        TestRun run = new();

        run.Case("Send Feedback: source, plugin version and Revit version, and no order", () =>
        {
            run.Equal(
                FeedbackLink.Build(Site, "0.6.0", "2026"),
                "https://mantle.place/feedback?source=dcc-revit&v=0.6.0&hv=2026",
                "the general link");
        });

        run.Case("Report a problem: the same link plus the bundle's order id", () =>
        {
            run.Equal(
                FeedbackLink.Build(Site, "0.6.0", "2026", Order),
                $"https://mantle.place/feedback?source=dcc-revit&v=0.6.0&hv=2026&order={Order}",
                "the bundle-scoped link");
        });

        run.Case("each supported Revit year is sent as given", () =>
        {
            foreach (string year in new[] { "2025", "2026", "2027" })
            {
                run.Equal(
                    FeedbackLink.Build(Site, "1.2.3", year),
                    $"https://mantle.place/feedback?source=dcc-revit&v=1.2.3&hv={year}",
                    $"Revit {year}");
            }
        });

        run.Case("a version that fails the pattern is left out, never sent raw", () =>
        {
            string[] malformed =
            [
                "0.1.0+a36fcd8", "0.1.0-beta", "v1.2", "1.2.3.4.5", "1..2", "1.", ".1", "", "  ", "unknown",
                "1.2\n", "1.2 ", "1.2&order=x", "١.٢", "1.2;drop",
            ];

            foreach (string bad in malformed)
            {
                run.Equal(
                    FeedbackLink.Build(Site, bad, "2026"),
                    "https://mantle.place/feedback?source=dcc-revit&hv=2026",
                    $"plugin version \"{bad}\" is dropped");
                run.Equal(
                    FeedbackLink.Build(Site, "0.6.0", bad),
                    "https://mantle.place/feedback?source=dcc-revit&v=0.6.0",
                    $"host version \"{bad}\" is dropped");
            }

            run.Equal(FeedbackLink.Build(Site, null, null), "https://mantle.place/feedback?source=dcc-revit", "null versions");
        });

        run.Case("up to four numeric parts is a version", () =>
        {
            run.Equal(
                FeedbackLink.Build(Site, "1", "2026.0.1.2"),
                "https://mantle.place/feedback?source=dcc-revit&v=1&hv=2026.0.1.2",
                "one part and four parts");
        });

        run.Case("order is present only when given, and only as a UUID", () =>
        {
            string withoutOrder = "https://mantle.place/feedback?source=dcc-revit&v=0.6.0&hv=2026";

            foreach (string? bad in new[] { null, "", " ", "42", "not-a-uuid", Order + "x", "{" + Order + "}", Order.Replace("-", "", StringComparison.Ordinal), Order + "&x=1", "../" + Order })
            {
                run.Equal(FeedbackLink.Build(Site, "0.6.0", "2026", bad), withoutOrder, $"order \"{bad}\" is left out");
            }

            run.Equal(
                FeedbackLink.Build(Site, "0.6.0", "2026", Order.ToUpperInvariant()),
                withoutOrder + "&order=" + Order,
                "a UUID is sent in its canonical lower-case form");
        });

        run.Case("the post-import prompt: the same link plus the order id and a rating of up or down", () =>
        {
            run.Equal(
                FeedbackLink.Build(Site, "0.6.0", "2026", Order, "up"),
                $"https://mantle.place/feedback?source=dcc-revit&v=0.6.0&hv=2026&order={Order}&rating=up",
                "Yes");
            run.Equal(
                FeedbackLink.Build(Site, "0.6.0", "2026", Order, "down"),
                $"https://mantle.place/feedback?source=dcc-revit&v=0.6.0&hv=2026&order={Order}&rating=down",
                "No");
            run.Equal(FeedbackLink.RatingUp, "up", "the token Yes sends");
            run.Equal(FeedbackLink.RatingDown, "down", "the token No sends");
        });

        run.Case("a rating that is not exactly up or down is left out, never sent raw", () =>
        {
            string withoutRating = $"https://mantle.place/feedback?source=dcc-revit&v=0.6.0&hv=2026&order={Order}";

            foreach (string? bad in new[] { null, "", " ", "UP", "Down", "yes", "no", "1", "5", " up", "up ", "up\n", "up&x=1", "down;", "thumbs-up" })
            {
                run.Equal(FeedbackLink.Build(Site, "0.6.0", "2026", Order, bad), withoutRating, $"rating \"{bad}\" is left out");
            }
        });

        run.Case("the general action and Report a Problem carry no rating", () =>
        {
            run.False(FeedbackLink.Build(Site, "0.6.0", "2026").Contains("rating", StringComparison.Ordinal), "Send Feedback");
            run.False(FeedbackLink.Build(Site, "0.6.0", "2026", Order).Contains("rating", StringComparison.Ordinal), "Report a Problem");
        });

        run.Case("no other parameter is ever emitted", () =>
        {
            string[] allowed = ["source", "v", "hv", "order", "rating"];

            foreach (string? link in new[]
            {
                FeedbackLink.Build(Site, "0.6.0", "2026", Order),
                FeedbackLink.Build(Site, "x&email=a@b.c", "2026&token=abc", "email=a@b.c"),
                FeedbackLink.Build(Site, "1.0", "2026", "&name=me"),
                FeedbackLink.Build(Site, "0.6.0", "2026", Order, "up"),
                FeedbackLink.Build(Site, "0.6.0", "2026", Order, "up&email=a@b.c"),
            })
            {
                run.True(link is not null, "a link comes back");
                string query = new Uri(link!).Query.TrimStart('?');
                foreach (string pair in query.Split('&'))
                {
                    run.True(allowed.Contains(pair.Split('=')[0]), $"\"{pair}\" is one of the five parameters in {link}");
                }
            }
        });

        run.Case("the source matches the pattern HPS-56 fixes", () =>
        {
            run.Equal(FeedbackLink.Source, "dcc-revit", "dcc- plus the host's directory name");
            run.True(System.Text.RegularExpressions.Regex.IsMatch(FeedbackLink.Source, "^[a-z0-9-]{1,40}$"), "^[a-z0-9-]{1,40}$");
        });

        run.Case("the base address follows the configured deployment", () =>
        {
            run.Equal(
                FeedbackLink.Build("http://localhost:3000/", "0.6.0", "2026"),
                "http://localhost:3000/feedback?source=dcc-revit&v=0.6.0&hv=2026",
                "a trailing slash does not double");
            run.Equal(
                FeedbackLink.Build("  https://dev.mantle.place  ", "0.6.0", "2026"),
                "https://dev.mantle.place/feedback?source=dcc-revit&v=0.6.0&hv=2026",
                "whitespace is trimmed");
        });

        run.Case("an unusable base address falls back to the production site", () =>
        {
            foreach (string? bad in new[] { null, "", "https:", "not a url" })
            {
                run.Equal(
                    FeedbackLink.Build(bad, "0.6.0", "2026"),
                    "https://mantle.place/feedback?source=dcc-revit&v=0.6.0&hv=2026",
                    $"base \"{bad}\"");
            }
        });

        run.Case("the build metadata an informational version carries is trimmed before the check", () =>
        {
            run.Equal(FeedbackLink.ReleaseVersion("0.1.0+a36fcd8abc"), "0.1.0", "the commit sha is not part of the version");
            run.Equal(FeedbackLink.ReleaseVersion("0.1.0"), "0.1.0", "a bare version is unchanged");
            run.Equal(FeedbackLink.ReleaseVersion(null), null, "null stays null");
            run.Equal(
                FeedbackLink.Build(Site, FeedbackLink.ReleaseVersion("0.1.0+a36fcd8"), "2026"),
                "https://mantle.place/feedback?source=dcc-revit&v=0.1.0&hv=2026",
                "the sha never reaches the link");
        });

        run.Case("the two actions carry the words HPS-51 fixes", () =>
        {
            run.Equal(AccountRibbon.SendFeedbackFace, "Send Feedback", "the Account dropdown row");
            run.Equal(WindowLabels.ReportProblem, "Report a Problem", "the vault window's bundle button, in Revit's casing");
        });

        return run.Report("FeedbackLink");
    }
}
