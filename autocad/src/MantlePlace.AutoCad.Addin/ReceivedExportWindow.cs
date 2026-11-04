using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MantlePlace.AutoCad;
using MantlePlace.Revit.Core;

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
/// </remarks>
internal sealed class ReceivedExportWindow : Window
{
    internal ReceivedExportWindow(HostExport export, string zipPath, IntPtr hostWindow)
    {
        ExportId = export.Id;

        Title = ExportReceipt.WindowTitle;
        Width = 460;
        Height = 230 + BrandChrome.HeaderHeight;
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
        folder.Click += (_, _) => ShowInFolder(zipPath);
        _import.Click += (_, _) =>
        {
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
        _text = new TextBlock { Text = ExportReceipt.Describe(export, zipPath), TextWrapping = TextWrapping.Wrap };
        root.Children.Add(new ScrollViewer { Content = _text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }

    private readonly Button _import;
    private readonly TextBlock _text;

    /// <summary>Import was pressed. Raised once: the button stays off afterwards.</summary>
    internal event EventHandler? ImportRequested;

    /// <summary>The export this window was opened for.</summary>
    internal string ExportId { get; }

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
