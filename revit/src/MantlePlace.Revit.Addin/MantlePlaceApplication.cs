using System.Reflection;
using Autodesk.Revit.UI;
using MantlePlace.Revit.Client;

namespace MantlePlace.Revit.Addin;

/// <summary>Registers the Mantle Place ribbon and owns the process's one auth session.</summary>
public sealed class MantlePlaceApplication : IExternalApplication
{
    private const string TabName = "Mantle Place";

    private static AuthSession? _session;
    private static VaultClient? _vault;
    private static BundleCache? _cache;
    private static ExternalEvent? _importEvent;
    private static BundleImportEventHandler? _importHandler;

    /// <summary>
    /// The one session for this Revit process.
    /// </summary>
    /// <remarks>
    /// Static accessors rather than parameters because Revit constructs each
    /// <see cref="IExternalCommand"/> itself and gives us no way to inject anything. One of each per
    /// process, not one per command: two sessions would each hold their own access token, and
    /// signing out in one would leave the other authenticated and the ribbon lying about it.
    /// </remarks>
    public static AuthSession Session => _session ?? throw NotStarted();

    internal static VaultClient Vault => _vault ?? throw NotStarted();

    internal static BundleCache Cache => _cache ?? throw NotStarted();

    /// <summary>The only supported way onto Revit's document thread from a modeless window.</summary>
    internal static ExternalEvent ImportEvent => _importEvent ?? throw NotStarted();

    internal static BundleImportEventHandler ImportHandler => _importHandler ?? throw NotStarted();

    /// <summary>
    /// Resumes a stored session at startup (<c>HPS-13</c>), without making Revit wait for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is what makes the DPAPI refresh token worth writing.</b> It was being written on
    /// every sign-in and never read back, so every Revit restart forced a full browser round-trip —
    /// and <c>SignInCommand</c> promised otherwise, telling the curator they would only have to sign
    /// in again if the machine had no secure store.
    /// </para>
    /// <para>
    /// Fire-and-forget on purpose. <c>OnStartup</c> runs on Revit's thread before the UI exists;
    /// blocking it on a network call delays the splash screen by however long the platform takes to
    /// answer, and throwing from it costs the entire ribbon. <see cref="AuthSession.RestoreAsync"/>
    /// is already silent when there is no stored token and keeps the token on a network failure, so
    /// there is nothing here worth interrupting a curator for: the worst case is that the ribbon
    /// comes up signed out, which is exactly where it came up before.
    /// </para>
    /// </remarks>
    private static void BeginSessionRestore(AuthSession session)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await session.RestoreAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Deliberately total. Nothing is observing this task, an unobserved exception on the
                // finalizer thread can take the process down, and there is no surface to report to
                // this early. The session simply stays signed out.
            }
        });
    }

    public Result OnStartup(UIControlledApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        MantlePlaceEndpoints endpoints = MantlePlaceEndpoints.Load();
        _session = new AuthSession(endpoints, SecretStores.ForCurrentPlatform());
        _vault = new VaultClient(endpoints, _session);
        _cache = new BundleCache();

        BeginSessionRestore(_session);

        // Created during OnStartup because ExternalEvent.Create must run on Revit's own thread, and
        // a modeless window has no other moment when that is guaranteed.
        _importHandler = new BundleImportEventHandler();
        _importEvent = ExternalEvent.Create(_importHandler);

        // CreateRibbonTab throws when the tab already exists — which happens whenever the .addin
        // manifest is installed both machine-wide and per-user. An unhandled throw here disables the
        // whole add-in with a load error, so a duplicate tab is treated as "already there".
        try
        {
            application.CreateRibbonTab(TabName);
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException)
        {
        }

        string assemblyPath = Assembly.GetExecutingAssembly().Location;

        RibbonPanel account = application.CreateRibbonPanel(TabName, "Account");
        account.AddItem(new PushButtonData(
            "MantlePlaceSignIn",
            "Sign in",
            assemblyPath,
            typeof(SignInCommand).FullName)
        {
            LongDescription =
                "Sign in to Mantle Place in your browser. Revit never sees your password: the browser "
                + "returns an authorization code to a local address only this session is listening on.",
        });

        account.AddItem(new PushButtonData(
            "MantlePlaceSignOut",
            "Sign out",
            assemblyPath,
            typeof(SignOutCommand).FullName)
        {
            LongDescription = "Sign out and forget the stored session on this machine.",
        });

        RibbonPanel panel = application.CreateRibbonPanel(TabName, "Bundles");
        panel.AddItem(new PushButtonData(
            "MantlePlaceOpenVault",
            "Open\nvault",
            assemblyPath,
            typeof(VaultBrowserCommand).FullName)
        {
            LongDescription =
                "Browse the bundles you own, prepare their Revit deliverables, download them and import. "
                + "The window stays open while a bundle builds — closing it does not cancel the job, and "
                + "reopening rejoins it.",
        });

        panel.AddItem(new PushButtonData(
            "MantlePlaceImportLocalBundle",
            "Import\nbundle zip",
            assemblyPath,
            typeof(ImportLocalBundleCommand).FullName)
        {
            LongDescription =
                "Import a Mantle Place bundle zip you have already downloaded: builds the toposurface from "
                + "the points file and links the IFC site model. Importing straight from your vault arrives "
                + "next; this local path stays as the permanent fallback.",
        });

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        _importEvent?.Dispose();
        _importEvent = null;
        _importHandler = null;
        _vault = null;
        _cache = null;
        _session?.Dispose();
        _session = null;
        return Result.Succeeded;
    }

    private static InvalidOperationException NotStarted()
        => new("the Mantle Place add-in did not finish starting up");
}
