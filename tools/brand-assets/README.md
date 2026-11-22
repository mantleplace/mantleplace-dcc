# brand-assets

Renders the Mantle Place mark into every file a host plugin ships it as.

```bash
python -m pip install pillow
python tools/brand-assets/render_mark.py
```

## The source

[`mark.svg`](mark.svg) is the mark: the round orange disc, the white `mp`
monogram and its black extrude, clipped to the disc. It is the same artwork `mantle.place` serves as
its logo. Every host shows exactly this, in every place it shows a mark, and no host keeps a copy of
its own ([ADR 0033](../../docs/adr/0033-every-host-shows-the-round-mark-with-its-extrude.md),
`HPS-64` in the [host plugin standard](../../docs/host-plugin-standard.md)).

If the web's logo changes, copy the new file over this one and re-run the script.

## Why it is two files

`mark_geometry.py` reads the SVG into layers of polygons and owns the list of files to write. It is
standard library only, and `test_mark_geometry.py` covers it, so `ci-brand-assets` needs no install.

`render_mark.py` fills those polygons with Pillow at sixteen times the target size, clips them to the
disc, and scales down. It decides nothing about what the mark looks like.

That split is the same one the plugins themselves are built on
([CONTRIBUTING.md](../../CONTRIBUTING.md#style-and-shape)): an impure shim, a pure core, and a
headless test of the core.

## What it writes

| file | size |
| --- | --- |
| `revit/src/MantlePlace.Revit.Addin/Resources/MantlePlaceMark_16.png` | 16 |
| `…/MantlePlaceMark_24.png` | 24 |
| `…/MantlePlaceMark_32.png` | 32 |
| `…/MantlePlaceMark_48.png` | 48 |
| `…/MantlePlaceMark_64.png` | 64 |
| `unreal/MantlePlace/Resources/MantlePlaceTabIcon.png` | 64 |
| `unreal/MantlePlace/Resources/Icon128.png` | 128 |

The .NET hosts pick a `MantlePlaceMark_*` file by slot and display scale (`MarkRenders`): the ribbon
buttons, the window title bars and the dialog headers. Both Autodesk bundle icons are the 48 px file.
Unreal's tab icon and panel header draw `MantlePlaceTabIcon.png`, and its plugin browser draws
`Icon128.png`.

The AutoCAD plugin and the shared WPF windows read the Revit folder's files, so one set serves every
.NET host. A new host or a new size is a new entry in `mark_geometry.targets()`.

## Regenerating

Re-running overwrites every file above in place. Output is not guaranteed byte-identical across
Pillow versions, so provenance is the source and this script, not a hash. Check the diff before
committing: `git diff --stat` should show only the files you meant to change.

Binaries in this repository are [asked about before they are added](../../CLAUDE.md), and these
are no exception.
