# MPB manifest — consolidated changelog

The version history of the bundle manifest, in one place. Before this file, it was scattered across
a producer's source comments, the schema's own description text, and a private decision record.

Published schemas: `https://mantle.place/.well-known/schemas/bundle-manifest/` —
the integer pre-history at `v<N>.json`, the semver era at `<X.Y.Z>.json`. Every version listed here
is still served. The namespace has no directory listing; the index is
[`frozen.lock.json`](https://mantle.place/.well-known/schemas/bundle-manifest/frozen.lock.json),
which names each published version and the sha256 of its exact bytes.

**What is specified, and what is merely recorded.** The semver era is specified: the schema, the
prose in this directory, and the conformance corpus describe it together. The integer era is
**Public pre-history** — served forever, frozen, and summarised below so that a second implementer
can see how the shape arrived where it did, but not specified. Entries below the semver line are
history, not contract.

---

## Semver era

### 1.0.0 — the Mantle Place Bundle re-baseline (published and frozen 2026-08-22)

One clean break ends the integer era. No artifact, no pointer value and no unit changed: the
restructure is entirely key names, key locations, and honesty about fields that were already being
emitted.

- **`version` becomes the semver string `"1.0.0"`**, replacing the integer `19`. Consumers
  distinguish the two families by JSON type and compare the semver major.
- **snake_case everywhere.** The surviving camelCase keys were renamed — the top-level job and
  timestamp keys, the quantized-mesh block and its members, its licensing entry, five `layout` keys,
  and the sidecar block's size field.
- **`hosts.<hostId>` generalisation.** The per-host top-level blocks moved under one `hosts`
  envelope, and the separate readiness block folded in as `hosts.<hostId>.readiness`. A host plugin
  now reads exactly one subtree. This is the shape every future host inherits; adding one is a minor
  release.
- **The legacy PMTiles-era `terrain` block was retired**, with no replacement. Historical artifacts
  in re-packaged bundles stay discoverable through the declared legacy `layout` pointers, which keep
  their earlier "emitted only when the artifact exists" semantics.
- **Fields that were emitted but undeclared became declared**: the delivery-completeness verdict,
  the order identity, and the sidecar-only `bundle` block.
- **Platform-private runtime thresholds were evicted** into a non-normative `platform` block,
  explicitly outside the interchange contract.
- **Policy**: freeze-on-publish replaced freeze-on-supersede, and the compatibility policy became
  spec-facing and explicit. See [`compatibility.md`](compatibility.md).

---

## Integer era — Public pre-history

Every version below is frozen and still served. Versions 1–6 predate the published-schema era; no
schema was ever published for them, and their only record is the producer's own source history. The
published pre-history begins at v7.

### v19 — the Revit host block, and one closed reason vocabulary (2026-08-10)

- New optional top-level `revit` host block: pre-derived placement the plugin applies verbatim — a
  georeference in the delivery CRS, with identity grid rotation by construction and an origin
  carrying its own linear unit — plus per-artifact toposurface-points, surface-DXF and IFC-site
  entries with fail-closed digests. Deliberately **not** shaped like the Unreal georeference, which
  stays metric UTM unconditionally.
- BREAKING (restriction): the absence-reason field, in both the packaging block and the readiness
  block, became a closed enum drawn from one shared vocabulary — which required growing that
  vocabulary to cover every case it now had to close.
- Two additive edits landed after publication, legal under the freeze-on-supersede rule then in
  force: optional digest and point-count fields on the foliage-points entry, and the declaration of
  the imagery drape and a DEM bounds field.

### v18 — per-host readiness, and declaring what was already emitted (2026-08-09)

- BREAKING: the readiness block was normalised per host — `<host>.<import_path>` carrying presence
  and a reason — and the anonymous engine-specific keys moved under their host, beside the Revit
  sub-block that had been shipping undeclared. A reason is now carried on every absent path of every
  host.
- Everything the producer already emitted became schema-declared: the Unreal landscape-layer and
  foliage-point blocks, the Revit layout pointers, and the landcover and flood blocks with their
  pointers.
- An additive edit landed two hours after publication. That event is what exposed the freeze gap and
  produced the freeze ledger — and, eventually, freeze-on-publish.

### v17 — DCC-agnostic layout (2026-07-17)

- Pointer **values** moved to the tool-free zip layout: the engine- and tool-named folders folded
  into the modality vocabulary. Manifest **key names** were unchanged, and the pointer-driven
  importer needed zero code change — the clearest demonstration of why
  [the pointer doctrine](format.md#3-the-pointer-doctrine) exists.
- Additive: a top-level updated-at timestamp, re-stamped on each rebuild while the created-at
  timestamp began preserving the original build time; and a layout key recording lazy-migration
  provenance for older archives rebuilt on their next vault pick.

### v16 — imperial raster completion and the US coverage ladder (2026-07-16)

- On an effective-imperial order the delivered rasters convert too: the DEM *is* the feet DEM on the
  State Plane foot grid, imagery warps to the zone, and hillshade and contours derive from the feet
  DEM at round-foot intervals.
- One bump declared the whole coverage-ladder vocabulary up front: a new top-level `delivery` block
  naming the unit system, the tier, the horizontal EPSG, the linear unit and — where no projected
  foot zone exists — a local origin. Per-artifact units extended to raster entries, alongside a
  separate horizontal-units field. Ground sample distance stays honest metres always.

### v15 — delivery units (2026-07-16)

- Additive top-level unit system, and per-artifact units on the CAD/BIM entries. Engine geometry and
  georeferenced rasters stayed metres in this version; the rasters converted in v16.

### v14 — vault pick-and-process packaging semantics (2026-07-11)

- Honest packaging for a marker bundle whose artifacts have not been materialised yet: a delivery
  model, the fixed base set the initial job always produces, a packaging source value naming the
  on-demand path, and an absence reason meaning "available on request". On a marker bundle the
  selected set is overloaded to the full entitlement, while delivered and not-delivered carry the
  per-archive truth.

### v13 — layout renames and honest manifest semantics (2026-07-10)

- In-zip layout renames; keys unchanged, only pointer values moved.
- New top-level quantized-mesh block, making the tileset a first-class deliverable and reducing the
  older `terrain` block to legacy PMTiles only. The two legacy layout pointers became
  emitted-only-when-the-artifact-exists.
- A deliverable class marking bundles that carry an ODbL vector set alongside produced-work layers,
  plus the per-family `licensing` block that says which is which.

### v12 — packaging-format selection (2026-07-02)

- New required `packaging` block recording the resolved format selection, from which the
  completeness contract derives its expected set. Later format additions joined additively, without
  a bump.

### v11 — the ODbL vector-export set (2026-06-28)

- Optional `vector` block: vectors shipped **as** a licensed Derivative Database under ODbL
  pass-through, with per-layer file, format and digest entries.

### v10 — building massing (2026-06-27)

- Optional `buildings` block: extruded footprints baked as a Produced Work mesh, with footprint and
  triangle counts, height-source disclosure, format entries, and a layout pointer.

### v9 — unconditional maximum-resolution elevation (2026-06-26)

- A dead feature-flag was removed and coverage-aware maximum-resolution elevation became
  unconditional, with the resolved resolution recorded honestly. Added the per-zone mosaic
  breakdown.

### v8 — pipeline provenance (2026-06-22)

- Additive, always-present `pipeline` block, so that every bundle self-reports which code produced
  it and two bundles of the same AOI are distinguishable. Unresolved values use an explicit
  "unknown" sentinel rather than being omitted.

### v7 — the Unreal Engine import path (2026-06-17)

- Additive top-level `unreal` block: the 16-bit Landscape heightmap entry with its exact transform
  math, a flat projected georeference at the AOI-centroid UTM origin, the imagery drape, and a mesh
  alternative pointer — with the two matching layout pointers.
- The same change established the published, versioned schema series itself, publishing v7 and
  everything up to the then-current version retroactively. Which is why the public record starts
  here.
