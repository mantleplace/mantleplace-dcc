using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.AutoCad.Addin;

/// <summary>
/// The modeless vault browser: the bundles you keep, a search over them, what is on this machine, and
/// the way to report a problem with one.
/// </summary>
/// <remarks>
/// <para>
/// Copied from the Revit plugin's window, and cut to what does not need a host hook. Preparing a
/// bundle, downloading a host's files and importing are the plugin's hooks into the drawing, and they
/// are not known for AutoCAD yet, so the window has no Prepare, Download or Import and no watcher for
/// a Prepare: those arrive with the import. What a curator can do here is what the Revit window does
/// before any of that: list, search, see which bundles are already on this machine, remove a local
/// copy and open the feedback form scoped to a bundle.
/// </para>
/// <para>
/// Modeless on purpose, so AutoCAD stays usable while the listing loads. Built in code rather than
/// XAML, as the Revit window is: no build action or resource lookup to go wrong inside a host.
/// </para>
/// </remarks>
internal sealed class VaultBrowserWindow : Window
{
    private readonly AuthSession _session;
    private readonly VaultClient _vault;
    private readonly BundleCache _cache;

    /// <summary>Roughly what the search row takes. The window grows by this so the list does not shrink to make room.</summary>
    private const double SearchRowHeight = 32;

    private readonly ListBox _list = new() { Margin = new Thickness(0, 0, 0, 8), MinHeight = 220 };
    private readonly TextBox _search = new() { ToolTip = WindowLabels.SearchToolTip, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 40 };
    private readonly Button _refresh = new() { Content = WindowLabels.Refresh, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
    private readonly Button _remove = new() { Content = WindowLabels.RemoveDownload, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
    private readonly Button _report = new() { Content = WindowLabels.ReportProblem, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
    private readonly Button _cancel = new() { Content = WindowLabels.Cancel, Padding = new Thickness(12, 4, 12, 4), IsEnabled = false };

    /// <summary>
    /// A row for every bundle the listing gave, whether the filter shows it or not. The list holds the
    /// subset <see cref="VaultRows.Matches"/> keeps, as the same row objects, so a row rewritten while
    /// hidden reads right when the filter lets it back in.
    /// </summary>
    private List<VaultRow> _rows = [];
    private int _skippedRows;
    private CancellationTokenSource? _work;

    /// <summary>A row to select once the list has loaded: the bundle a notice was clicked for.</summary>
    private string? _selectOnLoad;

    /// <summary>One list row, found and rewritten after a removal. An action reads its bundle from the selected row, never an index.</summary>
    private sealed class VaultRow(VaultBundle bundle, string text)
    {
        internal VaultBundle Bundle { get; set; } = bundle;

        internal string Text { get; set; } = text;

        public override string ToString() => Text;
    }

    internal VaultBrowserWindow(AuthSession session, VaultClient vault, BundleCache cache, IntPtr hostWindow, string? selectOrderId)
    {
        _session = session;
        _vault = vault;
        _cache = cache;
        _selectOnLoad = selectOrderId;

        Title = WindowLabels.VaultWindowTitle;
        Width = 720;
        Height = 460 + BrandChrome.HeaderHeight + SearchRowHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // Owned by AutoCAD's main window so it stays in front of it and minimises with it, rather than
        // becoming a stray top-level window the curator loses behind the drawing.
        new WindowInteropHelper(this) { Owner = hostWindow };

        Content = BuildLayout();

        _refresh.Click += (_, _) => _ = RefreshAsync();
        _remove.Click += (_, _) => RemoveSelected();
        _report.Click += (_, _) => ReportProblemWithSelected();
        _cancel.Click += (_, _) => _work?.Cancel();
        _search.TextChanged += (_, _) => OnSearchChanged();

        Loaded += (_, _) => _ = LoadAsync();
    }

    /// <summary>Selects <paramref name="orderId"/>'s row, now or once the list has loaded.</summary>
    /// <remarks>A row the search is hiding is let back in by clearing the search: selecting a row nobody can see selects nothing.</remarks>
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

    private async Task LoadAsync()
    {
        await RefreshAsync().ConfigureAwait(true);

        if (_selectOnLoad is { } orderId)
        {
            _selectOnLoad = null;
            Select(orderId);
        }
    }

    private UIElement BuildLayout()
    {
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        buttons.Children.Add(_refresh);
        buttons.Children.Add(_remove);
        buttons.Children.Add(_report);
        buttons.Children.Add(_cancel);

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

    /// <summary>The bundle whose row is selected, read off the row itself: an index would name a different bundle once the filter hid a row above it.</summary>
    private VaultBundle? Selected => (_list.SelectedItem as VaultRow)?.Bundle;

    private async Task RefreshAsync()
    {
        if (!Begin("Loading your vault…"))
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

            // The curator is looking at every order this listing holds, so none of them is news to the
            // background listing any more (HPS-55). Off this thread: the record waits on a lock another
            // host process may hold. The store is read here, while the plugin is certainly running.
            AnnouncedOrderStore announced = MantlePlaceApplication.Announced;
            string email = _session.UserEmail;
            _ = Task.Run(() => announced.Seen(listing, email));

            ShowMatchingRows();

            // HPS-21: rows that were skipped are said, not swallowed.
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

    private void RemoveSelected()
    {
        if (Selected is not { } bundle)
        {
            Report("Pick a bundle first.");
            return;
        }

        // HPS-44: eviction is explicit and per-order, and this button is the only thing that does it.
        _cache.Remove(bundle.OrderId);
        UpdateRow(bundle, QuickEntry(bundle));
        Report($"Removed the local copy of {bundle.AoiLabel}. It stays in your vault and can be downloaded again.");
    }

    /// <summary>HPS-56: the web Feedback form in the system browser, scoped to the selected bundle by its order id.</summary>
    private void ReportProblemWithSelected()
    {
        if (Selected is not { } bundle)
        {
            Report("Pick a bundle first.");
            return;
        }

        Report(FeedbackLauncher.TryOpen(bundle.OrderId, out string refused)
            ? $"Opened the feedback form for {bundle.AoiLabel} in your browser."
            : $"Could not open your browser. Open this address manually: {refused}");
    }

    /// <summary>Re-filters as the curator types. Local to the window: nothing is sent to the platform.</summary>
    private void OnSearchChanged()
    {
        ShowMatchingRows();

        // The count only while nothing is in flight, so typing never overwrites a step's progress line.
        if (_work is null)
        {
            ReportCount();
        }
    }

    /// <summary>
    /// Puts the rows the filter keeps into the list, keeping the selected bundle selected if it is still
    /// among them. Which rows is <see cref="VaultRows.Show"/>'s to decide and the headless suite's to hold.
    /// </summary>
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

    private bool Begin(string message)
    {
        if (_work is not null)
        {
            Report("Already busy — wait for the current step, or cancel it.");
            return false;
        }

        if (_session.State != AuthState.Authenticated)
        {
            Report($"Sign in first: Add-ins tab ▸ Mantle Place ▸ {AccountRibbon.SignInFace}.");
            return false;
        }

        // Renewal is VaultClient.SendAsync's: it renews on a 401 and sends the request again, so this
        // layer, which CI never builds, decides nothing about auth.
        _work = new CancellationTokenSource();
        _cancel.IsEnabled = true;
        Report(message);
        return true;
    }

    private void EndWork()
    {
        _work?.Dispose();
        _work = null;
        _cancel.IsEnabled = false;
    }

    private void Report(string message) => _status.Text = message;

    /// <summary>Rewrites one row in place, from a verdict the caller already holds.</summary>
    private void UpdateRow(VaultBundle bundle, CacheEntry entry)
    {
        VaultRow? target = _rows.Find(row => string.Equals(row.Bundle.OrderId, bundle.OrderId, StringComparison.Ordinal));
        if (target is null)
        {
            return;
        }

        target.Bundle = bundle;
        target.Text = VaultRows.Describe(bundle, entry.Describe());

        for (int index = 0; index < _list.Items.Count; index++)
        {
            if (_list.Items[index] is VaultRow row && ReferenceEquals(row, target))
            {
                // A ListBox of plain objects does not re-read ToString on its own: re-seating the item
                // redraws it and keeps the selection where it was.
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

    private string Describe(VaultBundle bundle) => VaultRows.Describe(bundle, QuickEntry(bundle).Describe());
}
