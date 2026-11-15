using System.IO;
using Autodesk.Revit.UI;

// Not unused: AccountFace() is an extension method on AuthSession, declared in Client.
using MantlePlace.Client;
using MantlePlace.Core;
using MantlePlace.Wpf;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// Opens the shared About window (<see cref="AboutWindow"/>) with what only Revit can say: which
/// build, which Revit, and the Logs button. Reachable from the Account face and from its dropdown.
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
/// The Revit version is asked of the running application rather than assumed, because one build
/// loads in three of them and "which Revit" is the other half of the same question.
/// </para>
/// <para>
/// The third half, for a maintainer: which branch and which worktree. The assembly cannot know that;
/// the deploy script did, and wrote it beside the manifest as <c>MantlePlace.install.json</c>
/// (<see cref="InstallStamp"/>). Reading it here is the one file read in this command, and it is
/// allowed to fail: a missing or unreadable stamp is a sentence, not an exception.
/// </para>
/// <para>
/// It was a <c>TaskDialog</c>, and AutoCAD's a WPF window of its own; both hosts now open the one
/// window, which gains Revit the privacy link AutoCAD's had. One window per Revit session, as the
/// vault's and sign-in's are: a second press brings the open one forward.
/// </para>
/// </remarks>
internal static class AboutMantlePlace
{
    /// <summary>The release gate: a real import completing in each of these, on a licensed install.</summary>
    private const string SupportedRevitVersions = "2025, 2026 and 2027";

    /// <summary>The face of the button that opens the logs, as the Bundles panel's own button reads.</summary>
    private const string LogsFace = "Logs";

    private static AboutWindow? _window;

    internal static Result Show(UIApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        if (_window is not null)
        {
            _window.Activate();
            return Result.Succeeded;
        }

        string body = AboutPage.Body(
            InstalledBuild.Describe(BuildIdentity.Parse(PluginVersion.Current), ReadStamp()),
            $"Running in {application.Application.VersionName} (build {application.Application.VersionBuild}).",
            $"This build supports Revit {SupportedRevitVersions}; every release is proven by a real import in all three.",

            // The same sentence the ribbon's own tooltip opens with, from the same function, so the
            // window and the face cannot disagree about whether this Revit is signed in.
            MantlePlaceApplication.Session.AccountFace().SessionSummary);

        // The same function the Bundles slide-out's Logs button calls, so the window and the button
        // can never send a curator to two different folders.
        HostCommand logs = new(
            LogsFace,
            "Shows the newest import or probe log in the bundle cache; a zip you downloaded yourself has its log beside your own copy.",
            () => OpenLogsCommand.Show(fromWindow: true));

        _window = new AboutWindow(
            body,
            AboutPage.PrivacyUrl(MantlePlaceApplication.Endpoints.ApiBaseUrl),
            application.MainWindowHandle,
            logs);
        _window.Closed += (_, _) => _window = null;
        _window.Show();
        return Result.Succeeded;
    }

    /// <summary>The deploy stamp beside the loaded assembly, or <c>null</c> when there is none to read.</summary>
    private static InstallStamp? ReadStamp() =>
        InstallStampFile.Read(Path.GetDirectoryName(typeof(AboutMantlePlace).Assembly.Location));
}
