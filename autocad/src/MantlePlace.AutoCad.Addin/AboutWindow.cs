using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using MantlePlace.AutoCad;
using MantlePlace.Revit.Client;
using MantlePlace.Revit.Core;

namespace MantlePlace.AutoCad.Addin;

/// <summary>
/// The About window: which build is loaded, whether the Civil 3D part is on, the session, and a link to
/// the privacy page on mantle.place. The only logo in it is the monogram (<see cref="BrandChrome"/>).
/// </summary>
/// <remarks>
/// What it says is assembled in the pure core (<see cref="AboutPage"/>); this is the window around it.
/// Modeless and owned by AutoCAD's main window, like the plugin's other windows, so AutoCAD stays usable.
/// </remarks>
internal sealed class AboutWindow : Window
{
    internal AboutWindow(string body, string privacyUrl, IntPtr hostWindow)
    {
        Title = AboutPage.Title;
        BrandChrome.ShowMarkInTitleBar(this);
        Width = 520;
        Height = 280 + BrandChrome.HeaderHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        new WindowInteropHelper(this) { Owner = hostWindow };

        Hyperlink link = new(new Run("Privacy at mantle.place")) { NavigateUri = new Uri(privacyUrl), ToolTip = privacyUrl };
        link.RequestNavigate += (_, args) =>
        {
            ShellOpen(args.Uri.AbsoluteUri);
            args.Handled = true;
        };

        TextBlock privacy = new() { Margin = new Thickness(0, 12, 0, 0) };
        privacy.Inlines.Add(link);

        Button close = new() { Content = WindowLabels.Close, Padding = new Thickness(12, 4, 12, 4), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        DockPanel root = new() { Margin = new Thickness(16) };
        BrandChrome.AddHeader(root, "Mantle Place for AutoCAD");
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);

        StackPanel text = new();
        text.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(privacy);
        root.Children.Add(text);
        Content = root;
    }

    private static void ShellOpen(string address)
    {
        // The default browser, through the shell launcher every other link uses. A refusal is a link
        // that does not open; the address is on screen in the window's link text's tooltip.
        _ = ShellLauncher.TryOpen(address, out _);
    }
}
