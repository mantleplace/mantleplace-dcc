namespace MantlePlace.Core.Tests;

internal static class RibbonImageryTests
{
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
            run.Equal(MarkRenders.SizeFor(16, 1.5), 24, "150%, small");
            run.Equal(MarkRenders.SizeFor(32, 1.5), 48, "150%, large");
            run.Equal(MarkRenders.SizeFor(16, 2.0), 32, "200%, small");
            run.Equal(MarkRenders.SizeFor(32, 2.0), 64, "200%, large");
        });

        run.Case("a scale between the steps takes the next render up", () =>
        {
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
                return;
            }

            HashSet<string> wanted = new(EveryName().Select(render => render.Name), StringComparer.Ordinal);

            foreach (string path in Directory.EnumerateFiles(resources, "*.png", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(path);
                run.True(wanted.Contains(name), $"{name} is in Resources and nothing draws it");
            }
        });

        run.Case("every render that is there draws something", () =>
        {
            if (FindResourcesDirectory() is not { } resources)
            {
                return;
            }

            foreach ((string name, _) in EveryName())
            {
                string path = Path.Combine(resources, name);
                if (File.Exists(path))
                {
                    run.True(DrawsSomething(path), $"{name} is blank -- every pixel is transparent");
                }
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
            run.True(Vignettes.Width <= Vignettes.MaxPixels, $"width {Vignettes.Width}");
            run.True(Vignettes.Height <= Vignettes.MaxPixels, $"height {Vignettes.Height}");
            run.Equal(Vignettes.MaxPixels, 355, "the API's own number, in 2025, 2026 and 2027 alike");
        });

        run.Case("every committed vignette is exactly the size that was declared", () =>
        {
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

    private static string? FindResourcesDirectory() => RepoTree.Find(ResourcesRelativePath);

    private static bool DrawsSomething(string path)
    {
        byte[] png = File.ReadAllBytes(path);
        int height = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
        using MemoryStream compressed = new();
        for (int at = 8; at + 8 <= png.Length;)
        {
            int length = (png[at] << 24) | (png[at + 1] << 16) | (png[at + 2] << 8) | png[at + 3];
            if (png[at + 4] == 'I' && png[at + 5] == 'D' && png[at + 6] == 'A' && png[at + 7] == 'T')
            {
                compressed.Write(png, at + 8, length);
            }

            at += 12 + length;
        }

        compressed.Position = 0;
        using System.IO.Compression.ZLibStream inflate = new(compressed, System.IO.Compression.CompressionMode.Decompress);
        using MemoryStream raw = new();
        inflate.CopyTo(raw);
        byte[] rows = raw.ToArray();
        int stride = rows.Length / height;
        for (int i = 0; i < rows.Length; i++)
        {
            if (i % stride != 0 && rows[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

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
