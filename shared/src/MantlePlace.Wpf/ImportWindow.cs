using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.Wpf;

internal sealed partial class ImportWindow : Window
{
    private static readonly TimeSpan ClockTick = TimeSpan.FromMilliseconds(250);

    private const double PlainWidth = 520;

    private readonly ImportChecklist? _checklist;

    private readonly bool _beforeImport;
    private readonly IImportWindowHost _host;
    private readonly IntPtr _hostWindow;
    private readonly ImportWindowState _state;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly ForegroundReading _opening;

    private readonly Dictionary<ImportLayer, CheckBox> _boxes = [];
    private readonly Dictionary<ImportLayer, TextBlock> _notes = [];
    private readonly Border _body = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly ProgressBar _progress = new()
    {
        Height = 14,
        Margin = new Thickness(0, 12, 0, 4),
        Minimum = 0,
        Maximum = 1,
        Visibility = Visibility.Collapsed,
    };

    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 20 };

    private readonly LevelChoiceRow _levelRow = new();
    private readonly TextBlock _levelNote = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
    private readonly LevelSwitch? _levels;
    private readonly TextBox _report = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Margin = new Thickness(0, 8, 0, 0),
        Visibility = Visibility.Collapsed,
    };

    private readonly StackPanel _ask = new()
    {
        Orientation = Orientation.Horizontal,
        Margin = new Thickness(0, 8, 0, 0),
        Visibility = Visibility.Collapsed,
    };

    private readonly TextBlock _slowLayers = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 6, 0, 0),
        Visibility = Visibility.Collapsed,
    };

    private readonly Button _import = new() { Content = WindowLabels.Import, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
    private readonly Button _cancel = new() { Content = WindowLabels.Cancel, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
    private readonly Button _close = new() { Content = WindowLabels.Close, Padding = new Thickness(12, 4, 12, 4) };

    private readonly DispatcherTimer _tick;

    private TextBlock[] _states = [];
    private ForegroundWatch? _foreground;
    private bool _refreshing;

    internal ImportWindow(ImportWindowRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        _checklist = request.Checklist;
        _beforeImport = request.Checklist is null && request.BeforeImport is not null;
        _host = request.Host;
        _levels = request.Levels is { } levels ? new LevelSwitch(levels) : null;
        _levelRow.Show(request.Levels);
        _levelRow.Chosen += OnLevelChosen;
        SayLevel(request.LevelNote);
        _hostWindow = request.HostWindow;
        _state = new ImportWindowState(_checklist is not null || _beforeImport, WindowLabels.HostName(HostIdentity.Id), request.MeasuredCosts);

        Title = WindowLabels.ImportWindowTitle;
        BrandChrome.ShowMarkInTitleBar(this);
        Width = _checklist is { OffersLevels: true } ? WidthWithLevels : PlainWidth;

        MinHeight = 460 + BrandChrome.HeaderHeight;
        SizeToContent = SizeToContent.Height;

        ShowInTaskbar = true;
        _opening = ForegroundWatch.Read(IntPtr.Zero, _hostWindow);
        ShowActivated = ImportWindowStacking.ShowsActivated(_opening.Facts);
        PlaceOver(_hostWindow);

        _tick = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = ClockTick };
        _tick.Tick += (_, _) => PaintRun();

        Content = BuildLayout(request.BundleName, request.DeliveryLine, request.UnitsDisagreement);
        if (_checklist is not null)
        {
            _body.Child = BuildChecklist(_checklist);
        }
        else if (_beforeImport)
        {
            TextBlock before = new() { Text = request.BeforeImport, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetAutomationId(before, ImportWindowAutomation.BeforeImport);
            _body.Child = before;
        }

        AutomationProperties.SetAutomationId(this, ImportWindowAutomation.Window);
        AutomationProperties.SetAutomationId(_import, ImportWindowAutomation.Import);
        AutomationProperties.SetAutomationId(_cancel, ImportWindowAutomation.Cancel);
        AutomationProperties.SetAutomationId(_close, ImportWindowAutomation.Close);
        _levelRow.SetAutomationId(ImportWindowAutomation.Level);
        AutomationProperties.SetAutomationId(_levelNote, ImportWindowAutomation.LevelNote);
        AutomationProperties.SetAutomationId(_status, ImportWindowAutomation.Status);
        AutomationProperties.SetAutomationId(_report, ImportWindowAutomation.Report);

        BrandChrome.MakePrimary(_import);

        _import.Click += (_, _) => Begin();
        _cancel.Click += (_, _) => Cancel();
        _close.Click += (_, _) => Close();
        Closing += OnClosing;

        SourceInitialized += (_, _) => _foreground ??= new ForegroundWatch(
            new WindowInteropHelper(this).Handle,
            _hostWindow,
            _opening,
            fault => _host.Note($"The import window could not restack itself: {fault.GetType().Name}: {fault.Message}"));
        Closed += (_, _) =>
        {
            _tick.Stop();
            _foreground?.Dispose();
        };

        RefreshChecklist();
        PaintButtons();
    }

    internal void ShowRun(ImportRunView view)
    {
        if (!_state.ShowRun(view, _clock.Elapsed))
        {
            return;
        }

        SizeToContent = SizeToContent.Manual;
        _body.Child = BuildSteps();
        _progress.Visibility = Visibility.Visible;
        _tick.Start();
        PaintButtons();
        PaintView(view);
    }

    internal void Refresh(ImportRunView view)
    {
        if (_state.Refresh(view, _clock.Elapsed))
        {
            PaintView(view);
        }
    }

    internal void ShowFinished(ImportRunView? view, string? outcome, string report, ImportFeedbackLinks? ask = null)
    {
        ImportWindowFinish finish = _state.Finish(view, _clock.Elapsed);
        if (finish == ImportWindowFinish.Ignored)
        {
            return;
        }

        if (view is not null)
        {
            PaintStates();
        }

        _tick.Stop();
        _status.Text = outcome ?? string.Empty;
        _progress.IsIndeterminate = false;
        _progress.Value = 1;
        _report.Text = report;
        _report.Visibility = Visibility.Visible;
        PaintButtons();

        if (ask is not null && _state.ShowsFeedbackAsk)
        {
            ShowAsk(ask);
        }

        if (finish == ImportWindowFinish.Close)
        {
            Close();
        }
    }

    private void ShowAsk(ImportFeedbackLinks links)
    {
        _ask.Children.Clear();
        _ask.Children.Add(new TextBlock
        {
            Text = ImportFeedbackPrompt.Question,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        });
        _ask.Children.Add(AskButton(ImportFeedbackPrompt.Yes, links.Yes));
        _ask.Children.Add(AskButton(ImportFeedbackPrompt.No, links.No));
        _ask.Children.Add(AskButton(ImportFeedbackPrompt.Dismiss, link: null));
        _ask.Visibility = Visibility.Visible;
    }

    private Button AskButton(string face, string? link)
    {
        Button button = new() { Content = face, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 2, 12, 2) };
        button.Click += (_, _) =>
        {
            _ask.Visibility = Visibility.Collapsed;
            if (link is not null && !ShellLauncher.TryOpen(link, out string refused))
            {
                _host.Note($"The feedback link could not be opened: {refused}");
            }
        };
        return button;
    }

    internal void BringForward() => ComeForward(ImportWindowStacking.BringsForward);

    internal void BringForwardAtTheEnd() => ComeForward(ImportWindowStacking.ComesForwardAtTheEnd);

    private void ComeForward(Func<ForegroundFacts, bool> allowed)
    {
        if (!allowed(ForegroundWatch.Read(new WindowInteropHelper(this).Handle, _hostWindow).Facts))
        {
            return;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    internal void Lower() => _foreground?.Lower();

    internal void CloseNow()
    {
        _state.CloseNow();
        Close();
    }

    private void PaintView(ImportRunView view)
    {
        PaintStates();

        _report.Text = string.Join(Environment.NewLine + Environment.NewLine, view.Notices);
        _report.Visibility = view.Notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _progress.IsIndeterminate = view.Indeterminate;
        _progress.Value = view.Progress?.Fraction ?? 0;

        PaintRun();
    }

    private void PaintStates()
    {
        IReadOnlyList<ImportWindowRow> rows = _state.Rows;
        for (int index = 0; index < rows.Count && index < _states.Length; index++)
        {
            _states[index].Text = rows[index].State;
        }
    }

    private void PaintRun()
    {
        if (_state.StatusLine(_clock.Elapsed) is { } line)
        {
            _status.Text = line;
        }
    }

    private void PaintButtons()
    {
        _import.Visibility = _state.ImportVisible ? Visibility.Visible : Visibility.Collapsed;
        _import.IsEnabled = _state.ImportEnabled(CanImport) && (_levels?.CanImport ?? true);
        _cancel.IsEnabled = _state.CancelEnabled;
        _close.Visibility = _state.CloseVisible ? Visibility.Visible : Visibility.Collapsed;
        _close.IsEnabled = _state.CloseEnabled;
    }

    private void PlaceOver(IntPtr hostWindow)
    {
        if (HostWindowGeometry.Bounds(hostWindow, whereRestored: true) is not { } host)
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        ScreenRect place = ImportWindowPlacement.CentreOver(host, Width, MinHeight);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = place.Left;
        Top = place.Top;
    }

    private DockPanel BuildLayout(string bundleName, string? deliveryLine, string? unitsDisagreement)
    {
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(_import);
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_close);

        StackPanel top = new();
        top.Children.Add(new TextBlock { Text = bundleName, TextTrimming = TextTrimming.CharacterEllipsis });

        if (deliveryLine is not null)
        {
            top.Children.Add(new TextBlock { Text = deliveryLine, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
        }

        if (unitsDisagreement is not null)
        {
            top.Children.Add(new TextBlock { Text = unitsDisagreement, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        }

        top.Children.Add(_levelRow);
        top.Children.Add(_levelNote);
        top.Children.Add(_body);
        top.Children.Add(_progress);
        top.Children.Add(_status);

        DockPanel root = new() { Margin = new Thickness(12) };
        BrandChrome.AddHeader(root, WindowLabels.ImportHeading);
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(_ask, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(buttons);
        root.Children.Add(_ask);
        root.Children.Add(_report);
        return root;
    }

    private StackPanel BuildChecklist(ImportChecklist checklist)
    {
        Grid rows = new() { Margin = new Thickness(0, 4, 0, 0) };
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        for (int index = 0; index < checklist.Layers.Count; index++)
        {
            ImportLayer layer = checklist.Layers[index];
            rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            CheckBox box = new() { Content = WindowLabels.LayerName(layer), Margin = new Thickness(0, 2, 12, 2) };
            box.Checked += (_, _) => OnBoxChanged(layer, on: true);
            box.Unchecked += (_, _) => OnBoxChanged(layer, on: false);
            Grid.SetRow(box, index);
            rows.Children.Add(box);
            _boxes[layer] = box;

            TextBlock note = new() { Margin = new Thickness(0, 2, 0, 2), Opacity = 0.7, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 };
            Grid.SetRow(note, index);
            Grid.SetColumn(note, 1);
            rows.Children.Add(note);
            _notes[layer] = note;
        }

        if (checklist.OffersLevels)
        {
            AddLevelColumn(checklist, rows);
        }

        StackPanel panel = new();
        panel.Children.Add(checklist.OffersLevels
            ? BuildLevelHeading()
            : new TextBlock { Text = WindowLabels.IncludeHeading, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(rows);
        panel.Children.Add(_slowLayers);

        if (checklist.Unavailable.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = WindowLabels.UnavailableHeading, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
            foreach (UnavailableLayers group in checklist.Unavailable)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = string.Join(", ", group.Layers.Select(WindowLabels.LayerName)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0),
                });
                panel.Children.Add(new TextBlock { Text = group.Reason, TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
            }
        }

        return panel;
    }

    private void OnBoxChanged(ImportLayer layer, bool on)
    {
        if (_refreshing || _checklist is null)
        {
            return;
        }

        _checklist.Set(layer, on);
        RefreshChecklist();
    }

    private void RefreshChecklist()
    {
        if (_checklist is null)
        {
            return;
        }

        _refreshing = true;
        try
        {
            IReadOnlyList<string> slow = _checklist.SlowLayerWarnings;
            _slowLayers.Text = string.Join(Environment.NewLine, slow);
            _slowLayers.Visibility = slow.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (ImportLayer layer in _checklist.Layers)
            {
                _boxes[layer].IsChecked = _checklist.IsChecked(layer);
                _boxes[layer].IsEnabled = _checklist.IsEnabled(layer);
                _notes[layer].Text = _checklist.MissingPrerequisite(layer) is { } missing
                    ? WindowLabels.Needs(missing)
                    : _checklist.EstimateNote(layer) ?? string.Empty;
            }

            RefreshLevels(_checklist);
            _import.IsEnabled = _state.ImportEnabled(_checklist.CanImport);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private Grid BuildSteps()
    {
        IReadOnlyList<ImportWindowRow> rows = _state.Rows;
        _states = new TextBlock[rows.Count];

        Grid steps = new();
        steps.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        steps.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int index = 0; index < rows.Count; index++)
        {
            steps.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock name = new() { Text = rows[index].Step, Margin = new Thickness(0, 2, 12, 2) };
            Grid.SetRow(name, index);
            steps.Children.Add(name);

            _states[index] = new TextBlock { Margin = new Thickness(0, 2, 0, 2) };
            Grid.SetRow(_states[index], index);
            Grid.SetColumn(_states[index], 1);
            steps.Children.Add(_states[index]);
        }

        return steps;
    }

    private void Begin()
    {
        if ((_checklist is null && !_beforeImport) || !_state.PressImport(CanImport))
        {
            return;
        }

        _levelRow.IsEnabled = false;
        PaintButtons();
        RefreshChecklist();

        _host.Begin(_checklist?.Choice ?? ImportLayerChoice.All);
    }

    private bool CanImport => _checklist?.CanImport ?? _beforeImport;

    private void OnLevelChosen(FidelityLevel level)
    {
        if (_levels?.Choose(level) is not { } fetch)
        {
            if (_levels is not null)
            {
                _levelRow.Select(_levels.Control);
            }

            return;
        }

        _levelRow.IsEnabled = false;
        _import.IsEnabled = false;
        SayLevel(WindowLabels.LevelDownloading(fetch));
        _host.ChooseLevel(fetch);
    }

    internal void LevelKept(FidelityLevel held, string note)
    {
        if (_levels is null)
        {
            return;
        }

        _levels.Fetched(ready: false);
        _ = _levels.Control.Choose(held);
        _levelRow.Select(_levels.Control);
        _levelRow.IsEnabled = true;
        SayLevel(note);
        PaintButtons();
    }

    private void SayLevel(string? note)
    {
        _levelNote.Text = note ?? string.Empty;
        _levelNote.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Cancel()
    {
        switch (_state.PressCancel())
        {
            case ImportWindowCancel.CloseWindow:
                Close();
                break;
            case ImportWindowCancel.AskCancel:
                PaintButtons();
                _host.CancelRun();
                PaintRun();
                break;
            default:
                break;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        switch (_state.Closing())
        {
            case ImportWindowClose.GoAndDismiss:
                _host.Dismiss();
                break;
            case ImportWindowClose.StayAndCancel:
                e.Cancel = true;
                PaintButtons();
                _host.CancelRun();
                PaintRun();
                break;
            case ImportWindowClose.Stay:
                e.Cancel = true;
                break;
            default:
                break;
        }
    }
}
