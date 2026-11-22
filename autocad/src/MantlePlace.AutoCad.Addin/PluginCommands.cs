using System.Globalization;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using MantlePlace.AutoCad.Addin;
using MantlePlace.Client;
using MantlePlace.Core;
using MantlePlace.Wpf;

namespace MantlePlace.AutoCad.Addin;

public sealed class PluginCommands
{
    private static SignInWindow? _signIn;

    private static AboutWindow? _about;

    [CommandMethod("MANTLEPLACESIGNIN")]
    public void SignIn()
    {
        AuthSession session = MantlePlaceApplication.Session;

        if (_signIn is not null)
        {
            _ = _signIn.Activate();
            return;
        }

        if (session.State == AuthState.Authenticated)
        {
            MantlePlaceApplication.Say(session.AccountFace().SessionSummary);
            return;
        }

        if (session.State == AuthState.Refreshing)
        {
            MantlePlaceApplication.Say("Mantle Place is restoring the sign-in stored on this machine. "
                + "Give it a moment, then try again; you may not need to sign in at all.");
            return;
        }

        _signIn = new SignInWindow(session, MantlePlaceApplication.MainWindowHandle());
        _signIn.Closed += (_, _) => _signIn = null;
        _signIn.Show();

        _ = _signIn.RunAsync();
    }

    [CommandMethod("MANTLEPLACESIGNOUT")]
    public void SignOut()
    {
        MantlePlaceApplication.Session.SignOut();
        MantlePlaceApplication.Say("Signed out. The stored session on this machine has been cleared.");
    }

    [CommandMethod("MANTLEPLACEVAULT")]
    public void Vault()
        => VaultBrowser.Open(MantlePlaceApplication.MainWindowHandle(), selectOrderId: null);

    [CommandMethod("MANTLEPLACEIMPORTBUNDLE")]
    public void ImportBundle()
    {
        if (Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument is not { } document)
        {
            return;
        }

        string? named = LocalBundleSource.Unattended(Environment.GetEnvironmentVariable(LocalBundleSource.PathVariable));
        if (named is null)
        {
            PromptOpenFileOptions options = new("\nMantle Place bundle (.zip) to import: ")
            {
                DialogCaption = "Choose a Mantle Place bundle",
                Filter = "Mantle Place bundle (*.zip)|*.zip",
                PreferCommandLine = Convert.ToInt32(Autodesk.AutoCAD.ApplicationServices.Core.Application.GetSystemVariable("FILEDIA"), CultureInfo.InvariantCulture) == 0,
            };

            PromptFileNameResult picked = document.Editor.GetFileNameForOpen(options);
            if (picked.Status != PromptStatus.OK)
            {
                return;
            }

            named = picked.StringResult;
        }

        string zipPath = LocalBundleImport.Typed(named);
        if (!WindowedImport.Start(document, zipPath, end => WriteRecord(zipPath, end.Summary), out string? refusal))
        {
            string reason = refusal ?? WindowedImport.AlreadyRunning;
            MantlePlaceApplication.Say(reason);
            WriteRecord(zipPath, reason);
        }
    }

    private static void WriteRecord(string zipPath, string outcome)
    {
        try
        {
            File.WriteAllText(LocalBundleSource.LogPathFor(zipPath), outcome + Environment.NewLine);
        }
        catch (System.Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
    }

    [CommandMethod("MANTLEPLACEFEEDBACK")]
    public void Feedback()
    {
        if (!FeedbackLauncher.TryOpen(null, out string refused))
        {
            MantlePlaceApplication.Say($"Could not open your browser. Open this address manually: {refused}");
        }
    }

    [CommandMethod("MANTLEPLACEABOUT")]
    public void About()
    {
        if (_about is not null)
        {
            _ = _about.Activate();
            return;
        }

        BuildIdentity build = BuildIdentity.Parse(PluginVersion.Current);
        string dirty = build.Dirty ? ", with uncommitted changes" : string.Empty;
        string runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

        PresenceVerdict verdict = Presence.Decide(HostObservations.Observe());

        string body = AboutPage.Body(
            $"Mantle Place for AutoCAD {build.Version} ({build.ShortSha}{dirty}), on {runtime}.",
            Civil3dPartLoader.Describe(verdict),
            MantlePlaceApplication.Session.AccountFace().SessionSummary);

        _about = new AboutWindow(
            body,
            AboutPage.PrivacyUrl(MantlePlaceApplication.Endpoints.ApiBaseUrl),
            MantlePlaceApplication.MainWindowHandle());
        _about.Closed += (_, _) => _about = null;
        _about.Show();
    }
}
