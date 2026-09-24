---
name: adr-0014-revit-hazards-are-drawn-on-a-hazard-plan
description: Revit draws a bundle's flood zones and steep ground as filled regions on a hazard plan of their own — one floor plan per build, with a zone key — as context, never on the terrain, never through the Analysis Visualization Framework, and never redrawn once drawn. Read before changing how a hazard layer reaches a Revit project, before reaching for a toposolid subdivision or an analysis display style to show one, and before asking why a re-import does not refresh the plan.
status: accepted
---

# 14. Revit's flood zones and steep ground are drawn on a hazard plan

Date: 2026-09-24

## Status

Accepted, and implemented for both layers.

## Context

MPB 1.4.0 hands the Revit host its own copy of two hazard layers in its own frame: the flood map's
zones, each carrying the zone and subtype the map publishes, and steep ground at a threshold the
platform chose and states. The glossary names them **flood zone** and **steep ground**, and names
where they are drawn the **hazard plan**.

Every other polygon layer this host places becomes a toposolid subdivision. The curator an import is
made for is the visualiser presenting a render; the hazard layers serve someone planning the site,
and a flood zone read off a model can be taken for a determination about it, which only the
authoritative map panel makes.

## Decision

**Each hazard layer becomes filled regions in a floor plan the add-in makes for the build, beside a
zone key drawn in the same plan.**

- **One plan per build, on the project's lowest level**, named `Mantle Place Hazard Plan` and the
  build's token, cropped to the imagery's published rectangle and widened only to take in the key's
  swatches. The lowest level because an import that leaves the terrain out makes no level choice of
  its own. Both hazard layers share it.
- **Never redrawn.** A layer already on the plan is left alone; a re-import of the same build adds a
  layer the plan does not hold yet and appends its rows to the key; a later build gets a plan of its
  own and the old one is not touched. Each layer's regions carry their own stamp in Comments, and
  the plan is found by name.
- **Colour is host-owned and keyed on the published words**, (`fld_zone`, `zone_subty`) falling back
  to the zone alone, with a neutral type named with the code for a zone the table does not know.
  Steep ground is a hatch with no fill, drawn after the flood zones.
- **The zone key quotes the bundle.** Only what the plan shows, in the published wording; a heading
  saying it is context and not a flood determination; the FIRM panels to verify against where the
  bundle names them. The steep-ground threshold is the manifest's number, in degrees, after the
  schema's own comparison: "at or above", as MPB 1.4.0 describes `threshold_deg`.
- **A polygon Revit refuses is skipped and counted, never repaired.** Repairing a published polygon
  is derivation.
- **Both rows start unticked**, and neither needs the terrain.

## Considered options

- **Toposolid subdivisions**, as land use, land cover, water and road surfaces are cut. Rejected:
  flood zones overlap those layers, and coincident subdivisions do not draw in cut order, so the
  flood colour would fight whatever it lies over — and it would change the model a render is made
  from.
- **The Analysis Visualization Framework.** Built for overlays with a legend, and rejected because
  its results are not saved with the model: the plan would be gone the next time the project opened.
- **A translucent DirectShape draped over the terrain.** Rejected: 3-D clutter in every render, and
  an offset to lift it clear of the surface would be a placement value computed here.
- **A Revit Legend view for the key.** Rejected: the API cannot create one, only duplicate one a
  project already has.
- **Redrawing the plan on every import.** Rejected: a curator may have annotated it or put it on a
  sheet, and the site-context view's reuse-by-name holds a filter, not a build's drawing.

## Consequences

- **A hazard plan goes stale by design.** A later build's zones arrive on a new plan; deleting the
  old one is the curator's call.
- **The key sits outside the published rectangle.** The crop is widened east to hold the swatches,
  and the key's text shows because the plan's annotation crop is off.
- **The colours are this host's.** A curator who recolours a type keeps it, because the types are
  found by name.
