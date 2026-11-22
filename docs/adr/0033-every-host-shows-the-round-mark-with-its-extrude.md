---
name: adr-0033-every-host-shows-the-round-mark-with-its-extrude
description: The Mantle Place mark is the round orange disc with the white mp monogram and its black extrude, the same artwork mantle.place serves, and every host plugin shows exactly that in every place it shows a mark - ribbon and toolbar buttons, title bars and window corners, dialog headers, bundle and plugin icons. One vector source in tools/brand-assets renders every host's files. Supersedes ADR 0009. Read before changing a mark asset, adding a host's icon, or putting the mark somewhere new.
status: accepted
---

# 33. Every host shows the round mark, extrude included

Date: 2026-11-22

## Status

Accepted. Decided by the founder. Supersedes [ADR 0009](0009-host-assets-render-the-monogram.md).

## Context

[ADR 0009](0009-host-assets-render-the-monogram.md) made every host asset a square, full-bleed
orange tile with the white `mp` monogram alone: the black extrude was stripped and the monogram
recentred, because at 16 px the extrude is less than a pixel wide. It also treated the round
treatment as retired, and rendered from a private raster master no one else could use.

Meanwhile the web never changed. `mantle.place/logo.svg` and every favicon the site serves are the
round orange disc with the monogram and its extrude. So a curator who signs in from Revit saw one
mark on the ribbon and the Sign In dialog, and another in the browser tab that the same dialog
opened. Reported as a defect, the plugin's mark read as cropped and missing its shadow, and both
were true: it was the same letterform with the disc cut to a square and the shadow removed.

The size argument in 0009 still holds as a measurement. At 16 px the extrude is under a pixel and
blurs into the monogram's edge. It does not hold as a reason to show a different mark. The browser
draws this same artwork as a 16 px favicon, and it reads there as the Mantle Place mark because it is
the one a curator already knows.

## Decision

**The Mantle Place mark is the round disc with its extrude, as the web serves it, in every host and
every place a host shows a mark.**

- **One artwork.** The orange disc (`#ff7110`), the white `mp` monogram, and the black extrude
  beneath it, clipped to the disc. Transparent outside the disc. Nothing is recentred, recoloured,
  cropped to a square or removed at any size.
- **Everywhere.** Ribbon and toolbar buttons at their small and large sizes; window title bars and
  window corners; dialog and panel headers; bundle, add-in and plugin-browser icons; any new place a
  host shows a mark. A host shows the mark in each of those it has.
- **One source.** `tools/brand-assets/mark.svg` is the vector source, the same file the
  web serves as its logo. `tools/brand-assets/render_mark.py` renders it at each size a host ships,
  directly from the vector at that size, and writes every host's files. No host draws or edits its
  own copy; a new size or a new host is a new target in that tool.
- **Every host.** This is the standard for every host plugin, recorded as `HPS-64` in the
  [host plugin standard](../host-plugin-standard.md). A host whose mark assets do not yet come from
  this source is a gap, tracked in an issue, not a choice.

## Consequences

The square tile and the extrude-stripped monogram are retired. They shipped in installed copies of
the Revit and AutoCAD plugins and in the Unreal plugin's resources, so
[TRADEMARK.md](../../TRADEMARK.md) keeps covering them alongside the round mark.

The source is public, so anyone can regenerate the committed renders and check them. The renders
are still committed rather than built, because each host's build reads image files and no host
build runs Python.

At 16 px the extrude is a soft dark edge rather than a clear shadow. That is accepted. A mark the
curator recognizes from the web is worth more than a sharper one they do not.

The rules for matting, recentring and edge hardening that ADR 0009 introduced, and that
[ADR 0031](0031-ci-autocad-blender-and-gate-tooling.md) records as the minimum-channel floor of 40
and the 12 percent margin, are gone with it, as are their tests. The extrude no longer needs
balancing, because it is no longer removed.
