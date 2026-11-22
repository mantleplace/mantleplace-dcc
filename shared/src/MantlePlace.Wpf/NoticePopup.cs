using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MantlePlace.Core;

namespace MantlePlace.Wpf;

public sealed class NoticePopup : Window
{
    private readonly DispatcherTimer _timer;

    public NoticePopup(string? selectOrderId, string text, IntPtr hostWindow)
    {
        SelectOrderId = selectOrderId;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = false;
        Focusable = false;
        Width = NoticeStack.Width;
        Height = NoticeStack.Height;
        Background = SystemColors.WindowBrush;
        BorderBrush = BrandChrome.Frozen(BrandPalette.Mantle);
        BorderThickness = new Thickness(1, 1, 1, 1);
        Cursor = Cursors.Hand;
        ToolTip = text;

        new WindowInteropHelper(this) { Owner = hostWindow };

        Content = BuildLayout(text);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(PrepareNotices.ShowSeconds) };
        _timer.Tick += (_, _) =>
        {
            if (!IsMouseOver)
            {
                Close();
            }
        };

        SourceInitialized += (_, _) => RefuseActivation();
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
        MouseLeftButtonUp += (_, _) => Chosen?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Chosen;

    public string? SelectOrderId { get; }

    public void PlaceAt(ScreenRect place)
    {
        Left = place.Left;
        Top = place.Top;
    }

    private UIElement BuildLayout(string text)
    {
        Button dismiss = new()
        {
            Content = "✕",
            ToolTip = WindowLabels.Close,
            Padding = new Thickness(6, 0, 6, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Focusable = false,
            Cursor = Cursors.Arrow,
            VerticalAlignment = VerticalAlignment.Top,
        };

        dismiss.PreviewMouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Close();
        };

        TextBlock heading = new()
        {
            Text = WindowLabels.NoticeTitle,
            FontWeight = FontWeights.SemiBold,
            Foreground = SystemColors.GrayTextBrush,
            Margin = new Thickness(0, 0, 0, 4),
        };

        TextBlock body = new()
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = SystemColors.WindowTextBrush,
        };

        DockPanel top = new();
        DockPanel.SetDock(dismiss, Dock.Right);
        top.Children.Add(dismiss);
        top.Children.Add(heading);

        DockPanel root = new() { Margin = new Thickness(12, 8, 6, 10) };
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(body);

        Border accent = new() { Width = 4, Background = BrandChrome.Frozen(BrandPalette.Mantle) };
        DockPanel frame = new();
        DockPanel.SetDock(accent, Dock.Left);
        frame.Children.Add(accent);
        frame.Children.Add(root);
        return frame;
    }

    private void RefuseActivation()
    {
        WindowStyles.Add(new WindowInteropHelper(this).Handle, WindowStyles.NoActivate | WindowStyles.ToolWindow);
    }
}
