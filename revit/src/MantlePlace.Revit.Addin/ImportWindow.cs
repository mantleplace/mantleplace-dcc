using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// The modeless import window: first the checklist of what the bundle carries, then every chosen
/// step and where it stands, the step in flight with its clock, Cancel, and the report when the run
/// is over.
/// </summary>
/// <remarks>
/// <para>
/// It shows an <see cref="ImportChecklist"/> and then <see cref="ImportRunView"/>s, and decides
/// nothing about either.
/// </para>
/// <para>
/// ⛔ <b>It lives on a thread of its own, and nothing here may touch the import.</b> It used to share
/// Revit's thread and be owned by Revit's main window, so it repainted only between slices and froze
/// with Revit through every commit — twenty minutes of road surfaces, forty of drape, with no step
/// named and no reading answered. Now <see cref="ImportWindowHost"/> runs it on its own dispatcher,
/// unowned, and it hears about the run only through the views Revit's thread posts to it. What the
/// curator does here goes back the same way: Import, Cancel and a dismissal are callbacks the host
/// has already marshalled onto Revit's thread. Its clock is its own, so it keeps moving while Revit
/// posts nothing at all. How it stays in front of Revit without an owner is
/// <see cref="ForegroundWatch"/>.
/// </para>
/// <para>
/// <b>Closing it while the import runs is a cancel.</b> The vault window's close box only detaches a
/// view, because the job it shows runs on a server and can be rejoined. This one shows work on
/// Revit's own thread with no other place to see or stop it, so a closed window over a running import
/// would leave the curator with a model changing under them and nothing to click. Closing asks for
/// the cancel and the window goes once the run has stopped at its next boundary. Closing it — or
/// pressing Cancel — before Import has nothing to wait for, and the window simply goes.
/// </para>
/// <para>
/// Built in code rather than XAML, for <see cref="VaultBrowserWindow"/>'s reason.
/// </para>
/// </remarks>
internal sealed class ImportWindow : Window
{
    /// <summary>How often the step's clock is repainted.</summary>
    private static readonly TimeSpan ClockTick = TimeSpan.FromSeconds(1);

    private const double BaselineDpi = 96.0;

    private readonly ImportChecklist _checklist;
    private readonly Action<ImportLayerChoice> _begin;
    private readonly Action _cancelRun;
    private readonly Action _dismiss;

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
    private readonly TextBox _report = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Margin = new Thickness(0, 8, 0, 0),
        Visibility = Visibility.Collapsed,
    };

    private readonly Button _import = new() { Content = WindowLabels.Import, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
    private readonly Button _cancel = new() { Content = WindowLabels.Cancel, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
    private readonly Button _close = new() { Content = WindowLabels.Close, Padding = new Thickness(12, 4, 12, 4), Visibility = Visibility.Collapsed };

    /// <summary>Repaints the status line once a second, so the clock moves while Revit posts nothing.</summary>
    private readonly DispatcherTimer _tick;

    /// <summary>How long the step in flight has run, from the moment a view naming it arrived here.</summary>
    private readonly Stopwatch _stepClock = new();

    private ImportRunView? _view;
    private TextBlock[] _states = [];
    private ForegroundWatch? _foreground;
    private bool _begun;
    private bool _cancelAsked;
    private bool _finished;
    private bool _closeWhenFinished;
    private bool _refreshing;

    /// <param name="deliveryLine">The order's unit system, linear unit and delivery CRS; <c>null</c> shows no line.</param>
    /// <param name="unitsDisagreement">Shown only when set: the project displays the other unit system.</param>
    /// <param name="revitWindow">Revit's main window: where the window opens. It is not the owner.</param>
    /// <param name="begin">Called once, with the curator's choice, when Import is pressed.</param>
    /// <param name="cancelRun">Called once, when the curator asks a running import to stop.</param>
    /// <param name="dismiss">Called once when the window goes before Import was pressed.</param>
    internal ImportWindow(
        string bundleName,
        string? deliveryLine,
        string? unitsDisagreement,
        ImportChecklist checklist,
        IntPtr revitWindow,
        Action<ImportLayerChoice> begin,
        Action cancelRun,
        Action dismiss)
    {
        _checklist = checklist;
        _begin = begin;
        _cancelRun = cancelRun;
        _dismiss = dismiss;

        Title = WindowLabels.ImportWindowTitle;
        Width = 520;

        // The height it always had, and taller when the unavailable list below the boxes needs it:
        // at a fixed height that list pushed the buttons off the window. Fixed again once the run
        // starts (ShowRun), so the report fills the window rather than growing it.
        MinHeight = 460 + BrandChrome.HeaderHeight;
        SizeToContent = SizeToContent.Height;

        // ⛔ No owner. An owned window shares its owner's input queue, and Revit's is stuck for the
        // whole of every long commit. ForegroundWatch keeps it in front of Revit instead, and without
        // an owner it needs a taskbar button of its own to be found again behind something else.
        ShowInTaskbar = true;
        ShowActivated = ImportWindowStacking.ShowsActivated(ForegroundWatch.Holder());
        WindowStartupLocation = WindowStartupLocation.Manual;
        CentreOver(revitWindow);

        _tick = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = ClockTick };
        _tick.Tick += (_, _) => PaintRun();

        Content = BuildLayout(bundleName, deliveryLine, unitsDisagreement);
        _body.Child = BuildChecklist();
        BrandChrome.MakePrimary(_import);

        _import.Click += (_, _) => Begin();
        _cancel.Click += (_, _) => Cancel();
        _close.Click += (_, _) => Close();
        Closing += OnClosing;
        Loaded += (_, _) => _foreground ??= new ForegroundWatch(new WindowInteropHelper(this).Handle);
        Closed += (_, _) =>
        {
            _tick.Stop();
            _foreground?.Dispose();
        };

        RefreshChecklist();
    }

    /// <summary>Swaps the checklist for the chosen steps. Called once the import has its plan.</summary>
    internal void ShowRun(ImportRunView view)
    {
        if (_finished)
        {
            return;
        }

        SizeToContent = SizeToContent.Manual;
        _body.Child = BuildSteps(view);
        _import.Visibility = Visibility.Collapsed;
        _close.Visibility = Visibility.Visible;
        _close.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        _tick.Start();
        Refresh(view);
    }

    /// <summary>Takes the run as Revit's thread last saw it: after a slice, and as a commit starts or ends.</summary>
    internal void Refresh(ImportRunView view)
    {
        if (_finished || _states.Length != view.Rows.Count)
        {
            return;
        }

        // The clock is this window's own, and restarts when a view names a new step, so it keeps
        // counting through a commit that lets Revit's thread post nothing.
        if (view.CurrentIndex != (_view?.CurrentIndex ?? -1))
        {
            _stepClock.Restart();

            // Ticking from the step's own start, so the clock turns over on its whole seconds
            // rather than up to a second late.
            _tick.Stop();
            _tick.Start();
        }

        _view = view;
        for (int index = 0; index < view.Rows.Count; index++)
        {
            _states[index].Text = WindowLabels.StateWord(view.Rows[index].State);
        }

        // What the step said about its wait, beside the clock that counts it. The report box is
        // otherwise empty until the run is over.
        _report.Text = string.Join(Environment.NewLine + Environment.NewLine, view.Notices);
        _report.Visibility = view.Notices.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // A step that is one commit has no part to show, so the bar says "working" rather than 0%.
        _progress.IsIndeterminate = view.Current is not null && view.Progress is null;
        _progress.Value = view.Progress?.Fraction ?? 0;

        PaintRun();
    }

    /// <summary>Shows the report and hands the window back to the curator.</summary>
    /// <param name="view">The run as it ended, or <c>null</c> for one that never began.</param>
    /// <param name="outcome">How a cancelled run ended, in one line; <c>null</c> otherwise.</param>
    /// <param name="report">The closing report.</param>
    internal void ShowFinished(ImportRunView? view, string? outcome, string report)
    {
        if (_finished)
        {
            // Dismissed before Import: the window is already on its way out.
            return;
        }

        if (view is not null)
        {
            Refresh(view);
        }

        _finished = true;
        _tick.Stop();
        _stepClock.Stop();

        _status.Text = outcome ?? string.Empty;
        _progress.IsIndeterminate = false;
        _progress.Value = 1;
        _report.Text = report;
        _report.Visibility = Visibility.Visible;
        _import.Visibility = Visibility.Collapsed;
        _cancel.IsEnabled = false;
        _close.Visibility = Visibility.Visible;
        _close.IsEnabled = true;

        if (_closeWhenFinished)
        {
            Close();
        }
    }

    /// <summary>Brings the window forward, for a second click on Import Bundle.</summary>
    internal void BringForward()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <summary>Repaints the status line from the last view and the window's own clock.</summary>
    private void PaintRun()
    {
        if (_view is not { } view || _finished)
        {
            return;
        }

        // A cancel pressed here reaches Revit's thread only when its commit returns; it is said from
        // the moment it is pressed.
        _status.Text = WindowLabels.StatusLine(_cancelAsked ? view.WithCancelRequested() : view, _stepClock.Elapsed);
    }

    /// <summary>
    /// Opens the window centred over Revit's, in device-independent pixels, the way
    /// <see cref="PrepareNotifier"/> places its notices.
    /// </summary>
    /// <remarks>
    /// Centred on its smallest height: the unavailable list can make it taller once laid out, which
    /// leaves it a little low rather than off the monitor. A minimised Revit is centred on by where it
    /// would restore to. Nothing here sends Revit's window a message.
    /// </remarks>
    private void CentreOver(IntPtr revitWindow)
    {
        if (revitWindow == IntPtr.Zero || !RevitRectangle(revitWindow, out NativeRect rect))
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            return;
        }

        double scale = GetDpiForWindow(revitWindow) is var dpi and > 0 ? dpi / BaselineDpi : 1.0;
        double left = rect.Left / scale;
        double top = rect.Top / scale;
        double width = (rect.Right - rect.Left) / scale;
        double height = (rect.Bottom - rect.Top) / scale;

        Left = left + Math.Max(0, (width - Width) / 2);
        Top = top + Math.Max(0, (height - MinHeight) / 2);
    }

    private static bool RevitRectangle(IntPtr revitWindow, out NativeRect rect)
    {
        if (!IsIconic(revitWindow))
        {
            return GetWindowRect(revitWindow, out rect);
        }

        WindowPlacement placement = new() { Length = Marshal.SizeOf<WindowPlacement>() };
        bool read = GetWindowPlacement(revitWindow, ref placement);
        rect = placement.NormalPosition;
        return read;
    }

    private UIElement BuildLayout(string bundleName, string? deliveryLine, string? unitsDisagreement)
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
        top.Children.Add(_body);
        top.Children.Add(_progress);
        top.Children.Add(_status);

        DockPanel root = new() { Margin = new Thickness(12) };
        BrandChrome.AddHeader(root, WindowLabels.ImportHeading);
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(buttons);
        root.Children.Add(_report);
        return root;
    }

    /// <summary>One box per layer the bundle carries, each with room beside it to say what it needs.</summary>
    private StackPanel BuildChecklist()
    {
        Grid rows = new() { Margin = new Thickness(0, 4, 0, 0) };
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rows.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        for (int index = 0; index < _checklist.Layers.Count; index++)
        {
            ImportLayer layer = _checklist.Layers[index];
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

            TextBlock note = new() { Margin = new Thickness(0, 2, 0, 2), Opacity = 0.7 };
            Grid.SetRow(note, index);
            Grid.SetColumn(note, 1);
            rows.Children.Add(note);
            _notes[layer] = note;
        }

        StackPanel panel = new();
        panel.Children.Add(new TextBlock { Text = WindowLabels.IncludeHeading, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(rows);

        // Below the boxes, and never a box itself: what the bundle holds and this import cannot
        // offer, said before anything runs. Nothing at all for a bundle with nothing withheld.
        if (_checklist.Unavailable.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = WindowLabels.UnavailableHeading, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0) });
            foreach (UnavailableLayers group in _checklist.Unavailable)
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
        if (_refreshing)
        {
            return;
        }

        _checklist.Set(layer, on);
        RefreshChecklist();
    }

    private void RefreshChecklist()
    {
        _refreshing = true;
        try
        {
            foreach (ImportLayer layer in _checklist.Layers)
            {
                _boxes[layer].IsChecked = _checklist.IsChecked(layer);
                _boxes[layer].IsEnabled = _checklist.IsEnabled(layer);
                _notes[layer].Text = _checklist.MissingPrerequisite(layer) is { } missing
                    ? WindowLabels.Needs(missing)
                    : string.Empty;
            }

            _import.IsEnabled = !_begun && _checklist.CanImport;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private Grid BuildSteps(ImportRunView view)
    {
        _states = new TextBlock[view.Rows.Count];

        Grid steps = new();
        steps.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        steps.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int index = 0; index < view.Rows.Count; index++)
        {
            steps.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock name = new() { Text = WindowLabels.StepName(view.Rows[index].Kind), Margin = new Thickness(0, 2, 12, 2) };
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
        if (_begun || !_checklist.CanImport)
        {
            return;
        }

        // Dark before the call rather than after it: a second click queued behind a slow re-plan
        // would otherwise begin the same import twice. And from here a close is a cancel, even
        // before the plan's steps arrive: the import is already Revit's thread's to run.
        _begun = true;
        _import.IsEnabled = false;
        _begin(_checklist.Choice);
    }

    private void Cancel()
    {
        if (!_begun)
        {
            Close();
            return;
        }

        if (_cancelAsked)
        {
            return;
        }

        _cancelAsked = true;
        _cancel.IsEnabled = false;
        _cancelRun();
        PaintRun();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_finished)
        {
            return;
        }

        if (!_begun)
        {
            // Nothing has run and nothing will: the window goes now, and the import is dropped.
            _finished = true;
            _dismiss();
            return;
        }

        e.Cancel = true;
        _closeWhenFinished = true;
        Cancel();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr hWnd, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
