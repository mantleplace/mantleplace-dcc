using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using MantlePlace.Client;
using MantlePlace.Core;
using AcApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace MantlePlace.AutoCad.Addin;

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
    private static VaultExportSource? _exportSource;
    private static PrepareWatcher? _watcher;
    private static readonly SignInEdge SignIns = new();
    private static readonly ConnectedHostEdge ConnectedEdge = new();

    private static volatile string? _document;

    private static string? _hostVersion;

    private static Dispatcher? _uiDispatcher;

    internal static AuthSession Session => _session ?? throw NotStarted();

    internal static MantlePlaceEndpoints Endpoints => _endpoints ?? throw NotStarted();

    internal static string? HostVersion => _hostVersion;

    internal static VaultClient Vault => _vault ?? throw NotStarted();

    internal static BundleCache Cache => _cache ?? throw NotStarted();

    internal static AnnouncedOrderStore Announced => _announced ?? throw NotStarted();

    internal static ConnectedHostService? Connected => _connected;

    internal static VaultExportSource? ExportSource => _exportSource;

    internal static PrepareWatcher Watcher => _watcher ?? throw NotStarted();

    internal static void Start()
    {
        _uiDispatcher = Dispatcher.CurrentDispatcher;
        _uiDispatcher.UnhandledException += OnDispatcherUnhandledException;

        _hostVersion = HostYear();

        MantlePlaceEndpoints endpoints = MantlePlaceEndpoints.Load();
        _endpoints = endpoints;

        ClientUpdateDialog.Attach();
        _session = new AuthSession(endpoints, SecretStores.ForCurrentPlatform());
        _vault = new VaultClient(endpoints, _session, threeFiles: AutoCadImportPlan.ThreeFiles);
        _cache = new BundleCache();
        _announced = new AnnouncedOrderStore(AnnouncedOrderStore.DefaultPath);
        _news = new VaultNewsChecker(new SessionNewsSource(_session, _vault), _announced, () => VaultBrowser.IsOpen);

        ConnectedHostLog connectedLog = new(ConnectedHostLog.DefaultPath);
        string connectionId = ConnectionIdSource.ThisLaunch.Id;
        _exportSource = new VaultExportSource(_vault, _cache);
        _watcher = new PrepareWatcher(new VaultPrepareSteps(_vault, _cache));
        ExportBundleFetcher fetcher = new(_exportSource, new VaultPrepareSteps(_vault, _cache));
        _connected = new ConnectedHostService(
            connectionId,
            () => RealtimeClient.For(endpoints, connectionId, connectedLog.Append),
            new ExportsClient(endpoints, new AuthBearerSession(_session)),
            fetcher.FetchAsync,
            connectedLog.Append);
        _connected.Attach(new AutoCadExportImportHost());

        AcApplication.DocumentManager.DocumentBecameCurrent += OnDocumentBecameCurrent;
        PluginRibbon.Attach();

        _session.StateChanged += OnAuthStateChanged;

        _renewer = new SessionRenewer(_session);
        _news.Arrived += OnNewsArrived;
        BeginSessionRestore(_session, new ConnectedHostLog(SessionRestoreLogPath).Append);

        _news.Start();
    }

    internal static void Stop()
    {
        AcApplication.DocumentManager.DocumentBecameCurrent -= OnDocumentBecameCurrent;
        PluginRibbon.Detach();

        if (_connected is { } connected)
        {
            _connected = null;
            _ = Task.Run(() => LeaveQuietlyAsync(connected)).Wait(TimeSpan.FromSeconds(3));
        }

        if (_news is not null)
        {
            _news.Stop();
            _news.Arrived -= OnNewsArrived;
        }

        VaultBrowser.Close();
        ArrivalNotifier.Forget();
        ClientUpdateDialog.Detach();

        _watcher?.InterruptAll();
        _watcher = null;
        _exportSource = null;

        if (_uiDispatcher is not null)
        {
            _uiDispatcher.UnhandledException -= OnDispatcherUnhandledException;
            _uiDispatcher = null;
        }

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

    internal static IntPtr MainWindowHandle()
    {
        try
        {
            return AcApplication.MainWindow?.Handle ?? IntPtr.Zero;
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            return IntPtr.Zero;
        }
    }

    internal static void Say(string text)
        => AcApplication.DocumentManager.MdiActiveDocument?.Editor.WriteMessage("\n" + text + "\n");

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

    internal static void SayFault(Exception fault)
    {
        ArgumentNullException.ThrowIfNull(fault);

        Say($"Something in Mantle Place went wrong: {fault.Message} AutoCAD is still running and your "
            + "drawing is untouched. If this keeps happening, please report it.");
    }

    private static string SessionRestoreLogPath => MachineRecordFile.InLocalAppData("autocad-session-restore.log");

    private static void BeginSessionRestore(AuthSession session, Action<string> log)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await session.RestoreAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031
            catch (Exception fault)
#pragma warning restore CA1031
            {
                log($"Session restore failed; the plugin stays signed out. {fault}");
            }
        });
    }

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

    private static void OnDocumentBecameCurrent(object? sender, DocumentCollectionEventArgs e)
        => AnnounceDocument(e?.Document is { } document ? Path.GetFileName(document.Name) : null);

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

    private static ConnectedHost Presence()
        => ConnectedHosts.Announce(_hostVersion ?? string.Empty, PluginVersion.Current, _document, Environment.MachineName);

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
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static void OnNewsArrived(object? sender, IReadOnlyList<VaultBundle> arrivals)
        => OnUiThread(() => ArrivalNotifier.OnArrived(arrivals, VaultBrowser.IsOpen));

    private static string HostYear()
    {
        try
        {
            Version version = AcApplication.Version;
            return HostReleaseYear.From(version.Major, version.Minor);
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            return string.Empty;
        }
    }

    private static InvalidOperationException NotStarted()
        => new("the Mantle Place plugin did not finish starting up");
}
