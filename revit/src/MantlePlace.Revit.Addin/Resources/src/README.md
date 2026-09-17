# Resources/src

Sources for the images beside this folder.

## The mark has no source here

`MantlePlaceMark_*.png` are rendered from the canonical Mantle Place mark, which is a **private
vector source** held by Mantle Place LLC and is not in this repository. The renders are committed;
their input is not, and cannot be.

The script that produces them is [`tools/brand-assets/`](../../../../../tools/brand-assets/), and
the reasoning behind what it does to the artwork — the dropped extrude, the recentring, the
hardened edge below 32 px — is
[ADR 0009](../../../../../docs/adr/0009-host-assets-render-the-monogram.md).

Command glyphs are a different matter: those are MIT-licensed SVGs and their sources belong in
this folder, beside the licence text.

## The command glyphs, and where they came from

One glyph per command, chosen from the [Tabler](https://tabler.io/icons) icon set, which is MIT
licensed — the licence is [`LICENSE-tabler-icons.txt`](./LICENSE-tabler-icons.txt) beside them, and
the SVGs are the files Tabler publishes, unedited. The version taken was **3.46.0**.

| command | source | what it says |
| --- | --- | --- |
| Vault | [`building-bank.svg`](./building-bank.svg) | the remote library: what is kept here is yours, and is locked up |
| Import Bundle | [`package-import.svg`](./package-import.svg) | a bundle zip already on this disk, going in |
| Probe Terrain | [`ruler-measure.svg`](./ruler-measure.svg) | measuring, and changing nothing — the label already says *Terrain* |
| Open Logs | [`logs.svg`](./logs.svg) | lines of record, and at 16 px the only glyph in the set that is nothing but straight rules |

The Account button is not in that table: its face is the mark, and the mark is rendered somewhere
else entirely, from something that is not here.

**The one orange is on one glyph.** `Import Bundle` is the panel's primary action and the only
source in the set with a separable action element — a box, plus an arrow going into it. The arrow
is drawn in the brand orange and the box is not. Everything else on this ribbon is the theme's own
grey, which is the point: the tab has to sit beside Autodesk's own without reading as an advert.

## Rendering them

```powershell
./revit/tools/Render-RibbonIcons.ps1   # from the repository root
```

[`Render-RibbonIcons.ps1`](../../../../tools/Render-RibbonIcons.ps1) writes every
`<Command><Theme>_<size>.png` in the folder above from the SVGs in this one — four commands, two
themes, two sizes, sixteen files. **It is how they are regenerated, not a build step**: nothing in
the build, the packaging script or CI runs it, and every output is committed.

That is what earns those sixteen binaries their place. The
[binaries rule](../../../../../CLAUDE.md) protects a stranger's first clone, and the answer to "may
this be added" is much easier when every byte of it is reproducible from a text file committed
beside it.

Output is not guaranteed byte-identical across Windows releases — the rasteriser is WPF's, not
ours — so the provenance is the method and the script, exactly as it is for
[`tools/brand-assets/`](../../../../../tools/brand-assets/). Check `git diff --stat` before
committing.

## Which file for which theme

Revit has run a light and a dark UI theme since 2024, and **one icon set cannot serve both**: a
near-black glyph is invisible on the dark ribbon and a near-white one is invisible on the light. Both
sets come out of the same SVGs, recoloured:

| theme | foreground | accent |
| --- | --- | --- |
| Light | `#3C3C3C` | `#FF7110` |
| Dark | `#E6E6E6` | `#FF7110` |

Neither foreground is pure black or pure white, because Autodesk's own ribbon glyphs are neither.
The accent is the mark's own tile colour in both themes, unmodified — a second orange mixed for
legibility on one of them would be a second brand colour, and there is one.

Which of the two sets is drawn is decided at startup from `UIThemeManager.CurrentTheme` and swapped
on `UIControlledApplication.ThemeChanged`, so a curator who changes theme with Revit open does not
have to restart it to get a readable ribbon.

## Which mark file for which display scale

Revit's ribbon takes exactly two images per button — `Image` at 16 px and `LargeImage` at 32 px —
and scales whatever it is handed to the display. The plugin's two windows head themselves with a
third slot, 32 logical px beside the heading. Handing any of them a render that already matches the
display scale is the difference between crisp and blurry on a high-DPI laptop:

| Windows display scale | 16 px slot | 32 px slot |
| --- | --- | --- |
| 100% | `MantlePlaceMark_16.png` | `MantlePlaceMark_32.png` |
| 150% | `MantlePlaceMark_24.png` | `MantlePlaceMark_48.png` |
| 200% | `MantlePlaceMark_32.png` | `MantlePlaceMark_64.png` |

Scales between these steps take the next size up and let Revit scale down, which is the direction
that costs least.

**Without this table the 24, 48 and 64 px renders look like dead weight and someone deletes them.**
That is what it is for.

The same rule runs over the glyphs, which ship in two sizes rather than five: at 150% the 16 px slot
wants 24 px, there is no 24 px glyph, so the 32 px render goes in and Revit scales it down. The rule
is one function — [`RenderSizes`](../../../MantlePlace.Revit.Core/RenderSizes.cs) — and both
[`MarkRenders`](../../../MantlePlace.Revit.Core/MarkRenders.cs) and
[`RibbonGlyphs`](../../../MantlePlace.Revit.Core/RibbonGlyphs.cs) are that table expressed over it,
in the pure core where a machine with no Revit licence can assert them.

The window headers are the third caller: `BrandChrome` fills a 32 logical px slot beside each
window's heading from the same `MarkRenders`. It differs from the ribbon in one way — a window can
be dragged to a monitor at another scale, so it re-picks on `DpiChanged`, where the ribbon reads the
system scale once at startup and never again.
