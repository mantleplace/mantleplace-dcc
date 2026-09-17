# brand-assets

Renders the Mantle Place mark into the sizes the Revit and Unreal hosts ship.

```bash
python tools/brand-assets/render_mark.py --mark-source <path to the canonical master>
```

## The master is private

`--mark-source` is mandatory and has no default. The canonical mark is a private vector source
held by Mantle Place LLC, and the largest PNG render of it is what this script reads. A default
would be a filesystem path: it would either publish somebody's private layout into a
world-readable repository or be wrong for everybody else. So the path is typed, every time.

The consequence is that **this script cannot run in CI and no workflow attempts it.**
`ci-brand-assets` runs `test_mark_geometry.py` alone, which needs no image and no dependency.

## Why it is two files

`mark_geometry.py` holds every rule that decides what a render looks like — where the monogram
ends and the tile begins, the optical box, the margin, which sizes get a hardened edge. Plain
numbers and plain buffers, standard library only, and fully covered by `test_mark_geometry.py`.

`render_mark.py` opens PNGs, writes PNGs, and decides nothing. Pillow lives here and nowhere else:

```bash
python -m pip install pillow
```

That split is the same one the plugins themselves are built on
([CONTRIBUTING.md](../../CONTRIBUTING.md#style-and-shape)): an impure shim, a pure core, and a
headless test of the core.

## What it writes

| file | size | treatment |
| --- | --- | --- |
| `revit/src/MantlePlace.Revit.Addin/Resources/MantlePlaceMark_16.png` | 16 | monogram, hardened |
| `…/MantlePlaceMark_24.png` | 24 | monogram, hardened |
| `…/MantlePlaceMark_32.png` | 32 | monogram |
| `…/MantlePlaceMark_48.png` | 48 | monogram |
| `…/MantlePlaceMark_64.png` | 64 | monogram |
| `unreal/MantlePlace/Resources/MantlePlaceTabIcon.png` | 64 | monogram |
| `unreal/MantlePlace/Resources/Icon128.png` | 128 | the mark as drawn, extrude intact |

Every render drops the extrude and recentres the monogram except the last, which is the one slot
large enough for the mark as drawn. Unreal's tab icon and vault header both draw the 64 px file.

**Why any of that, rather than scaling the mark down as it is, is
[ADR 0009](../../docs/adr/0009-host-assets-render-the-monogram.md).** Read it before you restore
the extrude, re-centre anything, or split the Unreal file back in two.

## Regenerating

Re-running overwrites every file above in place. Output is not guaranteed byte-identical across
Pillow versions — the provenance here is the method and this script, not a hash. Check the diff
before committing: `git diff --stat` should show only the files you meant to change.

Binaries in this repository are [asked about before they are added](../../CLAUDE.md), and these
are no exception.
