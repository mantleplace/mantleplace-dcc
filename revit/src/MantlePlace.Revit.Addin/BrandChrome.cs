using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// The two brand touches the plugin's windows share: the header row, and the fill on the one primary
/// action.
/// </summary>
/// <remarks>
/// <para>
/// Both windows had nothing but a title bar saying who they belonged to. This puts the mark and the
/// window's purpose above the content, and the brand orange on exactly one button in the whole
/// plugin. <b>Everything else keeps Revit's own chrome</b> — no restyled list rows, no custom fonts,
/// no brand background. An add-in that paints its own windows stops looking like part of the host,
/// which is the failure the ribbon pass exists to fix, not one to introduce here.
/// </para>
/// <para>
/// Shared rather than copied into each window because there are two of them and the second copy is
/// the one that drifts. What is decided here is WPF assembly and nothing else: the words are
/// <see cref="WindowLabels"/>, the colour is <see cref="BrandPalette"/>, and which render of the mark
/// goes in the <see cref="MarkRenders.HeaderSlotPixels"/> slot is <see cref="MarkRenders"/> — all
/// three in the pure core, where CI can see them (<c>HPS-02</c>, <c>HPS-42</c>). The decode itself is
/// <see cref="ResourceImages.Decode"/>, shared with the ribbon so the pack URI has one home.
/// </para>
/// </remarks>
internal static class BrandChrome
{
    /// <summary>Names the one element in the primary button's template that the triggers repaint.</summary>
    private const string FillName = "MantlePlaceFill";

    /// <summary>
    /// The primary action's look, built once and shared.
    /// </summary>
    /// <remarks>
    /// Built on first use, which is on Revit's UI thread because only a window reaches for it — a
    /// <see cref="Style"/> has thread affinity and could not be shared otherwise.
    /// </remarks>
    private static readonly Style PrimaryStyle = BuildPrimaryStyle();

    /// <summary>The gap between the header and the content beneath it.</summary>
    private const double HeaderGap = 12;

    /// <summary>
    /// What a header costs a window in logical pixels — what to make it taller by.
    /// </summary>
    /// <remarks>
    /// The mark is the tallest thing in the row: the heading is 18pt, which sets about 24 px. So the
    /// height is the mark's slot plus the gap, and a window that grows by exactly this keeps the
    /// content it showed before.
    /// </remarks>
    internal const double HeaderHeight = MarkRenders.HeaderSlotPixels + HeaderGap;

    /// <summary>
    /// Puts the mark and the window's purpose above everything else in <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// ⛔ Call it before adding anything else. A <see cref="DockPanel"/> lays its children out in the
    /// order they were added, so a header added second docks below whatever went first — which is how
    /// a header ends up under a button row.
    /// </remarks>
    /// <param name="root">The window's root panel, still empty.</param>
    /// <param name="heading">The window's purpose — one of <see cref="WindowLabels"/>'s headings.</param>
    internal static void AddHeader(DockPanel root, string heading)
    {
        ArgumentNullException.ThrowIfNull(root);

        HeaderRow header = new(heading);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Insert(0, header);
    }

    /// <summary>
    /// Marks <paramref name="button"/> as the window's one primary action and fills it with the brand
    /// orange.
    /// </summary>
    /// <remarks>
    /// ⛔ Setting <c>Background</c> alone does not hold. Revit 2025's WPF ships the Aero2 theme, whose
    /// default <c>Button</c> template replaces the background with a theme brush on hover and on
    /// press — so a button painted that way is orange until the cursor touches it and blue-grey
    /// after. Replacing the template is the only way the colour survives an interaction, and it is
    /// still one button in one window rather than a restyle.
    /// </remarks>
    internal static void MakePrimary(Button button)
    {
        ArgumentNullException.ThrowIfNull(button);
        button.Style = PrimaryStyle;
    }

    private static Style BuildPrimaryStyle()
    {
        SolidColorBrush normal = Frozen(BrandPalette.Mantle);
        SolidColorBrush hovered = Frozen(BrandPalette.MixToWhite(BrandPalette.Mantle, BrandPalette.HoverMix));
        SolidColorBrush pressed = Frozen(BrandPalette.MixToWhite(BrandPalette.Mantle, BrandPalette.PressedMix));

        FrameworkElementFactory fill = new(typeof(Border), FillName);
        fill.SetValue(Border.BackgroundProperty, normal);

        // Aero2's own button is a rounded rectangle, and this one sits in a row beside three of
        // them. A square-cornered fill would read as a different kind of control rather than as the
        // same button in the brand's colour, which is the opposite of the point.
        fill.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));

        // The button keeps setting its own Padding, as every other button in the row does.
        fill.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        fill.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        FrameworkElementFactory content = new(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

        // Aero2's own template sets this, and a replaced template that does not would draw a literal
        // underscore the day a face gains an access key.
        content.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
        fill.AppendChild(content);

        ControlTemplate template = new(typeof(Button)) { VisualTree = fill };
        template.Triggers.Add(Repaint(UIElement.IsMouseOverProperty, true, Border.BackgroundProperty, hovered));
        template.Triggers.Add(Repaint(ButtonBase.IsPressedProperty, true, Border.BackgroundProperty, pressed));

        // A replaced template replaces the theme's disabled look too. Without this a disabled primary
        // action would sit there at full brand orange, reading as the most clickable thing on screen.
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

    private static SolidColorBrush Frozen(BrandColour colour)
    {
        SolidColorBrush brush = new(Color.FromRgb(colour.R, colour.G, colour.B));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The mark and the window's purpose, on one row.
    /// </summary>
    /// <remarks>
    /// A type rather than a builder method because the mark has to be re-chosen when the display
    /// scale changes: WPF hands an <see cref="Image"/> logical pixels and scales whatever is in it, so
    /// the render that is crisp at 100% is a blurred square at 200% (<see cref="MarkRenders"/>). The
    /// scale is not knowable in the constructor — the window is not in a tree yet — so it is read at
    /// <see cref="FrameworkElement.Loaded"/> and again whenever the window crosses to a monitor at a
    /// different scale.
    /// </remarks>
    private sealed class HeaderRow : StackPanel
    {
        private readonly Image _mark = new()
        {
            Width = MarkRenders.HeaderSlotPixels,
            Height = MarkRenders.HeaderSlotPixels,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        };

        /// <summary>The render currently in <see cref="_mark"/>, so an unchanged scale re-decodes nothing.</summary>
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

            // 100% until the real scale is known, so the header is never briefly empty.
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

            if (ResourceImages.Decode(fileName) is not { } source)
            {
                return;
            }

            _mark.Source = source;
            _showing = fileName;
        }
    }
}
