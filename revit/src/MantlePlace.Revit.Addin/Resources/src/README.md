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

## Which mark file for which display scale

Revit's ribbon takes exactly two images per button — `Image` at 16 px and `LargeImage` at 32 px —
and scales whatever it is handed to the display. Handing it a render that already matches the
display scale is the difference between crisp and blurry on a high-DPI laptop:

| Windows display scale | `Image` | `LargeImage` |
| --- | --- | --- |
| 100% | `MantlePlaceMark_16.png` | `MantlePlaceMark_32.png` |
| 150% | `MantlePlaceMark_24.png` | `MantlePlaceMark_48.png` |
| 200% | `MantlePlaceMark_32.png` | `MantlePlaceMark_64.png` |

Scales between these steps take the next size up and let Revit scale down, which is the direction
that costs least.

**Without this table the 24, 48 and 64 px renders look like dead weight and someone deletes them.**
That is what it is for.
