using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace MantlePlace.AutoCad.Addin;

/// <summary>
/// Owns the process's one auth session, the vault client and the connected host, and starts and stops
/// them with the plugin.
/// </summary>
/// <remarks>
/// <para>
/// Static accessors rather than parameters because AutoCAD constructs the command class itself and
/// gives no way to inject anything. One of each per process: two sessions would each hold an access
/// token, and signing out in one would leave the other authenticated.
/// </para>
/// <para>
/// The wiring is the Revit plugin's, minus the parts that need a hook into the drawing: no Prepare
/// watcher, no re-joiner and no import event. What each part decides is in the shared sources, where
/// the headless suite holds it; what is here is construction, threads and teardown.
/// </para>
/// </remarks>
internal static class MantlePlaceApplication
{
    private static AuthSession? _session;
    private static SessionRenewer? _renewer;
    private static MantlePlaceEndpoints? _endpoints;
    private static VaultClient? _vault;
    private static BundleCache? _cache;
    private static AnnouncedOrderStore? _announced;
    private static VaultNewsChecker? _news;
    private static ConnectedHostService? _connected;
    private static readonly SignInEdge SignIns = new();
    private static readonly ConnectedHostEdge ConnectedEdge = new();

    /// <summary>The active document's name, as the presence payload last announced it; <c>null</c> for none.</summary>
    private static volatile string? _document;

    private static string? _hostVersion;

    /// <summary>AutoCAD's UI dispatcher, held so the handler below can be detached again.</summary>
    private static Dispatcher? _uiDispatcher;

    /// <summary>The one session for this AutoCAD process.</summary>
    internal static AuthSession Session => _session ?? throw NotStarted();

    /// <summary>The routes this process signs in against and sends curators to.</summary>
    internal static MantlePlaceEndpoints Endpoints => _endpoints ?? throw NotStarted();

    /// <summary>The running AutoCAD's release year, read once at startup so a window can build the feedback link (HPS-56).</summary>
    internal static string? HostVersion => _hostVersion;

    internal static VaultClient Vault => _vault ?? throw NotStarted();

    internal static BundleCache Cache => _cache ?? throw NotStarted();

    /// <summary>This machine's record of announced orders, shared with every other host on it.</summary>
    internal static AnnouncedOrderStore Announced => _announced ?? throw NotStarted();

    /// <summary>
    /// The connected host: this AutoCAD on the curator's private channel, receiving exports from the
    /// web vault. <c>null</c> before startup and after shutdown, never otherwise.
    /// </summary>
    internal static ConnectedHostService? Connected => _connected;

    /// <summary>Starts everything. AutoCAD's UI thread, from <c>IExtensionApplication.Initialize</c>.</summary>
    internal static void Start()
    {
        // Initialize runs on AutoCAD's UI thread, the only moment this plugin is certain to see the
        // dispatcher it needs to guard.
        _uiDispatcher = Dispatcher.CurrentDispatcher;
        _uiDispatcher.UnhandledException += OnDispatcherUnhandledException;

        _hostVersion = HostYear();

        MantlePlaceEndpoints endpoints = MantlePlaceEndpoints.Load();
        _endpoints = endpoints;

        // Before the first Platform API call (the restore below), so a 426 on it is heard.
        ClientUpdateDialog.Attach();
        _session = new AuthSession(endpoints, SecretStores.ForCurrentPlatform());
        _vault = new VaultClient(endpoints, _session);
        _cache = new BundleCache();
        _announced = new AnnouncedOrderStore(AnnouncedOrderStore.DefaultPath);
        _news = new VaultNewsChecker(new SessionNewsSource(_session, _vault), _announced, () => VaultBrowser.IsOpen);

        // One connected host for the whole AutoCAD session, under this launch's connection id. It joins
        // on the first sign-in (the restore below included) and stays off while the channel is not
        // configured. Its route calls and fetches run on the pool; the window it opens is posted to
        // the UI thread by AutoCadExportImportHost.
        ConnectedHostLog connectedLog = new(ConnectedHostLog.DefaultPath);
        string connectionId = ConnectionIdSource.ThisLaunch.Id;
        ExportBundleFetcher fetcher = new(new VaultExportSource(_vault, _cache), new VaultPrepareSteps(_vault, _cache));
        _connected = new ConnectedHostService(
            connectionId,
            () => RealtimeClient.For(endpoints, connectionId, connectedLog.Append),
            new ExportsClient(endpoints, new AuthBearerSession(_session)),
            fetcher.FetchAsync,
            connectedLog.Append);
        _connected.Attach(new AutoCadExportImportHost());

        AcApplication.DocumentManager.DocumentBecameCurrent += OnDocumentBecameCurrent;
        PluginRibbon.Attach();

        // Subscribed before the restore, so a transition it makes is not missed.
        _session.StateChanged += OnAuthStateChanged;

        // Renews ahead of each lapse: the connected host's channel makes no route call to renew on.
        _renewer = new SessionRenewer(_session);
        _news.Arrived += OnNewsArrived;
        BeginSessionRestore(_session);

        // Its first listing is now, and asks nothing while the restore above is still signing in;
        // the restore's own sign-in wakes it (OnAuthStateChanged, SignInEdge).
        _news.Start();
    }

    /// <summary>Stops everything, in the order the Revit plugin does. AutoCAD's UI thread, from <c>Terminate</c>.</summary>
    internal static void Stop()
    {
        AcApplication.DocumentManager.DocumentBecameCurrent -= OnDocumentBecameCurrent;
        PluginRibbon.Detach();

        // Leaves the channel before the window goes, so no export is opened behind it. Bounded: the
        // client waits at most two seconds for the leave to be answered, and AutoCAD's close waits no
        // longer than that for the untrack to go out rather than leaning on the socket dropping.
        if (_connected is { } connected)
        {
            _connected = null;
            _ = Task.Run(() => LeaveQuietlyAsync(connected)).Wait(TimeSpan.FromSeconds(3));
        }

        // Stopped before it is unsubscribed: a listing that lands after the stop claims nothing, so no
        // order is recorded as announced with nobody left to announce it.
        if (_news is not null)
        {
            _news.Stop();
            _news.Arrived -= OnNewsArrived;
        }

        VaultBrowser.Close();
        ArrivalNotifier.Forget();
        ClientUpdateDialog.Detach();

        if (_uiDispatcher is not null)
        {
            _uiDispatcher.UnhandledException -= OnDispatcherUnhandledException;
            _uiDispatcher = null;
        }

        // Before the session is disposed: a transition raised after the windows are gone would post to
        // a dispatcher that is shutting down.
        if (_session is not null)
        {
            _session.StateChanged -= OnAuthStateChanged;
        }

        _renewer?.Dispose();
        _renewer = null;
        _news = null;
        _announced = null;
        _vault = null;
        _cache = null;
        _endpoints = null;
        _session?.Dispose();
        _session = null;
    }

    /// <summary>AutoCAD's main window, for owning a window to. Zero when there is none to own it.</summary>
    internal static IntPtr MainWindowHandle()
    {
        try
        {
            return AcApplication.MainWindow?.Handle ?? IntPtr.Zero;
        }
#pragma warning disable CA1031 // A window with no owner is worse than no window; a missing main window is not an error to raise.
        catch (Exception)
#pragma warning restore CA1031
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>Writes a line to the active drawing's command line, if there is one.</summary>
    internal static void Say(string text)
        => AcApplication.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n" + text + "\n");

    /// <summary>
    /// Runs <paramref name="action"/> on AutoCAD's UI thread: now when already on it, posted otherwise,
    /// and not at all once the plugin has shut down.
    /// </summary>
    internal static void OnUiThread(Action action)
    {
        Dispatcher? dispatcher = _uiDispatcher;
        if (dispatcher is null)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = dispatcher.BeginInvoke(action);
    }

    /// <summary>Tells the curator a fault of this plugin's was caught and AutoCAD is still running.</summary>
    internal static void SayFault(Exception fault)
    {
        ArgumentNullException.ThrowIfNull(fault);

        Say($"Something in Mantle Place went wrong: {fault.Message} AutoCAD is still running and your "
            + "drawing is untouched. If this keeps happening, please report it.");
    }

    /// <summary>
    /// Resumes a stored session at startup (<c>HPS-13</c>), without making AutoCAD wait for it.
    /// </summary>
    /// <remarks>
    /// Fire-and-forget on purpose: <c>Initialize</c> runs on AutoCAD's thread during startup, and
    /// blocking it on a network call delays AutoCAD by however long the platform takes to answer. The
    /// restore is silent when there is no stored token and keeps the token on a network failure, so
    /// the worst case is that the plugin comes up signed out.
    /// </remarks>
    private static void BeginSessionRestore(AuthSession session)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await session.RestoreAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Nothing observes this task, and an unobserved fault can take the process down.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        });
    }

    /// <summary>
    /// Keeps a throw out of one of our own UI handlers from terminating AutoCAD, when a frame on the
    /// stack is ours (<see cref="AddinFaults.IsOurs"/>); anything else passes through untouched.
    /// </summary>
    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string?[] declaringTypes = new StackTrace(e.Exception, fNeedFileInfo: false)
            .GetFrames()
            .Select(frame => frame.GetMethod()?.DeclaringType?.FullName)
            .ToArray();

        if (!AddinFaults.IsOurs(declaringTypes))
        {
            return;
        }

        e.Handled = true;
        SayFault(e.Exception);
    }

    /// <summary>A drawing became current, or the last one closed (a null document): presence follows the active drawing.</summary>
    private static void OnDocumentBecameCurrent(object? sender, DocumentCollectionEventArgs e)
        => AnnounceDocument(e?.Document is { } document ? Path.GetFileName(document.Name) : null);

    /// <summary>Re-tracks presence when the active document changed; an unchanged one sends nothing.</summary>
    private static void AnnounceDocument(string? title)
    {
        string? document = string.IsNullOrWhiteSpace(title) ? null : title;
        if (string.Equals(document, _document, StringComparison.Ordinal))
        {
            return;
        }

        _document = document;
        _connected?.Announce(Presence());
    }

    /// <summary>The presence payload for this AutoCAD, its plugin and its active drawing.</summary>
    private static ConnectedHost Presence()
        => ConnectedHosts.Announce(_hostVersion ?? string.Empty, PluginVersion.Current, _document, Environment.MachineName);

    /// <summary>
    /// Joins, re-authorises or leaves the curator's channel as the session moves (<see cref="ConnectedHostEdge"/>),
    /// and wakes the vault listing on a sign-in (<see cref="SignInEdge"/>).
    /// </summary>
    /// <remarks>
    /// Raised on whichever thread finished the work. Nothing here waits: a join opens its socket on the
    /// pool, and a leave is not awaited. Locked because the session raises this outside its own lock.
    /// </remarks>
    private static void OnAuthStateChanged(object? sender, AuthState state)
    {
        bool signedIn;
        lock (SignIns)
        {
            signedIn = SignIns.Observe(state);
        }

        if (signedIn)
        {
            _news?.Wake();
        }

        if (_connected is not { } connected || _session is not { } session)
        {
            return;
        }

        ConnectedHostMove move;
        lock (ConnectedEdge)
        {
            move = ConnectedEdge.Observe(state);
        }

        switch (move)
        {
            case ConnectedHostMove.Join:
                connected.Join(session.AccessToken, Presence());
                break;

            case ConnectedHostMove.Reauthorise:
                connected.Reauthorise(session.AccessToken);
                break;

            case ConnectedHostMove.Leave:
                _ = LeaveQuietlyAsync(connected);
                break;
        }
    }

    private static async Task LeaveQuietlyAsync(ConnectedHostService connected)
    {
        try
        {
            await connected.LeaveAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Nothing observes this task, and a leave that faults leaves a socket the process drops anyway.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>Marshals orders the background listing found onto AutoCAD's UI thread, then tells the curator.</summary>
    /// <remarks>
    /// ⛔ <see cref="VaultNewsChecker.Arrived"/> is raised on a thread-pool thread, and the notifier
    /// touches windows. Whether the vault is open is read after the hop.
    /// </remarks>
    private static void OnNewsArrived(object? sender, IReadOnlyList<VaultBundle> arrivals)
        => OnUiThread(() => ArrivalNotifier.OnArrived(arrivals, VaultBrowser.IsOpen));

    /// <summary>The release year AutoCAD reports (2025, 2026, 2027), for the feedback link and the presence payload.</summary>
    /// <remarks>AutoCAD's version numbers its releases from 25.0 for 2025, so the year is 2000 plus the major part.</remarks>
    private static string HostYear()
    {
        try
        {
            return (2000 + AcApplication.Version.Major).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
#pragma warning disable CA1031 // The host year is a label; its absence is not worth a failed start.
        catch (Exception)
#pragma warning restore CA1031
        {
            return string.Empty;
        }
    }

    private static InvalidOperationException NotStarted()
        => new("the Mantle Place plugin did not finish starting up");
}
