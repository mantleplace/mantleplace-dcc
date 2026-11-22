using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using MantlePlace.Core;
using MantlePlace.Wpf;

namespace MantlePlace.Revit.Addin;

internal static class RibbonImagery
{
    private const int SmallSlot = 16;

    private const int LargeSlot = 32;

    private const double BaselineDpi = 96.0;

    private static readonly List<(RibbonButton Button, RibbonGlyph? Glyph)> Given = [];

    private static readonly List<(RibbonButton Button, Vignette Vignette)> GivenVignettes = [];

    private static readonly Dictionary<RibbonButton, string> Badges = [];

    private static double? _displayScale;

    internal static void Give(RibbonButton button, RibbonGlyph glyph) => Remember(button, glyph);

    internal static void GiveMark(RibbonButton button) => Remember(button, null);

    internal static void GiveVignette(RibbonButton button, Vignette vignette)
    {
        ArgumentNullException.ThrowIfNull(button);

        GivenVignettes.Add((button, vignette));
        ApplyVignette(button, vignette, CurrentTheme());
    }

    internal static void Retheme()
    {
        RibbonTheme theme = CurrentTheme();
        double scale = DisplayScale();

        foreach ((RibbonButton button, RibbonGlyph? glyph) in Given)
        {
            Apply(button, glyph, theme, scale);
        }

        foreach ((RibbonButton button, Vignette vignette) in GivenVignettes)
        {
            ApplyVignette(button, vignette, theme);
        }
    }

    internal static void SetBadge(RibbonButton button, string? text)
    {
        ArgumentNullException.ThrowIfNull(button);

        if (text is null)
        {
            Badges.Remove(button);
        }
        else
        {
            Badges[button] = text;
        }

        foreach ((RibbonButton given, RibbonGlyph? glyph) in Given)
        {
            if (ReferenceEquals(given, button))
            {
                Apply(given, glyph, CurrentTheme(), DisplayScale());
            }
        }
    }

    internal static void Forget()
    {
        Given.Clear();
        GivenVignettes.Clear();
        Badges.Clear();
        ResourceImages.Forget();
        _displayScale = null;
    }

    internal static RibbonTheme CurrentTheme()
        => UIThemeManager.CurrentTheme == UITheme.Dark ? RibbonTheme.Dark : RibbonTheme.Light;

    private static void Remember(RibbonButton button, RibbonGlyph? glyph)
    {
        ArgumentNullException.ThrowIfNull(button);

        Given.Add((button, glyph));
        Apply(button, glyph, CurrentTheme(), DisplayScale());
    }

    private static void Apply(RibbonButton button, RibbonGlyph? glyph, RibbonTheme theme, double scale)
    {
        string NameFor(int slot) => glyph is { } command
            ? RibbonGlyphs.FileNameFor(command, theme, slot, scale)
            : MarkRenders.FileNameFor(slot, scale);

        string? badge = Badges.TryGetValue(button, out string? held) ? held : null;

        try
        {
            button.Image = Badged(ResourceImages.Decode(NameFor(SmallSlot)), badge, SmallSlot);
            button.LargeImage = Badged(ResourceImages.Decode(NameFor(LargeSlot)), badge, LargeSlot);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
        }
    }

    private static void ApplyVignette(RibbonButton button, Vignette vignette, RibbonTheme theme)
    {
        try
        {
            button.ToolTipImage = ResourceImages.Decode(Vignettes.FileNameOf(vignette, theme));
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
        }
    }

    private static ImageSource? Badged(ImageSource? image, string? badge, int slot)
    {
        if (badge is null || image is not BitmapSource bitmap)
        {
            return image;
        }

        double width = bitmap.Width;
        double height = bitmap.Height;
        double diameter = width * VaultBadge.DiscFraction;
        Point centre = new(width - (diameter / 2), diameter / 2);

        DrawingVisual visual = new();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawImage(bitmap, new Rect(0, 0, width, height));
            context.DrawEllipse(BrandChrome.Frozen(BrandPalette.Mantle), null, centre, diameter / 2, diameter / 2);

            if (VaultBadge.DrawsCount(slot))
            {
                FormattedText count = new(
                    badge,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    diameter * (badge.Length > 1 ? 0.55 : 0.72),
                    BrandChrome.Frozen(BrandPalette.OnMantle),
                    bitmap.DpiX > 0 ? bitmap.DpiX / BaselineDpi : 1.0);
                context.DrawText(count, new Point(centre.X - (count.Width / 2), centre.Y - (count.Height / 2)));
            }
        }

        RenderTargetBitmap badged = new(
            bitmap.PixelWidth,
            bitmap.PixelHeight,
            bitmap.DpiX > 0 ? bitmap.DpiX : BaselineDpi,
            bitmap.DpiY > 0 ? bitmap.DpiY : BaselineDpi,
            PixelFormats.Pbgra32);
        badged.Render(visual);
        badged.Freeze();
        return badged;
    }

    private static double DisplayScale()
    {
        if (_displayScale is { } known)
        {
            return known;
        }

        double scale = 1.0;
        try
        {
            uint dpi = GetDpiForSystem();
            if (dpi > 0)
            {
                scale = dpi / BaselineDpi;
            }
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (DllNotFoundException)
        {
        }

        _displayScale = scale;
        return scale;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
