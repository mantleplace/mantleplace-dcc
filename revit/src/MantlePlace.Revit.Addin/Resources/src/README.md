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

## The vignettes, and the sources that are only theirs

A **vignette** is the picture Revit shows under a button's long description on hover — the thing
that makes an Autodesk button feel explained. Two buttons have one, `Vault` and `Import Bundle`, and
neither picture is a screenshot: [ADR 0010](../../../../../docs/adr/0010-tooltip-vignettes-are-drawn-not-photographed.md)
records why, and why that is the opposite of what the issue asked for.

They are drawn two ways, with one pen. `Vault` is **composed** from unedited Tabler icons.
`Import Bundle` is **computed** — an analytic height field sampled on a grid and projected
isometrically, which is a toposolid arrived at by arithmetic rather than by drawing one. Both use
Tabler's 2-on-24 stroke ratio at a 96 px element box, so a picture made of icons and a picture made
of geometry read as one family.

A separate table because a source's job here is not the job it has above. `building-bank.svg`
appears in both and is not listed twice by accident: on the ribbon it is drawn *on* the Vault button,
and here it is one element *inside* that button's picture. Only `package.svg` is new to the tree.

| source | used by | what it says |
| --- | --- | --- |
| [`building-bank.svg`](./building-bank.svg) | `Vault` | the vault itself — the same source its glyph uses, so the button and its picture are visibly one thing |
| [`package.svg`](./package.svg) | `Vault`, `Import Bundle` | a bundle. Three of them under the vault, one of them raised and orange; and one above the toposolid it becomes |

`package.svg` is Tabler **3.46.0**, the same version pinned above, taken unedited. The one orange in
each picture is one element — the raised bundle in `Vault`, the descending arrow in `Import Bundle`
— which is the accent rule the glyphs already follow.

## Rendering them

```powershell
./revit/tools/Render-RibbonIcons.ps1        # from the repository root
./revit/tools/Render-TooltipVignettes.ps1   # and this one, for the two vignettes
```

[`Render-RibbonIcons.ps1`](../../../../tools/Render-RibbonIcons.ps1) writes every
`<Command><Theme>_<size>.png` in the folder above from the SVGs in this one — four commands, two
themes, two sizes, sixteen files.
[`Render-TooltipVignettes.ps1`](../../../../tools/Render-TooltipVignettes.ps1) writes every
`<Command>Vignette<Theme>.png` — two commands, two themes, one size, four files. **Both are how
those files are regenerated, not build steps**: nothing in the build, the packaging script or CI
runs either, and every output is committed.

**Two scripts, and not because one of them reads something private.** That is why
[`tools/brand-assets/`](../../../../../tools/brand-assets/) is separate; both halves of the vignettes
are here. They are separate because the glyph script's contract is exact — every
`<Command><Theme>_<size>.png` from one Tabler icon scaled — and a vignette breaks all of it: a
different name shape, no size in the name, a 355×266 canvas rather than a square, and one of the two
pictures with no SVG source at all. Folding them together would turn that contract into a list of
exceptions.

That is what earns those twenty binaries their place. The
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
and scales whatever it is handed to the display. The plugin's windows head themselves with a
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

**The vignettes are not a fourth caller, and their absence from this table is the point.** Revit caps
a tooltip image at 355 px on its longest side, so there is no headroom for a second, larger render to
pick between — a 2× render of a 355 px picture is 710 px, twice what the ribbon accepts. One file per
theme, at 96 DPI, softened by Revit on a scaled display. `RenderSizes` has nothing to decide, and
[`Vignettes`](../../../MantlePlace.Revit.Core/Vignettes.cs) carries the cap instead, asserted by the
headless suite against these files' own PNG headers — because an over-large tooltip image is clipped
or dropped **silently**, on a surface that only appears after a hover delay.
