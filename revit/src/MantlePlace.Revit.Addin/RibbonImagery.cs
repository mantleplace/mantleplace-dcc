using System.Runtime.InteropServices;
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
/// The impure half of the ribbon's imagery: pack URIs, WPF decoding, a Win32 call for the display
/// scale, and a list of the buttons that have to be repainted. Every decision it acts on —
/// <em>which</em> file, for which theme, at which size — is <see cref="RibbonGlyphs"/> and
/// <see cref="MarkRenders"/> in the pure core, where it is asserted without Revit
/// (<c>HPS-02</c>, <c>HPS-42</c>). Nothing here chooses anything.
/// </para>
/// <para>
/// ⚠ <b>The images are embedded resources, not files beside the DLL.</b> The build action in
/// <c>MantlePlace.Revit.Addin.csproj</c> is <c>Resource</c> and not <c>EmbeddedResource</c>, because
/// a pack URI resolves against the WPF resource table and <c>EmbeddedResource</c> does not populate
/// it. Baking them in is what keeps <c>Package-MantlePlaceRevit.ps1</c> free of a copy step, and
/// what stops a release whose ribbon is blank because one folder did not get zipped.
/// </para>
/// </remarks>
internal static class RibbonImagery
{
    /// <summary>The two slots a Revit ribbon button has, in logical pixels.</summary>
    private const int SmallSlot = 16;

    private const int LargeSlot = 32;

    /// <summary>The assembly's own resource table, as a pack URI stem.</summary>
    private const string PackStem = "pack://application:,,,/MantlePlace.Revit.Addin;component/Resources/";

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

    private static readonly Dictionary<string, ImageSource?> Decoded = new(StringComparer.Ordinal);

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
    /// Repaints every button given so far, for whatever theme Revit is in now.
    /// </summary>
    /// <remarks>
    /// Cheap enough to do unconditionally: the decoded images are cached and frozen, so a theme flip
    /// is nine property assignments and two dictionary hits.
    /// </remarks>
    internal static void Retheme()
    {
        RibbonTheme theme = CurrentTheme();
        double scale = DisplayScale();

        foreach ((RibbonButton button, RibbonGlyph? glyph) in Given)
        {
            Apply(button, glyph, theme, scale);
        }
    }

    /// <summary>Drops every retained reference. Called from <c>OnShutdown</c> and nowhere else.</summary>
    internal static void Forget()
    {
        Given.Clear();
        Decoded.Clear();
        _displayScale = null;
    }

    /// <summary>Revit's current UI theme, as the pure core spells it.</summary>
    /// <remarks>
    /// <c>UIThemeManager</c> has existed since Revit 2024, so the 2025 floor covers 2025, 2026 and
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

        try
        {
            button.Image = Load(NameFor(SmallSlot));
            button.LargeImage = Load(NameFor(LargeSlot));
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException)
        {
            // Swallowed for the same reason ApplyAccountState swallows its assignments: this runs on
            // a theme change, which can arrive while Revit is tearing the ribbon down, and a fault
            // dialog raised over a button image would be worse than the stale image it replaces.
        }
    }

    /// <summary>
    /// Decodes one render out of this assembly's resource table.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>OnLoad</c> and then <c>Freeze</c>: the stream is closed before this returns and the result
    /// is usable from any thread, which matters because a frozen <see cref="ImageSource"/> is the
    /// only kind a ribbon item will hold without complaint.
    /// </para>
    /// <para>
    /// A miss is cached as a miss. A render this assembly asks for and does not find is a defect the
    /// headless suite already fails on, so the remedy is a fixed build rather than a retry on every
    /// theme change.
    /// </para>
    /// </remarks>
    private static ImageSource? Load(string fileName)
    {
        if (Decoded.TryGetValue(fileName, out ImageSource? cached))
        {
            return cached;
        }

        ImageSource? image = null;
        try
        {
            // ⚠ Registers the "pack" URI scheme if nothing has yet, and this is the line that does
            // it: touching System.IO.Packaging.PackUriHelper is NOT enough — WPF's own registration
            // runs in Application's static constructor, and without it every pack URI here fails
            // with "The URI prefix is not recognized". Revit hosts WPF, so in practice the scheme is
            // already registered by the time OnStartup runs; this costs a null property read and
            // removes the dependency on that being true.
            _ = System.Windows.Application.ResourceAssembly;

            BitmapImage bitmap = new();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(PackStem + fileName, UriKind.Absolute);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
        }
        catch (System.IO.IOException)
        {
            // Fully qualified: RevitAPI.dll puts an inaccessible IOException in the global namespace,
            // and an unqualified one here resolves to that rather than to the framework's.
            // The resource is not in the table, or is not a decodable PNG. Either is a build defect.
        }
        catch (UriFormatException)
        {
        }

        Decoded[fileName] = image;
        return image;
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
