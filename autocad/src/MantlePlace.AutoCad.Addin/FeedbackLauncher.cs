using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.AutoCad.Addin;

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
}
