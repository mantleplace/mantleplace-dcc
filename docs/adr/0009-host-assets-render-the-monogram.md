---
name: adr-0009-host-assets-render-the-monogram
description: Every host asset renders the monogram alone - extrude stripped, recentred on its optical box at a 12% margin, hardened below 32 px - because the mark as drawn carries detail finer than a pixel at ribbon sizes. Read before restoring the extrude, re-centring anything, splitting Unreal's shared 64 px file back in two, or rendering this pipeline large.
status: accepted
---

# 9. Host assets render the monogram, not the mark as drawn

Date: 2026-09-16

## Status

Accepted.

## Context

The canonical Mantle Place mark is a square, full-bleed tile in the brand orange carrying a white
lowercase `mp` monogram with a hard black extrude offset down and to the right. It is held as a
private vector source; nothing in this repository can regenerate it.

Both hosts need it small. Revit's ribbon asks for a 16 px and a 32 px image per button. Unreal
draws its tab icon at 16 px and the vault panel's header lockup at 32 px. Until now Revit shipped
no imagery at all, and Unreal shipped a *round* treatment of the monogram — two hosts, two marks.

Rendering the mark as drawn does not survive those sizes. Measured off the master:

| | share of the tile | at 16 px | at 64 px |
| --- | --- | --- | --- |
| monogram + extrude | 71% x 63% | 11.4 x 10.0 px | 45.6 x 40.0 px |
| stem width | 7.8% | 1.25 px | 5.0 px |
| extrude offset | 3.9% | 0.63 px | 2.5 px |

At 16 px the extrude is two thirds of one pixel. It does not read as a shadow; it reads as a grey
fringe, and the `m` shoulders merge behind it. Rendering at exact pixel size does not help — the
artwork carries detail finer than a pixel, and no rasterizer setting invents resolution that the
geometry does not have. Renders at 24, 32, 48 and 64 px were judged the same way. Even at 64 px the
extrude is a heavy black mass rather than a shadow.

Removing the extrude exposes a second problem. The monogram alone does not sit centred: it is at
18.6% from the left against 14.2% from the right, and 22.9% from the top against 18.6% from the
bottom. The extrude, falling to the bottom-right, was balancing the composition. Take it away and
the imbalance is no longer a counterweight, just a lopsided mark.

## Decision

**Host assets render the monogram alone, recentred on its own optical box, at a 12% margin.**

- **No extrude, at any size a host ships.** A flat rule, not a threshold: it failed at all five
  sizes tested, so a threshold would only invite argument about where to put it. The extrude
  remains part of the canonical mark for brand-size use.
- **The monogram is recentred**, because with the extrude gone it is no longer balanced.
- **Edges are hardened at 16 and 24 px and left antialiased at 32 px and above.** Below 32 px the
  antialiased edge is wider than the stem it softens; above it, the same hardening produces stairs.
- **One exception.** `unreal/MantlePlace/Resources/Icon128.png` is Unreal's plugin-browser icon,
  shown large enough for the extrude to read as a shadow. It renders the mark exactly as drawn,
  and it is the only asset in either host that does.
- **The renders are produced by `tools/brand-assets/render_mark.py`** from the largest available
  PNG of the canonical mark, passed as a mandatory `--mark-source`. There is no default, because a
  default is a path and any path would either publish a private layout or be wrong for everybody.

Unreal's tab icon and header logo draw the same 64 px file. Both are the same artwork at the same
size, and shipping two byte-identical blobs under two names reads as an oversight forever.

## Consequences

The retired roundel stays a covered mark in [TRADEMARK.md](../../TRADEMARK.md). It shipped through
`unreal-0.4.0` and is live in installed copies, so a fork stripping our marks still has to change
it.

**A reader who finds the script deleting part of the logo will assume it is a bug.** That is the
main thing this record exists to prevent. The same goes for the missing extrude in the shipped
PNGs, and for two style entries pointing at one file.

The renders cannot be verified in CI. The input is private, so no workflow can regenerate them and
none tries; `ci-brand-assets` runs the geometry tests alone, and provenance is this record plus the
script, not a checksum file that nothing checks.

The pipeline reads a raster master rather than the vector source. Measured against a vector render,
agreement on the optical box is within 3 parts in 10,000 and per-channel differences across all
five sizes peak at 6 of 255 — invisible at a 64:1 downsample. This holds only while 128 px is the
ceiling. **Rendering the mark large from this pipeline would be wrong**, and would want the vector
source and a rasterizer instead.

Pillow is a dependency, and `tools/` had none. It is confined to `render_mark.py`; the decisions
live in `mark_geometry.py`, which is standard library only, so the tests inherit the no-install
property the other tools in `tools/` rely on.
