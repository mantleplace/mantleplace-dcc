namespace MantlePlace.Revit.Core;

/// <summary>One command whose extended tooltip carries an explanatory picture.</summary>
/// <remarks>
/// <para>
/// Two commands, and deliberately not five. A vignette answers <em>what does this produce</em>, and
/// only the two on the Bundles panel face produce anything a picture can show: the other three would
/// be a picture of a window opening, of a browser, or of a model that by design did not change.
/// </para>
/// <para>
/// <b>The Account button is not merely out of scope — it is ineligible.</b> Revit's own API says so:
/// <em>"SplitButton and RadioButtonGroup cannot display the tooltip set by this method."</em> Its
/// face is a <c>PushButton</c> inside a <c>SplitButton</c>, and the extended tooltip Revit shows
/// there is the split button's, which has no image slot at all.
/// </para>
/// </remarks>
public enum Vignette
{
    /// <summary>The vault, with the bundles it holds — what is kept there is yours, and you pick one.</summary>
    Vault,

    /// <summary>A bundle zip on this disk becoming one toposolid in the model.</summary>
    ImportBundle,
}

/// <summary>
/// The committed vignette renders, and the one dimension Revit refuses to exceed.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Revit caps a tooltip image at 355 pixels on its longest side, and enforces it silently.</b>
/// The API's own words, identical in 2025, 2026 and 2027: <em>"Maximum height or width is 355
/// pixels."</em> Hand it more and the picture is clipped or dropped with no error, on a surface that
/// only appears after a hover delay — so the failure is invisible to every check except somebody's
/// eyes. <see cref="MaxPixels"/> is asserted against the committed files' own PNG headers by the
/// headless suite, which is the only detector there can be.
/// </para>
/// <para>
/// That cap is also why <see cref="RenderSizes"/> has nothing to do here. A ribbon button has two
/// slots and a ladder of renders to fill them from; a tooltip image has no slot, and no headroom
/// either — a render at twice the layout size would be 710 px on its long side, which is over twice
/// the limit. One file per theme, at 96 DPI, and a display at 200% gets a softened picture rather
/// than no picture.
/// </para>
/// <para>
/// Two themes for the reason <see cref="RibbonGlyphs"/> has two: Revit's extended tooltip panel
/// follows the UI theme, and a near-black stroke on the dark panel is not there. Unlike the glyphs,
/// these are repainted through <c>RibbonItem.ToolTipImage</c>, which exists alongside
/// <c>RibbonItemData.ToolTipImage</c> — so a theme flip mid-session reaches them.
/// </para>
/// </remarks>
public static class Vignettes
{
    /// <summary>
    /// The longest side Revit will accept on a tooltip image, in pixels.
    /// </summary>
    /// <remarks>
    /// Revit 2025, 2026 and 2027 all document the same number, so this is a property of the ribbon
    /// rather than an artefact of compiling against Revit 2025's API.
    /// </remarks>
    public const int MaxPixels = 355;

    /// <summary>The width every vignette is rendered at, in pixels.</summary>
    public const int Width = 355;

    /// <summary>
    /// The height every vignette is rendered at, in pixels.
    /// </summary>
    /// <remarks>
    /// 4:3 rather than 16:9, and the full width the cap allows. At this size the constraint is drawing
    /// area, not shape: 355 × 266 is a third more canvas than a 16:9 picture of the same width, and
    /// neither composition needs to be wide — the import's zip, arrow and slab stack downwards as
    /// readably as they sit in a row.
    /// </remarks>
    public const int Height = 266;

    /// <summary>Every command with a vignette — what the headless suite iterates.</summary>
    public static IReadOnlyList<Vignette> All { get; } = [Vignette.Vault, Vignette.ImportBundle];

    /// <summary>
    /// Both themes.
    /// </summary>
    /// <remarks>
    /// Its own list rather than <see cref="RibbonGlyphs.Themes"/>: these are two independent tables
    /// over one enum, and a vignette dropped for one theme should not be expressible as an edit to
    /// the glyphs' table.
    /// </remarks>
    public static IReadOnlyList<RibbonTheme> Themes { get; } = [RibbonTheme.Light, RibbonTheme.Dark];

    /// <summary>
    /// The file name of one vignette, as it is spelled in <c>Resources</c>.
    /// </summary>
    /// <remarks>
    /// <b>No size suffix, and its absence is the point.</b> Every other render in that folder carries
    /// one because a ladder exists to choose from; this one has no ladder, and a name that looked like
    /// the others would invite somebody to add a size and have it silently ignored.
    /// </remarks>
    public static string FileNameOf(Vignette vignette, RibbonTheme theme)
        => $"{StemOf(vignette)}Vignette{NameOf(theme)}.png";

    /// <summary>
    /// The file-name stem for a command.
    /// </summary>
    /// <remarks>
    /// Written out rather than taken from <c>Enum.ToString</c>, for the reason
    /// <see cref="RibbonGlyphs"/> gives: these are the names of committed files, and renaming an enum
    /// member is a refactor that must not quietly become a rename of four binaries nobody
    /// regenerated.
    /// </remarks>
    private static string StemOf(Vignette vignette) => vignette switch
    {
        Vignette.Vault => "Vault",
        Vignette.ImportBundle => "ImportBundle",
        _ => throw new ArgumentOutOfRangeException(nameof(vignette), vignette, "no vignette is rendered for this command"),
    };

    /// <summary>The file-name half that names the theme, for the same reason.</summary>
    /// <remarks>
    /// Spelled here rather than borrowed from the glyphs' table: the two tables name the same two
    /// themes today, which is two designs agreeing and not a constraint either owes the other.
    /// </remarks>
    private static string NameOf(RibbonTheme theme) => theme switch
    {
        RibbonTheme.Light => "Light",
        RibbonTheme.Dark => "Dark",
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, "Revit has two UI themes"),
    };
}
