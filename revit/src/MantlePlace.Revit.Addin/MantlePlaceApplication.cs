using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.Revit.Addin;

public sealed class MantlePlaceApplication : IExternalApplication
{
    private const string TabName = "Mantle Place";

    private static AuthSession? _session;
    private static SessionRenewer? _renewer;
    private static MantlePlaceEndpoints? _endpoints;
    private static VaultClient? _vault;
    private static BundleCache? _cache;
    private static PrepareWatcher? _watcher;
    private static AnnouncedOrderStore? _announced;
    private static VaultNewsChecker? _news;
    private static PrepareRejoiner? _rejoiner;
    private static readonly SignInEdge SignIns = new();
    private static string? _hostVersion;
    private static ExternalEvent? _importEvent;
    private static BundleImportEventHandler? _importHandler;
    private static ConnectedHostService? _connected;
    private static readonly ConnectedHostEdge ConnectedEdge = new();

    private static volatile string? _document;

    private static Dispatcher? _uiDispatcher;

    private static SplitButton? _accountButton;

    private static PushButton? _accountFace;
    private static PushButton? _accountIdentity;
    private static PushButton? _signOutButton;

    public static AuthSession Session => _session ?? throw NotStarted();

    internal static MantlePlaceEndpoints Endpoints => _endpoints ?? throw NotStarted();

    internal static string? HostVersion => _hostVersion;

    internal static VaultClient Vault => _vault ?? throw NotStarted();

    internal static BundleCache Cache => _cache ?? throw NotStarted();

    internal static PrepareWatcher Watcher => _watcher ?? throw NotStarted();

    internal static AnnouncedOrderStore Announced => _announced ?? throw NotStarted();

    internal static ExternalEvent ImportEvent => _importEvent ?? throw NotStarted();

    internal static BundleImportEventHandler ImportHandler => _importHandler ?? throw NotStarted();

    internal static ConnectedHostService? Connected => _connected;

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

    private static void OnDocumentClosed(object? sender, DocumentClosedEventArgs e)
    {
        _importHandler?.OnDocumentClosed();

        if (sender is Autodesk.Revit.ApplicationServices.Application application)
        {
            bool anyOpen = false;
            foreach (Autodesk.Revit.DB.Document document in application.Documents)
            {
                anyOpen |= !document.IsLinked;
            }

            if (!anyOpen)
            {
                AnnounceDocument(null);
            }
        }
    }

    private static void OnViewActivated(object? sender, ViewActivatedEventArgs e)
    {
        if (e?.Document is { IsLinked: false } document)
        {
            AnnounceDocument(document.Title);
        }
    }

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

    private static void MoveConnectedHost(AuthState state)
    {
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

    internal static void SayFault(Exception fault)
    {
        ArgumentNullException.ThrowIfNull(fault);

        _importHandler?.LowerWindow();

        new TaskDialog("Mantle Place")
        {
            MainInstruction = "Something in Mantle Place went wrong.",
            MainContent = $"{fault.Message}\n\nRevit is still running and your model is "
                + "untouched. If this keeps happening, please report it.",
        }.Show();
    }

    private static void GiveHelp(RibbonItem item) =>
        item.SetContextualHelp(new ContextualHelp(ContextualHelpType.Url, HelpLinks.RibbonHelpUrl));

    private static void BuildAccountPanel(UIControlledApplication application, string assemblyPath)
    {
        RibbonPanel account = application.CreateRibbonPanel(TabName, "Account");

        AccountRibbonState initial = AccountRibbon.For(AuthStateMachine.Initial, null, false);

        _accountButton = (SplitButton)account.AddItem(
            new SplitButtonData("MantlePlaceAccount", initial.FaceText));

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

        RibbonImagery.GiveMark(_accountFace);
        GiveHelp(_accountFace);

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
        RibbonImagery.GiveMark(_accountIdentity);
        GiveHelp(_accountIdentity);

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
        RibbonImagery.GiveMark(_signOutButton);
        GiveHelp(_signOutButton);

        PushButton openSite = _accountButton.AddPushButton(new PushButtonData(
            "MantlePlaceOpenSite",
            "Open mantle.place",
            assemblyPath,
            typeof(OpenMantlePlaceCommand).FullName)
        {
            ToolTip = "Open the Mantle Place website in your browser.",
        });
        RibbonImagery.GiveMark(openSite);
        GiveHelp(openSite);

        PushButton feedback = _accountButton.AddPushButton(new PushButtonData(
            "MantlePlaceSendFeedback",
            AccountRibbon.SendFeedbackFace,
            assemblyPath,
            typeof(SendFeedbackCommand).FullName)
        {
            ToolTip = "Tell Mantle Place about a problem, or what would help, in your browser.",
            LongDescription =
                "Open the Mantle Place feedback form in your browser. It needs no account, so it works "
                + "signed in or out. Only this plugin's version and your Revit version are sent along "
                + "with the link: nothing from your model, and nothing about you.",
        });
        RibbonImagery.GiveMark(feedback);
        GiveHelp(feedback);

        PushButton about = _accountButton.AddPushButton(new PushButtonData(
            "MantlePlaceAbout",
            "About Mantle Place",
            assemblyPath,
            typeof(AboutMantlePlaceCommand).FullName)
        {
            ToolTip = "Which build this is, and which Revit it is running in.",
        });
        RibbonImagery.GiveMark(about);
        GiveHelp(about);

        GiveHelp(_accountButton);

        _accountButton.IsSynchronizedWithCurrentItem = false;
    }

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
        }
    }

    private static void OnAuthStateChanged(object? sender, AuthState state)
    {
        bool signedIn;
        lock (SignIns)
        {
            signedIn = SignIns.Observe(state);
        }

        if (signedIn)
        {
            _rejoiner?.Arm();
            _news?.Wake();
        }

        MoveConnectedHost(state);

        OnUiThread(ApplyAccountState);
    }

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

    private static void OnPrepareEnded(object? sender, PrepareRun run)
    {
        OnUiThread(() =>
        {
            if (_session is not { } session || _announced is not { } announced)
            {
                return;
            }

            if (PrepareNotifier.OnEnded(run, VaultBrowserCommand.IsOpen, session.State == AuthState.Authenticated))
            {
                _ = Task.Run(() => announced.Announce(run.OrderId));
            }
        });
    }

    private static void OnNewsArrived(object? sender, IReadOnlyList<VaultBundle> arrivals)
        => OnUiThread(() => PrepareNotifier.OnArrived(arrivals, VaultBrowserCommand.IsOpen));

    private static void OnVaultListed(object? sender, VaultListing listing) => _rejoiner?.OnListed(listing);

    private static void OnThemeChanged(object? sender, ThemeChangedEventArgs e)
    {
        if (e is null || e.ThemeChangedType != ThemeType.UITheme)
        {
            return;
        }

        RibbonImagery.Retheme();
    }

    public Result OnStartup(UIControlledApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        _uiDispatcher = Dispatcher.CurrentDispatcher;
        _uiDispatcher.UnhandledException += OnDispatcherUnhandledException;

        if (Environment.GetEnvironmentVariable(PlantingFamilyAuthoring.DirectoryVariable) is { Length: > 0 } familyDirectory)
        {
            application.ControlledApplication.ApplicationInitialized += (sender, initialized) =>
                _ = PlantingFamilyAuthoring.Run((Autodesk.Revit.ApplicationServices.Application)sender!, familyDirectory);
        }

        _hostVersion = application.ControlledApplication.VersionNumber;

        MantlePlaceEndpoints endpoints = MantlePlaceEndpoints.Load();
        _endpoints = endpoints;
        ClientUpdateDialog.Attach();
        _session = new AuthSession(endpoints, SecretStores.ForCurrentPlatform());
        _vault = new VaultClient(endpoints, _session, threeFiles: BundleImportPlanner.ThreeFiles);
        _cache = new BundleCache();
        AuthSession session = _session;
        InterruptedPrepareStore interrupted = new(
            InterruptedPrepareStore.DefaultPath, PrepareOwners.Current, PrepareOwners.IsAlive, () => session.UserEmail);
        _watcher = new PrepareWatcher(new VaultPrepareSteps(_vault, _cache), interrupted);
        _announced = new AnnouncedOrderStore(AnnouncedOrderStore.DefaultPath);

        _news = new VaultNewsChecker(new SessionNewsSource(_session, _vault), _announced, () => VaultBrowserCommand.IsOpen);

        _rejoiner = new PrepareRejoiner(interrupted, _watcher);

        _importHandler = new BundleImportEventHandler();
        _importEvent = ExternalEvent.Create(_importHandler);

        _importHandler.Attach(_importEvent);

        ConnectedHostLog connectedLog = new(ConnectedHostLog.DefaultPath);
        string connectionId = ConnectionIdSource.ThisLaunch.Id;
        VaultExportSource exportSource = new(_vault, _cache);
        _importHandler.AttachLevels(exportSource);
        ExportBundleFetcher fetcher = new(exportSource, new VaultPrepareSteps(_vault, _cache));
        _connected = new ConnectedHostService(
            connectionId,
            () => RealtimeClient.For(endpoints, connectionId, connectedLog.Append),
            new ExportsClient(endpoints, new AuthBearerSession(_session)),
            fetcher.FetchAsync,
            connectedLog.Append);
        _connected.Attach(new RevitExportImportHost(_importHandler, _importEvent));
        application.ViewActivated += OnViewActivated;

        application.ControlledApplication.DocumentClosed += OnDocumentClosed;

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
        PushButton vault = (PushButton)panel.AddItem(new PushButtonData(
            "MantlePlaceOpenVault",
            "Vault",
            assemblyPath,
            typeof(VaultBrowserCommand).FullName)
        {
            ToolTip = VaultBadge.ToolTip,
            LongDescription =
                "Open the vault browser: it lists the bundles you keep, prepares their Revit deliverables, "
                + "downloads them and imports them. Closing it while a bundle builds does not cancel the "
                + "job: you are told when it is ready to import, and reopening shows it still going. You "
                + "are told, too, when a new order arrives in your vault. This is the one surface that "
                + "needs you signed in.",
        });
        RibbonImagery.Give(vault, RibbonGlyph.Vault);
        RibbonImagery.GiveVignette(vault, Vignette.Vault);
        GiveHelp(vault);

        PrepareNotifier.Attach(vault, () => application.MainWindowHandle);

        PushButton importLocal = (PushButton)panel.AddItem(new PushButtonData(
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
        RibbonImagery.Give(importLocal, RibbonGlyph.ImportBundle);
        RibbonImagery.GiveVignette(importLocal, Vignette.ImportBundle);
        GiveHelp(importLocal);

        PushButton handover = (PushButton)panel.AddItem(new PushButtonData(
            "MantlePlacePrepareForHandover",
            "Prepare for\nHandover",
            assemblyPath,
            typeof(PrepareForHandoverCommand).FullName)
        {
            ToolTip = "Make this project safe to hand to a client: remove the ODbL content and leave the credit.",
            LongDescription =
                "Removes the content an import made from ODbL files, which Mantle Place keeps in selection "
                + "filters named \"Mantle Place ODbL\", keeps a Produced Work terrain of the same order where "
                + "there is one, and makes sure the data credit is in the project's attribution view. It asks "
                + "first, is one undo step, and saves nothing.",
        });
        RibbonImagery.Give(handover, RibbonGlyph.PrepareForHandover);
        GiveHelp(handover);

        PushButton curator = (PushButton)panel.AddItem(new PushButtonData(
            "MantlePlaceOpenInCurator",
            "Open in\nCurator",
            assemblyPath,
            typeof(OpenInCuratorCommand).FullName)
        {
            ToolTip = "Open the web curator on the extent this project already knows.",
            LongDescription =
                "Opens the Mantle Place curator in your browser with the extent drawn and ready. The extent "
                + "is the one of the order this project was imported from, read from your vault, or else the "
                + "project's site location, with a size you choose. A project with neither opens the plain "
                + "curator. Nothing in your project changes.",
        });
        RibbonImagery.Give(curator, RibbonGlyph.OpenInCurator);
        GiveHelp(curator);

        panel.AddSlideOut();

        PushButton probe = (PushButton)panel.AddItem(new PushButtonData(
            "MantlePlaceProbeTerrain",
            "Probe\nTerrain",
            assemblyPath,
            typeof(TerrainProbeCommand).FullName)
        {
            ToolTip = "Measure what a terrain import would find here, changing nothing.",
            LongDescription =
                "Measure what this project would give the terrain import — its levels, its toposolid "
                + "types and the bundle's own elevations — and try each way of placing the terrain. "
                + "Every attempt is rolled back, so nothing in your project changes. Use it when a bundle "
                + "import is refused and the log does not say enough.",
        });
        RibbonImagery.Give(probe, RibbonGlyph.ProbeTerrain);
        GiveHelp(probe);

        PushButton logs = (PushButton)panel.AddItem(new PushButtonData(
            "MantlePlaceOpenLogs",
            "Logs",
            assemblyPath,
            typeof(OpenLogsCommand).FullName)
        {
            ToolTip = "Show the most recent import or probe log in File Explorer.",
            LongDescription =
                "Every import and every probe writes its record beside the bundle zip it was given, so "
                + "a bundle downloaded from your vault logs inside its own folder in the bundle cache, "
                + "and a zip you opened from somewhere else logs beside that zip. This opens the newest "
                + "of them. The About dialog on the Account button goes to the same place.",
        });
        RibbonImagery.Give(logs, RibbonGlyph.OpenLogs);
        GiveHelp(logs);

        application.ThemeChanged += OnThemeChanged;

        _session.StateChanged += OnAuthStateChanged;

        _renewer = new SessionRenewer(_session);
        _watcher.Ended += OnPrepareEnded;
        _news.Arrived += OnNewsArrived;
        _news.Listed += OnVaultListed;
        BeginSessionRestore(_session);
        ApplyAccountState();

        _news.Start();

        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        if (application is not null)
        {
            application.ThemeChanged -= OnThemeChanged;
        }

        _rejoiner?.Stop();

        if (application is not null)
        {
            application.ControlledApplication.DocumentClosed -= OnDocumentClosed;
            application.ViewActivated -= OnViewActivated;
        }

        if (_connected is { } connected)
        {
            _connected = null;
            _ = Task.Run(() => LeaveQuietlyAsync(connected)).Wait(TimeSpan.FromSeconds(3));
        }

        _importHandler?.Shutdown();

        if (_watcher is not null)
        {
            _watcher.Ended -= OnPrepareEnded;
            _watcher.InterruptAll();
        }

        if (_news is not null)
        {
            _news.Stop();
            _news.Arrived -= OnNewsArrived;
            _news.Listed -= OnVaultListed;
        }

        PrepareNotifier.Forget();
        RibbonImagery.Forget();
        ClientUpdateDialog.Detach();

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

        _accountButton = null;
        _accountFace = null;
        _accountIdentity = null;
        _signOutButton = null;

        _importEvent?.Dispose();
        _importEvent = null;
        _importHandler = null;
        _watcher = null;
        _news = null;
        _rejoiner = null;
        _announced = null;
        _vault = null;
        _cache = null;
        _endpoints = null;
        _session?.Dispose();
        _session = null;
        return Result.Succeeded;
    }

    private static InvalidOperationException NotStarted()
        => new("the Mantle Place plugin did not finish starting up");
}
