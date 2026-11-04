using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;
using MantlePlace.Revit.Core.Tests;

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

        return run.Report("Host identity");
    }
}
