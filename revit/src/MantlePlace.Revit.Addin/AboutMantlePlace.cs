using System.IO;
using Autodesk.Revit.UI;

using MantlePlace.Client;
using MantlePlace.Core;
using MantlePlace.Wpf;

namespace MantlePlace.Revit.Addin;

internal static class AboutMantlePlace
{
    private const string SupportedRevitVersions = "2025, 2026 and 2027";

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

            MantlePlaceApplication.Session.AccountFace().SessionSummary);

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

    private static InstallStamp? ReadStamp() =>
        InstallStampFile.Read(Path.GetDirectoryName(typeof(AboutMantlePlace).Assembly.Location));
}
