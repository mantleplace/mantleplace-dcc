using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using MantlePlace.Core;

namespace MantlePlace.Wpf;

/// <summary>
/// A 3.0 order's level control as every .NET host draws it: the glossary's heading, then Max, Med and
/// Min with their sizes, the recommendation marked and the counts in each tooltip
/// (<see cref="LevelControl"/>). The shared vault window and Revit's import window both show this
/// one row, so the control is the same wherever it is drawn.
/// </summary>
/// <remarks>
/// Collapsed until it is shown a control; a 2.x order shows none. <see cref="Chosen"/> is raised for
/// the curator's choices only, never for the row being filled or put back.
/// </remarks>
public sealed class LevelChoiceRow : DockPanel
{
    private readonly ComboBox _levels = new() { MinWidth = 220, Margin = new Thickness(8, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    private bool _filling;

    public LevelChoiceRow()
    {
        Margin = new Thickness(0, 0, 0, 8);
        Visibility = Visibility.Collapsed;

        TextBlock heading = new() { Text = LevelControl.Heading, VerticalAlignment = VerticalAlignment.Center };
        SetDock(heading, Dock.Left);
        Children.Add(heading);
        Children.Add(_levels);

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
                Visibility = Visibility.Collapsed;
                return;
            }

            foreach (DownloadLevelOption option in control.Options)
            {
                _levels.Items.Add(new ComboBoxItem { Content = option.Label, ToolTip = option.ToolTip, Tag = option.Level });
            }

            _levels.SelectedIndex = control.SelectedIndex;
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
