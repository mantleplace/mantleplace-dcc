using System.Globalization;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using MantlePlace.AutoCad.Addin;
using MantlePlace.Client;
using MantlePlace.Core;

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
    /// Import Bundle (HPS-61): a bundle zip on disk, placed in the active drawing by the importer a received
    /// export's Import button uses, so the same zone check, refusal and surface. No account is needed.
    /// </summary>
    /// <remarks>
    /// The zip comes from AutoCAD's own file prompt: a dialog, or the command line under <c>FILEDIA</c> 0 or
    /// a script, which is how a script runs it with nobody at the screen. What happened is said on the
    /// command line and written beside the zip, as the Revit plugin's local import writes its record.
    /// </remarks>
    [CommandMethod("MANTLEPLACEIMPORTBUNDLE")]
    public void ImportBundle()
    {
        if (Autodesk.AutoCAD.ApplicationServices.Core.Application.DocumentManager.MdiActiveDocument is not { } document)
        {
            return;
        }

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

        string zipPath = LocalBundleImport.Typed(picked.StringResult);
        string outcome;
        try
        {
            PresenceVerdict presence = Presence.Decide(HostObservations.Observe());
            outcome = OrderImporter.Import(document, zipPath, presence).Summary;
            MantlePlaceApplication.Say(outcome);
        }
#pragma warning disable CA1031 // An import that throws is reported, never left to take AutoCAD down.
        catch (System.Exception ex)
#pragma warning restore CA1031
        {
            outcome = $"The import stopped on an error: {ex.Message}. What it placed before the error stays in the drawing.";
            MantlePlaceApplication.Say(outcome);
            MantlePlaceApplication.SayFault(ex);
        }

        try
        {
            File.WriteAllText(LocalBundleSource.LogPathFor(zipPath), outcome + Environment.NewLine);
        }
        catch (System.Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A folder that cannot be written to keeps no record; the command line has it.
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
