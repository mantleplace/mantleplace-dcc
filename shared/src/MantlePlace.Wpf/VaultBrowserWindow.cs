using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.Wpf;

/// <summary>
/// Opens the web Feedback form scoped to one bundle (<c>HPS-56</c>), through the host's own launcher,
/// which knows the host's version.
/// </summary>
/// <param name="orderId">The bundle's order id.</param>
/// <param name="refused">The address, when the browser would not open it.</param>
/// <returns>Whether the browser opened.</returns>
public delegate bool FeedbackOpener(string? orderId, out string refused);

/// <summary>
/// A host's import from the vault window: its hook into the open document, which only the host has.
/// </summary>
public interface IVaultImport
{
    /// <summary>
    /// Raised with the import's closing report. Any thread: the window hops to its own.
    /// </summary>
    event EventHandler<string>? Completed;

    /// <summary>
    /// Hands a bundle zip the cache has verified to the host's import. Called on the window's thread;
    /// the host queues it onto its own and returns.
    /// </summary>
    /// <param name="bundleZipPath">The verified bundle zip.</param>
    void Open(string bundleZipPath);
}

/// <summary>
/// What a host that imports from the vault window hands it: the session's Prepare watcher and its own
/// import. A host with neither shows no Prepare and no Import (<see cref="WindowLabels.VaultButtons"/>).
/// </summary>
/// <param name="Watcher">The session's Prepare watcher, which a Prepare outlives the window in.</param>
/// <param name="Import">The host's import.</param>
public sealed record VaultImporting(PrepareWatcher Watcher, IVaultImport Import);

/// <summary>
/// The modeless vault browser: list, search, Prepare, download, import, remove a local copy, and
/// report a problem with one bundle.
/// </summary>
/// <remarks>
/// <para>
/// One source for every .NET host. What a host hands it is its own: the owner window, its import from
/// the vault (<see cref="VaultImporting"/>; Revit has one, AutoCAD does not yet, and its orders arrive
/// as an export from the web vault or through Import Bundle), and its feedback launcher. Its name
/// comes from <see cref="HostIdentity"/>. Which buttons show, in which order, and where a signed-out
/// curator is sent are the pure core's (<see cref="WindowLabels.VaultButtons"/>,
/// <see cref="WindowLabels.SignInFirst"/>), where the headless suite holds them for both hosts.
/// </para>
/// <para>
/// Modeless on purpose. A materialize can take ten minutes, and a modal dialog would hold the host's
/// message loop for all of it. The curator keeps working; the window updates when the job does.
/// </para>
/// <para>
/// <b>Closing this window is not cancelling.</b> Only the Cancel button cancels. A Prepare belongs to
/// the session's <see cref="PrepareWatcher"/>, not to this window: closed, the Prepare runs on and the
/// curator is told when it ends; reopened, the window shows it still running, and pressing Prepare
/// again follows it rather than polling the order twice. A window's close box is "I am done looking",
/// not "throw away the thing I paid for".
/// </para>
/// <para>
/// Built in code rather than XAML: it is a header, a search field, one list and a row of buttons, and
/// a code-only window has no build-action, resource-lookup or designer surface to go wrong inside a
/// host.
/// </para>
/// </remarks>
public sealed class VaultBrowserWindow : Window
{
    private readonly AuthSession _session;
    private readonly VaultClient _vault;
    private readonly BundleCache _cache;
    private readonly AnnouncedOrderStore _announced;
    private readonly FeedbackOpener _reportProblem;
    private readonly PrepareWatcher? _watcher;
    private readonly IVaultImport? _import;

    /// <summary>
    /// Roughly what the search row takes: a one-line text box and the 8 px margin under it. The
    /// window grows by this so the list does not shrink to make room. An estimate, not a measurement.
    /// </summary>
    private const double SearchRowHeight = 32;

    private readonly ListBox _list = new() { Margin = new Thickness(0, 0, 0, 8), MinHeight = 220 };
    private readonly TextBox _search = new() { ToolTip = WindowLabels.SearchToolTip, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 40 };

    /// <summary>The row of buttons, in the core's order. Cancel is kept to enable and disable.</summary>
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
    private Button? _cancel;

    /// <summary>
    /// A 3.0 order's level control, beside Import: Max, Med and Min with their sizes, on the platform's
    /// recommendation (<see cref="LevelControl"/>). Shown only for a 3.0 row, in a host that imports
    /// from this window; a 2.x row's levels stay in the import checklist. The same row the window a
    /// received send opens shows (<see cref="LevelChoiceRow"/>).
    /// </summary>
    private readonly LevelChoiceRow _levelRow = new();
    private LevelControl? _levelControl;

    /// <summary>The levels the curator has chosen in this window, which a refreshed row keeps.</summary>
    private readonly LevelChoices _choices = new();

    /// <summary>
    /// A row for every bundle the listing gave, whether the filter shows it or not. The list holds
    /// the subset <see cref="VaultRows.Matches"/> keeps, as the same row objects, so a row rewritten
    /// while hidden reads right when the filter lets it back in.
    /// </summary>
    private List<VaultRow> _rows = [];
    private int _skippedRows;
    private CancellationTokenSource? _work;

    /// <summary>A row to select once the list has loaded — the bundle a notice was clicked for.</summary>
    private string? _selectOnLoad;

    /// <summary>
    /// One list row. It exists so a row can be found and rewritten after a download, a removal or an
    /// import — the ListBox held bare strings, so the only way to update one was to rebuild all of
    /// them, and rebuilding all of them is what made <see cref="RefreshAsync"/> the only refresh
    /// there was. It is also what an action reads its bundle from: the selected row, never an index,
    /// because under a filter the list's indices are not the listing's.
    /// </summary>
    private sealed class VaultRow(VaultBundle bundle, string text)
    {
        /// <summary>Settable so the re-list after a download can hand the row the facts it was checked against.</summary>
        internal VaultBundle Bundle { get; set; } = bundle;

        internal string Text { get; set; } = text;

        public override string ToString() => Text;
    }

    /// <param name="session">The plugin's session.</param>
    /// <param name="vault">The vault client.</param>
    /// <param name="cache">The bundle cache.</param>
    /// <param name="announced">The record of orders already announced, which a listing marks as seen (<c>HPS-55</c>).</param>
    /// <param name="reportProblem">The host's feedback launcher, for Report a Problem.</param>
    /// <param name="importing">The host's Prepare and import, or null for a host with no import from the vault.</param>
    /// <param name="hostWindow">The host's main window, which owns this one.</param>
    /// <param name="selectOrderId">A bundle to select once the list loads: the one a notice was clicked for.</param>
    public VaultBrowserWindow(
        AuthSession session,
        VaultClient vault,
        BundleCache cache,
        AnnouncedOrderStore announced,
        FeedbackOpener reportProblem,
        VaultImporting? importing,
        IntPtr hostWindow,
        string? selectOrderId)
    {
        _session = session;
        _vault = vault;
        _cache = cache;
        _announced = announced;
        _reportProblem = reportProblem;
        _watcher = importing?.Watcher;
        _import = importing?.Import;
        _selectOnLoad = selectOrderId;

        Title = WindowLabels.VaultWindowTitle;
        BrandChrome.ShowMarkInTitleBar(this);
        Width = 720;

        // The header and the search row taller than it was, so the list keeps about the rows it
        // showed before either.
        Height = 460 + BrandChrome.HeaderHeight + SearchRowHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // Owned by the host's main window so it stays in front of it and minimises with it, rather
        // than becoming a stray top-level window the curator loses behind the model.
        new WindowInteropHelper(this) { Owner = hostWindow };

        Content = BuildLayout(WindowLabels.VaultButtons(importing is not null, WindowLabels.HostName(HostIdentity.Id)));

        _search.TextChanged += (_, _) => OnSearchChanged();
        _list.SelectionChanged += (_, _) => OnSelectionChanged();
        _levelRow.Chosen += OnLevelChosen;

        if (_import is not null)
        {
            _import.Completed += OnImportCompleted;
        }

        if (_watcher is not null)
        {
            // ⛔ Both raised on a thread-pool thread (PrepareWatcher's remarks). Every handler hops.
            _watcher.Said += OnPrepareSaid;
            _watcher.Ended += OnPrepareEnded;
        }

        Closed += (_, _) =>
        {
            if (_import is not null)
            {
                _import.Completed -= OnImportCompleted;
            }

            if (_watcher is not null)
            {
                _watcher.Said -= OnPrepareSaid;
                _watcher.Ended -= OnPrepareEnded;
            }
        };

        Loaded += (_, _) => _ = LoadAsync();
    }

    /// <summary>Selects <paramref name="orderId"/>'s row, now or once the list has loaded.</summary>
    /// <remarks>
    /// A row the search is hiding is let back in by clearing the search: the curator clicked a notice
    /// for that bundle, and selecting a row they cannot see would be selecting nothing.
    /// </remarks>
    /// <param name="orderId">The bundle's order id.</param>
    public void Select(string orderId)
    {
        VaultRow? row = _rows.Find(held => string.Equals(held.Bundle.OrderId, orderId, StringComparison.Ordinal));
        if (row is null)
        {
            _selectOnLoad = orderId;
            return;
        }

        if (!_list.Items.Contains(row))
        {
            _search.Text = string.Empty;
        }

        _list.SelectedItem = row;
        _list.ScrollIntoView(row);
    }

    /// <summary>
    /// The first list, then whatever a Prepare still running has to say — a window reopened onto a
    /// Prepare shows it, rather than a bundle count that reads as if nothing were happening.
    /// </summary>
    private async Task LoadAsync()
    {
        await RefreshAsync().ConfigureAwait(true);

        if (_selectOnLoad is { } orderId)
        {
            _selectOnLoad = null;
            Select(orderId);
        }

        if (_watcher?.Runs is { Count: > 0 } running)
        {
            Report(running[^1].LastMessage);
        }

        UpdateCancel();
    }

    private UIElement BuildLayout(IReadOnlyList<VaultButton> faces)
    {
        foreach (VaultButton face in faces)
        {
            Button button = new() { Content = face.Face, Padding = new Thickness(12, 4, 12, 4) };
            if (face.Action != VaultAction.Cancel)
            {
                button.Margin = new Thickness(0, 0, 8, 0);
            }

            // The one primary action in the plugin, Import where the host has it. Prepare is the step
            // before it and Remove undoes it; neither is what the curator came here to do.
            if (face.Primary)
            {
                BrandChrome.MakePrimary(button);
            }

            VaultAction action = face.Action;
            button.Click += (_, _) => Press(action);
            if (action == VaultAction.Cancel)
            {
                button.IsEnabled = false;
                _cancel = button;
            }

            _buttons.Children.Add(button);
        }

        // Below the buttons, above the list it narrows.
        DockPanel search = new() { Margin = new Thickness(0, 0, 0, 8) };
        TextBlock searchLabel = new()
        {
            Text = WindowLabels.Search,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        DockPanel.SetDock(searchLabel, Dock.Left);
        search.Children.Add(searchLabel);
        search.Children.Add(_search);

        // Under the buttons, so the level is chosen before Import fetches it.
        DockPanel root = new() { Margin = new Thickness(12) };
        BrandChrome.AddHeader(root, WindowLabels.VaultHeading);
        DockPanel.SetDock(_buttons, Dock.Top);
        DockPanel.SetDock(_levelRow, Dock.Top);
        DockPanel.SetDock(search, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_buttons);
        root.Children.Add(_levelRow);
        root.Children.Add(search);
        root.Children.Add(_status);
        root.Children.Add(_list);
        return root;
    }

    private void Press(VaultAction action)
    {
        switch (action)
        {
            case VaultAction.Refresh:
                _ = RefreshAsync();
                break;
            case VaultAction.Prepare:
                Prepare();
                break;
            case VaultAction.Import:
                _ = ImportAsync();
                break;
            case VaultAction.RemoveDownload:
                RemoveSelected();
                break;
            case VaultAction.ReportProblem:
                ReportProblemWithSelected();
                break;
            case VaultAction.Cancel:
                CancelWork();
                break;
        }
    }

    /// <summary>
    /// The bundle whose row is selected. Read off the row itself: an index into the listing would name
    /// a different bundle as soon as the filter hid a row above it.
    /// </summary>
    private VaultBundle? Selected => (_list.SelectedItem as VaultRow)?.Bundle;

    /// <summary>
    /// A row is selected: a 3.0 order's control is built on the curator's choice for it, else on the
    /// recommendation, and a 2.x order, or a host with no import here, shows none. A refreshed row is
    /// re-seated, which clears the selection and selects it again, so the choice is held in
    /// <see cref="_choices"/> and not in the control this builds.
    /// </summary>
    private void OnSelectionChanged()
    {
        _levelControl = _import is null ? null : _choices.ControlFor(Selected);
        _levelRow.Show(_levelControl);
    }

    private void OnLevelChosen(FidelityLevel level)
    {
        if (_levelControl is null)
        {
            return;
        }

        if (_choices.Choose(_levelControl, level) && Selected is { } bundle)
        {
            UpdateRow(bundle, QuickEntry(bundle));
        }
    }

    /// <summary>The level the selected 3.0 order downloads at; <c>null</c> for a 2.x order.</summary>
    private FidelityLevel? ChosenLevel(VaultBundle bundle)
        => _levelControl is { } control && string.Equals(control.Bundle.OrderId, bundle.OrderId, StringComparison.Ordinal)
            ? control.Selected
            : null;

    /// <summary>The cache slot level for <paramref name="bundle"/>: its chosen level, else its recommendation.</summary>
    private string SlotLevel(VaultBundle bundle) => DownloadLevels.SlotLevel(bundle, ChosenLevel(bundle));

    private async Task RefreshAsync()
    {
        if (!await BeginAsync("Loading your vault…").ConfigureAwait(true))
        {
            return;
        }

        try
        {
            (VaultListing? listing, string? error) = await _vault.ListAsync(_work!.Token).ConfigureAwait(true);

            if (error is not null)
            {
                Report(error);
                return;
            }

            _rows = [.. listing!.Bundles.Select(bundle => new VaultRow(bundle, Describe(bundle)))];
            _skippedRows = listing.Warnings.Count;

            // The curator is looking at every order this listing holds, so none of them is news to
            // the background listing any more (HPS-55). Off this thread: the record waits on a lock
            // another host process may hold, and a window that stutters for it would be the wrong trade.
            AnnouncedOrderStore announced = _announced;
            string email = _session.UserEmail;
            _ = Task.Run(() => announced.Seen(listing, email));

            // Selection survives a refresh, as long as the bundle still matches the filter. Losing it
            // is the other reason a curator clicks Refresh twice: they lose their place and go
            // looking for it.
            ShowMatchingRows();

            // ⛔HPS-21: rows that were skipped are SAID, not swallowed. A silent skip hides platform
            // corruption for as long as it lasts, and a filter does not hide it either.
            ReportCount();
        }
        catch (OperationCanceledException)
        {
            Report("Cancelled.");
        }
        finally
        {
            EndWork();
        }
    }

    /// <summary>
    /// Materialize → poll → <b>re-list</b> → download, handed to the session's watcher.
    /// </summary>
    /// <remarks>
    /// The watcher owns it from here (<see cref="PrepareWatcher"/>), so it outlives this window. A
    /// Prepare on an order already being watched follows that one: pressing it again after a reopen
    /// is how a curator asks "where is it?", and answering with a second poll loop spends the rate
    /// budget twice on one order.
    /// </remarks>
    private void Prepare()
    {
        if (Selected is not { } bundle)
        {
            Report("Pick a bundle first.");
            return;
        }

        Prepare(bundle);
    }

    /// <summary>As <see cref="Prepare()"/>, for a bundle already chosen — the Import press that found an on-demand order.</summary>
    private void Prepare(VaultBundle bundle)
    {
        if (_watcher is null || !SignedIn())
        {
            return;
        }

        PrepareRun run = _watcher.Prepare(bundle, out bool joined);
        if (joined)
        {
            Report(PrepareMessages.AlreadyPreparing(run.Label));
        }

        UpdateRow(bundle, QuickEntry(bundle));
        UpdateCancel();
    }

    /// <summary>
    /// Cancels the refresh in flight, and the Prepare of the selected row — or, with no row selected,
    /// the one Prepare there is when there is exactly one.
    /// </summary>
    /// <remarks>
    /// Never a guess among several: cancelling a bundle the curator is not looking at throws away a
    /// wait they chose. With two running and none selected, Cancel stops only the refresh.
    /// </remarks>
    private void CancelWork()
    {
        _work?.Cancel();

        if (_watcher is null)
        {
            return;
        }

        PrepareRun? run = Selected is { } bundle
            ? _watcher.Watching(bundle.OrderId)
            : _watcher.Runs is { Count: 1 } running ? running[0] : null;
        run?.Cancel();
    }

    private void OnPrepareSaid(object? sender, PrepareRun run) => OnUiThread(() => Report(run.LastMessage));

    /// <summary>
    /// A Prepare ended while this window was open. The window is the notice
    /// (<see cref="PrepareNotices"/>), so the row and the status line say it.
    /// </summary>
    private void OnPrepareEnded(object? sender, PrepareRun run) => OnUiThread(() =>
    {
        // The row is rewritten from the re-listed bundle, so it stops saying "Not downloaded." about
        // a bundle that is sitting on disk — and stops saying it is being prepared.
        UpdateRow(run.Bundle, QuickEntry(run.Bundle));
        Report(run.LastMessage);
        UpdateCancel();
    });

    private void OnUiThread(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = Dispatcher.BeginInvoke(action);
    }

    private async Task ImportAsync()
    {
        if (Selected is not { } bundle)
        {
            Report("Pick a bundle first.");
            return;
        }

        // The full hash, not InspectQuick: this is the pre-import gate, and it is exactly the "once
        // per import" cost Inspect's remark defends.
        // A 3.0 order at another level than the slot holds is a miss: the fetch below replaces the slot.
        CacheEntry entry = _cache.Inspect(bundle.OrderId, bundle.SizeBytes, bundle.Sha256, bundle.ManifestVersion, SlotLevel(bundle));
        UpdateRow(bundle, entry);

        if (entry.State != CacheState.CachedValid)
        {
            // Nothing importable on disk: ask the order whether it is complete, and fetch it if so.
            (CacheEntry? fetched, bool prepare) = await FetchCompleteAsync(bundle).ConfigureAwait(true);
            if (prepare)
            {
                // The Prepare is the watcher's, and this window's own work is over.
                Prepare(bundle);
                return;
            }

            if (fetched is null)
            {
                return;
            }

            entry = fetched.Value;
        }

        OpenImport(entry);
    }

    /// <summary>
    /// One materialize start, then the host's download view for a complete order — or the existing
    /// Prepare for an on-demand one (<see cref="CompleteOrders"/>).
    /// </summary>
    /// <remarks>
    /// A complete order was built whole at procurement, so a Prepare on it does nothing but fill the
    /// cache; this fills it directly and opens the import, with one press and no Prepare shown. An
    /// on-demand order is handed to the Prepare as it was, and its import is the curator's next
    /// press once the Prepare says the bundle is ready.
    /// </remarks>
    /// <returns>
    /// The cache entry to import; or <c>Prepare</c> set when the order is the Prepare's to follow; or
    /// neither, when there is nothing yet and the status line says why.
    /// </returns>
    private async Task<(CacheEntry? Entry, bool Prepare)> FetchCompleteAsync(VaultBundle bundle)
    {
        if (!await BeginAsync($"Checking {bundle.AoiLabel} in your vault…").ConfigureAwait(true))
        {
            return (null, false);
        }

        try
        {
            ImportRouteDecision route = await _vault.RouteImportAsync(bundle.OrderId, _work!.Token).ConfigureAwait(true);
            if (route.Route == ImportRoute.Refused)
            {
                Report(route.Message);
                return (null, false);
            }

            if (route.Route == ImportRoute.Prepare)
            {
                return (null, true);
            }

            CompleteDownload download = await _vault.DownloadCompleteAsync(
                bundle,
                route.Delivered,
                _cache,
                new Progress<string>(Report),
                _work!.Token,
                ChosenLevel(bundle)).ConfigureAwait(true);

            if (download.NeedsPrepare)
            {
                return (null, true);
            }

            CacheEntry entry = _cache.Inspect(
                download.Bundle.OrderId,
                download.Bundle.SizeBytes,
                download.Bundle.Sha256,
                download.Bundle.ManifestVersion,
                SlotLevel(download.Bundle));
            UpdateRow(download.Bundle, entry);

            if (!download.Ready || entry.State != CacheState.CachedValid)
            {
                Report(download.Ready ? entry.Describe() : download.Message);
                return (null, false);
            }

            return (entry, false);
        }
        catch (OperationCanceledException)
        {
            Report("Cancelled.");
            return (null, false);
        }
        finally
        {
            EndWork();
        }
    }

    private void OpenImport(CacheEntry entry)
    {
        if (_import is null)
        {
            return;
        }

        // Not "Importing…": the import window opens on its checklist, and nothing is imported until
        // the curator presses Import there.
        Report($"Opening the {WindowLabels.ImportHeading} window — choose what to include, then press {WindowLabels.Import}.");

        // Hands off to the host's thread. The window stays responsive and hears back through Completed.
        _import.Open(entry.Layout.BundleZipPath);
    }

    private void RemoveSelected()
    {
        if (Selected is not { } bundle)
        {
            Report("Pick a bundle first.");
            return;
        }

        // HPS-44: eviction is explicit and per-order, and this button is the only thing that does
        // it. Nothing in this plugin reclaims disk on the curator's behalf.
        _cache.Remove(bundle.OrderId);
        UpdateRow(bundle, QuickEntry(bundle));
        Report($"Removed the local copy of {bundle.AoiLabel}. It stays in your vault and can be downloaded again.");
    }

    /// <summary>
    /// <c>HPS-56</c>: the web Feedback form in the system browser, scoped to the selected bundle by
    /// its order id. Needs no session and does no work, so it is never gated on a busy step.
    /// </summary>
    private void ReportProblemWithSelected()
    {
        if (Selected is not { } bundle)
        {
            Report("Pick a bundle first.");
            return;
        }

        Report(_reportProblem(bundle.OrderId, out string refused)
            ? $"Opened the feedback form for {bundle.AoiLabel} in your browser."
            : $"Could not open your browser. Open this address manually: {refused}");
    }

    private void OnImportCompleted(object? sender, string report) => OnUiThread(() => Report(report));

    /// <summary>
    /// Re-filters as the curator types. Local to the window: nothing is sent to the platform.
    /// </summary>
    private void OnSearchChanged()
    {
        ShowMatchingRows();

        // The count only while nothing is in flight: a download's progress is the line the curator
        // is waiting on, and typing must not overwrite it.
        if (_work is null && (_watcher is null || _watcher.Runs.Count == 0))
        {
            ReportCount();
        }
    }

    /// <summary>
    /// Puts the rows the filter keeps into the list, keeping the selected bundle selected if it is
    /// still among them.
    /// </summary>
    /// <remarks>
    /// Which rows, and which index is selected among them, is <see cref="VaultRows.Show"/>'s to
    /// decide and the headless suite's to hold; this only puts the answer into the list.
    /// </remarks>
    private void ShowMatchingRows()
    {
        VaultView<VaultRow> view = VaultRows.Show(_rows, row => row.Bundle, _search.Text, Selected?.OrderId);

        _list.Items.Clear();
        foreach (VaultRow row in view.Shown)
        {
            _list.Items.Add(row);
        }

        _list.SelectedIndex = view.SelectedIndex;
    }

    private void ReportCount()
        => Report(VaultRows.CountLine(_list.Items.Count, _rows.Count, _skippedRows, _search.Text));

    private async Task<bool> BeginAsync(string message)
    {
        if (_work is not null)
        {
            Report("Already busy — wait for the current step, or cancel it.");
            return false;
        }

        if (!SignedIn())
        {
            return false;
        }

        // Renewal used to be decided here, from the clock, before each operation. It has moved to
        // VaultClient.SendAsync, which renews on a 401 and sends the request again. Two reasons: a
        // wall-clock check is a guess about a decision the platform is already making and cannot see
        // a long poll cross the expiry boundary mid-flight; and this is the Addin, the one layer CI
        // never builds, which is the last place the most important auth decision should live.
        _work = new CancellationTokenSource();
        UpdateCancel();
        Report(message);
        return true;
    }

    private bool SignedIn()
    {
        if (_session.State == AuthState.Authenticated)
        {
            return true;
        }

        // The path is the host's ribbon's own words (WindowLabels.SignInFirst). A status line that
        // names a control the curator cannot find, as AutoCAD's copy of this window once named the
        // Add-ins tab, is worse than one that says nothing.
        Report(WindowLabels.SignInFirst(HostIdentity.Id));
        return false;
    }

    private void EndWork()
    {
        _work?.Dispose();
        _work = null;
        UpdateCancel();
    }

    /// <summary>Cancel is live while there is anything to cancel: a refresh, or any Prepare.</summary>
    private void UpdateCancel()
    {
        if (_cancel is not null)
        {
            _cancel.IsEnabled = _work is not null || _watcher?.Runs.Count > 0;
        }
    }

    private void Report(string message) => _status.Text = message;

    /// <summary>
    /// Rewrites one row in place, from a verdict the caller is already holding.
    /// </summary>
    /// <remarks>
    /// ⛔ It takes the <see cref="CacheEntry"/> rather than looking one up, and that is the whole
    /// point. Download, remove and import each already know the cache state they just produced;
    /// re-deriving it would mean another <c>Inspect</c>, and the naive version of this fix — call
    /// <see cref="RefreshAsync"/> after a download — re-lists over the network and re-inspects every
    /// row besides. Passing the entry through costs nothing at all.
    /// </remarks>
    private void UpdateRow(VaultBundle bundle, CacheEntry entry)
    {
        // Rewritten whether or not the filter shows it, so the row reads right when it is let back
        // in. Only a shown row needs re-seating.
        VaultRow? target = _rows.Find(
            row => string.Equals(row.Bundle.OrderId, bundle.OrderId, StringComparison.Ordinal));
        if (target is null)
        {
            return;
        }

        target.Bundle = bundle;
        target.Text = Describe(bundle, entry);

        for (int index = 0; index < _list.Items.Count; index++)
        {
            if (_list.Items[index] is VaultRow row && ReferenceEquals(row, target))
            {
                // A ListBox of plain objects does not re-read ToString on its own. Re-seating the
                // item is what makes the row redraw, and it keeps the selection where it was.
                bool wasSelected = _list.SelectedIndex == index;
                _list.Items[index] = row;
                if (wasSelected)
                {
                    _list.SelectedIndex = index;
                }

                return;
            }
        }
    }

    private CacheEntry QuickEntry(VaultBundle bundle)
        => _cache.InspectQuick(bundle.OrderId, bundle.SizeBytes, bundle.Sha256, bundle.ManifestVersion, SlotLevel(bundle));

    private string Describe(VaultBundle bundle) => Describe(bundle, QuickEntry(bundle));

    private string Describe(VaultBundle bundle, CacheEntry entry)
    {
        string row = VaultRows.Describe(bundle, entry.Describe());

        // A row being prepared says so, so a window reopened onto a Prepare shows which one.
        return _watcher?.Watching(bundle.OrderId) is null ? row : $"{row} — {PrepareMessages.RowPreparing}";
    }
}
