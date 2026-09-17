---
name: adr-0008-revit-drape-is-anchored-to-the-smooth-shading-origin
description: The Revit imagery drape's real-world texture offsets are written for the smooth-shading origin — the element's bounding-box corner — which makes a project-wide display checkbox the curator owns load-bearing for the photograph's correctness. Read before touching the drape, the smoothing call, or any prose that predicts what turning that checkbox off will look like.
status: accepted
---

# The Revit drape is anchored to the smooth-shading origin, and a curator's checkbox is therefore load-bearing

Revit measures a real-world-scaled bitmap's offset from **two different origins depending on a
display setting**, and Autodesk documents neither the second origin nor the dependency:

- **Toposolid Smooth Shading off** — the offset is measured from the **project origin**, and applied
  **per face, in each face's own plane**.
- **Toposolid Smooth Shading on** — the offset is measured from the **element's bounding-box minimum
  corner**, continuously across the whole surface.

One material carries one offset. There is no third value that is correct under both.

So the import turns smoothing on, reads the setting back, and writes every drape offset for the
smoothed renderer — the terrain from its own corner, and each site-boundary subdivision from its own,
each with its own material because one material carries one offset. Measured on a 1,419 × 1,413 m
site by exporting one view under both settings and matching every region against the published
photograph: anchored to the corner, the photograph sits within 1.6 m of the truth everywhere, on
smooth ground; correlation 0.955 against 0.31 for the faceted original.

## The decision, and what it costs

**The photograph's correctness depends on a project-wide display setting that belongs to the
curator.** Toposolid Smooth Shading is on the ribbon under Massing & Site ▸ Model Site, it applies to
every toposolid in the project including ones this plugin never touched, and while it is on Revit
does not draw toposolid surface patterns and ignores paint and graphic overrides on them. Those are
real reasons for a curator to turn it off — and turning it off scrambles the imagery.

That was accepted, over three alternatives:

| alternative | why not |
| --- | --- |
| **Anchor to the project origin and leave smoothing off.** | Flat shading maps the bitmap per face, in each face's own plane, so every triangle carries its own slice and the ground reads as a mosaic no view style, sun setting or self-illumination touches. Correct placement, unusable picture. |
| **Find one offset correct under both.** | There is one offset and two origins that differ by the element's own position — up to 978 m on a 1,086 m site. No single value satisfies both. |
| **Lock the setting, or turn it back on when the curator turns it off.** | It is project-wide and theirs. Silently reversing someone's display setting is the same trespass as silently setting it, and the plugin has no standing to hold a document-wide switch against its owner. |

The remedy that was taken instead is disclosure: the import log says which origin each material was
written for, names the ribbon path, and says what turning the switch off will do. Where Revit refuses
the setting the photograph is anchored to the origin instead and the log says to import again after
turning smoothing on by hand.

## The two mismatches do not look alike

This matters enough to record, because the plugin's own warning text got it backwards and the error
survived long enough to cost a session.

- An offset written for the **project origin** and rendered **smooth** rolls the photograph by half
  its size in both axes, and the repeat seam lands on the origin as **four quarters meeting at a
  cross**. This is the case the plugin never ships.
- An offset written for the **element's corner** and rendered **flat** — what a curator reaches by
  unticking the box — is mapped per face against an offset wrong by up to the element's own extent,
  so every triangle lands on unrelated ground and the site reads as a **shattered mosaic**.

Predicting the wrong one is worse than predicting nothing: a curator who sees a shattered mosaic and
was promised quarters concludes the sentence is about something else and goes looking. The sentence
now describes what they will see, and `TerrainSmoothingTests` asserts that it does not describe the
other mismatch.

## Consequences

- The drape and the smoothing call are one decision and cannot be reordered. `EnsureSmoothedSurface`
  runs **before** the material is written, because the offsets depend on its read-back answer.
- A subdivision cannot share the terrain's material. Each element's corner is its own, so each needs
  its own material — which is why an imported site carries as many drape materials as it has draped
  toposolids.
- Any future change to how drape offsets are computed has to state which origin it is writing for.
- `Toposolid.SetSmoothedSurface` and `Toposolid.IsSmoothedSurfaceEnabled` are **static**: the setting
  is per document, so there is no per-element escape from this and never will be while that is true.

## What was superseded

An earlier reading of the same evidence concluded that smoothing was *incompatible* with the drape
and that the importer should smooth only ground carrying no photograph. That was true of the code
that anchored to the project origin, and it stopped being true when the anchor moved to the element's
corner. The conclusion outlived the code in three comments and one README paragraph, where it read as
settled fact and sent a later session hunting an API call the importer had already been making for
two weeks.

The general lesson is cheap to state and was expensive to learn: **a measurement recorded as prose
beside the code it describes does not fail when the code changes.** Where the finding decides
behaviour, it belongs in a test.
