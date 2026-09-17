namespace MantlePlace.Revit.Core;

/// <summary>Revit's two UI themes, since 2024.</summary>
/// <remarks>
/// Not <c>Autodesk.Revit.UI.UITheme</c>, which this assembly cannot see and must not: the pure core
/// carries no Revit reference, which is what lets the whole of it be asserted on a machine with no
/// Revit licence (<c>HPS-42</c>). The shim maps one onto the other in one line.
/// </remarks>
public enum RibbonTheme
{
    /// <summary>Revit's light theme, and the default in every supported version.</summary>
    Light,

    /// <summary>Revit's dark theme.</summary>
    Dark,
}

/// <summary>One command on the Mantle Place ribbon, as the thing its glyph draws.</summary>
/// <remarks>
/// The Account button is absent on purpose: its face is the mark, which is <see cref="MarkRenders"/>
/// and comes from a private source that <c>revit/tools/Render-RibbonIcons.ps1</c> cannot and must
/// not render.
/// </remarks>
public enum RibbonGlyph
{
    /// <summary>Browse the vault.</summary>
    Vault,

    /// <summary>Import a bundle zip already on disk.</summary>
    ImportBundle,

    /// <summary>Measure what a terrain import would find, changing nothing.</summary>
    ProbeTerrain,

    /// <summary>Show the newest import or probe log.</summary>
    OpenLogs,
}

/// <summary>
/// Which committed PNG a command's ribbon button is handed, for a theme and a display scale.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>Revit runs a light and a dark UI theme, and one icon set cannot serve both.</b> A near-black
/// glyph is invisible on the dark ribbon and a near-white one is invisible on the light. Both sets
/// are rendered from the same SVG sources by <c>revit/tools/Render-RibbonIcons.ps1</c>, which is why
/// there is no drawing here — only the decision about which of its outputs to ask for.
/// </para>
/// <para>
/// The glyphs ship in the two sizes a Revit ribbon button has slots for, so a scaled display takes
/// the 32 px render into the small slot rather than magnifying the 16 (<see cref="RenderSizes"/>).
/// The mark ships in five sizes because its renderer is private and can afford to; these are
/// reproducible from committed text, and every size added is another binary in a repository that
/// counts them.
/// </para>
/// </remarks>
public static class RibbonGlyphs
{
    /// <summary>The sizes <c>Render-RibbonIcons.ps1</c> writes, smallest first.</summary>
    public static IReadOnlyList<int> Sizes { get; } = [16, 32];

    /// <summary>Every command with a glyph — what the headless suite iterates.</summary>
    public static IReadOnlyList<RibbonGlyph> All { get; } =
        [RibbonGlyph.Vault, RibbonGlyph.ImportBundle, RibbonGlyph.ProbeTerrain, RibbonGlyph.OpenLogs];

    /// <summary>Both themes — the same, for the same reason.</summary>
    public static IReadOnlyList<RibbonTheme> Themes { get; } = [RibbonTheme.Light, RibbonTheme.Dark];

    /// <summary>
    /// The file name to hand a slot of <paramref name="slotPixels"/> logical pixels.
    /// </summary>
    /// <param name="glyph">The command.</param>
    /// <param name="theme">Revit's current UI theme.</param>
    /// <param name="slotPixels">The slot's size in logical pixels — 16 or 32 for a ribbon button.</param>
    /// <param name="displayScale">The display's scale factor; see <see cref="RenderSizes.Pick"/>.</param>
    public static string FileNameFor(RibbonGlyph glyph, RibbonTheme theme, int slotPixels, double displayScale)
        => FileNameOf(glyph, theme, RenderSizes.Pick(Sizes, slotPixels, displayScale));

    /// <summary>The file name of one rendered size, without choosing it.</summary>
    /// <remarks>
    /// The spelling is the render script's, and the two are checked against each other by the
    /// headless suite reading the directory — because nothing else could. The script is run by hand,
    /// so a stem renamed on one side and not the other would otherwise be found in Revit, by eye, on
    /// the machine of whoever was verifying something else.
    /// </remarks>
    public static string FileNameOf(RibbonGlyph glyph, RibbonTheme theme, int size)
        => $"{StemOf(glyph)}{NameOf(theme)}_{size}.png";

    /// <summary>
    /// The file-name stem for a command.
    /// </summary>
    /// <remarks>
    /// Written out rather than taken from <c>Enum.ToString</c>. These are the names of files on disk;
    /// a rename of the enum member is a refactor, and it must not silently become a rename of four
    /// committed binaries that nobody regenerated.
    /// </remarks>
    private static string StemOf(RibbonGlyph glyph) => glyph switch
    {
        RibbonGlyph.Vault => "Vault",
        RibbonGlyph.ImportBundle => "ImportBundle",
        RibbonGlyph.ProbeTerrain => "ProbeTerrain",
        RibbonGlyph.OpenLogs => "OpenLogs",
        _ => throw new ArgumentOutOfRangeException(nameof(glyph), glyph, "no glyph is rendered for this command"),
    };

    /// <summary>The file-name half that names the theme, for the same reason.</summary>
    private static string NameOf(RibbonTheme theme) => theme switch
    {
        RibbonTheme.Light => "Light",
        RibbonTheme.Dark => "Dark",
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, "Revit has two UI themes"),
    };
}
