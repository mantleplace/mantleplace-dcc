using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>Opens the web Feedback form in the default browser (<c>HPS-56</c>).</summary>
/// <remarks>
/// Shared by the Account dropdown's <c>Send Feedback</c> and the vault window's <c>Report a Problem</c>
/// so both build the link the same way. The decisions are <see cref="FeedbackLink"/>'s, in the pure
/// core; what is left here is reading the three inputs that only the running add-in has.
/// </remarks>
internal static class FeedbackLauncher
{
    /// <param name="orderId">The bundle's order id for <c>Report a Problem</c>; null for the general link.</param>
    /// <param name="refused">Empty on success; otherwise the address, so the curator can type it.</param>
    internal static bool TryOpen(string? orderId, out string refused)
    {
        // The site is the deployment the add-in already talks to, as "Open mantle.place" does: a
        // dev stack that moves apiBaseUrl moves the feedback link with it.
        string link = FeedbackLink.Build(
            MantlePlaceApplication.Endpoints.ApiBaseUrl,
            FeedbackLink.ReleaseVersion(PluginVersion.Current),
            MantlePlaceApplication.HostVersion,
            orderId);

        return ShellLauncher.TryOpen(link, out refused);
    }

    /// <summary>
    /// What Yes and No open on the import window's "Did this import come in right?" line, for the
    /// bundle just imported. Built on Revit's thread, where the endpoints and the host version are read.
    /// </summary>
    internal static ImportFeedbackLinks ImportLinks(string orderId)
        => ImportFeedbackPrompt.Links(
            MantlePlaceApplication.Endpoints.ApiBaseUrl,
            FeedbackLink.ReleaseVersion(PluginVersion.Current),
            MantlePlaceApplication.HostVersion,
            orderId);
}
