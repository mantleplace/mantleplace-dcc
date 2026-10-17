using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// The address F1 opens from the ribbon.
/// </summary>
/// <remarks>
/// The shim that applies it to each button is never built in CI (<c>HPS-02</c>), so what can be
/// pinned is the address itself: it must be a secure URL, and it must name a file that exists in
/// this tree, because a help link that 404s is worse than Revit's own help falling through.
/// </remarks>
internal static class HelpLinksTests
{
    private const string BlobPrefix = "https://github.com/mantleplace/mantleplace-dcc/blob/main/";

    internal static int Run()
    {
        TestRun run = new();

        run.Case("F1 opens the Revit plugin's public page, over https", () =>
        {
            run.True(Uri.TryCreate(HelpLinks.RibbonHelpUrl, UriKind.Absolute, out Uri? uri), "it is an absolute URL");
            run.Equal(uri?.Scheme, "https", "help never opens over plain http");
            run.Equal(HelpLinks.RibbonHelpUrl, BlobPrefix + "revit/README.md", "it is the plugin's README on main");
        });

        run.Case("the page the help link names is a file in this tree", () =>
        {
            string relative = HelpLinks.RibbonHelpUrl.Substring(BlobPrefix.Length);
            string folder = relative.Substring(0, relative.LastIndexOf('/'));
            string file = relative.Substring(relative.LastIndexOf('/') + 1);

            string? found = RepoTree.Find(folder, dir => File.Exists(Path.Combine(dir, file)));

            run.True(found is not null, $"{relative} exists, so the link is not dead on the day it ships");
        });

        return run.Report("HelpLinks");
    }
}
