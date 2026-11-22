using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MantlePlace.Core;

namespace MantlePlace.Wpf;

public static class BrandChrome
{
    private const string FillName = "MantlePlaceFill";

    [ThreadStatic]
    private static Style? _primaryStyle;

    private const double HeaderGap = 12;

    public const double HeaderHeight = MarkRenders.HeaderSlotPixels + HeaderGap;

    public static void AddHeader(DockPanel root, string heading)
    {
        ArgumentNullException.ThrowIfNull(root);

        HeaderRow header = new(heading);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Insert(0, header);
    }

    public static void ShowMarkInTitleBar(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (TitleBarIcon() is { } icon)
        {
            window.Icon = icon;
        }
    }

    private static BitmapFrame? TitleBarIcon()
    {
        TiffBitmapEncoder encoder = new();
        foreach (int size in MarkRenders.Sizes)
        {
            if (MarkImages.Decode(MarkRenders.FileNameOf(size)) is BitmapSource render)
            {
                encoder.Frames.Add(BitmapFrame.Create(render));
            }
        }

        if (encoder.Frames.Count == 0)
        {
            return null;
        }

        System.IO.MemoryStream stream = new();
        encoder.Save(stream);
        stream.Position = 0;
        return new TiffBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
    }

    public static void MakePrimary(Button button)
    {
        ArgumentNullException.ThrowIfNull(button);
        button.Style = _primaryStyle ??= BuildPrimaryStyle();
    }

    private static Style BuildPrimaryStyle()
    {
        SolidColorBrush normal = Frozen(BrandPalette.Mantle);
        SolidColorBrush hovered = Frozen(BrandPalette.MixToWhite(BrandPalette.Mantle, BrandPalette.HoverMix));
        SolidColorBrush pressed = Frozen(BrandPalette.MixToWhite(BrandPalette.Mantle, BrandPalette.PressedMix));

        FrameworkElementFactory fill = new(typeof(Border), FillName);
        fill.SetValue(Border.BackgroundProperty, normal);

        fill.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));

        fill.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        fill.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        FrameworkElementFactory content = new(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        fill.AppendChild(content);

        ControlTemplate template = new(typeof(Button)) { VisualTree = fill };
        template.Triggers.Add(Repaint(UIElement.IsMouseOverProperty, true, Border.BackgroundProperty, hovered));
        template.Triggers.Add(Repaint(ButtonBase.IsPressedProperty, true, Border.BackgroundProperty, pressed));

        template.Triggers.Add(Repaint(UIElement.IsEnabledProperty, false, UIElement.OpacityProperty, 0.45));

        Style style = new(typeof(Button));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.ForegroundProperty, Frozen(BrandPalette.OnMantle)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        return style;
    }

    private static Trigger Repaint(DependencyProperty when, object value, DependencyProperty set, object to)
    {
        Trigger trigger = new() { Property = when, Value = value };
        trigger.Setters.Add(new Setter(set, to, FillName));
        return trigger;
    }

    public static SolidColorBrush Frozen(BrandColour colour)
    {
        SolidColorBrush brush = new(Color.FromRgb(colour.R, colour.G, colour.B));
        brush.Freeze();
        return brush;
    }

    private sealed class HeaderRow : StackPanel
    {
        private readonly Image _mark = new()
        {
            Width = MarkRenders.HeaderSlotPixels,
            Height = MarkRenders.HeaderSlotPixels,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };

        private string _showing = string.Empty;

        internal HeaderRow(string heading)
        {
            Orientation = Orientation.Horizontal;
            Margin = new Thickness(0, 0, 0, HeaderGap);

            RenderOptions.SetBitmapScalingMode(_mark, BitmapScalingMode.HighQuality);

            Children.Add(_mark);
            Children.Add(new TextBlock
            {
                Text = heading,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            });

            Show(1.0);
            Loaded += (_, _) => Show(VisualTreeHelper.GetDpi(this).DpiScaleX);
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            Show(newDpi.DpiScaleX);
        }

        private void Show(double displayScale)
        {
            string fileName = MarkRenders.FileNameFor(MarkRenders.HeaderSlotPixels, displayScale);
            if (string.Equals(fileName, _showing, StringComparison.Ordinal))
            {
                return;
            }

            if (MarkImages.Decode(fileName) is not { } source)
            {
                return;
            }

            _mark.Source = source;
            _showing = fileName;
        }
    }
}
