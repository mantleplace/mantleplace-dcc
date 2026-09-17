namespace MantlePlace.Revit.Core.Tests;

/// <summary>
/// Which committed PNG each ribbon button is handed — the render size, the theme, and whether the
/// file that name asks for is actually there.
/// </summary>
/// <remarks>
/// <para>
/// The last of those is the one that could not be checked any other way. The glyph PNGs are written
/// by <c>revit/tools/Render-RibbonIcons.ps1</c>, which is run BY HAND and never by CI — so a stem
/// renamed on one side and not the other, or a render deleted in a tidy-up, is a defect whose only
/// other detector is a blank button noticed in Revit by whoever happened to be verifying something
/// else. Reading the directory costs a millisecond and turns that into a failing build.
/// </para>
/// <para>
/// Both directions are read: a name this assembly can ask for with no file behind it, and a file in
/// <c>Resources</c> that nothing here can ask for. The second is what catches a render added and
/// never wired up, which looks exactly like a render that IS wired up until somebody deletes it as
/// dead weight.
/// </para>
/// </remarks>
internal static class RibbonImageryTests
{
    /// <summary>Where the renders live, relative to the repository root.</summary>
    private const string ResourcesRelativePath = "revit/src/MantlePlace.Revit.Addin/Resources";

    internal static int Run()
    {
        TestRun run = new();

        run.Case("a slot at 100% takes the render that matches it", () =>
        {
            run.Equal(MarkRenders.SizeFor(16, 1.0), 16, "the ribbon's small slot");
            run.Equal(MarkRenders.SizeFor(32, 1.0), 32, "the ribbon's large slot");
        });

        run.Case("the documented display scales land on the documented renders", () =>
        {
            // Resources/src/README.md, the three rows that stop the 24, 48 and 64 px renders from
            // looking like dead weight.
            run.Equal(MarkRenders.SizeFor(16, 1.5), 24, "150%, small");
            run.Equal(MarkRenders.SizeFor(32, 1.5), 48, "150%, large");
            run.Equal(MarkRenders.SizeFor(16, 2.0), 32, "200%, small");
            run.Equal(MarkRenders.SizeFor(32, 2.0), 64, "200%, large");
        });

        run.Case("a scale between the steps takes the next render up", () =>
        {
            // 125% wants 40 px, which nothing is. Scaling 48 down costs less than scaling 32 up.
            run.Equal(MarkRenders.SizeFor(32, 1.25), 48, "125%, large");
            run.Equal(MarkRenders.SizeFor(16, 1.25), 24, "125%, small");
        });

        run.Case("past the largest render, the largest render is the answer", () =>
        {
            run.Equal(MarkRenders.SizeFor(32, 3.0), 64, "300%, large");
            run.Equal(MarkRenders.SizeFor(256, 1.0), 64, "a slot larger than anything rendered");
        });

        run.Case("a scale Windows cannot report is treated as 100%", () =>
        {
            // The scale comes off a Win32 call or a visual that may not be in a tree yet. A zero or
            // a NaN there must not pick a render by accident, and must not throw on Revit's thread.
            run.Equal(MarkRenders.SizeFor(32, 0), 32, "zero");
            run.Equal(MarkRenders.SizeFor(32, -2), 32, "negative");
            run.Equal(MarkRenders.SizeFor(32, double.NaN), 32, "NaN");
            run.Equal(MarkRenders.SizeFor(32, double.PositiveInfinity), 32, "an infinity is not a scale either");
            run.Equal(MarkRenders.SizeFor(32, 0.75), 32, "below 100%, which Windows does not offer");
        });

        run.Case("a slot with no size is a programming error, not a guess", () =>
        {
            run.True(Throws(() => MarkRenders.SizeFor(0, 1.0)), "a zero-pixel slot is refused");
            run.True(Throws(() => MarkRenders.SizeFor(-16, 1.0)), "and a negative one");
            run.True(Throws(() => RenderSizes.Pick([], 16, 1.0)), "so is an empty list of renders");
        });

        run.Case("the mark's file name is the render it picked", () =>
        {
            run.Equal(MarkRenders.FileNameFor(32, 2.0), "MantlePlaceMark_64.png", "the 200% large slot");
            run.Equal(MarkRenders.FileNameFor(16, 1.0), "MantlePlaceMark_16.png", "the 100% small slot");
            run.Equal(string.Join(",", MarkRenders.Sizes), "16,24,32,48,64", "the committed renders");
        });

        run.Case("a glyph's file name carries its command, its theme and its size", () =>
        {
            run.Equal(
                RibbonGlyphs.FileNameFor(RibbonGlyph.Vault, RibbonTheme.Light, 16, 1.0),
                "VaultLight_16.png",
                "the 100% small slot");
            run.Equal(
                RibbonGlyphs.FileNameFor(RibbonGlyph.ImportBundle, RibbonTheme.Dark, 32, 1.0),
                "ImportBundleDark_32.png",
                "the 100% large slot, dark");
            run.Equal(string.Join(",", RibbonGlyphs.Sizes), "16,32", "the two slots a ribbon button has");
        });

        run.Case("a scaled display takes the large glyph into the small slot rather than magnifying", () =>
        {
            // The whole point of picking at all: at 150% the 16 px slot is 24 device pixels, and
            // there is no 24 px glyph, so the 32 px render goes in and Revit scales it DOWN.
            run.Equal(
                RibbonGlyphs.FileNameFor(RibbonGlyph.ProbeTerrain, RibbonTheme.Light, 16, 1.5),
                "ProbeTerrainLight_32.png",
                "150%, small slot");
            run.Equal(
                RibbonGlyphs.FileNameFor(RibbonGlyph.ProbeTerrain, RibbonTheme.Light, 32, 2.0),
                "ProbeTerrainLight_32.png",
                "200%, large slot — 64 px is not rendered, so the largest there is");
        });

        run.Case("the two themes are different files, always", () =>
        {
            foreach (RibbonGlyph glyph in RibbonGlyphs.All)
            {
                foreach (int size in RibbonGlyphs.Sizes)
                {
                    string light = RibbonGlyphs.FileNameOf(glyph, RibbonTheme.Light, size);
                    string dark = RibbonGlyphs.FileNameOf(glyph, RibbonTheme.Dark, size);
                    run.False(string.Equals(light, dark, StringComparison.Ordinal), $"{glyph} at {size} px");
                }
            }
        });

        run.Case("every command and every theme is listed", () =>
        {
            // An enum member added without a row in All would never be drawn, and the omission is
            // invisible: the button simply keeps whatever image it was built with, which is none.
            run.Equal(RibbonGlyphs.All.Count, Enum.GetValues<RibbonGlyph>().Length, "commands");
            run.Equal(RibbonGlyphs.Themes.Count, Enum.GetValues<RibbonTheme>().Length, "themes");
        });

        run.Case("every render this assembly can ask for is a file that is there", () =>
        {
            if (FindResourcesDirectory() is not { } resources)
            {
                run.Fail(
                    $"could not locate {ResourcesRelativePath} by walking up from '{AppContext.BaseDirectory}'. "
                    + "The renders are committed; a working tree without them cannot check the ribbon.");
                return;
            }

            foreach ((string name, string script) in EveryName())
            {
                run.True(
                    File.Exists(Path.Combine(resources, name)),
                    $"{name} — regenerate with {script}");
            }
        });

        run.Case("every PNG that is there is one this assembly can ask for", () =>
        {
            if (FindResourcesDirectory() is not { } resources)
            {
                // Already reported by the case above; failing twice for one cause reads as two bugs.
                return;
            }

            HashSet<string> wanted = new(EveryName().Select(render => render.Name), StringComparer.Ordinal);

            foreach (string path in Directory.EnumerateFiles(resources, "*.png", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(path);
                run.True(wanted.Contains(name), $"{name} is in Resources and nothing draws it");
            }
        });

        run.Case("a vignette's file name carries its command and its theme, and no size", () =>
        {
            run.Equal(
                Vignettes.FileNameOf(Vignette.Vault, RibbonTheme.Light),
                "VaultVignetteLight.png",
                "the vault, light");
            run.Equal(
                Vignettes.FileNameOf(Vignette.ImportBundle, RibbonTheme.Dark),
                "ImportBundleVignetteDark.png",
                "the local import, dark");

            // The absence of a size is load-bearing, not an oversight: there is no ladder to pick
            // from, and a name shaped like the glyphs' would invite a size that nothing would read.
            foreach (Vignette vignette in Vignettes.All)
            {
                foreach (RibbonTheme theme in Vignettes.Themes)
                {
                    run.False(
                        Vignettes.FileNameOf(vignette, theme).Any(char.IsDigit),
                        $"{vignette} in {theme} carries no number");
                }
            }
        });

        run.Case("every vignette and every theme is listed", () =>
        {
            run.Equal(Vignettes.All.Count, Enum.GetValues<Vignette>().Length, "vignettes");
            run.Equal(Vignettes.Themes.Count, Enum.GetValues<RibbonTheme>().Length, "themes");
        });

        run.Case("the declared vignette size is inside the cap Revit enforces", () =>
        {
            // Checked here as well as against the files, because this is the pair the render script
            // is written to and a change to them is where an over-large render would start.
            run.True(Vignettes.Width <= Vignettes.MaxPixels, $"width {Vignettes.Width}");
            run.True(Vignettes.Height <= Vignettes.MaxPixels, $"height {Vignettes.Height}");
            run.Equal(Vignettes.MaxPixels, 355, "the API's own number, in 2025, 2026 and 2027 alike");
        });

        run.Case("every committed vignette is exactly the size that was declared", () =>
        {
            // The only detector there can be. Revit clips or drops an over-large tooltip image
            // SILENTLY, on a surface that appears after a hover delay -- so a render regenerated at
            // the wrong size is a picture that is simply not there, found by eye or not at all.
            // Reading the IHDR of a committed file turns that into a failing build.
            if (FindResourcesDirectory() is not { } resources)
            {
                return;
            }

            foreach (Vignette vignette in Vignettes.All)
            {
                foreach (RibbonTheme theme in Vignettes.Themes)
                {
                    string name = Vignettes.FileNameOf(vignette, theme);
                    string path = Path.Combine(resources, name);
                    if (!File.Exists(path))
                    {
                        // Reported by the case that walks every name; failing twice reads as two bugs.
                        continue;
                    }

                    byte[] prefix = new byte[PngHeader.PrefixLength];
                    using (FileStream file = File.OpenRead(path))
                    {
                        file.ReadExactly(prefix);
                    }

                    if (PngHeader.TryReadSize(prefix) is not { } size)
                    {
                        run.Fail($"{name} -- not a PNG, or its IHDR cannot be read");
                        continue;
                    }

                    run.Equal(size.Width, Vignettes.Width, $"{name} width");
                    run.Equal(size.Height, Vignettes.Height, $"{name} height");
                    run.True(
                        size.Width <= Vignettes.MaxPixels && size.Height <= Vignettes.MaxPixels,
                        $"{name} is {size} -- Revit accepts no side over {Vignettes.MaxPixels} px");
                }
            }
        });

        return run.Report("ribbon imagery");
    }

    /// <summary>
    /// Every file name the ribbon can ask <c>Resources</c> for, with what regenerates it.
    /// </summary>
    /// <remarks>
    /// The script travels with the name because three different things write into that one folder
    /// and only one of them is even in this repository. A message naming the wrong one sends whoever
    /// hits it to a script that cannot produce the missing file.
    /// </remarks>
    private static IEnumerable<(string Name, string Script)> EveryName()
    {
        foreach (int size in MarkRenders.Sizes)
        {
            yield return (MarkRenders.FileNameOf(size), "tools/brand-assets (its input is private)");
        }

        foreach (RibbonGlyph glyph in RibbonGlyphs.All)
        {
            foreach (RibbonTheme theme in RibbonGlyphs.Themes)
            {
                foreach (int size in RibbonGlyphs.Sizes)
                {
                    yield return (
                        RibbonGlyphs.FileNameOf(glyph, theme, size),
                        "revit/tools/Render-RibbonIcons.ps1");
                }
            }
        }

        foreach (Vignette vignette in Vignettes.All)
        {
            foreach (RibbonTheme theme in Vignettes.Themes)
            {
                yield return (
                    Vignettes.FileNameOf(vignette, theme),
                    "revit/tools/Render-TooltipVignettes.ps1");
            }
        }
    }

    /// <summary>Locates the add-in's <c>Resources</c> directory, or <c>null</c>.</summary>
    private static string? FindResourcesDirectory() => RepoTree.Find(ResourcesRelativePath);

    private static bool Throws(Action body)
    {
        try
        {
            body();
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }
}
