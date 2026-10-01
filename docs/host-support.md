---
name: host-support
description: The host support matrix — one row per user-facing capability, one column per DCC host, a fixed status in every cell, and a link to the issue or ADR behind every gap. The only place a per-host feature status is stated. Read before saying what a host does or does not do, before planning work in one host from another's behaviour, and when a change adds, narrows or drops a feature.
---

# Host support matrix

What each host plugin does with each feature a bundle or the platform offers, and — where it does
not — which of three reasons applies: it has **not been built yet**, it is **declined** on purpose,
or it has **no meaning** in that host. A host that does the same job with a different construct (a
toposolid subdivision where the other paints a landscape layer) is `supported`, and a link says why
the construct differs where an ADR records it.

**This file states status and nothing else.** How a feature behaves is the host's own README
([Unreal](../unreal/README.md), [Revit](../revit/README.md)); when a gap will close is
[`ROADMAP.md`](../ROADMAP.md); why a host differs is its ADR or issue, linked from the cell. None of
those keeps a feature list of its own. The one table elsewhere that names a host surface per action
is the standard's shared-vocabulary table (`HPS-51`), and it says which *words* a host shows, not
whether the host has the feature. Which pointer each host imports a category from, whether it comes in
by default, and what it was measured to cost is the [import inventory](import-inventory.md)'s, and
it states no status either.

**Each column belongs to its host.** A change that adds, narrows or drops a feature in one host
edits that host's column in the same pull request, and no other. A new host adds its own column; a
new capability adds a row with a cell for every host.

## Statuses

| Status | Meaning |
| --- | --- |
| supported | imported and placed — or, for a row that imports nothing, working — and verified in the host |
| partial | as `supported`, with a named gap — the cell links the issue |
| planned | not yet — the cell links the issue that will do it |
| declined | will not be done in this host — the cell links the ADR or issue, or says why in one line |
| not applicable | the capability has no meaning in this host |

Every cell is a status alone, or a status, an em dash and one or more links: an issue by its full
URL, an ADR or the standard by a path relative to this file, so the docs-integrity gate checks it.
A `declined` cell with no ADR may carry one line of reason instead. No cell describes behaviour.

## Account and vault

| Capability | Unreal | Revit |
| --- | --- | --- |
| Sign in and out in the system browser | supported | supported |
| List, prepare and download your orders | supported | supported |
| Search the vault | supported | supported |
| Remove a download on request | planned — [issue 264](https://github.com/mantleplace/mantleplace-dcc/issues/264) | supported |
| A notice when a Prepare ends | partial — [issue 265](https://github.com/mantleplace/mantleplace-dcc/issues/265) | supported |
| A notice when an order arrives in the vault | planned — [issue 234](https://github.com/mantleplace/mantleplace-dcc/issues/234) | supported |
| Re-join a Prepare the host was closed during | planned — [issue 265](https://github.com/mantleplace/mantleplace-dcc/issues/265) | supported |
| Each bundle's thumbnail in the vault | planned — [issue 241](https://github.com/mantleplace/mantleplace-dcc/issues/241) | planned — [issue 267](https://github.com/mantleplace/mantleplace-dcc/issues/267) |

## Importing a bundle

| Capability | Unreal | Revit |
| --- | --- | --- |
| Import a downloaded bundle with no account | supported | supported |
| Refuse a manifest version outside the supported range | supported | supported |
| Refuse a bundle whose bytes do not match its published digest | supported | supported |
| Say the bundle's unit system and delivery CRS | supported | supported |
| Choose what an import brings in, before it runs | planned — [issue 93](https://github.com/mantleplace/mantleplace-dcc/issues/93) | supported |
| Say what a bundle holds that an import cannot place, and why | partial — [issue 266](https://github.com/mantleplace/mantleplace-dcc/issues/266) | supported |
| Show an import step by step, and stop it | planned — [issue 269](https://github.com/mantleplace/mantleplace-dcc/issues/269) | supported |
| Import the same order again | supported — [ADR 0002](adr/0002-import-identity.md) | supported — [ADR 0004](adr/0004-revit-terrain-identity.md) |
| Find everything an import made | supported | supported |

## Placement

| Capability | Unreal | Revit |
| --- | --- | --- |
| Place content at the published georeference | supported — [fixed-frame host](host-plugin-standard.md#11-frames-and-placement-hps-52--hps-54) | supported — [order-frame host](host-plugin-standard.md#11-frames-and-placement-hps-52--hps-54) |
| Refuse a file that is not in the host frame | partial — [issue 222](https://github.com/mantleplace/mantleplace-dcc/issues/222) | supported |
| Site location, sun and time zone | planned — [issue 262](https://github.com/mantleplace/mantleplace-dcc/issues/262) | supported |

## Terrain and land

| Capability | Unreal | Revit |
| --- | --- | --- |
| Terrain | supported — [ADR 0006](adr/0006-unreal-declines-elevation-bounds-target-crs.md) | supported |
| Terrain as a mesh | supported | declined — [ADR 0012](adr/0012-context-buildings-come-from-the-site-model.md) |
| Imagery drape | supported | supported — [ADR 0008](adr/0008-revit-drape-is-anchored-to-the-smooth-shading-origin.md) |
| Stylized map drape | planned — [issue 255](https://github.com/mantleplace/mantleplace-dcc/issues/255) | planned — [issue 255](https://github.com/mantleplace/mantleplace-dcc/issues/255) |
| Land cover | partial — [issue 43](https://github.com/mantleplace/mantleplace-dcc/issues/43) | supported |
| Land use, water bodies and road surfaces | planned — [issue 260](https://github.com/mantleplace/mantleplace-dcc/issues/260) | supported |
| Coverage rasters as data textures | supported | not applicable |
| Ground material names a renderer recognises | not applicable | supported |
| Published contours | planned — [issue 261](https://github.com/mantleplace/mantleplace-dcc/issues/261) | supported — [ADR 0013](adr/0013-revit-published-contours-are-directshapes.md) |
| Flood zones and steep ground | planned — [issue 270](https://github.com/mantleplace/mantleplace-dcc/issues/270) | supported — [ADR 0014](adr/0014-revit-hazards-are-drawn-on-a-hazard-plan.md) |

## Site context

| Capability | Unreal | Revit |
| --- | --- | --- |
| Context buildings | supported | supported — [ADR 0012](adr/0012-context-buildings-come-from-the-site-model.md) |
| Road centrelines | partial — [issue 280](https://github.com/mantleplace/mantleplace-dcc/issues/280) | supported |
| Tree points | partial — [issue 76](https://github.com/mantleplace/mantleplace-dcc/issues/76), [issue 196](https://github.com/mantleplace/mantleplace-dcc/issues/196) | supported |
| Parcels and zoning | planned — [issue 259](https://github.com/mantleplace/mantleplace-dcc/issues/259) | planned — [issue 268](https://github.com/mantleplace/mantleplace-dcc/issues/268) |
| The bundle's attribution, written into the project | planned — [issue 263](https://github.com/mantleplace/mantleplace-dcc/issues/263) | supported — [ADR 0011](adr/0011-revit-provenance-record-and-attribution-note-identity.md) |

## Large AOIs and streaming

| Capability | Unreal | Revit |
| --- | --- | --- |
| An AOI too large for one terrain | planned — [issue 73](https://github.com/mantleplace/mantleplace-dcc/issues/73), [issue 74](https://github.com/mantleplace/mantleplace-dcc/issues/74), [issue 75](https://github.com/mantleplace/mantleplace-dcc/issues/75) | declined — [issue 163](https://github.com/mantleplace/mantleplace-dcc/issues/163) |
| Stream the bundle beside the import, through Cesium | supported | not applicable |
