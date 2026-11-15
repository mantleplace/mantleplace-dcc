namespace MantlePlace.Core.Tests;

/// <summary>The About window's text and its privacy link, for every host that opens it.</summary>
internal static class AboutPageTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("the privacy page is on the site the plugin signs in against, however the base is written", () =>
        {
            run.Equal("https://mantle.place/privacy", AboutPage.PrivacyUrl("https://mantle.place"), "bare base");
            run.Equal("https://mantle.place/privacy", AboutPage.PrivacyUrl("https://mantle.place/"), "trailing slash");
            run.Equal("http://localhost:3000/privacy", AboutPage.PrivacyUrl("http://localhost:3000/"), "a dev stack");
        });

        run.Case("the body keeps the build, Civil 3D and the session, in that order", () =>
        {
            string body = AboutPage.Body("build line", "civil line", "session line");

            run.Equal("build line\n\ncivil line\n\nsession line", body, "three paragraphs");
        });

        run.Case("the body keeps as many paragraphs as a host gives, in its order", () =>
        {
            string body = AboutPage.Body("build line", "running line", "supports line", "session line");

            run.Equal("build line\n\nrunning line\n\nsupports line\n\nsession line", body, "four paragraphs");
        });

        return run.Report("About page");
    }
}
