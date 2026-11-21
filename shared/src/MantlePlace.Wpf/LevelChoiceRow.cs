using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MantlePlace.Core;

namespace MantlePlace.Wpf;

/// <summary>
/// A 3.0 order's level control as every .NET host draws it: the glossary's heading, then Max, Med and
/// Min with their sizes, the recommendation marked and the counts in each tooltip
/// (<see cref="LevelControl"/>). The shared vault window and Revit's import window both show this
/// one row, so the control is the same wherever it is drawn. Under it, one locked row for each Extra the
/// platform locks on a free bundle: its name and the words, greyed, with nothing to press.
/// </summary>
/// <remarks>
/// Collapsed until it is shown a control; a 2.x order shows none. <see cref="Chosen"/> is raised for
/// the curator's choices only, never for the row being filled or put back.
/// </remarks>
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

    /// <summary>The curator chose a level.</summary>
    public event Action<FidelityLevel>? Chosen;

    /// <summary>Names the row's list for UI Automation, so a check can find and set it in the window that draws it.</summary>
    public void SetAutomationId(string automationId) => AutomationProperties.SetAutomationId(_levels, automationId);

    /// <summary>Fills the row from <paramref name="control"/>, on its selected level; <c>null</c> hides the row.</summary>
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

            // A locked Extra is words and nothing to press: the platform refuses a request for it.
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

    /// <summary>Puts the row on <paramref name="control"/>'s selected level without raising <see cref="Chosen"/>.</summary>
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
