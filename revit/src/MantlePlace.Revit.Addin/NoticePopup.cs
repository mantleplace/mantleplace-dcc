using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// One notice — a Prepare that ended, or an order new in the vault: a small window in the corner of
/// Revit's that never takes focus.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>It must never take focus.</b> A curator modelling when a bundle finishes is typing into a
/// view, and a notice that grabs the keyboard eats their next keystroke. Revit has no notification
/// API of its own, so this is a WPF window made unable to activate twice over: <c>ShowActivated</c>
/// keeps showing it from activating, and <c>WS_EX_NOACTIVATE</c> keeps a click on it from doing so.
/// A click still reaches it; only activation is refused.
/// </para>
/// <para>
/// Owned by Revit's main window, so it stays with Revit — in front of it, minimised with it — and is
/// never a stray top-level window. Not topmost: another application in front of Revit stays in front.
/// </para>
/// <para>
/// A click anywhere but the close box opens the vault on the bundle; that is the only thing it leads
/// to. It closes itself after <see cref="PrepareNotices.ShowSeconds"/>, though not while the pointer
/// is on it — a curator reading it is not a curator who missed it.
/// </para>
/// </remarks>
internal sealed class NoticePopup : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private readonly DispatcherTimer _timer;

    /// <param name="selectOrderId">The order a click selects in the vault; <c>null</c> for none in particular.</param>
    /// <param name="text">What the notice says.</param>
    /// <param name="revitWindow">The window the notice belongs to.</param>
    internal NoticePopup(string? selectOrderId, string text, IntPtr revitWindow)
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

        new WindowInteropHelper(this) { Owner = revitWindow };

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

    /// <summary>The curator clicked the notice: open the vault on its bundle.</summary>
    internal event EventHandler? Chosen;

    /// <summary>The order a click selects in the vault, when the notice is about one.</summary>
    internal string? SelectOrderId { get; }

    /// <summary>Moves the notice to <paramref name="place"/>, in device-independent pixels.</summary>
    internal void PlaceAt(NoticeRect place)
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

        // Handled here so the click that closes the notice is not also the click that opens the vault.
        dismiss.PreviewMouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Close();
        };

        TextBlock heading = new()
        {
            Text = WindowLabels.VaultWindowTitle,
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

        // The brand orange as a strip down the leading edge: enough to say whose notice this is,
        // without painting a window that stops looking like part of the host (BrandChrome's rule).
        Border accent = new() { Width = 4, Background = BrandChrome.Frozen(BrandPalette.Mantle) };
        DockPanel frame = new();
        DockPanel.SetDock(accent, Dock.Left);
        frame.Children.Add(accent);
        frame.Children.Add(root);
        return frame;
    }

    private void RefuseActivation()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(handle, GwlExStyle);
        _ = SetWindowLong(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
    }

    // DllImport rather than LibraryImport, for RibbonImagery's reason: the generator needs
    // AllowUnsafeBlocks across the whole assembly.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
