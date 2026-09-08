---
name: adr-0006-unreal-declines-elevation-bounds-target-crs
description: The Unreal reader declines `elevation.dem.bounds_target_crs` rather than consuming it, because every ground extent this host applies is already published inside its own `hosts.unreal` block — while the Revit reader consumes the same field, for a reason that does not transfer. Read before adding a reader for it, or before assuming the two hosts disagree by accident.
status: accepted
---

# The Unreal reader declines `elevation.dem.bounds_target_crs`

The bundle manifest publishes `elevation.dem.bounds_target_crs` — `[left, bottom, right, top]` of
the delivered DEM grid, in the delivery frame named by the sibling `elevation.dem.crs`. The Unreal
reader does not read it, and this record is the difference between *declined* and *forgotten*.

A published field that no reader consumes is indistinguishable from one a reader missed. The two
have opposite remedies, and only one of them can be told from the source: a decline that is written
down stays a decline when the next person reads the parser, and an omission that is not written down
gets "fixed" by someone who assumes it was an oversight.

## Why this host declines it

**Every ground extent this host applies is already inside `hosts.unreal`, addressed to it by name.**
The imagery drape's extent is `hosts.unreal.imagery_drape.extent`, with its own `extent_crs`, and
both are schema-**required** wherever a drape shipped. The heightmap's extent is the AOI implied by
`hosts.unreal.heightmap.landscape_transform` — the component counts times the quad span times the
per-axis scale — which is what `GetAoiSizeUeCm()` returns. Neither needs a second source.

**A second source is not a fallback; it is a conflict this host has no authority to resolve.**
`bounds_target_crs` describes the *delivered DEM grid* in the *delivery* frame. That frame is not
required to be this host's AOI-UTM frame, and on the imperial delivery tiers it is not: the DEM's
`horizontal_units` can read `ftUS` while the `hosts.unreal` georeference stays metric. Reading both
means writing a precedence rule for the case where they disagree — which is deriving a placement
value locally under a different name. `HPS-33` says a host applies its own block verbatim, and
`spec/format.md` §6 says placement values are pre-derived and must not be re-derived. Declining is
the rule; consuming would be the exception, and there is nothing here that needs one.

**Nothing this host does is currently blocked by not having it.** Consuming a field only to hold it
in a struct nobody reads is the same state this record exists to close, one field over: a value the
platform publishes, a reader that stores it, and no behaviour anywhere that depends on it.

## Why Revit consumes it, and why that does not transfer

The Revit reader **does** read `elevation.dem.bounds_target_crs`, as the imagery drape's ground
extent, and that is deliberate on both sides. The asymmetry is not two hosts disagreeing about one
field; it is two hosts in genuinely different positions:

- The declared drape extent lives in `hosts.unreal.imagery_drape`, and **Revit may not read another
  host's block** (`HPS-33`). It has no declared extent of its own to prefer, so the host-neutral
  field is the only one it can reach.
- Because the field is *not described* by the published schema's `elevation.dem` properties, the
  Revit planner does not trust it on the manifest's word: it is used only when the drape PNG's own
  IHDR grid times `imagery.gsd_m` reproduces the extent within 2 px, and the drape is skipped as
  `ExtentNotCorroborated` otherwise. That corroboration is the price of reading an undeclared field,
  and it exists precisely because the field is a fallback rather than a contract.

Unreal has the contract. It does not need the fallback, and taking it on would mean taking on the
corroboration too — for a value it already has, declared, in its own block.

## Consequences

- **`FMantlePlaceVaultManifest` gets no field for it, on purpose.** A parsed field with no reader is
  the thing this record refuses; the decline lives as a comment at the georeference block, pointing
  here.
- **This is reversible on evidence, not on tidiness.** If the platform publishes a host-neutral
  `imagery.drape` block — one is proposed — or if a delivery tier stops publishing
  `hosts.unreal.imagery_drape.extent`, the trade changes and this record gets a successor. "The
  field exists and we ignore it" is not that evidence.
- **No corpus case asserts the decline.** A shared case built on a field the schema does not
  describe would bind every host to a coincidence, which is the same reason the Revit host proposed
  none for its own use of it. The record is the artifact; the parser is the proof.
