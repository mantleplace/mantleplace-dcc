using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

internal static class FeedbackLauncher
{
    internal static bool TryOpen(string? orderId, out string refused)
    {
        string link = FeedbackLink.Build(
            MantlePlaceApplication.Endpoints.ApiBaseUrl,
            FeedbackLink.ReleaseVersion(PluginVersion.Current),
            MantlePlaceApplication.HostVersion,
            orderId);

        return ShellLauncher.TryOpen(link, out refused);
    }

    internal static ImportFeedbackLinks ImportLinks(string orderId)
        => ImportFeedbackPrompt.Links(
            MantlePlaceApplication.Endpoints.ApiBaseUrl,
            FeedbackLink.ReleaseVersion(PluginVersion.Current),
            MantlePlaceApplication.HostVersion,
            orderId);
}
