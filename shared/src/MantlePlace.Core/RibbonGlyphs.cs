namespace MantlePlace.Core;

public enum RibbonTheme
{
    Light,

    Dark,
}

public enum RibbonGlyph
{
    Vault,

    ImportBundle,

    ProbeTerrain,

    OpenLogs,

    PrepareForHandover,

    OpenInCurator,
}

public static class RibbonGlyphs
{
    public static IReadOnlyList<int> Sizes { get; } = [16, 32];

    public static IReadOnlyList<RibbonGlyph> All { get; } =
        [RibbonGlyph.Vault, RibbonGlyph.ImportBundle, RibbonGlyph.ProbeTerrain, RibbonGlyph.OpenLogs, RibbonGlyph.PrepareForHandover, RibbonGlyph.OpenInCurator];

    public static IReadOnlyList<RibbonTheme> Themes { get; } = [RibbonTheme.Light, RibbonTheme.Dark];

    public static string FileNameFor(RibbonGlyph glyph, RibbonTheme theme, int slotPixels, double displayScale)
        => FileNameOf(glyph, theme, RenderSizes.Pick(Sizes, slotPixels, displayScale));

    public static string FileNameOf(RibbonGlyph glyph, RibbonTheme theme, int size)
        => $"{StemOf(glyph)}{NameOf(theme)}_{size}.png";

    private static string StemOf(RibbonGlyph glyph) => glyph switch
    {
        RibbonGlyph.Vault => "Vault",
        RibbonGlyph.ImportBundle => "ImportBundle",
        RibbonGlyph.ProbeTerrain => "ProbeTerrain",
        RibbonGlyph.OpenLogs => "OpenLogs",
        RibbonGlyph.PrepareForHandover => "PrepareForHandover",
        RibbonGlyph.OpenInCurator => "OpenInCurator",
        _ => throw new ArgumentOutOfRangeException(nameof(glyph), glyph, "no glyph is rendered for this command"),
    };

    private static string NameOf(RibbonTheme theme) => theme switch
    {
        RibbonTheme.Light => "Light",
        RibbonTheme.Dark => "Dark",
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, "Revit has two UI themes"),
    };
}
