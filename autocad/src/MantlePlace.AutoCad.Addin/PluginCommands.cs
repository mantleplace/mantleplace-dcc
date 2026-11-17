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

/// <summary>
/// The shell's commands: the six the Mantle Place tab panel types (<see cref="PlugInsPanel.Commands"/>).
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>No command waits on the network</b>, and none throws out to AutoCAD. Sign-in starts a flow and
/// hands it to a modeless window, as the Revit plugin's does: the browser round-trip takes minutes and a
/// command that waited for it would freeze AutoCAD. Every decision (PKCE, state validation, which failure
/// a callback is) is in the shared sources; these are the buttons.
/// </para>
/// <para>
/// The names are registered here and listed again in the pure core, which the ribbon panel is built from;
/// a command added here and not there has no button, and one there and not here types an unknown command.
/// The shell is not built in CI, so the two are checked by loading it (the release check on the ticket).
/// </para>
/// </remarks>
public sealed class PluginCommands
{
    /// <summary>The one sign-in window. A second command focuses it rather than starting a second browser round-trip.</summary>
    private static SignInWindow? _signIn;

    /// <summary>The one About window.</summary>
    private static AboutWindow? _about;

    /// <summary>Signs in: the system browser opens and a modeless window waits for the loopback redirect (HPS-05).</summary>
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

        // Restoring a stored session, or renewing an expiring one, lands here: the state machine would
        // refuse a sign-in raised on top of either, so say what is happening instead of doing nothing.
        if (session.State == AuthState.Refreshing)
        {
            MantlePlaceApplication.Say("Mantle Place is restoring the sign-in stored on this machine. "
                + "Give it a moment, then try again; you may not need to sign in at all.");
            return;
        }

        _signIn = new SignInWindow(session, MantlePlaceApplication.MainWindowHandle());
        _signIn.Closed += (_, _) => _signIn = null;
        _signIn.Show();

        // Discarded deliberately: the outcome is reported on the window, and RunAsync lets no
        // exception escape. Awaiting it here would reintroduce the freeze this avoids.
        _ = _signIn.RunAsync();
    }

    /// <summary>Signs out: drops the session and clears the stored refresh token.</summary>
    [CommandMethod("MANTLEPLACESIGNOUT")]
    public void SignOut()
    {
        MantlePlaceApplication.Session.SignOut();
        MantlePlaceApplication.Say("Signed out. The stored session on this machine has been cleared.");
    }

    /// <summary>Opens the vault browser (or brings it forward).</summary>
    [CommandMethod("MANTLEPLACEVAULT")]
    public void Vault()
        => VaultBrowser.Open(MantlePlaceApplication.MainWindowHandle(), selectOrderId: null);

    /// <summary>
    /// Import Bundle (HPS-61): a bundle zip on disk, imported into the active drawing in the shared import
    /// window (HPS-60), as a send from the web vault is, so the same zone check, refusal and surface. No
    /// account is needed.
    /// </summary>
    /// <remarks>
    /// The zip comes from AutoCAD's own file prompt: a dialog, or the command line under <c>FILEDIA</c> 0 or
    /// a script; or, with nobody at the screen, from <c>MANTLEPLACE_BUNDLE_ZIP</c>, the variable Revit's local
    /// import reads (<see cref="LocalBundleSource.PathVariable"/>), which skips the prompt. The command returns once the
    /// window is open and the steps run after it, one between AutoCAD's messages, so a script waits for the
    /// record written beside the zip when the run is over, as the Revit plugin's local import writes its own;
    /// the command line and the window say the same.
    /// </remarks>
    [CommandMethod("MANTLEPLACEIMPORTBUNDLE")]
    public void ImportBundle()
    {
        if (Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument is not { } document)
        {
            return;
        }

        // An unattended run names its zip in the shared variable, as Revit's local import reads it, and
        // no prompt is raised for nobody to answer.
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
            // Nothing started, so no run will end to write the record: write it now, with the reason,
            // or a script waiting for it waits until it gives up.
            string reason = refusal ?? WindowedImport.AlreadyRunning;
            MantlePlaceApplication.Say(reason);
            WriteRecord(zipPath, reason);
        }
    }

    /// <summary>
    /// Writes what the import did beside the zip, once the run is over: the record a script waits for,
    /// since the import's steps run after the command has returned.
    /// </summary>
    private static void WriteRecord(string zipPath, string outcome)
    {
        try
        {
            File.WriteAllText(LocalBundleSource.LogPathFor(zipPath), outcome + Environment.NewLine);
        }
        catch (System.Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A folder that cannot be written to keeps no record; the command line and the window have it.
        }
    }

    /// <summary>The web Feedback form, in the default browser (HPS-56). Works signed in or out.</summary>
    [CommandMethod("MANTLEPLACEFEEDBACK")]
    public void Feedback()
    {
        if (!FeedbackLauncher.TryOpen(null, out string refused))
        {
            MantlePlaceApplication.Say($"Could not open your browser. Open this address manually: {refused}");
        }
    }

    /// <summary>Says which Mantle Place build is loaded, whether the Civil 3D part is on, and the session, with the privacy page linked.</summary>
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

        // Decided here, at first use, not when the bundle loads: see PluginApplication.
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
