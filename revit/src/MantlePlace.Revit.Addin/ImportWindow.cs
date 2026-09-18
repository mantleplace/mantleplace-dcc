using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// The modeless import window: every step of the plan and where it stands, the current step's
/// progress in elements, Cancel, and the report when the run is over.
/// </summary>
/// <remarks>
/// <para>
/// It shows a <see cref="StagedImport"/> and decides nothing about it. It repaints because the import
/// hands Revit its message loop back between slices; inside one slice — one commit — it cannot, and
/// the step's own line in the log says so (<see cref="SlowStepNotice"/>).
/// </para>
/// <para>
/// <b>Closing it while the import runs is a cancel.</b> The vault window's close box only detaches a
/// view, because the job it shows runs on a server and can be rejoined. This one shows work on
/// Revit's own thread with no other place to see or stop it, so a closed window over a running import
/// would leave the curator with a model changing under them and nothing to click. Closing asks for
/// the cancel and the window goes once the run has stopped at its next boundary.
/// </para>
/// <para>
/// Built in code rather than XAML, for <see cref="VaultBrowserWindow"/>'s reason.
/// </para>
/// </remarks>
internal sealed class ImportWindow : Window
{
    private readonly StagedImport _run;

    private readonly TextBlock[] _states;
    private readonly ProgressBar _progress = new() { Height = 14, Margin = new Thickness(0, 12, 0, 4), Minimum = 0, Maximum = 1 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 20 };
    private readonly TextBox _report = new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Margin = new Thickness(0, 8, 0, 0),
        Visibility = Visibility.Collapsed,
    };

    private readonly Button _cancel = new() { Content = WindowLabels.Cancel, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 4, 12, 4) };
    private readonly Button _close = new() { Content = WindowLabels.Close, Padding = new Thickness(12, 4, 12, 4), IsEnabled = false };

    private bool _finished;
    private bool _closeWhenFinished;

    internal ImportWindow(string bundleName, StagedImport run, IntPtr revitWindow)
    {
        _run = run;
        _states = new TextBlock[run.Steps.Count];

        Title = WindowLabels.ImportWindowTitle;
        Width = 520;
        Height = 460 + BrandChrome.HeaderHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // Owned by Revit's main window, for the vault window's reason: it stays in front of the model
        // it is changing and minimises with it.
        new WindowInteropHelper(this) { Owner = revitWindow };

        Content = BuildLayout(bundleName);

        _cancel.Click += (_, _) => Cancel();
        _close.Click += (_, _) => Close();
        Closing += OnClosing;

        Refresh();
    }

    /// <summary>Re-reads the run. Called after every slice.</summary>
    internal void Refresh()
    {
        for (int index = 0; index < _run.Steps.Count; index++)
        {
            _states[index].Text = WindowLabels.StateWord(_run.Steps[index].State);
        }

        _status.Text = WindowLabels.StatusLine(_run);

        // A step that is one commit has no part to show, so the bar says "working" rather than 0%.
        _progress.IsIndeterminate = _run.Current is { Progress: null };
        _progress.Value = _run.Current?.Progress?.Fraction ?? (_finished ? 1 : 0);
    }

    /// <summary>Shows the report and hands the window back to the curator.</summary>
    internal void ShowFinished(string report)
    {
        _finished = true;
        Refresh();

        _status.Text = _run.Outcome ?? string.Empty;
        _report.Text = report;
        _report.Visibility = Visibility.Visible;
        _cancel.IsEnabled = false;
        _close.IsEnabled = true;

        if (_closeWhenFinished)
        {
            Close();
        }
    }

    private UIElement BuildLayout(string bundleName)
    {
        Grid steps = new() { Margin = new Thickness(0, 8, 0, 0) };
        steps.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        steps.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int index = 0; index < _run.Steps.Count; index++)
        {
            steps.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock name = new() { Text = WindowLabels.StepName(_run.Steps[index].Step.Kind), Margin = new Thickness(0, 2, 12, 2) };
            Grid.SetRow(name, index);
            steps.Children.Add(name);

            _states[index] = new TextBlock { Margin = new Thickness(0, 2, 0, 2) };
            Grid.SetRow(_states[index], index);
            Grid.SetColumn(_states[index], 1);
            steps.Children.Add(_states[index]);
        }

        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_close);

        StackPanel top = new();
        top.Children.Add(new TextBlock { Text = bundleName, TextTrimming = TextTrimming.CharacterEllipsis });
        top.Children.Add(steps);
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

    private void Cancel()
    {
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

        e.Cancel = true;
        _closeWhenFinished = true;
        Cancel();
    }
}
