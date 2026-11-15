using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.Wpf;

/// <summary>
/// A command of the host's own that a shared window offers beside its own buttons: Revit's About
/// offers its import logs. The window shows it and calls it; what it does is the host's.
/// </summary>
/// <param name="Face">The words on its button.</param>
/// <param name="ToolTip">One line on what it does.</param>
/// <param name="Run">What pressing it does, on the window's thread.</param>
public sealed record HostCommand(string Face, string ToolTip, Action Run);

/// <summary>
/// The About window (<c>HPS-51</c>'s <c>About Mantle Place</c>): which build is loaded, what it is
/// running in, the session, and a link to the privacy page on mantle.place. The only logo in it is the
/// monogram (<see cref="BrandChrome"/>); no window shows a host's.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>It exists to answer "which build?" before anything else.</b> Host API calls compile but are
/// proven only by running them, so the first question about any of them throwing is which assembly
/// threw, and the artifact that reaches us is a log file from a machine we cannot inspect. This puts
/// the answer where a curator can read it out over a call.
/// </para>
/// <para>
/// What it says is the host's to read and the pure core's to order (<see cref="AboutPage"/>); this is
/// the window around it, the same in every host. A host's own commands go beside Close
/// (<see cref="HostCommand"/>). Modeless and owned by the host's main window, like the plugin's other
/// windows, so the host stays usable.
/// </para>
/// </remarks>
public sealed class AboutWindow : Window
{
    /// <param name="body">What the window says (<see cref="AboutPage.Body"/>).</param>
    /// <param name="privacyUrl">The privacy page (<see cref="AboutPage.PrivacyUrl"/>).</param>
    /// <param name="hostWindow">The host's main window, which owns this one.</param>
    /// <param name="commands">The host's own commands, shown before Close in the order given.</param>
    public AboutWindow(string body, string privacyUrl, IntPtr hostWindow, params HostCommand[] commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        Title = AboutPage.Title;
        BrandChrome.ShowMarkInTitleBar(this);
        Width = 520;

        // The host's body is as long as the host's facts are, so the height follows it rather than
        // clipping the last paragraph in one host and leaving a gap in the other.
        SizeToContent = SizeToContent.Height;
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

        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        foreach (HostCommand command in commands)
        {
            Button hostButton = new()
            {
                Content = command.Face,
                ToolTip = command.ToolTip,
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0),
            };
            hostButton.Click += (_, _) => command.Run();
            buttons.Children.Add(hostButton);
        }

        Button close = new() { Content = WindowLabels.Close, Padding = new Thickness(12, 4, 12, 4), IsDefault = true, IsCancel = true };
        close.Click += (_, _) => Close();
        buttons.Children.Add(close);

        DockPanel root = new() { Margin = new Thickness(16) };
        BrandChrome.AddHeader(root, AboutPage.Heading(WindowLabels.HostName(HostIdentity.Id)));
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

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
