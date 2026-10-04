using System.Windows;
using System.Windows.Controls;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// The level column: a plain dropdown on each row whose category publishes levels, and one set-all
/// control above the rows. What each lists, what can be chosen and what a choice does are the
/// checklist's (<see cref="ImportChecklist.LevelOptions"/>); this file only binds them.
/// </summary>
/// <remarks>
/// Plain on purpose: visual polish waits for the web redesign, so these are stock WPF combo boxes
/// whose items are the checklist's own labels. A level this plugin cannot import is listed and
/// disabled, so the four always read the same and a row never offers a choice that would import
/// the whole file in its place.
/// </remarks>
internal sealed partial class ImportWindow
{
    private readonly Dictionary<ImportLayer, ComboBox> _levelBoxes = [];
    private readonly ComboBox _setAllLevels = new() { MinWidth = 120, Margin = new Thickness(8, 0, 0, 0) };

    /// <summary>Wide enough for a level's label and its estimate beside the layer's name.</summary>
    private const double WidthWithLevels = 760;

    /// <summary>The third column: a level dropdown on each row that offers levels.</summary>
    private void AddLevelColumn(Grid rows)
    {
        rows.ColumnDefinitions.Insert(1, new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rows.ColumnDefinitions[0].Width = GridLength.Auto;

        for (int index = 0; index < _checklist.Layers.Count; index++)
        {
            ImportLayer layer = _checklist.Layers[index];
            if (!_checklist.HasLevels(layer))
            {
                continue;
            }

            ComboBox levels = new() { Margin = new Thickness(0, 2, 12, 2), HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (LevelOption option in _checklist.LevelOptions(layer))
            {
                levels.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Level, IsEnabled = option.IsAvailable });
            }

            levels.SelectionChanged += (_, _) => OnLevelChanged(layer, levels);
            Grid.SetRow(levels, index);
            Grid.SetColumn(levels, 1);
            rows.Children.Add(levels);
            _levelBoxes[layer] = levels;
        }

        // The notes beside a disabled box move one column right, past the levels.
        foreach (TextBlock note in _notes.Values)
        {
            Grid.SetColumn(note, 2);
        }
    }

    /// <summary>The heading row over the checklist: Include, and the set-all control at the right.</summary>
    private UIElement BuildLevelHeading()
    {
        foreach (FidelityLevel level in FidelityLevelNames.All)
        {
            _setAllLevels.Items.Add(new ComboBoxItem { Content = FidelityLevelNames.Token(level), Tag = level });
        }

        // Shown, never chosen: what the control says while the rows are not all at one level.
        _setAllLevels.Items.Add(new ComboBoxItem { Content = WindowLabels.MixedLevels, IsEnabled = false });
        _setAllLevels.SelectionChanged += (_, _) => OnSetAllLevels();

        DockPanel heading = new() { LastChildFill = false };
        TextBlock include = new() { Text = WindowLabels.IncludeHeading, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(include, Dock.Left);
        heading.Children.Add(include);

        StackPanel setAll = new() { Orientation = Orientation.Horizontal };
        setAll.Children.Add(new TextBlock { Text = WindowLabels.SetAllLevels, VerticalAlignment = VerticalAlignment.Center });
        setAll.Children.Add(_setAllLevels);
        DockPanel.SetDock(setAll, Dock.Right);
        heading.Children.Add(setAll);

        TextBlock levelHeading = new()
        {
            Text = WindowLabels.LevelHeading,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
        };
        DockPanel.SetDock(levelHeading, Dock.Right);
        heading.Children.Add(levelHeading);
        return heading;
    }

    private void OnLevelChanged(ImportLayer layer, ComboBox levels)
    {
        if (_refreshing || levels.SelectedItem is not ComboBoxItem { Tag: FidelityLevel level })
        {
            return;
        }

        _checklist.SetLevel(layer, level);
        RefreshChecklist();
    }

    private void OnSetAllLevels()
    {
        if (_refreshing || _setAllLevels.SelectedItem is not ComboBoxItem { Tag: FidelityLevel level })
        {
            return;
        }

        _checklist.SetAllLevels(level);
        RefreshChecklist();
    }

    /// <summary>Paints each dropdown from the checklist, and the set-all control from what the rows share.</summary>
    private void RefreshLevels()
    {
        foreach ((ImportLayer layer, ComboBox levels) in _levelBoxes)
        {
            FidelityLevel chosen = _checklist.ChosenLevel(layer);
            levels.SelectedItem = levels.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is FidelityLevel level && level == chosen);
            levels.IsEnabled = !_begun && _checklist.IsChecked(layer);
        }

        if (_checklist.OffersLevels)
        {
            FidelityLevel? all = _checklist.AllLevels;
            _setAllLevels.SelectedItem = _setAllLevels.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => all is { } shared ? item.Tag is FidelityLevel level && level == shared : item.Tag is null);
            _setAllLevels.IsEnabled = !_begun;
        }
    }
}
