---
name: adr-0012-context-buildings-come-from-the-site-model
description: Revit's context buildings are the site model's own per-building extrusions, copied out of Revit's IFC import into Generic Model DirectShapes — not footprints extruded here, not the building glb, and no longer a link. Read before changing where Revit's buildings come from, before reaching for footprints or the glb to get one, and before asking why a building's height is not a parameter.
status: accepted
---

# 12. Context buildings come from the site model

Date: 2026-09-17

## Status

Accepted.

## Context

Buildings reached a Revit project only as a linked IFC: the site model, converted to a companion
`.rvt` and linked origin to origin. A link is one element as far as a curator is concerned. It
cannot be selected, hidden or coloured per building, and the direct-link renderers a visualiser
presents in often drop or mislocate linked content. A curator building site context wants each
building as its own element.

A bundle carries buildings three ways, and each looks like a source:

- **The site model** — the IFC. One `IfcBuildingElementProxy` per building, each with its own
  `IfcExtrudedAreaSolid`, plus one `IfcGeographicElement` for the context terrain. No property sets.
- **The building mesh** — the glb. One merged mesh in one node: every building in the area is one
  object.
- **The building footprints** — the `building` vector layer. A polygon per building, with a `height`
  that is mostly null.

## Decision

**Copy each building's extrusion out of the site model into the project, as its own Generic Model
`DirectShape`, and leave the terrain element behind.** The site model is converted the way the link
step converts it; which of its elements are buildings is read from the IFC's own text
(`SiteModelReader`), each one is found again in Revit's conversion by the GlobalId the import records
on it, and its solid is handed to a new element in the project with no transform — the placement
the link had, origin to origin. No geometry is computed.

Each building is stamped `Mantle Place Building {stem}/{build}/{GlobalId}` in Comments, and
[ADR 0004](0004-revit-terrain-identity.md)'s table applies per building: this build's buildings are
reused, missing ones are created, and buildings from an earlier build of the same order refuse the
step and name the prefix to delete. The build half is the twelve-character token of the site
model's sha256; it is not redundant with the GlobalId, because an emitter is entitled to keep a
building's GlobalId stable across builds.

The link is **off by default** rather than removed. It is still planned, resolved and checked
against its digest, for the per-layer checklist to offer; its version-qualified companion `.rvt` is
written only when it is linked.

## Considered options

- **Keep the link.** Rejected: it is the problem. Every building is one unselectable element, and
  the renderers that matter to the people this import serves handle linked content worst.
- **Extrude the footprints here.** Rejected on the boundary, and on the data. Turning a footprint
  and a height into a solid is geometry derived from published data, which the root `CLAUDE.md`
  refuses at review even when the arithmetic is right — and the extrusion has already been done, by
  the platform, into the site model this bundle ships. Doing it a second time here is double work
  whose only possible outcome is a disagreement with the first. The heights are also mostly null in
  the vector layer, so it would not even be the same buildings.
- **Split the glb.** Rejected: it is one mesh in one node, so a per-building element would mean
  separating connected components of a merged mesh here — derivation again, with a failure mode
  (two touching buildings as one) the site model does not have.
- **`ElementTransformUtils.CopyElements` from the converted site model.** Rejected in favour of
  creating a `DirectShape` from each element's solid. Copying the element would carry whatever
  category Revit's IFC import assigned it, plus the import's own types and parameters, into the
  curator's project; a new `DirectShape` has the category this import chooses, the stamp it writes
  and nothing else.
- **Decide which elements are buildings from the category Revit assigns.** Rejected: that mapping is
  Revit's IFC importer's, not the bundle's, and a building filed under Site would vanish with nothing
  to notice. The entity the platform wrote is the published fact, and reading it is headlessly
  testable.

## Consequences

- **A building's height is geometry, not a parameter.** The site model ships no property sets, so
  there is nothing to fill a Height parameter with, and computing one from the solid is derivation.
  If that turns out to matter, the remedy is a platform request — a per-building height the manifest
  publishes — not arithmetic here.
- **The site model is converted on every import that has a building to create**, as one
  uninterruptible call, before the first chunk commits. An import of a build that is already present
  never converts it at all. The copying itself is chunked, 200 buildings to a transaction.
- **Revit's IFC import is now load-bearing twice**: the GlobalId it records is how each building is
  found, and its solids are what is copied. Neither is something the compiler can check. If a Revit
  version stops recording the GlobalId, the step says so in one line and copies nothing, rather than
  guessing by category.
- **A project imported before this change keeps its link**, and a re-import adds the context
  buildings beside it. Nothing here removes a link the curator may have set up views against; the
  link is theirs to remove under Manage Links.
