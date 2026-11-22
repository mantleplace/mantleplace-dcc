using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MantlePlace.Core;

namespace MantlePlace.Wpf;

public sealed class LevelChoiceRow : StackPanel
{
    private readonly ComboBox _levels = new() { MinWidth = 220, Margin = new Thickness(8, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _locked = new();
    private bool _filling;

    public LevelChoiceRow()
    {
        Margin = new Thickness(0, 0, 0, 8);
        Visibility = Visibility.Collapsed;

        DockPanel levelLine = new();
        TextBlock heading = new() { Text = LevelControl.Heading, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(heading, Dock.Left);
        levelLine.Children.Add(heading);
        levelLine.Children.Add(_levels);
        Children.Add(levelLine);
        Children.Add(_locked);

        _levels.SelectionChanged += (_, _) =>
        {
            if (!_filling && _levels.SelectedItem is ComboBoxItem { Tag: FidelityLevel level })
            {
                Chosen?.Invoke(level);
            }
        };
    }

    public event Action<FidelityLevel>? Chosen;

    public void SetAutomationId(string automationId) => AutomationProperties.SetAutomationId(_levels, automationId);

    public void Show(LevelControl? control)
    {
        _filling = true;
        try
        {
            _levels.Items.Clear();
            if (control is null)
            {
                _locked.Children.Clear();
                Visibility = Visibility.Collapsed;
                return;
            }

            foreach (DownloadLevelOption option in control.Options)
            {
                _levels.Items.Add(new ComboBoxItem { Content = option.Label, ToolTip = option.ToolTip, Tag = option.Level });
            }

            _levels.SelectedIndex = control.SelectedIndex;

            _locked.Children.Clear();
            foreach (string row in control.LockedRows)
            {
                _locked.Children.Add(new TextBlock
                {
                    Text = row,
                    Margin = new Thickness(0, 4, 0, 0),
                    IsEnabled = false,
                    Foreground = SystemColors.GrayTextBrush,
                });
            }

            Visibility = Visibility.Visible;
        }
        finally
        {
            _filling = false;
        }
    }

    public void Select(LevelControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        _filling = true;
        try
        {
            _levels.SelectedIndex = control.SelectedIndex;
        }
        finally
        {
            _filling = false;
        }
    }
}
