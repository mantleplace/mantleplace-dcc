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
/// is, and that nothing was imported (<see cref="ExportReceipt"/>).
/// </summary>
/// <remarks>
/// The Revit plugin opens its import window at this point. Importing is not in this plugin yet, so
/// this is the same moment with a different window: modeless, owned by AutoCAD's main window, and
/// closing it is what ends the export, reported by the host that opened it.
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
        Button close = new() { Content = WindowLabels.Close, Padding = new Thickness(12, 4, 12, 4), IsDefault = true };
        folder.Click += (_, _) => ShowInFolder(zipPath);
        close.Click += (_, _) => Close();

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        buttons.Children.Add(folder);
        buttons.Children.Add(close);

        DockPanel root = new() { Margin = new Thickness(16) };
        BrandChrome.AddHeader(root, ExportReceipt.Heading);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(new TextBlock { Text = ExportReceipt.Describe(export, zipPath), TextWrapping = TextWrapping.Wrap });
        Content = root;
    }

    /// <summary>The export this window was opened for.</summary>
    internal string ExportId { get; }

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
