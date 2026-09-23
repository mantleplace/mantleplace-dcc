---
name: adr-0013-revit-published-contours-are-directshapes
description: Revit draws a bundle's published contours as one DirectShape per contour on a dedicated subcategory — not as model lines — placed only from its own host block, clipped to the terrain's crop window, at the published elevation, and a re-import of an earlier build refuses. Read before changing how contour linework reaches a Revit project, before reaching for ModelCurve or a line style to draw it, and before asking why the lines are not editable with the line tools.
status: accepted
---

# 13. Revit's published contours are one DirectShape per contour

Date: 2026-09-22

## Status

Accepted. Nothing implements it yet: the placement path waits on a format version whose Revit host
block points at the contours.

## Context

A bundle ships contour linework as a DXF of flat polylines, one per contour, each at its elevation,
in absolute projected coordinates. A Revit toposolid already draws contours of its own, so the value
of the published ones is civil legibility: the linework the order was built with, fixed, beside a
surface the user may go on to edit. The glossary keeps the two apart as **published contours** and
**toposolid contours**.

The request was for "native model curves on their own subcategory". Two facts pushed against the
obvious reading. A `ModelCurve` is one element per segment, and a State Plane sample bundle carries
about 180 000 vertices across 945 contours. And contours are, by definition here, not to be edited:
they are the published surface's, and a user who wants their own contours edits the toposolid.

## Decision

**Each published contour becomes one `DirectShape` whose curves are the polyline's segments, on a
dedicated "Published Contours" subcategory** — in the Topography category where Revit accepts a
`DirectShape` there, Generic Models otherwise, as roads fall back.

- **Placed from the host block only** (`HPS-52`, `HPS-53`). The file is placed only through a
  `contours` pointer in Revit's own block, whose frame the block's `file_frame` states. The
  host-neutral `elevation.contours` pointer states no frame, and a bundle carrying only that keeps
  the "Also in this bundle" line it has today.
- **At the published elevation.** Z gets the same unit conversion as the TIN's vertices and nothing
  else. The lines will not sit exactly on the surface, because Revit re-triangulates the vertices it
  is given; an offset to lift them clear would be a placement value computed here, which the root
  `CLAUDE.md` refuses.
- **Clipped to the terrain's crop window**, exactly at its edges, so the published contours end
  where the terrain ends. The window is the one the terrain tiers already use.
- **Build-scoped identity, refused on an earlier build.** The layer is stamped
  `{stem}/{build}` in Comments, and [ADR 0004](0004-revit-terrain-identity.md)'s table applies to
  the layer as a whole: this build's contours are reused, and an earlier build's refuse the step and
  name the prefix to delete. Contours drawn from one build over a terrain from another would
  disagree with the surface under them, silently.
- **Off by default, and not gated on the terrain row.** The toposolid already draws contours, and
  linework over a user's own surface is a legitimate use.
- **Toposolid contour display is never touched.** It is a setting the user owns.

## Considered options

- **`ModelCurve`s on a `Lines` line style.** What was asked for. Rejected on cost, about 180 000
  elements on one sample, and because editability with the line tools is the one property published
  contours should not have.
- **One `DirectShape` for the whole set.** Rejected: a contour could no longer be selected or
  queried on its own.
- **Skip-existing identity, as roads use.** Rejected: roads carry no build half, and for contours
  the build is the point.
- **Flatten to the survey point's elevation.** Rejected: a sheet of linework floating in every 3-D
  view is worse than lines that dip in and out of the surface.

## Consequences

- **Published contours cannot be edited as lines.** A user who wants editable contours draws their
  own or uses the toposolid's.
- **Index contours and elevation labels are not part of this.** They are a follow-up, and whether
  the index rule is published or chosen here is still open.
