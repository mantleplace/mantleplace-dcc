---
name: host-support
description: The host support matrix — one row per user-facing capability, one column per DCC host, a fixed status in every cell, and a link to the release or ADR that status comes from. The only place a per-host feature status is stated. Read before saying what a host does or does not do, before planning work in one host from another's behaviour, and when a change adds, narrows or drops a feature.
---

# Host support matrix

What each host plugin does with each feature a bundle or the platform offers, and — where it does
not — which of three reasons applies: it has **not been built yet**, it is **declined** on purpose,
or it has **no meaning** in that host. A host that does the same job with a different construct (a
toposolid subdivision where the other paints a landscape layer) is `supported`, and a link says why
the construct differs where an ADR records it.

**This file states status and nothing else.** How a feature behaves is the host's own README
([Unreal](../unreal/README.md), [Revit](../revit/README.md); `autocad` has none until its first release); when a gap will close is
[`ROADMAP.md`](../ROADMAP.md); why a host differs is its ADR or issue, linked from the cell. None of
those keeps a feature list of its own. The one table elsewhere that names a host surface per action
is the standard's shared-vocabulary table (`HPS-51`), and it says which *words* a host shows, not
whether the host has the feature. Which pointer each host imports a category from, whether it comes in
by default, and what it was measured to cost is the [import inventory](import-inventory.md)'s, and
it states no status either.

**A status comes from a release or an ADR, never from an issue.** A release is the host's published
notes: what its *What's new* or *What it does* names is `supported`, what its *Known issues* names as a
gap is `partial`, and what no release of that host names is `not shipped`. An ADR decides `declined`.
Whether an issue is open or closed decides nothing here, and a cell does not link one for its status.

**Each column belongs to its host.** A change that adds, narrows or drops a feature in one host
edits that host's column in the same pull request, and no other. A new host adds its own column; a
new capability adds a row with a cell for every host.

## Statuses

| Status | Meaning |
| --- | --- |
| supported | imported and placed — or, for a row that imports nothing, working — and verified in the host |
| partial | as `supported`, with a gap the release names under *Known issues* — the cell links that release |
| not shipped | in no release of this host so far — the cell links the host's latest release, which is the record that it is absent. Nothing is promised: when it will ship is [`ROADMAP.md`](../ROADMAP.md)'s |
| declined | will not be done in this host — the cell links the ADR |
| not applicable | the capability has no meaning in this host |

A host with no release yet has no release to link: its cells say `not shipped` and that no release of the host exists, and the first
release replaces each with a status and a link. The `autocad` column is in that state. Each of its rows either matches Revit's,
the host it is built from, or names why it differs; none names a difference yet, and a row's `not applicable` is the same
reason Revit gives (the capability has no meaning in a host that has no streaming layer or no landscape material).

Every cell but `not applicable` is a status, an em dash and one or more links: the release it comes
from, by its tag, or an ADR by a path relative to this file, so the docs-integrity gate checks it. A
cell that names no more specific release links the host's latest one, whose *Verification* records
what was run. No cell describes behaviour. When a host ships a release, the cells it changes are
edited in the same pull request that updates the release.

## Account and vault

| Capability | Unreal | Revit | AutoCAD |
| --- | --- | --- | --- |
| Sign in and out in the system browser | supported — [unreal-0.8.0] | supported — [revit-0.1.0] | not shipped — no `autocad` release yet |
| List, prepare and download your orders | supported — [unreal-0.8.0] | supported — [revit-0.1.0] | not shipped — no `autocad` release yet |
| Import a complete order in one press, downloading only the host's files | supported — [unreal-0.8.1] | not shipped — [revit-1.0.0] | not shipped — no `autocad` release yet |
| Search the vault | supported — [unreal-0.8.0] | supported — [revit-0.2.0] | not shipped — no `autocad` release yet |
| Remove a download on request | not shipped — [unreal-0.8.1] | supported — [revit-0.2.0] | not shipped — no `autocad` release yet |
| A notice when a Prepare ends | not shipped — [unreal-0.8.1] | supported — [revit-0.2.0] | not shipped — no `autocad` release yet |
| A notice when an order arrives in the vault | not shipped — [unreal-0.8.1] | supported — [revit-0.2.0] | not shipped — no `autocad` release yet |
| Re-join a Prepare the host was closed during | not shipped — [unreal-0.8.1] | supported — [revit-0.2.0] | not shipped — no `autocad` release yet |
| Each bundle's thumbnail in the vault | not shipped — [unreal-0.8.1] | not shipped — [revit-1.0.0] | not shipped — no `autocad` release yet |

## Importing a bundle

| Capability | Unreal | Revit | AutoCAD |
| --- | --- | --- | --- |
| Import a downloaded bundle with no account | supported — [unreal-0.8.0] | supported — [revit-0.2.0] | not shipped — no `autocad` release yet |
| Refuse a manifest version outside the supported range | supported — [unreal-0.8.0] | supported — [revit-0.2.0] | not shipped — no `autocad` release yet |
| Read a 2.0 bundle as well as a 1.x one | supported — [unreal-0.8.0] | supported — [revit-0.2.0] | not shipped — no `autocad` release yet |
| Refuse a bundle whose bytes do not match its published digest | supported — [unreal-0.8.0] | supported — [revit-0.2.0] | not shipped — no `autocad` release yet |
| Say the bundle's unit system and delivery CRS | supported — [unreal-0.6.0] | supported — [revit-0.2.0] | not shipped — no `autocad` release yet |
| Choose what an import brings in, before it runs | not shipped — [unreal-0.8.1] | supported — [revit-0.1.0] | not shipped — no `autocad` release yet |
| Choose a fidelity level for each category, before it runs | partial — [unreal-0.8.0] | supported — [revit-0.1.0] | not shipped — no `autocad` release yet |
| Say what a bundle holds that an import cannot place, and why | partial — [unreal-0.6.0] | supported — [revit-0.1.0] | not shipped — no `autocad` release yet |
| Show an import step by step, and stop it | not shipped — [unreal-0.8.1] | supported — [revit-0.1.0] | not shipped — no `autocad` release yet |
| Import the same order again | supported — [ADR 0002](adr/0002-import-identity.md) | supported — [ADR 0004](adr/0004-revit-terrain-identity.md) | not shipped — no `autocad` release yet |
| Find everything an import made | supported — [unreal-0.8.0] | supported — [revit-0.2.0] | not shipped — no `autocad` release yet |

## Placement

| Capability | Unreal | Revit | AutoCAD |
| --- | --- | --- | --- |
| Place content at the published georeference | supported — [fixed-frame host](host-plugin-standard.md#11-frames-and-placement-hps-52--hps-54), [unreal-0.8.0] | supported — [order-frame host](host-plugin-standard.md#11-frames-and-placement-hps-52--hps-54), [revit-0.2.0] | not shipped — no `autocad` release yet |
| Refuse a file that is not in the host frame | supported — [unreal-0.6.0] | supported — [revit-0.2.0] | not shipped — no `autocad` release yet |
| Site location, sun and time zone | not shipped — [unreal-0.8.1] | supported — [revit-0.1.0] | not shipped — no `autocad` release yet |

## Terrain and land

| Capability | Unreal | Revit | AutoCAD |
| --- | --- | --- | --- |
| Terrain | supported — [ADR 0006](adr/0006-unreal-declines-elevation-bounds-target-crs.md), [unreal-0.8.0] | supported — [revit-0.2.0] | not shipped — no `autocad` release yet |
| Terrain as a mesh | supported — [unreal-0.8.0], [ADR 0016](adr/0016-unreal-mesh-terrain-is-parked-until-5-9.md) | declined — [ADR 0012](adr/0012-context-buildings-come-from-the-site-model.md) | not shipped — no `autocad` release yet |
| Imagery drape | supported — [unreal-0.8.0] | supported — [ADR 0008](adr/0008-revit-drape-is-anchored-to-the-smooth-shading-origin.md) | not shipped — no `autocad` release yet |
| Stylized map drape | not shipped — [unreal-0.8.1] | not shipped — [revit-1.0.0] | not shipped — no `autocad` release yet |
| Land cover | not shipped — [unreal-0.8.1] | supported, on a land plan — [ADR 0015](adr/0015-revit-subdivisions-are-for-built-surfaces.md) | not shipped — no `autocad` release yet |
| Land use, water bodies and road surfaces | not shipped — [unreal-0.8.1] | supported; land use on a land plan — [ADR 0015](adr/0015-revit-subdivisions-are-for-built-surfaces.md) | not shipped — no `autocad` release yet |
| Coverage rasters as data textures | supported — [unreal-0.8.0] | not applicable | not applicable |
| Ground material names a renderer recognises | not applicable | supported, for water bodies and road surfaces — [ADR 0015](adr/0015-revit-subdivisions-are-for-built-surfaces.md) | not shipped — no `autocad` release yet |
| Published contours | not shipped — [unreal-0.8.1] | supported — [ADR 0013](adr/0013-revit-published-contours-are-directshapes.md) | not shipped — no `autocad` release yet |
| Flood zones and steep ground | not shipped — [unreal-0.8.1] | supported — [ADR 0014](adr/0014-revit-hazards-are-drawn-on-a-hazard-plan.md) | not shipped — no `autocad` release yet |

## Site context

| Capability | Unreal | Revit | AutoCAD |
| --- | --- | --- | --- |
| Context buildings | supported — [unreal-0.7.0] | supported — [ADR 0012](adr/0012-context-buildings-come-from-the-site-model.md) | not shipped — no `autocad` release yet |
| Road centrelines | supported — [unreal-0.8.1] | supported — [revit-0.1.0] | not shipped — no `autocad` release yet |
| Tree points | supported — [unreal-0.7.0], [unreal-0.8.0] | supported — [revit-0.1.0] | not shipped — no `autocad` release yet |
| Parcels and zoning | partial — [unreal-0.8.1] | not shipped — [revit-1.0.0] | not shipped — no `autocad` release yet |
| The bundle's attribution, written into the project | supported — [unreal-0.8.0] | supported — [ADR 0011](adr/0011-revit-provenance-record-and-attribution-note-identity.md) | not shipped — no `autocad` release yet |
| ODbL content kept apart, and a Prepare for handover command | supported — [unreal-0.8.0] | supported — [revit-0.2.0] | not shipped — no `autocad` release yet |

## Large AOIs and streaming

| Capability | Unreal | Revit | AutoCAD |
| --- | --- | --- | --- |
| An AOI too large for one terrain | supported — [unreal-0.7.0] | not shipped — [revit-1.0.0] | not shipped — no `autocad` release yet |
| Stream the bundle beside the import, through Cesium | supported — [unreal-0.6.0] | not applicable | not applicable |

[unreal-0.8.1]: https://github.com/mantleplace/mantleplace-dcc/releases/tag/unreal-0.8.1
[unreal-0.8.0]: https://github.com/mantleplace/mantleplace-dcc/releases/tag/unreal-0.8.0
[unreal-0.7.0]: https://github.com/mantleplace/mantleplace-dcc/releases/tag/unreal-0.7.0
[unreal-0.6.0]: https://github.com/mantleplace/mantleplace-dcc/releases/tag/unreal-0.6.0
[revit-1.0.0]: https://github.com/mantleplace/mantleplace-dcc/releases/tag/revit-1.0.0
[revit-0.2.0]: https://github.com/mantleplace/mantleplace-dcc/releases/tag/revit-0.2.0
[revit-0.1.0]: https://github.com/mantleplace/mantleplace-dcc/releases/tag/revit-0.1.0
