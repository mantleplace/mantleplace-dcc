using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.Wpf;

public delegate bool FeedbackOpener(string? orderId, out string refused);

public interface IVaultImport
{
    event EventHandler<string>? Completed;

    void Open(string bundleZipPath);
}

public sealed record VaultImporting(PrepareWatcher Watcher, IVaultImport Import);

public sealed class VaultBrowserWindow : Window
{
    private readonly AuthSession _session;
    private readonly VaultClient _vault;
    private readonly BundleCache _cache;
    private readonly AnnouncedOrderStore _announced;
    private readonly FeedbackOpener _reportProblem;
    private readonly PrepareWatcher? _watcher;
    private readonly IVaultImport? _import;

    private readonly ListBox _list = new() { Margin = new Thickness(0, 0, 0, 8), MinHeight = 220 };
    private readonly TextBox _search = new() { ToolTip = WindowLabels.SearchToolTip, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 40 };
    private readonly DockPanel _searchRow = new() { Margin = new Thickness(0, 0, 0, 8) };

    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
    private Button? _cancel;

    private readonly LevelChoiceRow _levelRow = new();
    private LevelControl? _levelControl;

    private readonly LevelChoices _choices = new();

    private List<VaultRow> _rows = [];
    private int _skippedRows;
    private CancellationTokenSource? _work;

    private string? _selectOnLoad;

    private sealed class VaultRow(VaultBundle bundle, string text)
    {
        internal VaultBundle Bundle { get; set; } = bundle;

        internal string Text { get; set; } = text;

        public override string ToString() => Text;
    }

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
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        new WindowInteropHelper(this) { Owner = hostWindow };

        Content = BuildLayout(WindowLabels.VaultButtons(importing is not null, WindowLabels.HostName(HostIdentity.Id)));
        _searchRow.Measure(new Size(Width, double.PositiveInfinity));
        Height = 460 + BrandChrome.HeaderHeight + _searchRow.DesiredSize.Height;

        _search.TextChanged += (_, _) => OnSearchChanged();
        _list.SelectionChanged += (_, _) => OnSelectionChanged();
        _levelRow.Chosen += OnLevelChosen;

        if (_import is not null)
        {
            _import.Completed += OnImportCompleted;
        }

        if (_watcher is not null)
        {
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

        DockPanel search = _searchRow;
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

    private VaultBundle? Selected => (_list.SelectedItem as VaultRow)?.Bundle;

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

    private FidelityLevel? ChosenLevel(VaultBundle bundle)
        => _levelControl is { } control && string.Equals(control.Bundle.OrderId, bundle.OrderId, StringComparison.Ordinal)
            ? control.Selected
            : null;

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

            AnnouncedOrderStore announced = _announced;
            string email = _session.UserEmail;
            _ = Task.Run(() => announced.Seen(listing, email));

            ShowMatchingRows();

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

    private void Prepare()
    {
        if (Selected is not { } bundle)
        {
            Report("Pick a bundle first.");
            return;
        }

        Prepare(bundle);
    }

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

    private void OnPrepareEnded(object? sender, PrepareRun run) => OnUiThread(() =>
    {
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

        CacheEntry entry = _cache.Inspect(bundle.OrderId, bundle.SizeBytes, bundle.Sha256, bundle.ManifestVersion, SlotLevel(bundle));
        UpdateRow(bundle, entry);

        if (entry.State != CacheState.CachedValid)
        {
            (CacheEntry? fetched, bool prepare) = await FetchCompleteAsync(bundle).ConfigureAwait(true);
            if (prepare)
            {
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

        Report($"Opening the {WindowLabels.ImportHeading} window — choose what to include, then press {WindowLabels.Import}.");

        _import.Open(entry.Layout.BundleZipPath);
    }

    private void RemoveSelected()
    {
        if (Selected is not { } bundle)
        {
            Report("Pick a bundle first.");
            return;
        }

        string? removeError = _cache.Remove(bundle.OrderId);
        UpdateRow(bundle, QuickEntry(bundle));
        if (removeError is not null)
        {
            Report(removeError);
            return;
        }

        Report($"Removed the local copy of {bundle.AoiLabel}. It stays in your vault and can be downloaded again.");
    }

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

    private void OnSearchChanged()
    {
        ShowMatchingRows();

        if (_work is null && (_watcher is null || _watcher.Runs.Count == 0))
        {
            ReportCount();
        }
    }

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

        Report(WindowLabels.SignInFirst(HostIdentity.Id));
        return false;
    }

    private void EndWork()
    {
        _work?.Dispose();
        _work = null;
        UpdateCancel();
    }

    private void UpdateCancel()
    {
        if (_cancel is not null)
        {
            _cancel.IsEnabled = _work is not null || _watcher?.Runs.Count > 0;
        }
    }

    private void Report(string message) => _status.Text = message;

    private void UpdateRow(VaultBundle bundle, CacheEntry entry)
    {
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

        return _watcher?.Watching(bundle.OrderId) is null ? row : $"{row} — {PrepareMessages.RowPreparing}";
    }
}
