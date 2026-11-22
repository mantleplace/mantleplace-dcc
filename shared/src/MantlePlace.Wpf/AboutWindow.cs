using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using MantlePlace.Client;
using MantlePlace.Core;

namespace MantlePlace.Wpf;

public sealed record HostCommand(string Face, string ToolTip, Action Run);

public sealed class AboutWindow : Window
{
    public AboutWindow(string body, string privacyUrl, IntPtr hostWindow, params HostCommand[] commands)
    {
        ArgumentNullException.ThrowIfNull(commands);

        Title = AboutPage.Title;
        BrandChrome.ShowMarkInTitleBar(this);
        Width = 520;

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
        _ = ShellLauncher.TryOpen(address, out _);
    }
}
