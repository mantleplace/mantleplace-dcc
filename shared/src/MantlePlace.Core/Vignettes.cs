namespace MantlePlace.Core;

public enum Vignette
{
    Vault,

    ImportBundle,
}

public static class Vignettes
{
    public const int MaxPixels = 355;

    public const int Width = 355;

    public const int Height = 266;

    public static IReadOnlyList<Vignette> All { get; } = [Vignette.Vault, Vignette.ImportBundle];

    public static IReadOnlyList<RibbonTheme> Themes { get; } = [RibbonTheme.Light, RibbonTheme.Dark];

    public static string FileNameOf(Vignette vignette, RibbonTheme theme)
        => $"{StemOf(vignette)}Vignette{NameOf(theme)}.png";

    private static string StemOf(Vignette vignette) => vignette switch
    {
        Vignette.Vault => "Vault",
        Vignette.ImportBundle => "ImportBundle",
        _ => throw new ArgumentOutOfRangeException(nameof(vignette), vignette, "no vignette is rendered for this command"),
    };

    private static string NameOf(RibbonTheme theme) => theme switch
    {
        RibbonTheme.Light => "Light",
        RibbonTheme.Dark => "Dark",
        _ => throw new ArgumentOutOfRangeException(nameof(theme), theme, "Revit has two UI themes"),
    };
}
