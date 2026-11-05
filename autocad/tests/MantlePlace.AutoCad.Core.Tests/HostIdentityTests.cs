using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;
using MantlePlace.Revit.Core.Tests;
using System.Text.Json;

namespace MantlePlace.AutoCad.Tests;

/// <summary>
/// What the platform is told this plugin is. The Revit sources compile in with the AutoCAD define, so
/// the sign-in route, the client header and the connected-host presence all say <c>autocad</c>: a
/// plugin that signed in or joined the curator's channel as Revit would be a second Revit to the
/// platform, and the web would send an AutoCAD user's exports to the wrong host.
/// </summary>
internal static class HostIdentityTests
{
    internal static int Run()
    {
        TestRun run = new();

        run.Case("the plugin is autocad to the platform, in the one place that says so", () =>
            run.Equal(HostIdentity.Id, "autocad", "the host id the contracts name"));

        run.Case("sign-in goes to the web login with this host's id", () =>
        {
            string login = new MantlePlaceEndpoints().WebLoginUrl;

            run.Contains(login, "?host=autocad", "the native login names the host");
            run.False(login.Contains("host=revit", StringComparison.Ordinal), "and not Revit");
        });

        run.Case("every Platform API call carries the autocad client header", () =>
        {
            run.Equal(PlatformApiCalls.ClientName, "autocad", "the client's name in the platform's list");
            run.True(PlatformApiCalls.ClientHeader.StartsWith("autocad/", StringComparison.Ordinal), "name/version");
        });

        run.Case("the connected host announces itself as autocad", () =>
        {
            ConnectedHost presence = ConnectedHosts.Announce("25.0", "1.0.0", "Site.dwg", "DESK");

            run.Equal(presence.Host, "autocad", "the presence payload's host");
            run.Equal(ConnectedHosts.HostId, "autocad", "and the constant the export filter compares against");
        });

        run.Case("a feedback link says which host it came from", () =>
            run.Contains(FeedbackLink.Build("https://mantle.place", "1.0.0", "2026"), "source=dcc-autocad", "the form's source tag"));

        run.Case("the host-view request names autocad, so the platform cuts this host's files", () =>
        {
            using JsonDocument body = JsonDocument.Parse(HostViews.BuildRequestBody());
            JsonElement view = body.RootElement.GetProperty("view");

            run.Equal(view.GetProperty("host").GetString(), "autocad", "the view asked for is this plugin's own");
            run.False(HostViews.BuildRequestBody().Contains("revit", StringComparison.Ordinal), "and nowhere Revit's");
        });

        run.Case("a view cut from hosts.autocad is enough: nothing is read from outside that block", () =>
        {
            HostViews.TryParse(
                """{"url":"https://r2.example/v.zip","expiresAt":"","view":{"partial":false,"parts":["elevation.surface_dxf@MAX","elevation.landxml@MAX"]}}""",
                out HostViewLink link);

            run.Equal(HostViews.ReadOutsideHostBlock.Count, 0, "this import reads only its own block");
            run.True(HostViews.Decide(link, ["elevation.landxml", "landcover.tree_points_csv"]) == ViewFetch.View,
                "the view, not the whole bundle, because the tree points are Revit's to read");
        });

        return run.Report("Host identity");
    }
}
