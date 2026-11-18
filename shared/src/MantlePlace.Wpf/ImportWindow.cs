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

/// <summary>
/// The modeless import window every .NET host opens: first the checklist of what the bundle carries,
/// then every chosen step and where it stands, the step in flight with its clock, Cancel, and the
/// report when the run is over. A host with no per-layer choice opens it on its steps alone.
/// </summary>
/// <remarks>
/// <para>
/// It shows an <see cref="ImportChecklist"/> and then <see cref="ImportRunView"/>s, and decides
/// nothing about either: what a press, a close or a view does, and what the status line says, is
/// <see cref="ImportWindowState"/>'s, with its tests.
/// </para>
/// <para>
/// ⛔ <b>It lives on a thread of its own, unowned, and nothing here may touch the import</b>
/// (<see cref="ImportWindowThread"/> says why). It hears about the run only through the views the
/// host's thread posts, and what the curator does here goes back the same way: Import, Cancel, a
/// dismissal and a log line are calls the thread has already marshalled onto the host's thread
/// (<see cref="IImportWindowHost"/>). Its clock is its own, so it keeps moving while the host posts
/// nothing. How it stays in front of the host without an owner is <see cref="ForegroundWatch"/>.
/// </para>
/// <para>
/// <b>Closing it while the import runs is a cancel.</b> The vault window's close box only detaches a
/// view, because the job it shows runs on a server and can be rejoined. This one shows work on the
/// host's own thread with no other place to see or stop it, so a closed window over a running import
/// would leave the curator with a model changing under them and nothing to click. Closing asks for
/// the cancel and the window goes once the run has stopped at its next boundary. Closing it — or
/// pressing Cancel — before Import has nothing to wait for, and the window simply goes.
/// </para>
/// <para>
/// Built in code rather than XAML, for <see cref="VaultBrowserWindow"/>'s reason.
/// </para>
/// </remarks>
internal sealed partial class ImportWindow : Window
{
    /// <summary>
    /// How often the step's clock is repainted: a quarter of a second, so the whole seconds it shows
    /// are never more than that behind. A timer of one second that fires a hair early reads the same
    /// second twice, and the clock then looks stuck while the host is.
    /// </summary>
    private static readonly TimeSpan ClockTick = TimeSpan.FromMilliseconds(250);

    /// <summary>The window's width with no level column.</summary>
    private const double PlainWidth = 520;

    private readonly ImportChecklist? _checklist;

    /// <summary>
    /// A host with no checklist that asked for a stage before Import (<see cref="ImportWindowRequest.BeforeImport"/>):
    /// the window opens on that text with Import, and the run waits for the press.
    /// </summary>
    private readonly bool _beforeImport;
    private readonly IImportWindowHost _host;
    private readonly IntPtr _hostWindow;
    private readonly ImportWindowState _state;

    /// <summary>The window's own monotonic clock, which every reading of <see cref="_state"/> is against.</summary>
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary>What was in front as the window opened: read once, for its focus and its first place alike.</summary>
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

    /// <summary>A received send's 3.0 order's level control, and the line under it; collapsed otherwise.</summary>
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

    // Under the report once the import has ended: "Did this import come in right?" with Yes and No, each a
    // link to the web form (ImportFeedbackPrompt, HPS-56). Empty and collapsed unless the run asked.
    private readonly StackPanel _ask = new()
    {
        Orientation = Orientation.Horizontal,
        Margin = new Thickness(0, 8, 0, 0),
        Visibility = Visibility.Collapsed,
    };

    // Below the rows: what each ticked slow box was measured to cost (ImportChecklist.SlowLayerWarnings).
    private readonly TextBlock _slowLayers = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 6, 0, 0),
        Visibility = Visibility.Collapsed,
    };

    private readonly Button _import = new() { Content = WindowLabels.Import, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
    private readonly Button _cancel = new() { Content = WindowLabels.Cancel, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
    private readonly Button _close = new() { Content = WindowLabels.Close, Padding = new Thickness(12, 4, 12, 4) };

    /// <summary>Repaints the status line every <see cref="ClockTick"/>, so the clock moves while the host posts nothing.</summary>
    private readonly DispatcherTimer _tick;

    private TextBlock[] _states = [];
    private ForegroundWatch? _foreground;
    private bool _refreshing;

    /// <param name="request">What the window shows, and its host, whose calls are already marshalled onto the host's thread.</param>
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

        // The height it always had, and taller when the unavailable list below the boxes needs it:
        // at a fixed height that list pushed the buttons off the window. Fixed again once the run
        // starts (ShowRun), so the report fills the window rather than growing it.
        MinHeight = 460 + BrandChrome.HeaderHeight;
        SizeToContent = SizeToContent.Height;

        // ⛔ No owner (ImportWindowThread). Without one it needs a taskbar button of its own to be
        // found again behind something else.
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

        // At SourceInitialized, which is after the window exists and before it is shown, so one that
        // should not float appears behind what is in front rather than flashing over it first
        // (ForegroundWatch, which rewrites the show itself to do it).
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

    /// <summary>Swaps the checklist for the chosen steps. Called once the import has its plan.</summary>
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

    /// <summary>Takes the run as the host's thread last saw it: after a slice, and as a commit starts or ends.</summary>
    internal void Refresh(ImportRunView view)
    {
        if (_state.Refresh(view, _clock.Elapsed))
        {
            PaintView(view);
        }
    }

    /// <summary>Shows the report and hands the window back to the curator.</summary>
    /// <param name="view">The run as it ended, or <c>null</c> for one that never began.</param>
    /// <param name="outcome">How a cancelled run ended, in one line; <c>null</c> otherwise.</param>
    /// <param name="report">The closing report.</param>
    /// <param name="ask">What Yes and No open, when this import asks "Did this import come in right?"; otherwise <c>null</c>.</param>
    internal void ShowFinished(ImportRunView? view, string? outcome, string report, ImportFeedbackLinks? ask = null)
    {
        ImportWindowFinish finish = _state.Finish(view, _clock.Elapsed);
        if (finish == ImportWindowFinish.Ignored)
        {
            // Dismissed before Import: the window is already on its way out.
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

    /// <summary>
    /// The one-line question under the report. Never blocking: the report, Close and the rest of the
    /// window work as they did, and the line goes on a press of any of its three buttons, or with the
    /// window. The bundle is already remembered as asked about (<see cref="FeedbackAskedStore"/>), so
    /// none of these brings it back.
    /// </summary>
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

    /// <summary>A button that opens <paramref name="link"/> in the system browser, if it has one, and takes the line away.</summary>
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

    /// <summary>Brings the window forward, for a second click on the host's import command — never over a host modal.</summary>
    internal void BringForward() => ComeForward(ImportWindowStacking.BringsForward);

    /// <summary>
    /// Brings the window forward to show the report of an import that has ended — only while its host is
    /// in front, so it never takes the keyboard from another application.
    /// </summary>
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

    /// <summary>Stops floating now: the host is about to show a dialog, which must not open under this window.</summary>
    internal void Lower() => _foreground?.Lower();

    /// <summary>
    /// Closes the window whatever it shows, without asking for a cancel: the host is shutting down, or
    /// the project its checklist was for has closed.
    /// </summary>
    internal void CloseNow()
    {
        _state.CloseNow();
        Close();
    }

    /// <summary>Paints the rows, the notices and the bar from a view the state took.</summary>
    private void PaintView(ImportRunView view)
    {
        PaintStates();

        // What the step said about its wait, beside the clock that counts it. The report box is
        // otherwise empty until the run is over.
        _report.Text = string.Join(Environment.NewLine + Environment.NewLine, view.Notices);
        _report.Visibility = view.Notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // "Working" rather than 0% where there is nothing to count, or nothing left but the host's commit.
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

    /// <summary>Repaints the status line from the last view and the window's own clock.</summary>
    private void PaintRun()
    {
        if (_state.StatusLine(_clock.Elapsed) is { } line)
        {
            _status.Text = line;
        }
    }

    /// <summary>Shows and enables Import, Cancel and Close as the state says.</summary>
    private void PaintButtons()
    {
        _import.Visibility = _state.ImportVisible ? Visibility.Visible : Visibility.Collapsed;
        _import.IsEnabled = _state.ImportEnabled(CanImport) && (_levels?.CanImport ?? true);
        _cancel.IsEnabled = _state.CancelEnabled;
        _close.Visibility = _state.CloseVisible ? Visibility.Visible : Visibility.Collapsed;
        _close.IsEnabled = _state.CloseEnabled;
    }

    /// <summary>
    /// Opens the window centred over the host's (<see cref="ImportWindowPlacement"/>), or over where a
    /// minimised host would restore to.
    /// </summary>
    /// <remarks>
    /// Centred on its least height: the unavailable list can make it taller once laid out, which
    /// leaves it a little low rather than off the monitor.
    /// </remarks>
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

        // Under the name and above the checklist, so it is read before anything is chosen, and it
        // stays through the run. No line at all for a bundle that publishes no delivery block.
        if (deliveryLine is not null)
        {
            top.Children.Add(new TextBlock { Text = deliveryLine, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
        }

        if (unitsDisagreement is not null)
        {
            top.Children.Add(new TextBlock { Text = unitsDisagreement, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) });
        }

        // Above the checklist, which is read from the files at this level: a different level is
        // downloaded, and the window reopens on it, before anything is chosen below.
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

    /// <summary>One box per layer the bundle carries, each with room beside it to say what it needs.</summary>
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
            // Checked and Unchecked, not Click: a UI Automation Toggle — how a screen reader ticks a
            // box — changes IsChecked without raising Click, and the box would show one thing while
            // another was imported.
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

        // Below the boxes, and never a box itself: what the bundle holds and this import cannot
        // offer, said before anything runs. Nothing at all for a bundle with nothing withheld.
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
        // The repaint below writes IsChecked too, and those writes raise the same events. They are
        // the model's own state coming back, not the curator's, so they neither reach it nor start a
        // second repaint inside this one.
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
        // Dark before the call rather than after it (the state's reason). And from here a close is a
        // cancel, even before the plan's steps arrive: the import is already the host's to run.
        if ((_checklist is null && !_beforeImport) || !_state.PressImport(CanImport))
        {
            return;
        }

        _levelRow.IsEnabled = false;
        PaintButtons();
        RefreshChecklist();

        // A stage before Import has no per-layer choice: the host runs every part it planned.
        _host.Begin(_checklist?.Choice ?? ImportLayerChoice.All);
    }

    /// <summary>Whether what is chosen allows Import: the checklist's say, and a stage before Import always.</summary>
    private bool CanImport => _checklist?.CanImport ?? _beforeImport;

    /// <summary>
    /// Another level, before Import: Import and the control go dark while the host downloads it, and
    /// the window reopens on the new level (<see cref="LevelSwitch"/>).
    /// </summary>
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

    /// <summary>The host kept the level it holds: the control goes back to it, with why.</summary>
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
                // Nothing has run and nothing will: the window goes now, and the import is dropped.
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
