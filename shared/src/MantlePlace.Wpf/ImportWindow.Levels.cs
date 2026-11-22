using System.Windows;
using System.Windows.Controls;
using MantlePlace.Core;

namespace MantlePlace.Wpf;

internal sealed partial class ImportWindow
{
    private readonly Dictionary<ImportLayer, ComboBox> _levelBoxes = [];
    private readonly ComboBox _setAllLevels = new() { MinWidth = 120, Margin = new Thickness(8, 0, 0, 0) };

    private const double WidthWithLevels = 760;

    private void AddLevelColumn(ImportChecklist checklist, Grid rows)
    {
        rows.ColumnDefinitions.Insert(1, new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rows.ColumnDefinitions[0].Width = GridLength.Auto;

        for (int index = 0; index < checklist.Layers.Count; index++)
        {
            ImportLayer layer = checklist.Layers[index];
            if (!checklist.HasLevels(layer))
            {
                continue;
            }

            ComboBox levels = new() { Margin = new Thickness(0, 2, 12, 2), HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (LevelOption option in checklist.LevelOptions(layer))
            {
                levels.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option.Level, IsEnabled = option.IsAvailable });
            }

            levels.SelectionChanged += (_, _) => OnLevelChanged(layer, levels);
            Grid.SetRow(levels, index);
            Grid.SetColumn(levels, 1);
            rows.Children.Add(levels);
            _levelBoxes[layer] = levels;
        }

        foreach (TextBlock note in _notes.Values)
        {
            Grid.SetColumn(note, 2);
        }
    }

    private UIElement BuildLevelHeading()
    {
        foreach (FidelityLevel level in FidelityLevelNames.All)
        {
            _setAllLevels.Items.Add(new ComboBoxItem { Content = FidelityLevelNames.Token(level), Tag = level });
        }

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
        if (_refreshing || _checklist is null || levels.SelectedItem is not ComboBoxItem { Tag: FidelityLevel level })
        {
            return;
        }

        _checklist.SetLevel(layer, level);
        RefreshChecklist();
    }

    private void OnSetAllLevels()
    {
        if (_refreshing || _checklist is null || _setAllLevels.SelectedItem is not ComboBoxItem { Tag: FidelityLevel level })
        {
            return;
        }

        _checklist.SetAllLevels(level);
        RefreshChecklist();
    }

    private void RefreshLevels(ImportChecklist checklist)
    {
        foreach ((ImportLayer layer, ComboBox levels) in _levelBoxes)
        {
            FidelityLevel chosen = checklist.ChosenLevel(layer);
            levels.SelectedItem = levels.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is FidelityLevel level && level == chosen);
            levels.IsEnabled = _state.ChecklistEditable && checklist.IsChecked(layer);
        }

        if (checklist.OffersLevels)
        {
            FidelityLevel? all = checklist.AllLevels;
            _setAllLevels.SelectedItem = _setAllLevels.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(item => all is { } shared ? item.Tag is FidelityLevel level && level == shared : item.Tag is null);
            _setAllLevels.IsEnabled = _state.ChecklistEditable;
        }
    }
}
