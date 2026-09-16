using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;
using Autodesk.Revit.UI;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

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

    /// <summary>Revit's UI dispatcher, held only so the handler below can be detached again.</summary>
    private static Dispatcher? _uiDispatcher;

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

    /// <summary>
    /// Keeps a throw out of one of our own UI handlers from terminating Revit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An unhandled exception on the dispatcher is how a sign-in dialog took the whole application
    /// down: the throw left a <c>Window.Closed</c> handler, reached Revit's message pump, and Revit
    /// answered the only way it can — "an unrecoverable error has occurred", process terminated,
    /// with the add-in that caused it named nowhere. A modeless window has no outer <c>try</c> the
    /// way <c>IExternalCommand.Execute</c> does, so without this there is no layer between a handler
    /// and the end of the session.
    /// </para>
    /// <para>
    /// ⚠ <b>It marks a fault handled only when a frame on the stack is ours</b>
    /// (<see cref="AddinFaults.IsOurs"/>). The dispatcher is Revit's, not this add-in's, and
    /// swallowing Revit's own exceptions would leave the application running on state it had already
    /// given up on — a worse outcome than the crash, and one nobody could diagnose. Anything that is
    /// not ours passes through untouched, exactly as it did before this existed.
    /// </para>
    /// <para>
    /// This is a net under a mistake. The contract is still that a handler does not throw; see
    /// <c>SignInWindow.Guarded</c>, which is where the fault is supposed to be caught.
    /// </para>
    /// </remarks>
    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Frames, not just the throw site: a plugin usually mis-drives a framework type rather than
        // throwing one of its own, so the innermost frame is typically System.something.
        string?[] declaringTypes = new StackTrace(e.Exception, fNeedFileInfo: false)
            .GetFrames()
            .Select(frame => frame.GetMethod()?.DeclaringType?.FullName)
            .ToArray();

        if (!AddinFaults.IsOurs(declaringTypes))
        {
            return;
        }

        e.Handled = true;

        // Said out loud rather than swallowed. A curator who sees this once has a bug to report; one
        // who sees nothing has a plugin that quietly does not work.
        new TaskDialog("Mantle Place")
        {
            MainInstruction = "Something in Mantle Place went wrong.",
            MainContent = $"{e.Exception.Message}\n\nRevit is still running and your model is "
                + "untouched. If this keeps happening, please report it.",
        }.Show();
    }

    public Result OnStartup(UIControlledApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        // OnStartup runs on Revit's UI thread, which is the only moment this add-in is guaranteed to
        // see the dispatcher it needs to guard.
        _uiDispatcher = Dispatcher.CurrentDispatcher;
        _uiDispatcher.UnhandledException += OnDispatcherUnhandledException;

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

        panel.AddItem(new PushButtonData(
            "MantlePlaceProbeTerrain",
            "Probe\nterrain",
            assemblyPath,
            typeof(TerrainProbeCommand).FullName)
        {
            LongDescription =
                "Measure what this project would give the terrain importer — its levels, its toposolid "
                + "types and the bundle's own elevations — and try each way of placing the terrain. "
                + "Every attempt is rolled back, so nothing in your project changes. Use it when an "
                + "import is refused and the log does not say enough.",
        });

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        if (_uiDispatcher is not null)
        {
            _uiDispatcher.UnhandledException -= OnDispatcherUnhandledException;
            _uiDispatcher = null;
        }

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
