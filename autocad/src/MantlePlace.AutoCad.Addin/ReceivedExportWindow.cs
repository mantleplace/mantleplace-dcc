using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MantlePlace.AutoCad;
using MantlePlace.Client;
using MantlePlace.Core;
using MantlePlace.Wpf;

namespace MantlePlace.AutoCad.Addin;

/// <summary>
/// The window an export from the web vault opens in AutoCAD: it says the bundle arrived and where it
/// is, and its Import button places the whole order in the active drawing (<see cref="ExportReceipt"/>).
/// </summary>
/// <remarks>
/// The Revit plugin opens its import window at this point. This is the same moment with one press
/// instead of a checklist: modeless, owned by AutoCAD's main window. Pressing Import raises
/// <see cref="ImportRequested"/> for the host that opened it, which runs the import and hands back what
/// happened (<see cref="ShowOutcome"/>); closing the window before the press is what ends the export as
/// cancelled, reported by that host.
/// <para>
/// A 3.0 order shows the level control every host shows (<see cref="LevelChoiceRow"/>), on the level
/// the send was fetched at: the recommendation, since a send names no level. Another choice is
/// downloaded into the slot before Import, which stays off until it is there (<see cref="LevelSwitch"/>);
/// a download that fails leaves the control on the level the slot still holds.
/// </para>
/// </remarks>
internal sealed class ReceivedExportWindow : Window
{
    /// <param name="export">The export.</param>
    /// <param name="zipPath">The bundle in the cache.</param>
    /// <param name="hostWindow">AutoCAD's main window, which owns this one.</param>
    /// <param name="levels">A 3.0 order's level control, on the level in the slot; <c>null</c> for a 2.x order.</param>
    /// <param name="fetchLevel">Downloads the order at a chosen level into the slot; on the UI thread's context.</param>
    internal ReceivedExportWindow(
        HostExport export,
        string zipPath,
        IntPtr hostWindow,
        LevelControl? levels = null,
        Func<FidelityLevel, Task<LevelFetch>>? fetchLevel = null)
    {
        ExportId = export.Id;
        ZipPath = zipPath;
        _levels = levels is not null && fetchLevel is not null ? new LevelSwitch(levels) : null;
        _fetchLevel = fetchLevel;

        Title = ExportReceipt.WindowTitle;
        BrandChrome.ShowMarkInTitleBar(this);
        Width = 460;
        Height = 230 + BrandChrome.HeaderHeight + (_levels is null ? 0 : LevelRowHeight);
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        new WindowInteropHelper(this) { Owner = hostWindow };

        Button folder = new()
        {
            Content = ExportReceipt.ShowFolder,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 4, 12, 4),
            IsEnabled = File.Exists(zipPath),
        };
        _import = new()
        {
            Content = ExportReceipt.ImportButton,
            Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(12, 4, 12, 4),
            IsDefault = true,
            IsEnabled = File.Exists(zipPath),
        };
        Button close = new() { Content = WindowLabels.Close, Padding = new Thickness(12, 4, 12, 4), IsCancel = true };
        folder.Click += (_, _) => ShowInFolder(ZipPath);
        _import.Click += (_, _) =>
        {
            if (_levels is { CanImport: false })
            {
                return;
            }

            _importPressed = true;
            _levelRow.IsEnabled = false;
            _import.IsEnabled = false;
            ImportRequested?.Invoke(this, EventArgs.Empty);
        };
        close.Click += (_, _) => Close();

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(folder);
        buttons.Children.Add(_import);
        buttons.Children.Add(close);

        DockPanel root = new() { Margin = new Thickness(16) };
        BrandChrome.AddHeader(root, ExportReceipt.Heading);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        _levelRow.Show(_levels?.Control);
        _levelRow.Chosen += level => _ = OnLevelChosenAsync(level);
        DockPanel.SetDock(_levelRow, Dock.Top);
        root.Children.Add(_levelRow);
        _text = new TextBlock { Text = ExportReceipt.Describe(export, zipPath), TextWrapping = TextWrapping.Wrap };
        root.Children.Add(new ScrollViewer { Content = _text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    /// <summary>Roughly what the level row takes, so the receipt keeps its room. An estimate, not a measurement.</summary>
    private const double LevelRowHeight = 32;

    private readonly Button _import;
    private readonly TextBlock _text;
    private readonly LevelChoiceRow _levelRow = new();
    private readonly LevelSwitch? _levels;
    private readonly Func<FidelityLevel, Task<LevelFetch>>? _fetchLevel;
    private bool _importPressed;

    /// <summary>Import was pressed. Raised once: the button stays off afterwards.</summary>
    internal event EventHandler? ImportRequested;

    /// <summary>The export this window was opened for.</summary>
    internal string ExportId { get; }

    /// <summary>The bundle Import reads: the slot's zip, at the level the control shows.</summary>
    internal string ZipPath { get; private set; }

    /// <summary>
    /// Another level, before Import: Import and the control go dark while it downloads into the slot,
    /// and come back on the new level, or on the one the slot still holds when the download fails.
    /// </summary>
    private async Task OnLevelChosenAsync(FidelityLevel level)
    {
        if (_levels is null || _fetchLevel is null || _importPressed)
        {
            return;
        }

        if (_levels.Choose(level) is not { } fetch)
        {
            _levelRow.Select(_levels.Control);
            return;
        }

        _levelRow.IsEnabled = false;
        _import.IsEnabled = false;
        _text.Text = WindowLabels.LevelDownloading(fetch);

        LevelFetch fetched = await _fetchLevel(fetch).ConfigureAwait(true);
        FidelityLevel held = _levels.Held;
        _levels.Fetched(fetched.Ready);
        _levelRow.Select(_levels.Control);
        _levelRow.IsEnabled = true;
        if (fetched.Ready)
        {
            ZipPath = fetched.ZipPath;
            _text.Text = WindowLabels.LevelDownloaded(fetch);
        }
        else
        {
            _text.Text = WindowLabels.LevelNotDownloaded(fetch, held, fetched.Message);
        }

        _import.IsEnabled = _levels.CanImport && File.Exists(ZipPath);
    }

    /// <summary>Replaces the receipt with what the import did: what was placed, what was skipped, and why.</summary>
    internal void ShowOutcome(string summary)
    {
        _text.Text = summary;
        Height = Math.Max(Height, 360 + BrandChrome.HeaderHeight);
        ResizeMode = ResizeMode.CanResize;
    }

    private static void ShowInFolder(string zipPath)
    {
        // Explorer with the file selected; a path with a space is quoted, and the shell is not asked
        // to interpret it. A failure to start is not worth a dialog over a convenience button.
        try
        {
            _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{zipPath}\"") { UseShellExecute = false });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
