---
name: adr-0015-revit-subdivisions-are-for-built-surfaces
description: Revit cuts toposolid subdivisions only for the surfaces a designer builds against — road surfaces and water — and draws a bundle's land use and land cover as filled regions on a land plan of their own, one per layer per build, cropped to the order. Read before cutting a new polygon layer into the terrain, before bringing land use or land cover back onto the toposolid, and before asking where the Enscape grass keywords went.
status: accepted
---

# 15. Revit's subdivisions are for the surfaces a designer builds against

Date: 2026-10-03

## Status

Accepted, not yet implemented. It narrows what ADR 0014's context section describes: from this
decision on, land use and land cover are no longer cut as subdivisions.

## Context

Every polygon layer the Revit host placed on the terrain became a toposolid subdivision: land use,
land cover, water and road surfaces. Measured on a 2 km² order in Revit 2025, 2026 and 2027, those
cuts were most of a full import's time, and not a fixed share of it. Each subdivision commit makes
Revit rebuild the ground's relations, so each cut costs more as more exist, and in Revit 2025 a cut
costs in proportion to how much of the toposolid it covers rather than how many cuts share a commit.

Fidelity levels change the roads: the platform publishes a road class per polygon, so a host can cut
the major roads alone and the road step falls from about twenty minutes to under one. They do not
change land use and land cover. With every other layer at its lightest level, those two still took
six and a half minutes in Revit 2027 and eleven in Revit 2025 for 47 cuts, because they cover the
whole order, and a published land cover can cover more ground than the order does.

What a subdivision gives a curator was then weighed against that cost:

- **A region on the terrain** with its own material, area and edges, which follows the surface.
- **Under the imagery drape, its own look is gone.** The drape dresses every subdivision in the
  photograph, so in a render a land-cover cut looks exactly like the bare drape; what remains is an
  edge line and a selectable area.
- **Land use is a classification, not a surface.** A district named residential or commercial is
  context, which is what the hazard plan already draws.
- **Land cover carried one renderer benefit**: a grass keyword in the material name, which Enscape
  reads to grow 3D grass. That benefit has never been observed, because no machine this plugin is
  checked on holds an Enscape licence.
- **The cost outlives the import.** Every subdivision is a toposolid, so each later edit to the
  terrain regenerates every one of them.

Road surfaces and water are different in kind: they are surfaces a designer builds against, setting
paving edges, offsets and kerbs to them, and at their published levels they fit the budget.

## Decision

**A subdivision is cut only for a surface a designer builds against: road surfaces and water. Land
use and land cover are drawn as filled regions on a land plan, one per layer per build.**

- **Two plans per build**, a land use plan and a land cover plan, made as floor plans on the
  project's lowest level, as the hazard plan is, and named for the layer and the build's token. Two,
  because the layers lie over the same ground and their regions would cover one another on one plan.
- **Cropped to the order's published extent.** Polygons are drawn as published, and the view's crop
  decides what shows. Clipping a polygon to the order would be derivation; cropping a view is display.
- **One filled-region type per published class**, its colour host-owned and keyed on the published
  words, with a neutral type named with the class for a value the table does not know. Each plan
  carries a key quoting the published class names.
- **Never redrawn**, by the hazard plan's rule: a layer already on its plan is left alone, a later
  build gets plans of its own, and a plan a curator may have annotated or put on a sheet is not
  touched.
- **A polygon Revit refuses is skipped and counted, never repaired.**
- **The land-use and land-cover subdivision steps retire**, and so does the renderer-keyword table
  they alone fed. Road surfaces and water keep their subdivisions, and road surfaces take the
  published fidelity levels.

## Considered options

- **Keep both layers as subdivisions, unticked by default, and wait for published levels.** Rejected:
  a level that drops polygons does not lower Revit 2025's cost, which follows the area covered, and a
  box that cannot be ticked inside the budget is a layer the product does not really offer.
- **Retire every subdivision, road surfaces included,** and let the drape and the road centrelines
  carry the roads. Rejected: it removes the one cut a designer acts on, to save under a minute at the
  roads' lightest level.
- **Keep land cover as an opt-in subdivision for renderer grass.** Rejected: two representations of
  one layer, kept alive for a benefit no one has seen.
- **A land-classification image draped like the photograph.** Fast in Revit, but it gives a curator
  nothing to select, and the platform would have to render it. Not ruled out as a later addition;
  not a replacement for the plans.
- **Land use and land cover on the hazard plan.** Rejected: the hazard plan is for planning risk, its
  key is a flood key, and three layers over one ground would cover one another.

## Consequences

- **The terrain carries only roads and water.** A model imported before this decision keeps the
  subdivisions it was given; a terrain re-import refuses anyway (ADR 0004), so no model holds both
  representations of one build.
- **Land cover reads as a plan, not a render.** A visualiser who wants grass in a render paints it
  in the renderer, as with any other model.
- **The plans can show ground outside the terrain** where a published polygon runs past the order;
  the crop hides it, and a curator who widens the crop sees the polygon as published.
- **This host needs no fidelity level for land use or land cover.** A plan of filled regions costs
  seconds at any count, so a published level for either layer is ignored here.
