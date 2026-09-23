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

### 1.3.0 — files state their frame, and a host is handed its own (additive minor; published and frozen 2026-09-23)

One minor carries the whole change: every item below is additive, and nothing is removed or
re-meant, so it is a MINOR under [compatibility](compatibility.md) §2 and every host floor at 1.0.0
reads it.

- **`hosts.revit.drape`** is new and optional: the satellite imagery drape in the Revit origin's
  frame, with its `extent`, `extent_crs`, `units`, pixel `width` and `height`, and `sha256`. On a
  State Plane delivery it names a drape baked from the delivered State Plane imagery,
  `Imagery/Drape.StatePlane.png`; where the delivery grid is already the metric UTM grid it names
  `Imagery/Drape.png`. Why a drape is carried per frame is [format](format.md) §6.4.
- **`imagery.drape` is unchanged in shape and is now described as what it always was**: the
  fixed-frame drape, on the AOI's metric UTM grid on every delivery. Before 1.3.0 a State Plane
  bundle carried no drape on its delivery grid at all, so a Revit reader that placed
  `imagery.drape` against a State Plane origin had nothing correct to place.
- **`hosts.<hostId>.file_frame`** is new on `hosts.unreal` and `hosts.revit`, beside the
  `georeference` it is read with: the CRS and the horizontal and vertical units every file the block
  points at shares, as a projected or a local frame ([format](format.md) §4.2). Unreal's is the
  AOI's metric UTM zone on every delivery; Revit's follows the delivery.
- **`hosts.revit.vectors`** is new: the vector layers the Revit host places, as GeoJSON in its own
  frame on every delivery, so a State Plane delivery no longer leaves Revit with only a geographic
  set it may not project ([format](format.md) §6.5). `hosts.revit.readiness` gains `vectors`.
- **`vector.crs`** is new: `EPSG:4326`, stating the frame the shared set has always been in.
- **`landcover.tree_points` and `hosts.unreal.foliage_points` each state their file's frame** —
  `crs`, `units` (the `ground_z` column) and `horizontal_units` (`x`, `y`) — and REQUIRE it. The
  Unreal pointer's units are `m` by schema, and on a foot delivery it names a new file,
  `Landcover/TreePointsMetric.csv`, the same trees restated in the metric UTM grid; on a metric
  delivery it still names `Landcover/TreePoints.csv`. Before 1.3.0 it named the delivered file on
  every delivery, so on a State Plane delivery a reader subtracting its metric origin put every tree
  a thousand kilometres off site. A foot delivery built before 1.3.0 carries no `foliage_points`
  until its next rebuild. No column is renamed ([format](format.md) §6.6).
- **`delivery.label`** is new and optional: the display words for the delivery CRS — the EPSG
  registry name of the projected zone, or a fixed phrase on a local grid — the same words the
  platform shows a person. Every earlier bundle lacks it; there, the same words follow from `tier`
  and `horizontal_epsg`, which have described the delivery frame since the block arrived.

### 1.2.0 — the foliage type (additive minor; published and frozen 2026-09-19)

The tree-points CSV gains a sixth column, `foliage_type`, and `landcover.tree_points` gains an
optional `foliage_type_vocabulary` naming the closed vocabulary its values are drawn from — `"1"`,
which is `tree` and `shrub`, read from the ESA WorldCover class under each point. The rules a reader
follows are [format](format.md) §4.4. `hosts.unreal.foliage_points.columns` now states the columns
the CSV actually carries rather than a fixed list of five.

Nothing is removed or re-meant, so this is a MINOR under [compatibility](compatibility.md) §2 and
every host floor at 1.0.0 reads it. Both hosts re-pin to 1.2.0 with the column exercised: Unreal's
tree-points reader resolves columns by header name, and Revit's reader already did. The pins move
from 1.1.0, which both hosts took on 2026-09-19.

⚠️ **Additive in the schema is not additive in every reader.** Unreal's tree-points reader as
released through `unreal-0.4.0` matched the header as one exact string and counted five fields, so
the new column would have dropped the whole tree layer on import, with the import still reporting
success. The platform emits the column only once a tagged Unreal release carries the fixed
reader — `unreal-0.5.0`, tagged the same day — the order 1.0.0 was shipped in, for the same reason.
[Format](format.md) §4.4 now states the rule that reader broke.

### 1.1.0 — the location block and its time zone (additive minor; published and frozen 2026-09-19)

A new **required** top-level block, `location`, holds facts about the place that are the same for
every host. Unlike `hosts.<hostId>`, which a host reads only its own of, every host may read it
([format](format.md) §4.1). Its first member is `location.time_zone`:

| Field                   | Meaning                                                                                   |
| ----------------------- | ----------------------------------------------------------------------------------------- |
| `iana`                  | IANA zone name, e.g. `America/Denver`. The authority for a host with tzdata.              |
| `utc_offset_standard_h` | Standard-time offset in hours; the smallest offset in the build year. Fractional, unclamped. |
| `observes_dst`          | Whether the clocks show more than one offset in the build year.                           |
| `tzdata_version`        | The IANA tzdata release the offsets came from. Provenance only.                           |

**Why it is not under `hosts.revit`.** The Revit plugin asked for it there so that `SiteLocation`
could be completed without deriving a zone from longitude. But Unreal's SunSky takes a time zone
too, and neither host takes a zone name: both take a standard offset and a separate daylight-saving
switch. So the numbers are pre-computed once, beside the name, and published where every host can
read them.

**Where it is resolved.** At the AOI-bbox centroid, the point `delivery` already resolves from, for
the year of `updated_at`. The boundaries cover the whole globe, open ocean included (`Etc/GMT±N`), so
the block is always present on a 1.1.0 manifest.

**Attribution.** The zone is looked up in timezone-boundary-builder's boundaries, which are ODbL.
Every bundle credits them in `attribution.sources`.

**Required, and still a minor.** Each published schema pins `version.const` to exactly one version,
so "required" binds only a producer writing a 1.1.0 manifest; no 1.0.x document is ever validated
against it. To a consumer the block is additive, which is what [compatibility](compatibility.md) §2
asks of a minor. "Narrowed" there means narrowing an existing field, and nothing existing changed.

**What a host must do.** Nothing, to keep importing: a 1.0.x reader ignores the unknown block, per
[compatibility](compatibility.md) §3. A host that wants the sun right reads `location.time_zone`.
Each host re-pins `verified-against.json` to 1.1.0 once its reader has been exercised against it.
Both registered hosts did so on 2026-09-19, on the corpus case `manifest.locationBlockIgnored`, which
binds a reader to accepting the block it does not consume.

### Within 1.0.1 — the vector layer vocabulary, stated (2026-09-18)

No schema version was published, and nothing a reader parses changed. The seven names a
`vector.layers` entry can carry, and what a layer's presence or absence means, are now written down
in [format §6.3](format.md#63-vector-layers-by-name). The corpus gained
`manifest.vectorLayerVocabulary`, which names every layer, and `manifest.vectorLayerUnknownName`,
which a reader must accept while ignoring the name it does not know.

The vocabulary lives in the prose and the corpus, not in the schema. The schema has always typed a
layer's `name` as a free string. Closing it with an enum would narrow what validates, and under
[compatibility](compatibility.md) §2 a narrowing is a MAJOR change. It would also turn every future
layer into another release.

One producer behaviour changed to make the absence rule true. A layer used to be skipped silently
when reading or writing it failed, while the rest of the set shipped, so a failure looked like an
empty area. Now any failed base layer withholds the whole set, and the packaging block reports it
as a failure. That changed only when a set ships, not its shape, so this is not a version.

### 1.0.1 — the editorial patch (published and frozen 2026-08-24)

Nothing a reader parses changed. 1.0.0 was published *ahead* of the producer cutover, so its
`description` opened with a banner saying the version was not yet published and that v19 remained
canonical — true on the morning it was frozen, false by that afternoon. Freeze-on-publish has no
exception for prose, so the only way to correct a published description is to supersede it, and
§2 of [compatibility](compatibility.md) says an editorial change is a PATCH.

1.0.1 is that patch: the same shape, with the banner retired.

**"Editorial" is proved, not asserted.** The two documents differ at exactly seven JSON paths —
`/$id`, `/title`, `/description`, `/properties/version/const`,
`/properties/version/description`, `/examples[0]/version`, `/examples[1]/version` — and are equal
once those are normalised away. `check_manifest_conformance.py` performs that
recursive strip-and-compare itself, so a release mislabelled as a patch fails the gate.

**What a host must do: nothing.** Both plugins compare against a floor, so a 1.0.1 bundle imports
on the shipped v0.2.0 with no re-pin and no release. That is the PATCH obligation in
[compatibility](compatibility.md) §2 working as specified.

⚠️ **What it did cost.** `version.const` couples the schema document to the value every bundle
stamps, so an editorial patch restamps production output and obliges one more ETL Suite promote.
A patch is free for *consumers*; it is not free for the producer.

The conformance corpus stays pinned at 1.0.0 deliberately — nothing links the corpus pin to a host
pin, and `validity-truth-table.json`'s `minSupportedManifestVersion` is asserted with exact
equality against both host floors.

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

> **On this schema's own `description`.** 1.0.0 was published and frozen *ahead* of the producer
> cutover, so that host plugins could pin the next contract over the network before any bundle
> carried it. Its `description` therefore opens with a banner saying the version is not yet
> published and that v19 remains canonical — true on the morning it was frozen, false by that
> afternoon, when the producer and the web consumers cut over. Freeze-on-publish is one-way and has
> no exception for prose, so the banner stays exactly where it is: the bytes a reader hashes against
> [`frozen.lock.json`](https://mantle.place/.well-known/schemas/bundle-manifest/frozen.lock.json)
> are the bytes that were published. **1.0.0 is the canonical contract as of 2026-08-22**, banner
> notwithstanding; every constraint in the document is correct and current. It was superseded by
> the editorial [1.0.1](#101--the-editorial-patch-published-and-frozen-2026-08-24) on 2026-08-24;
> 1.0.0's bytes, banner included, stay published and frozen as the record of that publish moment.

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
