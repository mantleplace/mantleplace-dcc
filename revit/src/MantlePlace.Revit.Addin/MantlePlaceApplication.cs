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
    private static MantlePlaceEndpoints? _endpoints;
    private static VaultClient? _vault;
    private static BundleCache? _cache;
    private static ExternalEvent? _importEvent;
    private static BundleImportEventHandler? _importHandler;

    /// <summary>Revit's UI dispatcher, held only so the handler below can be detached again.</summary>
    private static Dispatcher? _uiDispatcher;

    /// <summary>
    /// The Account split button and the three items whose appearance depends on the session.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Retained on purpose.</b> The two push buttons these replace were added and their
    /// references dropped on the floor, which is why nothing could ever change their text or their
    /// enabled state — the ribbon was structurally incapable of reflecting the session, and the
    /// truth was left to a dialog at click time. A ribbon item is only ever as honest as the
    /// reference somebody kept.
    /// </remarks>
    private static SplitButton? _accountButton;

    private static PushButton? _accountFace;
    private static PushButton? _accountIdentity;
    private static PushButton? _signOutButton;

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

    /// <summary>The routes this process signs in against and sends curators to.</summary>
    internal static MantlePlaceEndpoints Endpoints => _endpoints ?? throw NotStarted();

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

    /// <summary>
    /// The Account panel: one split button whose face is the auth session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b><c>IsSynchronizedWithCurrentItem = false</c> is what makes the face a face.</b> A Revit
    /// <c>SplitButton</c> defaults to true, which means it adopts whichever dropdown item was used
    /// last — click "Open mantle.place" once and the button that reports your session is replaced by
    /// a link to a website. With it false the face is pinned to the first item added, which is why
    /// <c>MantlePlaceAccountFace</c> goes on first and why the order of the rest is not decorative.
    /// </para>
    /// <para>
    /// ⚠ The face's tooltip is set on the <b>push button</b>, not on the split button:
    /// <c>RibbonItem.ToolTip</c> on a <c>SplitButton</c> is documented as never being shown.
    /// </para>
    /// <para>
    /// The panel name stays <c>Account</c> and the tab stays <c>Mantle Place</c> — a recorded journal
    /// keys on both, and on the <c>Bundles</c> panel's command ids, which is why those are untouched
    /// here (<c>revit/README.md</c> ▸ the ribbon events a playback journal needs).
    /// <c>MantlePlaceSignOut</c> keeps its id for the same reason, one dropdown row lower.
    /// </para>
    /// </remarks>
    private static void BuildAccountPanel(UIControlledApplication application, string assemblyPath)
    {
        RibbonPanel account = application.CreateRibbonPanel(TabName, "Account");

        // Taken from the same place the live updates are, rather than written out again here. The
        // state a ribbon is built in is the state the session starts in, and two copies of one
        // string is how a face ends up saying something the core stopped saying a year ago.
        AccountRibbonState initial = AccountRibbon.For(AuthStateMachine.Initial, null, false);

        _accountButton = (SplitButton)account.AddItem(
            new SplitButtonData("MantlePlaceAccount", initial.FaceText));

        // First, and therefore the face. Its text, tooltip and enabled state are rewritten on every
        // auth transition by ApplyAccountState; what is set here is only what the ribbon shows in
        // the moment between the panel existing and the session being read.
        // ⚠ The id is MantlePlaceSignIn, not MantlePlaceAccountFace, and the difference is a rule
        // rather than a preference: command ids are fixed here because a recorded journal keys on
        // them. This button does more than its id now says — signed in, it opens About — and the id
        // stays anyway. An id is an address, not a description, and renaming it to read better
        // would break the one thing it is for.
        _accountFace = _accountButton.AddPushButton(new PushButtonData(
            "MantlePlaceSignIn",
            initial.FaceText,
            assemblyPath,
            typeof(AccountCommand).FullName)
        {
            ToolTip = initial.FaceToolTip,
            LongDescription =
                "Sign in to Mantle Place in your browser. Revit never sees your password: the browser "
                + "returns an authorization code to a local address only this Revit is listening on. "
                + "Signing in here signs you in for every Mantle Place plugin on this machine. Only the "
                + "vault needs you signed in; a bundle import does not. Once signed in, this button "
                + "opens About Mantle Place.",
        });

        // The address, shown only while there is one. Disabled for its whole life: it is a line of
        // text that happens to live in a control that can only hold buttons.
        _accountIdentity = _accountButton.AddPushButton(new PushButtonData(
            "MantlePlaceAccountIdentity",
            AccountRibbon.SignedInFace,
            assemblyPath,
            typeof(AccountIdentityCommand).FullName)
        {
            ToolTip = "The account this Revit is signed in to.",
        });
        _accountIdentity.Enabled = false;
        _accountIdentity.Visible = false;

        _signOutButton = _accountButton.AddPushButton(new PushButtonData(
            "MantlePlaceSignOut",
            "Sign Out",
            assemblyPath,
            typeof(SignOutCommand).FullName)
        {
            ToolTip = "Sign out and forget the stored credential.",
            LongDescription =
                "Sign out of Mantle Place and forget the credential stored for this OS user, which signs "
                + "you out of every Mantle Place plugin on this machine. The vault asks you to sign in "
                + "again afterwards; a bundle import still does not.",
        });

        _accountButton.AddPushButton(new PushButtonData(
            "MantlePlaceOpenSite",
            "Open mantle.place",
            assemblyPath,
            typeof(OpenMantlePlaceCommand).FullName)
        {
            ToolTip = "Open the Mantle Place website in your browser.",
        });

        _accountButton.AddPushButton(new PushButtonData(
            "MantlePlaceAbout",
            "About Mantle Place",
            assemblyPath,
            typeof(AboutMantlePlaceCommand).FullName)
        {
            ToolTip = "Which build this is, and which Revit it is running in.",
        });

        _accountButton.IsSynchronizedWithCurrentItem = false;
    }

    /// <summary>
    /// Copies the session onto the Account button. Revit's UI thread only.
    /// </summary>
    /// <remarks>
    /// Reads the session rather than the transition it was told about, so the ribbon can never be one
    /// event behind — and so the single call after the ribbon is built catches whatever the startup
    /// restore did while the panel did not exist yet.
    /// </remarks>
    private static void ApplyAccountState()
    {
        AuthSession? session = _session;
        if (session is null || _accountFace is null)
        {
            return;
        }

        AccountRibbonState face = session.AccountFace();

        try
        {
            _accountFace.ItemText = face.FaceText;
            _accountFace.ToolTip = face.FaceToolTip;
            _accountFace.Enabled = face.FaceEnabled;

            // Belt and braces: with IsSynchronizedWithCurrentItem false the split button draws its
            // first item, but it carries its own text for the case where the dropdown is empty, and
            // leaving that at whatever it was created with is a second thing able to lie.
            if (_accountButton is not null)
            {
                _accountButton.ItemText = face.FaceText;
            }

            if (_accountIdentity is not null)
            {
                if (face.IdentityVisible)
                {
                    _accountIdentity.ItemText = face.IdentityText;
                }

                _accountIdentity.Visible = face.IdentityVisible;
            }

            if (_signOutButton is not null)
            {
                _signOutButton.Enabled = face.SignOutEnabled;
            }
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            // Swallowed, and only here. This runs on every auth transition, so a ribbon that refuses
            // an assignment would otherwise raise the fault dialog repeatedly — for a button, while
            // the curator is signing in. The strings are pre-checked in AccountRibbon precisely so
            // this catch stays unreachable; what is left is Revit refusing a ribbon it is tearing
            // down, and a stale face on the way out costs nothing.
        }
    }

    /// <summary>
    /// Marshals an auth transition onto Revit's UI thread, then repaints the Account button.
    /// </summary>
    /// <remarks>
    /// ⛔ <c>AuthSession.StateChanged</c> is raised on whichever thread finished the work, which is a
    /// thread-pool thread for every transition a sign-in or a refresh produces. Touching a
    /// <c>PushButton</c> from there is a main-thread violation, and Revit answers those by
    /// terminating the process — so this hop is not a nicety.
    /// </remarks>
    /// <remarks>
    /// ⚠ <b>What this does and does not see when the other host changes the shared credential.</b>
    /// Both hosts keep one stored session per Windows user, so Unreal can sign in or out underneath
    /// this process. A renewal is the moment that becomes visible: <c>AuthSession.RefreshAsync</c>
    /// re-reads the freshest stored token before spending a round trip, so a credential the other
    /// host rotated arrives here as an ordinary Refreshing → Authenticated transition and the face is
    /// rebuilt from the session, never from anything cached at startup. <b>Nothing watches the store
    /// itself.</b> Unreal signing out does not move this process's session, and this ribbon goes on
    /// saying Signed In until the next renewal finds the credential gone — at which point Sign Out
    /// here is still correct, because it clears memory as well as the store.
    /// </remarks>
    private static void OnAuthStateChanged(object? sender, AuthState state)
    {
        Dispatcher? dispatcher = _uiDispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            ApplyAccountState();
            return;
        }

        _ = dispatcher.BeginInvoke(new Action(ApplyAccountState));
    }

    public Result OnStartup(UIControlledApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        // OnStartup runs on Revit's UI thread, which is the only moment this add-in is guaranteed to
        // see the dispatcher it needs to guard.
        _uiDispatcher = Dispatcher.CurrentDispatcher;
        _uiDispatcher.UnhandledException += OnDispatcherUnhandledException;

        MantlePlaceEndpoints endpoints = MantlePlaceEndpoints.Load();
        _endpoints = endpoints;
        _session = new AuthSession(endpoints, SecretStores.ForCurrentPlatform());
        _vault = new VaultClient(endpoints, _session);
        _cache = new BundleCache();

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

        BuildAccountPanel(application, assemblyPath);

        RibbonPanel panel = application.CreateRibbonPanel(TabName, "Bundles");
        panel.AddItem(new PushButtonData(
            "MantlePlaceOpenVault",
            "Vault",
            assemblyPath,
            typeof(VaultBrowserCommand).FullName)
        {
            ToolTip = "Browse the bundles in your vault and import one.",
            LongDescription =
                "Open the vault browser: it lists the bundles you own, prepares their Revit deliverables, "
                + "downloads them and imports them. It stays open while a bundle builds — closing it does "
                + "not cancel the job, and reopening rejoins it. This is the one surface that needs you "
                + "signed in.",
        });

        panel.AddItem(new PushButtonData(
            "MantlePlaceImportLocalBundle",
            "Import\nBundle",
            assemblyPath,
            typeof(ImportLocalBundleCommand).FullName)
        {
            ToolTip = "Import a bundle zip you already have on disk.",
            LongDescription =
                "Import a Mantle Place bundle zip you have already downloaded: builds the terrain as one "
                + "toposolid from the bundle's surface artifact, and links the IFC site model beside it. "
                + "A bundle import needs no account and no sign-in, so this path stays as the permanent "
                + "fallback beside the vault.",
        });

        panel.AddItem(new PushButtonData(
            "MantlePlaceProbeTerrain",
            "Probe\nTerrain",
            assemblyPath,
            typeof(TerrainProbeCommand).FullName)
        {
            ToolTip = "Measure what a terrain import would find here, changing nothing.",
            LongDescription =
                "Measure what this project would give the terrain importer — its levels, its toposolid "
                + "types and the bundle's own elevations — and try each way of placing the terrain. "
                + "Every attempt is rolled back, so nothing in your project changes. Use it when a bundle "
                + "import is refused and the log does not say enough.",
        });

        // Subscribed after the panel exists, and followed by one unconditional read of the session.
        // The startup restore below runs on a thread pool thread and can finish at any point,
        // including before this line; reading the session here rather than trusting the event means
        // a transition that landed while the ribbon was still being built is not missed.
        _session.StateChanged += OnAuthStateChanged;
        BeginSessionRestore(_session);
        ApplyAccountState();

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        if (_uiDispatcher is not null)
        {
            _uiDispatcher.UnhandledException -= OnDispatcherUnhandledException;
            _uiDispatcher = null;
        }

        // Before the session is disposed: a transition raised after the ribbon is gone would post to
        // a dispatcher that is shutting down.
        if (_session is not null)
        {
            _session.StateChanged -= OnAuthStateChanged;
        }

        _accountButton = null;
        _accountFace = null;
        _accountIdentity = null;
        _signOutButton = null;

        _importEvent?.Dispose();
        _importEvent = null;
        _importHandler = null;
        _vault = null;
        _cache = null;
        _endpoints = null;
        _session?.Dispose();
        _session = null;
        return Result.Succeeded;
    }

    private static InvalidOperationException NotStarted()
        => new("the Mantle Place add-in did not finish starting up");
}
