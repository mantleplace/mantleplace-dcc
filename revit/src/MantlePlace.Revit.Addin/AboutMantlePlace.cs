using Autodesk.Revit.UI;

// Not unused: AccountFace() is an extension method on AuthSession, declared in Client.
using MantlePlace.Revit.Client;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// The About dialog, reachable from the Account face and from its dropdown.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>It exists to answer "which build?" before anything else.</b> A pile of Revit API calls in
/// this tree compile but are proven only by a real import (<c>revit/CLAUDE.md</c> lists them), so the
/// first question about any of them throwing is which assembly threw — and the artifact that reaches
/// us is a log file from a machine we cannot inspect. <see cref="PluginVersion"/> put the answer at
/// the head of that log; this puts it where a curator can read it out over a call.
/// </para>
/// <para>
/// The Revit version is asked of the running application rather than assumed, because one
/// <c>net8.0-windows</c> build loads in three of them and "which Revit" is the other half of the
/// same question.
/// </para>
/// </remarks>
internal static class AboutMantlePlace
{
    /// <summary>The release gate: a real import completing in each of these, on a licensed install.</summary>
    private const string SupportedRevitVersions = "2025, 2026 and 2027";

    internal static Result Show(UIApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        TaskDialog dialog = new("About Mantle Place")
        {
            MainInstruction = "Mantle Place for Revit",
            MainContent =
                $"Add-in version {PluginVersion.Current}.\n"
                + $"Running in {application.Application.VersionName} "
                + $"(build {application.Application.VersionBuild}).\n"
                + $"This build supports Revit {SupportedRevitVersions}; every release is proven by a "
                + "real import in all three.\n\n"

                // The same sentence the ribbon's own tooltip opens with, from the same function, so
                // the dialog and the face cannot disagree about whether this Revit is signed in.
                + MantlePlaceApplication.Session.AccountFace().SessionSummary,
            CommonButtons = TaskDialogCommonButtons.Close,
            DefaultButton = TaskDialogResult.Close,
        };

        dialog.AddCommandLink(
            TaskDialogCommandLinkId.CommandLink1,
            "Open the import logs",
            "Each import and each probe writes its log beside the bundle zip it read. This shows the "
                + "newest one there is; a zip you downloaded yourself has its log beside your own copy.");

        if (dialog.Show() == TaskDialogResult.CommandLink1)
        {
            // The same function the Bundles slide-out's Logs button calls, so the dialog and the
            // button can never send a curator to two different folders.
            OpenLogsCommand.Show();
        }

        return Result.Succeeded;
    }
}
