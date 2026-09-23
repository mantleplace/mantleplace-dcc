using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;
using MantlePlace.Revit.Core;

namespace MantlePlace.Revit.Addin;

/// <summary>
/// Puts an image on a ribbon button, and puts a different one there when Revit changes theme.
/// </summary>
/// <remarks>
/// <para>
/// The impure half of the ribbon's imagery: a Win32 call for the display scale, and a list of the
/// buttons that have to be repainted. The pack URI and the WPF decode behind it are
/// <see cref="ResourceImages"/>, shared with the windows' <c>BrandChrome</c>. Every decision it acts
/// on —
/// <em>which</em> file, for which theme, at which size — is <see cref="RibbonGlyphs"/>,
/// <see cref="MarkRenders"/> and <see cref="Vignettes"/> in the pure core, where it is asserted
/// without Revit (<c>HPS-02</c>, <c>HPS-42</c>). Nothing here chooses anything.
/// </para>
/// <para>
/// Three kinds of picture, on two properties. A <b>glyph</b> and the <b>mark</b> go on
/// <c>Image</c>/<c>LargeImage</c> and are picked from a size ladder; a <b>vignette</b> goes on
/// <c>ToolTipImage</c> and has no ladder at all, because Revit caps that one at 355 px
/// (<see cref="Vignettes.MaxPixels"/>) and there is no headroom for a second size to choose between.
/// </para>
/// </remarks>
internal static class RibbonImagery
{
    /// <summary>The two slots a Revit ribbon button has, in logical pixels.</summary>
    private const int SmallSlot = 16;

    private const int LargeSlot = 32;

    /// <summary>What Windows reports at 100%.</summary>
    private const double BaselineDpi = 96.0;

    /// <summary>
    /// Every button that has been given an image, and what it draws.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>Retained for the same reason the Account button is</b> (see
    /// <c>MantlePlaceApplication._accountButton</c>): a ribbon item is only ever as changeable as the
    /// reference somebody kept, and a theme swap is exactly a change to every item at once. A null
    /// glyph is the mark, which is the Account panel's imagery and has no theme of its own.
    /// </remarks>
    private static readonly List<(RibbonButton Button, RibbonGlyph? Glyph)> Given = [];

    /// <summary>
    /// Every button that has been given a tooltip vignette, and which one.
    /// </summary>
    /// <remarks>
    /// ⛔ <b>A second list rather than a third case in the one above.</b> That tuple already carries
    /// two meanings in a nullable — a glyph, or the mark — and a vignette is a third thing entirely:
    /// a different property on the item, one file instead of a ladder, and two of the five buttons
    /// rather than all of them. A button with a vignette also has a glyph, so folding them together
    /// would mean either listing that button twice or widening the tuple until most entries carry a
    /// field that is null. What the two lists genuinely share is the repaint trigger, and that is
    /// <see cref="Retheme"/>, not a shape.
    /// </remarks>
    private static readonly List<(RibbonButton Button, Vignette Vignette)> GivenVignettes = [];

    /// <summary>
    /// The badge each button carries over its picture, when it carries one (<see cref="VaultBadge"/>).
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="Given"/> and applied inside <see cref="Apply"/>, so a theme change
    /// repaints the badge with the glyph instead of wiping it: the two are one picture on the button.
    /// </remarks>
    private static readonly Dictionary<RibbonButton, string> Badges = [];

    private static double? _displayScale;

    /// <summary>Gives <paramref name="button"/> a command's glyph, in the current theme.</summary>
    internal static void Give(RibbonButton button, RibbonGlyph glyph) => Remember(button, glyph);

    /// <summary>Gives <paramref name="button"/> the Mantle Place mark.</summary>
    /// <remarks>
    /// The mark is one artwork in both themes: it is a white monogram on the brand's orange tile, and
    /// it carries its own background, so it reads on the light ribbon and the dark one alike. That is
    /// why it is exempt from the theme swap rather than missing from it
    /// (<c>docs/adr/0009-host-assets-render-the-monogram.md</c>).
    /// </remarks>
    internal static void GiveMark(RibbonButton button) => Remember(button, null);

    /// <summary>
    /// Gives <paramref name="button"/> the picture Revit shows under its long description.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not every button gets one, and the two that do are <see cref="Vignettes.All"/>. The Account
    /// face could not have one even if it were wanted: Revit's API says <em>"SplitButton and
    /// RadioButtonGroup cannot display the tooltip set by this method"</em>, and that face is a push
    /// button inside a split button.
    /// </para>
    /// <para>
    /// Set through <c>RibbonItem.ToolTipImage</c> rather than on the <c>PushButtonData</c>, so that a
    /// theme change reaches it. Both properties exist; only this one is reachable after the ribbon is
    /// built.
    /// </para>
    /// </remarks>
    internal static void GiveVignette(RibbonButton button, Vignette vignette)
    {
        ArgumentNullException.ThrowIfNull(button);

        GivenVignettes.Add((button, vignette));
        ApplyVignette(button, vignette, CurrentTheme());
    }

    /// <summary>
    /// Repaints every button given so far, for whatever theme Revit is in now.
    /// </summary>
    /// <remarks>
    /// Cheap enough to do unconditionally: the decoded images are cached and frozen, so a theme flip
    /// is twenty property assignments over nine buttons and no new decode — eighteen of them images
    /// on the two ribbon slots, two of them vignettes on <c>ToolTipImage</c>.
    /// </remarks>
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

    /// <summary>
    /// Draws <paramref name="text"/> in a badge over <paramref name="button"/>'s picture, or takes the
    /// badge off when it is <c>null</c>. Revit's UI thread only.
    /// </summary>
    /// <remarks>
    /// The button must have been given a glyph first. What the badge says is
    /// <see cref="VaultBadge.CountText"/>; this only draws it.
    /// </remarks>
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

    /// <summary>Drops every retained reference. Called from <c>OnShutdown</c> and nowhere else.</summary>
    internal static void Forget()
    {
        Given.Clear();
        GivenVignettes.Clear();
        Badges.Clear();
        ResourceImages.Forget();
        _displayScale = null;
    }

    /// <summary>Revit's current UI theme, as the pure core spells it.</summary>
    /// <remarks>
    /// <c>UIThemeManager</c> has existed since Revit 2024, so the 2025 compile target covers 2025, 2026 and
    /// 2027 with one code path and no version check.
    /// </remarks>
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
            // Swallowed for the same reason ApplyAccountState swallows its assignments: this runs on
            // a theme change, which can arrive while Revit is tearing the ribbon down, and a fault
            // dialog raised over a button image would be worse than the stale image it replaces.
        }
    }

    /// <summary>
    /// Puts the vignette for <paramref name="theme"/> on <paramref name="button"/>.
    /// </summary>
    /// <remarks>
    /// No display scale, because there is nothing to pick: Revit caps a tooltip image at 355 px on
    /// its longest side (<see cref="Vignettes.MaxPixels"/>), which leaves no room for a second,
    /// larger render to choose between. One file per theme, and Revit softens it on a scaled display.
    /// </remarks>
    private static void ApplyVignette(RibbonButton button, Vignette vignette, RibbonTheme theme)
    {
        try
        {
            button.ToolTipImage = ResourceImages.Decode(Vignettes.FileNameOf(vignette, theme));
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            // Swallowed for the reason Apply swallows its assignments: a theme change can arrive
            // while Revit is tearing the ribbon down, and a fault dialog raised over a tooltip
            // picture would be worse than the stale picture it replaces.
        }
    }

    /// <summary>
    /// <paramref name="image"/> with a brand-orange disc in its top-right corner, carrying
    /// <paramref name="badge"/> where the slot is large enough to read it.
    /// </summary>
    /// <remarks>
    /// Drawn at run time rather than committed, because the count is not known until run time and
    /// every committed variant would be another binary in a repository that counts them. The decoded
    /// source stays cached and untouched; the badged copy is rendered at its pixel size and frozen, as
    /// the decoded ones are.
    /// </remarks>
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

    /// <summary>
    /// The display's scale factor — 1.0 at 100%, 1.5 at 150%.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Read once. The ribbon is built once, at startup, and Revit does not rebuild it when a window
    /// is dragged to a second monitor — so a per-monitor answer would be a more precise reading of a
    /// question the ribbon cannot act on.
    /// </para>
    /// <para>
    /// The system DPI rather than anything from WPF because there is no visual to ask: this is called
    /// from <c>OnStartup</c>, before any Mantle Place window exists. <c>GetDpiForSystem</c> has been
    /// in <c>user32</c> since Windows 10 1607, which every Revit 2025 host predates by years; the
    /// catch is there so a future Windows that retires it costs a blurry icon rather than a plugin
    /// that will not start.
    /// </para>
    /// </remarks>
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

    // DllImport rather than the LibraryImport source generator: that one requires
    // AllowUnsafeBlocks across the whole assembly, which is a large door to open for one call that
    // takes nothing and returns a number.
    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();
}
