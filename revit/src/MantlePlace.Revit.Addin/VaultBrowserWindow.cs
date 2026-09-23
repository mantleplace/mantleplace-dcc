using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.UI;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// The modeless vault browser: list, materialize, download, import.
/// </summary>
/// <remarks>
/// <para>
/// Modeless on purpose. A materialize can take ten minutes, and a modal dialog would hold Revit's
/// message loop for all of it. The curator keeps working; the window updates when the job does.
/// </para>
/// <para>
/// <b>Closing this window is not cancelling.</b> Only the Cancel button cancels. A Prepare belongs to
/// the session's <see cref="PrepareWatcher"/>, not to this window: closed, the Prepare runs on and the
/// curator is told when it ends (<see cref="PrepareNotifier"/>); reopened, the window shows it still
/// running, and pressing Prepare again follows it rather than polling the order twice. A window's
/// close box is "I am done looking", not "throw away the thing I paid for".
/// </para>
/// <para>
/// Built in code rather than XAML: it is a header, a search field, one list and five buttons, and a
/// code-only window has no build-action, resource-lookup or designer surface to go wrong inside a
/// Revit add-in.
/// </para>
/// </remarks>
internal sealed class VaultBrowserWindow : Window
{
    private readonly AuthSession _session;
    private readonly VaultClient _vault;
    private readonly BundleCache _cache;
    private readonly PrepareWatcher _watcher;
    private readonly ExternalEvent _importEvent;
    private readonly BundleImportEventHandler _importHandler;

    /// <summary>
    /// Roughly what the search row takes: a one-line text box and the 8 px margin under it. The
    /// window grows by this so the list does not shrink to make room. An estimate, not a measurement.
    /// </summary>
    private const double SearchRowHeight = 32;

    private readonly ListBox _list = new() { Margin = new Thickness(0, 0, 0, 8), MinHeight = 220 };

    // Qualified because Autodesk.Revit.UI has a TextBox of its own, a ribbon control: CS0104 otherwise.
    private readonly System.Windows.Controls.TextBox _search = new() { ToolTip = WindowLabels.SearchToolTip, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 40 };
    private readonly Button _refresh = new() { Content = WindowLabels.Refresh, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
    private readonly Button _prepare = new() { Content = WindowLabels.PrepareForRevit, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
    private readonly Button _import = new() { Content = WindowLabels.Import, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
    private readonly Button _remove = new() { Content = WindowLabels.RemoveDownload, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
    private readonly Button _cancel = new() { Content = WindowLabels.Cancel, Padding = new Thickness(12, 4, 12, 4), IsEnabled = false };

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

    internal VaultBrowserWindow(
        AuthSession session,
        VaultClient vault,
        BundleCache cache,
        PrepareWatcher watcher,
        ExternalEvent importEvent,
        BundleImportEventHandler importHandler,
        IntPtr revitWindow,
        string? selectOrderId)
    {
        _session = session;
        _vault = vault;
        _cache = cache;
        _watcher = watcher;
        _selectOnLoad = selectOrderId;
        _importEvent = importEvent;
        _importHandler = importHandler;

        Title = WindowLabels.VaultWindowTitle;
        Width = 720;

        // The header and the search row taller than it was, so the list keeps about the rows it
        // showed before either.
        Height = 460 + BrandChrome.HeaderHeight + SearchRowHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // Owned by Revit's main window so it stays in front of it and minimises with it, rather
        // than becoming a stray top-level window the curator loses behind the model.
        new WindowInteropHelper(this) { Owner = revitWindow };

        Content = BuildLayout();

        _refresh.Click += (_, _) => _ = RefreshAsync();
        _prepare.Click += (_, _) => Prepare();
        _import.Click += (_, _) => _ = ImportAsync();
        _remove.Click += (_, _) => RemoveSelected();
        _cancel.Click += (_, _) => CancelWork();
        _search.TextChanged += (_, _) => OnSearchChanged();

        _importHandler.Completed += OnImportCompleted;

        // ⛔ Both raised on a thread-pool thread (PrepareWatcher's remarks). Every handler hops.
        _watcher.Said += OnPrepareSaid;
        _watcher.Ended += OnPrepareEnded;

        Closed += (_, _) =>
        {
            _importHandler.Completed -= OnImportCompleted;
            _watcher.Said -= OnPrepareSaid;
            _watcher.Ended -= OnPrepareEnded;
        };

        Loaded += (_, _) => _ = LoadAsync();
    }

    /// <summary>Selects <paramref name="orderId"/>'s row, now or once the list has loaded.</summary>
    /// <remarks>
    /// A row the search is hiding is let back in by clearing the search: the curator clicked a notice
    /// for that bundle, and selecting a row they cannot see would be selecting nothing.
    /// </remarks>
    internal void Select(string orderId)
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

        if (_watcher.Runs is { Count: > 0 } running)
        {
            Report(running[^1].LastMessage);
        }

        UpdateCancel();
    }

    private UIElement BuildLayout()
    {
        // The one primary action in the plugin. Prepare is the step before it and Remove undoes it;
        // neither is what the curator came here to do.
        BrandChrome.MakePrimary(_import);

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        buttons.Children.Add(_refresh);
        buttons.Children.Add(_prepare);
        buttons.Children.Add(_import);
        buttons.Children.Add(_remove);
        buttons.Children.Add(_cancel);

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

        DockPanel root = new() { Margin = new Thickness(12) };
        BrandChrome.AddHeader(root, WindowLabels.VaultHeading);
        DockPanel.SetDock(buttons, Dock.Top);
        DockPanel.SetDock(search, Dock.Top);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(search);
        root.Children.Add(_status);
        root.Children.Add(_list);
        return root;
    }

    /// <summary>
    /// The bundle whose row is selected. Read off the row itself: an index into the listing would name
    /// a different bundle as soon as the filter hid a row above it.
    /// </summary>
    private VaultBundle? Selected => (_list.SelectedItem as VaultRow)?.Bundle;

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

            // Selection survives a refresh, as long as the bundle still matches the filter. Losing it
            // is the other reason a curator clicks Refresh twice: they lose their place and go
            // looking for it.
            ShowMatchingRows();

            // ⛔HPS-21: rows that were skipped are SAID, not swallowed. A silent skip hides platform
            // corruption for as long as it lasts, and a filter does not hide it either.
            ReportCount();
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

        if (!SignedIn())
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
    /// Cancels the refresh in flight, and the Prepare of the selected row — or, when that row is not
    /// being prepared, the newest Prepare there is.
    /// </summary>
    private void CancelWork()
    {
        _work?.Cancel();

        PrepareRun? run = Selected is { } bundle ? _watcher.Watching(bundle.OrderId) : null;
        run ??= _watcher.Runs is { Count: > 0 } running ? running[^1] : null;
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
        CacheEntry entry = _cache.Inspect(bundle.OrderId, bundle.SizeBytes, bundle.Sha256, bundle.ManifestVersion);
        UpdateRow(bundle, entry);

        if (entry.State != CacheState.CachedValid)
        {
            Report(entry.Describe() + $" Use “{WindowLabels.PrepareForRevit}” first.");
            return;
        }

        // Not "Importing…": the import window opens on its checklist, and nothing is imported until
        // the curator presses Import there.
        Report($"Opening the {WindowLabels.ImportHeading} window — choose what to include, then press {WindowLabels.Import}.");

        // Hands off to Revit's thread. The window stays responsive and hears back through Completed.
        _importHandler.QueueImport(entry.Layout.BundleZipPath);
        _importEvent.Raise();

        await Task.CompletedTask.ConfigureAwait(true);
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
        UpdateRow(bundle, _cache.InspectQuick(
            bundle.OrderId, bundle.SizeBytes, bundle.Sha256, bundle.ManifestVersion));
        Report($"Removed the local copy of {bundle.AoiLabel}. It stays in your vault and can be downloaded again.");
    }

    private void OnImportCompleted(object? sender, string report) => Report(report);

    /// <summary>
    /// Re-filters as the curator types. Local to the window: nothing is sent to the platform.
    /// </summary>
    private void OnSearchChanged()
    {
        ShowMatchingRows();

        // The count only while nothing is in flight: a download's progress is the line the curator
        // is waiting on, and typing must not overwrite it.
        if (_work is null && _watcher.Runs.Count == 0)
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

        // The path is the ribbon's own words. It was "Account ▸ Sign in" against a button that no
        // longer exists under that name, and a status line that names a control the curator cannot
        // find is worse than one that says nothing.
        Report($"Sign in first: Mantle Place ▸ Account ▸ {AccountRibbon.SignInFace}.");
        return false;
    }

    private void EndWork()
    {
        _work?.Dispose();
        _work = null;
        UpdateCancel();
    }

    /// <summary>Cancel is live while there is anything to cancel: a refresh, or any Prepare.</summary>
    private void UpdateCancel() => _cancel.IsEnabled = _work is not null || _watcher.Runs.Count > 0;

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
        => _cache.InspectQuick(bundle.OrderId, bundle.SizeBytes, bundle.Sha256, bundle.ManifestVersion);

    private string Describe(VaultBundle bundle) => Describe(bundle, QuickEntry(bundle));

    private string Describe(VaultBundle bundle, CacheEntry entry)
    {
        string row = VaultRows.Describe(bundle, entry.Describe());

        // A row being prepared says so, so a window reopened onto a Prepare shows which one.
        return _watcher.Watching(bundle.OrderId) is null ? row : $"{row} — {PrepareMessages.RowPreparing}";
    }
}
