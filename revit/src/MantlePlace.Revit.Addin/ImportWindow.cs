using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// The modeless import window: first the checklist of what the bundle carries, then every chosen
/// step and where it stands, the current step's progress in elements, Cancel, and the report when
/// the run is over.
/// </summary>
/// <remarks>
/// <para>
/// It shows an <see cref="ImportChecklist"/> and then a <see cref="StagedImport"/>, and decides
/// nothing about either. It repaints because the import hands Revit its message loop back between
/// slices; inside one slice — one commit — it cannot, and the step's own line in the log says so
/// (<see cref="SlowStepNotice"/>).
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
    private readonly ImportChecklist _checklist;
    private readonly Action<ImportLayerChoice> _begin;
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

    private StagedImport? _run;
    private TextBlock[] _states = [];
    private bool _finished;
    private bool _closeWhenFinished;
    private bool _refreshing;

    /// <param name="deliveryLine">The order's unit system, linear unit and delivery CRS; <c>null</c> shows no line.</param>
    /// <param name="unitsDisagreement">Shown only when set: the project displays the other unit system.</param>
    /// <param name="begin">Called once, with the curator's choice, when Import is pressed.</param>
    /// <param name="dismiss">Called once when the window goes before Import was pressed.</param>
    internal ImportWindow(
        string bundleName,
        string? deliveryLine,
        string? unitsDisagreement,
        ImportChecklist checklist,
        IntPtr revitWindow,
        Action<ImportLayerChoice> begin,
        Action dismiss)
    {
        _checklist = checklist;
        _begin = begin;
        _dismiss = dismiss;

        Title = WindowLabels.ImportWindowTitle;
        Width = 520;

        // The height it always had, and taller when the unavailable list below the boxes needs it:
        // at a fixed height that list pushed the buttons off the window. Fixed again once the run
        // starts (ShowRun), so the report fills the window rather than growing it.
        MinHeight = 460 + BrandChrome.HeaderHeight;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // Owned by Revit's main window, for the vault window's reason: it stays in front of the model
        // it is changing and minimises with it.
        new WindowInteropHelper(this) { Owner = revitWindow };

        Content = BuildLayout(bundleName, deliveryLine, unitsDisagreement);
        _body.Child = BuildChecklist();
        BrandChrome.MakePrimary(_import);

        _import.Click += (_, _) => Begin();
        _cancel.Click += (_, _) => Cancel();
        _close.Click += (_, _) => Close();
        Closing += OnClosing;

        RefreshChecklist();
    }

    /// <summary>Swaps the checklist for the chosen steps. Called once the import has its plan.</summary>
    internal void ShowRun(StagedImport run)
    {
        _run = run;
        SizeToContent = SizeToContent.Manual;
        _body.Child = BuildSteps(run);
        _import.Visibility = Visibility.Collapsed;
        _close.Visibility = Visibility.Visible;
        _close.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        Refresh();
    }

    /// <summary>Re-reads the run. Called after every slice.</summary>
    internal void Refresh()
    {
        if (_run is not { } run)
        {
            return;
        }

        for (int index = 0; index < run.Steps.Count; index++)
        {
            _states[index].Text = WindowLabels.StateWord(run.Steps[index].State);
        }

        _status.Text = WindowLabels.StatusLine(run);

        // A step that is one commit has no part to show, so the bar says "working" rather than 0%.
        _progress.IsIndeterminate = run.Current is { Progress: null };
        _progress.Value = run.Current?.Progress?.Fraction ?? (_finished ? 1 : 0);
    }

    /// <summary>Shows the report and hands the window back to the curator.</summary>
    internal void ShowFinished(string report)
    {
        if (_finished)
        {
            // Dismissed before Import: the window is already on its way out.
            return;
        }

        _finished = true;
        Refresh();

        _status.Text = _run?.Outcome ?? string.Empty;
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

            _import.IsEnabled = _checklist.CanImport;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private Grid BuildSteps(StagedImport run)
    {
        _states = new TextBlock[run.Steps.Count];

        Grid steps = new();
        steps.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        steps.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int index = 0; index < run.Steps.Count; index++)
        {
            steps.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock name = new() { Text = WindowLabels.StepName(run.Steps[index].Step.Kind), Margin = new Thickness(0, 2, 12, 2) };
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
        if (_run is not null || !_checklist.CanImport)
        {
            return;
        }

        // Dark before the call rather than after it: a second click queued behind a slow re-plan
        // would otherwise begin the same import twice.
        _import.IsEnabled = false;
        _begin(_checklist.Choice);
    }

    private void Cancel()
    {
        if (_run is null)
        {
            Close();
            return;
        }

        _run.RequestCancel();
        _cancel.IsEnabled = false;
        Refresh();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_finished)
        {
            return;
        }

        if (_run is null)
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
}
