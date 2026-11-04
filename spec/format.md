<!-- SPDX-License-Identifier: CC-BY-4.0 -->
<!-- SPDX-FileCopyrightText: Mantle Place LLC -->

# The MPB format

How a Mantle Place Bundle is put together, and how a consumer is meant to read it.

The normative shape of the manifest is the published JSON Schema, cited throughout by block name
rather than restated: `https://mantle.place/.well-known/schemas/bundle-manifest/`.
This document explains what the blocks are *for* and which of them a reader is obliged to honour.

Requirement words — MUST, MUST NOT, SHOULD, MAY — carry their
[RFC 2119](https://www.rfc-editor.org/rfc/rfc2119) meanings. Every MUST below is decided by the
schema, by the conformance corpus, or by both; where a corpus group settles the question, this
document says which one.

## 1. A bundle is a zip with one human file and one machine entry point

```
<bundle>.zip
  README.md                 the only top-level file — written for a person
  Metadata/manifest.json    the machine entry point — everything else is reached from here
  <Modality>/…              the artifacts
```

Everything else lives under a modality folder. The README is generated per bundle and is not part of
the contract; a consumer MUST NOT parse it. This section and §2 describe the layout of a 1.x bundle;
the layout of a 2.0 bundle is §10.

A **sidecar manifest** is also published beside the zip in the vault, carrying the same document
plus one extra block — see [§7](#7-the-sidecar-manifest). Inside the zip,
`Metadata/manifest.json` is the manifest.

## 2. Modality folders are human-facing, and machines never navigate by them

In a 1.x bundle the top-level folders are one-word nouns naming a **modality**, not a tool: `Basemap`, `Imagery`,
`Elevation`, `Surface`, `Site`, `Mesh`, `Vector`, `Landcover`, `Flood`, `Metadata`. The vocabulary
is a frozen allowlist on the producing side — growing it is a deliberate, reviewed act — and it is
frozen so that a curator opening the zip finds the same words next year.

The naming rule inside a folder: **basename is artifact identity, extension is format.**
`Surface.dxf` and `Surface.landxml` are one artifact in two formats. A compound basename appears
only where a file must still describe itself after being copied out of its folder.

**None of this is a machine contract.** Folder names are human-facing convention for a consumer;
they have already been reorganised twice (1.12.0 and 2.0), and a pointer-driven consumer needed no
change to survive either. Which brings us to the rule that matters most in this document.

## 3. The pointer doctrine

⛔ **A consumer MUST locate every file through a manifest pointer VALUE, and MUST NOT construct a
path from folder convention.**

Two kinds of pointer carry every file in a bundle:

- the **`layout` block**, whose keys name artifacts and whose values are archive-relative paths; and
- **per-artifact `path` fields** inside the blocks that describe an artifact in detail.

The keys are stable; the values move. A pointer is present when its artifact shipped, and some layer
pointers are explicitly null when their layer is absent — the schema states which behaviour each
pointer has. Reading a pointer value and extracting exactly that zip entry is the whole procedure.

**The manifest itself is the one exception**, of necessity: `Metadata/manifest.json` is found by
convention, because a reader has to open it before it can read a pointer to anything. It also
publishes a pointer to itself, which is how a consumer that already holds the parsed document can
name the entry it came from. Every *other* file in the bundle is found by pointer.

The failure this prevents is not hypothetical, and it is not loud. A consumer that hardcodes a
well-known relative path keeps working until the producer reorganises the archive, and then fails on
a customer's bundle rather than in anyone's CI.

**Archive paths are untrusted input.** A consumer MUST resolve every pointer inside its extraction
root and reject entries that escape it. The producer does not emit such paths; a reader that assumes
so is one malformed zip away from writing outside its own directory.

## 4. The manifest has three registers

One JSON document — but its top-level blocks divide by *who they are for*, and a reader's
obligations differ by register.

### 4.1 The neutral core — the bundle's own facts

Host-independent truth about what was delivered: the AOI bounds, the build's identity and
timestamps, the delivery CRS and unit system, the packaging contract this bundle was produced
against, the completeness verdict, a block per modality describing the artifacts that shipped,
licensing, attribution, and pipeline provenance.

Every consumer reads this register. It is the half that answers *what is in this bundle, and under
what terms*.

**`location` — facts about the place (from 1.1.0).** Some facts are the same for every host because
they describe the place, not a host's frame or units. Those live in the top-level `location` block,
and every host MAY read it. The first is `location.time_zone`: the IANA zone name, the standard-time
UTC offset in hours, whether the zone observes daylight saving, and the tzdata release the offsets
were read from. It is resolved once, at the AOI-bbox centroid, so a bundle has one time zone even
when its AOI straddles a zone line.

- ⛔ **Apply it; never derive it.** A host MUST NOT compute a time zone from longitude, and MUST NOT
  parse an offset out of the zone name: POSIX sign inversion makes `Etc/GMT+2` UTC−2.
- The offset is fractional where the zone is (5.5, 5.75, 12.75) and is **not clamped** to any host's
  range. Kiritimati is 14. A host whose API accepts only ±12 wraps the value and owns that choice.
- Daylight-saving **dates** are not published, because they change every year. A host that needs them
  resolves `iana` with its own tzdata; a host that only takes an on/off switch reads `observes_dst`.
- `location` is not an artifact family. `licensing.site` classifies the IFC Site model, which is
  unrelated.

**Per-file frames, the site origin and licensing (from 1.14.0).** The core can also state, file by
file, how each file is placed and under what terms (`file_entries`), and one point every offset
file is measured from (`location.site_origin`). Both are optional and neither replaces a host block.
§6.8 describes them.

### 4.2 `hosts.<hostId>` — pre-derived placement, one subtree per host

Everything host-specific lives under `hosts.<hostId>`, and a consumer targeting a host reads
**exactly that subtree** of the host blocks. Facts that are identical for every host do not belong
here. They live in `location` (§4.1), which is never copied into a host subtree.

⛔ **A consumer MUST NOT read another host's block**, and MUST NOT merge two. The blocks are
deliberately not shaped alike: one host wants a flat projected frame in metric UTM, another wants a
survey point in the delivery CRS. A reader that takes whichever block it finds first and treats it
as "the georeference" will report a plausible CRS and place a site in the wrong country, with
nothing failing. The corpus pins this with a case carrying two conflicting host georeferences on
purpose.

A host sub-block that places files states the frame they share, at `hosts.<hostId>.file_frame`: a
CRS, a horizontal linear unit and a vertical linear unit, each declared. A frame is either
**projected** — the files' coordinates are in the named `crs`, and any origin is placement carried
elsewhere in the block — or **local** — the files are offsets about an `origin` that is part of the
frame's identity, on the grid named by `base_crs`, and no `crs` is named because the files are in
none. Every coordinate-carrying file a block points at is in that block's `file_frame`; the
producer checks this before it publishes. `file_frame` describes the FILES. The origin keeps its
own unit, and on a delivery with no projected foot zone the two differ (§6).

A host sub-block always carries its **readiness** verdicts (§6.1). It does not always carry a
payload: a bundle whose artifacts for that host have not been materialised yet is readiness-only,
and that is a well-formed manifest, not an error.

New hosts join additively — a new sub-block in a minor release. A consumer MUST ignore host ids it
does not recognise, and MUST NOT infer anything from their absence.

### 4.3 `platform` — present, reserved, and not part of the contract

Non-normative. It carries values the Mantle Place web application reads instead of hard-coding them.

⛔ **No consumer may depend on the `platform` block.** Its inner shape may change in a minor
release, which is exactly what "not part of the interchange contract" buys. It is documented here so
that a reader who encounters it knows it is deliberate rather than an undocumented leak.

### 4.4 Tabular artifacts, and the foliage type

Where a delimited-text artifact's manifest entry publishes `columns`, the file carries that list as
its header row. ⛔ **A reader MUST resolve those columns by header name, never by position, and MUST
ignore a column it does not know.** A minor release may append a column; a reader that matches the
header as one exact string, or counts fields, turns that additive change into a missing layer. The
corpus pins this in `manifest.treePointsRowCount`, with an appended, a reordered and a missing
column. That case is scoped to the one host whose block it reads; a host it does not reach pins the
same rule in its own suite.

`Landcover/TreePoints.csv` is the case that exists today. From 1.2.0 its last column is
`foliage_type`, and `landcover.tree_points.foliage_type_vocabulary` names the vocabulary its values
come from. The vocabulary is **closed and owned by the platform**; a host maps each value to its own
family, symbol or asset and never derives one.

- **Vocabulary `"1"`** is `tree` and `shrub`. A point is a `shrub` where the ESA WorldCover 2021 v200
  class under it is Shrubland (20), and a `tree` for every other class and where WorldCover has no
  data. It is never inferred from `height_m` or `crown_radius_m`.
- ⛔ **A host that reads `foliage_type` MUST read a value it does not know as `tree`.** The
  vocabulary may grow, and a new value is a new vocabulary version — never a change of meaning for
  an existing one. So a vocabulary the host does not know is no reason to ignore the column: the
  values it knows keep their meaning, and it maps them. `manifest.foliageTypeUnknownValue` binds
  both halves, scoped to the first host that read the value; a second host reading it takes the
  scope off rather than adding a case of its own.
- A manifest without `foliage_type_vocabulary` has no `foliage_type` column; read every point as a
  tree.

## 5. Integrity

Artifacts carry a lowercase-hex sha256 of their exact bytes. The rules for using it are short, and
the interesting half is what to do when a hash is absent.

⛔ **Where the schema requires a hash, a consumer MUST verify it and MUST fail closed on a
mismatch** — refuse the import, and name the artifact. Importing bytes that failed verification is
worse than importing nothing, because the failure surfaces later, as data that looks real.

⛔ **A missing optional hash means _unknown_, not _corrupt_.** Where the schema does not require a
hash and none is present, the check is skipped and the artifact remains valid. A consumer SHOULD
report that distinction rather than claim a verification it did not perform. Treating unknown as
corrupt makes every older bundle un-openable; treating it as verified is a lie. A hash that IS
required and is absent is a producer bug and a refusal — the two cases are told apart by the schema,
never by guesswork.

Where an artifact carries no hash of its own, a consumer resolves one by matching its path against
the manifest's own format tables rather than concluding there is none.

Downloading is outside this spec, but one adjacent practice is worth stating because getting it
wrong corrupts a cache silently: write a download to a temporary path, verify it, and promote it by
rename. A consumer that streams onto the final path leaves a truncated file that looks cached, and
imports it on the next run.

Corpus groups `digest` (NIST FIPS 180-4 known answers, including the streaming-equivalence cases)
and `cache` are the executable form of this section.

## 6. Placement, coordinates and units

⛔ **Placement values are pre-derived by the platform and MUST be applied verbatim. A consumer MUST
NOT re-derive them.** Scales, offsets, origins, extents, rotations, quantisation mappings — all of
it is computed once, upstream, and published. The consumer multiplies published numbers into its own
coordinate convention and does nothing else.

This is the most important rule in the spec, and it is a rule rather than advice because the failure
mode is a bundle that imports successfully and is wrong. A consumer that recomputes a transform "to
be safe" has built a second implementation of a pipeline it cannot see, and the two drift in the
field rather than in CI. The corpus carries the expected derived values with explicit tolerances;
compare against those, and never against exact floats.

Two consequences are worth spelling out, because both have bitten:

- **Grid rotation is identity by construction.** The producing emitters reproject per vertex, so
  meridian convergence is already absorbed into the coordinates. A consumer that assumes rotation
  must be derived will derive it, and be wrong by metres per kilometre.
- **The units question has more than one answer in the same bundle, deliberately.** The bundle-level
  unit system, the delivery tier and its linear unit, and each artifact's own units field describe
  different scopes, and they can legitimately disagree — a delivery whose region has no projected
  foot zone ships an origin stated in one unit beside artifacts stated in another, which is exactly
  why the unit is published next to the coordinates it describes instead of being inferred once per
  bundle. **Read the unit from the thing you are placing.** The schema is the authority on which
  field describes which scope.

⛔ **A consumer MUST fail closed on a unit or an enum value it does not understand**, naming the
offending value. A linear unit misread by a factor is a site misplaced by a whole ratio, and a
silent fallback to a default is how that happens. The schema states unknown-value behaviour per
enum: some are informational and are echoed opaquely, others are load-bearing and fail closed.

**A height has a datum, and one bundle can carry two.** Every height is an orthometric height in a
named vertical datum. The files in the delivery CRS can be in a national datum while the content
made for an engine keeps one datum on every order, so that a fixed-frame host's ground is the same
everywhere. Which content is in which is the schema's to say: its `$defs/verticalDatum`, and the
`vertical_datum` each entry that carries heights declares. **A host whose ground is in one datum
places only heights in that datum.** A fixed-frame host's ground is the engine's, so where it
places a file that is not engine content it reads the datum the file states and refuses by name a
height in any other. An order-frame host's ground and everything it places from one bundle are
delivery-CRS content, and within one bundle they share the delivery's datum. A consumer MUST NOT
convert one datum to the other: the published difference between them is one number for the site,
while the producer shifted its content point by point, and applying that number is re-deriving a
placement value. A datum a consumer reads and does not know fails closed.

⛔ **Where the manifest states a value twice over, a consumer MUST verify the identity and refuse on
mismatch** rather than picking one. Those redundancies are published where a host's own API demands
internal consistency; they exist to be checked.

**Local projection is permitted in exactly one narrow case.** Where a bundle ships geometry whose
own coordinates are geographic — a vector layer in lon/lat, which the manifest describes as a layer
rather than vertex by vertex — a consumer must project in order to place it, and MAY do so. That is
the only projection a consumer performs; it is not licence to carry a geoprocessing stack. A
consumer that does project MUST match the corpus `projection` group's known answers, southern-
hemisphere false northing included. Getting a zone wrong places geometry kilometres away while every
test that does not check numbers still passes.

### 6.1 Honest absence

Three parts of the manifest exist so that a consumer can explain a missing artifact instead of
dead-ending on an empty import:

- **`packaging`** — the format contract this bundle was produced against: what was requested, what
  was delivered, and one honest row per requested-but-absent item saying why.
- **`completeness`** — the bundle's own verdict against its must-ship set.
- **`hosts.<hostId>.readiness`** — per-import-path verdicts for that host: present or not, and when
  not, the stated reason.

⛔ **A consumer MUST surface the stated reason rather than matching on it.** The reason vocabulary
is the schema's to grow; a consumer that branches on the values it knows shows a blank dialog for
the value it does not. Echo it.

An absent artifact is not an error. "Not produced", "not selected" and "available on request" are
three different sentences to show a user, and the manifest is what distinguishes them.

### 6.2 Is this bundle usable at all

⛔ **A consumer MUST decide "has this bundle been materialised" from the manifest's neutral signals,
not from the presence of its own host block.** The signals are host-agnostic — a non-empty `hosts`
object, or a non-empty set of vector layers — and any one of them is sufficient. Note what that
means: a bundle materialised for a host this consumer has never heard of still reads as
materialised, which is the point. Such a bundle is a well-formed manifest that the consumer parses
and then declines to *import*, with guidance — not an invalid document, and not an unmaterialised
one. The corpus decides this one, row by row.

⛔ **A bundle with no host block at all MUST still parse.** The top-level facts — bounds, layout
pointers, packaging, the order identity — are readable and MUST be read before returning "not
importable", so that a caller can still identify the bundle it is holding.

Two identifiers appear in the manifest and are **not interchangeable**: the per-rebuild job identity
and the order identity. Only the order identity joins a bundle to its vault entry.

### 6.3 Vector layers, by name

The `vector` block describes the vector set: one entry in `vector.layers` per layer, each carrying a
`name` and one row per delivered format. A layer that mixes geometry families may instead be
**split** by family: it then carries one row per family in a format, each naming its
`geometry_family` and pointing at a file that holds that family alone, or one GeoPackage row that
names a table per family in `tables`. The [changelog](changelog.md) records when splitting
arrived. A consumer finds a layer by its `name` and then its files by
their format rows, and it reads every row of the format it takes: a row naming no family is the
whole layer, and of a split layer it takes the file of each family it uses. It MUST NOT guess a
layer from a file name (§3). It also MUST NOT fall back to a format it cannot read when the one it
wants is missing, nor take one family's file for another's. The corpus pins the first two:
`manifest.roadSplinesGeojsonWins` puts a decoy layer beside the one being selected, and
`manifest.roadSplinesGpkgOnly` offers only a format the reader does not take. The Revit host's
`manifest.revitSplitLayerFallback` lists a split layer's line file before the polygon file that host
cuts water bodies from.

**A GeoPackage row that lists no `tables` holds exactly one feature table.** The row is then the
whole layer in one file, and the table is the one the file holds. A producer names that table in
PascalCase after the file stem (`RoadSplines` in `RoadSplines.gpkg`), and not after the layer
(`road_splines`). The spec states neither name for such a row, so a reader MUST NOT look the table
up by the layer's name: it reads the file's one feature table whatever it is called. It MUST NOT
take the only table of a file that holds several. A row whose file holds several lists them in
`tables`, and a reader reads the table it wants by name from that list. The same holds for the
`-Projected` twin of an untabled row. `mpb validate` rejects an untabled row whose file holds more
than one feature table (`gpkg.tables-several`) or none (`gpkg.table-none`). The corpus pins it:
`bundle.v2GpkgSoleTable` is a valid untabled row whose one table is CamelCase, and
`bundle.v2GpkgSoleTableSeveral` an invalid one whose file holds two.

The schema types `name` as a plain string on purpose. **This table states the vocabulary**, and
the corpus case `manifest.vectorLayerVocabulary` carries every name in it:

| `name` | What it holds | Geometry | Where it comes from |
| --- | --- | --- | --- |
| `building` | Building footprints, with subtype and a height where the source has one | Polygons | Overture buildings |
| `road` | Road centrelines (road segments only, not rail or paths), with class, subclass and name | Lines | Overture transportation |
| `water` | Water as the source maps it: streams as centrelines, water bodies as polygons | Lines, polygons and points | Overture base |
| `land_use` | Land-use areas, with class and subtype | Mostly polygons; lines and points occur | Overture base |
| `land_cover` | Physical ground cover, with a subtype such as forest | Polygons | Overture base |
| `road_splines` | The `road` centrelines draped onto the delivered elevation, carrying an estimated width, class and name | Lines with Z | Derived from `road`, and marked with `derived_from` |
| `road_polygons` | Road surfaces: the `road` centrelines widened by the same estimated width, merged per class, and cut so no two overlap (the wider class keeps the ground where classes meet), carrying class and width | Polygons | Derived from `road`, and marked with `derived_from` |

A layer's geometry can mix the families its row names, so a reader keys on each feature's own
geometry type rather than on the layer; each file of a split layer holds one family, and its format
row names it. Every layer's coordinates are geographic, and the block
says so: `vector.crs` is `EPSG:4326` whenever the set shipped. §6's one projection exception
applies to all of them — and a host whose own block carries the layer in its own frame (§6.5)
reads that copy instead. The heights `road_splines` carries are the delivered elevation's, in its
datum, and the layer states that datum: a host whose ground is in another places none of them (§6).

What presence and absence mean:

- **A base layer is present exactly when the AOI holds at least one of its features after the clip
  to the AOI.** An absent base layer means *zero features*, never a failure. The producer does not
  ship a vector set with a layer it failed to read or write: when any base layer fails, the whole
  set is withheld. The `vector` block then says `present: false` and lists no layers, and `packaging`
  says why (§6.1).
- **A layer is never emitted empty.** The producer writes no entry with a feature count of zero,
  and no placeholder entry for a layer the AOI does not have.
- **The derived layers are best-effort.** `road_splines` and `road_polygons` can each be absent
  while `road` is present, and their absence says nothing about whether the AOI has roads. A
  consumer that wants roads and finds no derived layer SHOULD say so rather than report an AOI
  without roads.
- ⛔ **A consumer MUST ignore a layer name it does not recognise.** The vocabulary grows additively.
  A new name is a change to this table and to the corpus, not to the schema. A consumer that
  refuses a bundle for carrying an unfamiliar layer turns every new layer into a breaking release.
  The corpus case `manifest.vectorLayerUnknownName` pins this.

### 6.4 The imagery drape, per host

A drape is an image stretched over terrain, and its only coordinates are its extent. That extent is
placement, stated in one projected CRS and one linear unit, and a consumer can use it only where
that CRS is the one its own origin is in. A rectangle aligned to one grid is not a rectangle on
another — a UTM-aligned image is rotated on a State Plane grid — so an extent in the wrong CRS
cannot be made right by arithmetic on its four numbers.

The bundle therefore carries the drape once per frame a host needs, never once for everyone:

- **`imagery.drape` is the fixed-frame drape**, on the AOI's metric UTM grid whatever the delivery
  CRS. It is the image `hosts.unreal.imagery_drape` points at.
- **A host whose frame follows the delivery reads its own drape pointer in its own block.** Where the
  delivery grid is already the metric UTM grid, that pointer names the same file as
  `imagery.drape`; on a State Plane delivery it names a second image baked on the State Plane grid.
  The producer publishes the pointer only after checking that its extent is in the host origin's
  CRS and unit and agrees with the image's own pixel grid.

What a host does with a drape it cannot show to be in its frame — refuse it, name why, and never
reproject it — is the host standard's (`HPS-52`, `HPS-53`), not this document's.

### 6.5 A host's own copy of the vector layers

The shared vector set is geographic, which suits a GIS reader and no one else. §6 lets a consumer
project lon/lat locally in one narrow case, and a host whose origin is not on a grid that case
reaches cannot place the set at all. So the bundle hands such a host its own copy, already in its
own frame, rather than asking it to convert.

`hosts.revit.vectors` is that copy for the Revit host, on every delivery:

- One GeoJSON file per layer, named by the §6.3 vocabulary, with its `path`, `feature_count` and
  `sha256`. The Revit copy carries `road_splines`, `road_polygons`, `water`, `land_use` and
  `land_cover`; `building` is the site model's, and raw `road` is carried as `road_splines`.
- Each layer states its `horizontal_frame` and `units`, and they are the same for every layer on a
  delivery: absolute coordinates in the delivery CRS wherever a projected CRS in the delivery unit
  exists, and east/north offsets about the origin where none does. Either way the file is in
  `hosts.revit.file_frame` (§4.2), and a reader places it by subtraction and offset alone.
- A projected file carries a GeoJSON `crs` member naming `file_frame.crs`. An offset file carries
  none, because its frame has no CRS; a reader that insists on one has misread the frame.
- `road_splines` alone has heights, and says so with `vertical_reference: absolute`: real
  orthometric height in the file's `units`, never an offset from the origin.
- The set is all or nothing. A layer missing from it had no features in the AOI; a layer that could
  not be converted withholds the whole copy, and `hosts.revit.readiness.vectors` says the copy is
  absent and why.

The shared set beside it is unchanged, and no host block points at it.

### 6.6 Tree points, per host

`Landcover/TreePoints.csv` follows the delivered elevation: `x` and `y` are in the delivery CRS and
`ground_z` is in the delivered elevation's vertical unit, so on a State Plane delivery all three are
feet, and on a delivery whose region has no projected foot zone the coordinates are metres beside
foot heights. `height_m` and `crown_radius_m` are metres on every delivery, as their names say.
From 1.3.0 `landcover.tree_points` states that frame beside the path — `crs`, `units` (the
`ground_z` column) and `horizontal_units` (`x`, `y`) — so a reader can refuse a file it cannot show
to be in its frame instead of assuming one. No column is renamed: `x`, `y` and `ground_z` assert no
unit, and the two that do are honest.

A fixed-frame host reads points in its own frame, and the delivered file is only in that frame on a
metric delivery. So the bundle carries the trees once per frame a host needs:

- **`hosts.unreal.foliage_points` names only a file in the Unreal frame** — its `crs` is
  `georeference.crs_projected`, its `units` and `horizontal_units` are `m`, and its `ground_z` is in
  the engine's datum (§6), by schema. On a metric delivery whose heights are in that datum, the
  delivered `Landcover/TreePoints.csv` is that file, and one file carries both pointers. On any foot
  delivery, and on any delivery whose heights are in another datum, it is
  `Landcover/TreePointsMetric.csv`: the same trees in the metric UTM grid with `ground_z` re-sampled
  in metres on the engine's ground, every other column carried verbatim.
- The producer chooses between the two by comparing the frames the files state with the host's, and
  never by the delivery tier. Where neither file is in the Unreal frame — a foot delivery built
  before 1.3.0, until its next rebuild — the block carries no `foliage_points` at all, rather than a
  pointer at a foot file.
- `sha256` and `point_count` on the pointer are the named file's, not the delivered file's; the two
  files hold the same trees, so `point_count` agrees, and the hashes differ.

What a host does with a tree-point file it cannot show to be in its frame — refuse it, name why, and
never convert it — is the host standard's (`HPS-52`, `HPS-53`), not this document's.

### 6.7 Published contours, per host

`Surface/Contours.dxf` is the contour linework the order was built with: one flat polyline per
contour, each at its elevation. `elevation.contours` points at it for any reader and states its
vertical unit and interval, but no horizontal frame, so a host that must show a file is in its frame
before placing it (§6) cannot place it from that pointer.

From 1.4.0 the Revit host has its own:

- **`hosts.revit.contours` names the same file**, one file under two pointers, and is present only
  when the file is in `hosts.revit.file_frame` (§4.2): absolute in the delivery CRS wherever a
  projected CRS in the delivery unit exists, and east/north offsets about the origin where none does.
  It states its `horizontal_frame`, its X/Y unit and its heights' unit, and the two units are stated
  apart.
- **A reader takes both units from the pointer, never from the drawing.** A DXF states one unit for
  the whole drawing, and a file whose X/Y and heights are in different units cannot state itself
  truthfully there.
- The heights are real orthometric heights, as `vertical_reference: absolute` says: never an offset
  from the origin. The contour interval stays on `elevation.contours`.
- A bundle whose contours were built before 1.4.0 carries no pointer, and
  `hosts.revit.readiness.contours` says so.

What a host does with contours it cannot show to be in its frame is the host standard's
(`HPS-52`, `HPS-53`), not this document's.

### 6.8 File frames, the site origin and licensing (from 1.14.0)

From 1.14.0 the neutral register can place a file and state its terms without a reader opening any
`hosts.<hostId>` block. Two optional members carry it: `location.site_origin`, one point for the
whole bundle, and `file_entries`, one entry per file keyed by its archive path. A bundle made before
its producer writes them carries neither, and the host blocks (§4.2) keep stating the same facts
through 1.x, so a host that reads only its own block loses nothing. `file_entries` is not the
`files` member of 1.10.0: `files` exists only on a bundle stored as parts and describes storage
(part, size, digest), where `file_entries` describes meaning and is valid on every bundle.

**The File frame, field by field.** An entry's `frame` is the file's full frame, written whole or
not at all. Every field is optional in the schema, so what follows is what each one says and what
its absence means; the schema owns the types and the permitted values.

- `horizontal_crs` is the CRS of the file's coordinates, or of its grid for a raster.
- `vertical_crs` is the CRS the heights are in, as a reference of its own (an NAVD88 height, an
  EGM2008 height), never folded into the horizontal one. It is absent for a file with no heights.
- `unit_to_metres` is the horizontal linear unit as a number, the factor to metres: 1 for metres,
  0.3048 for the international foot, 1200/3937 for the US survey foot. A number rather than a name,
  so that no reader maps "foot" to the wrong foot. It is the vertical unit too, unless
  `vertical_unit_to_metres` is present, which states a vertical unit that differs.
- `geoid_model` names the geoid the heights are referenced to (`GEOID18`, `EGM2008`). It is absent
  when the vertical CRS is ellipsoidal or the file has no heights.
- `axis_order` is the order of the horizontal axes as the file stores them: easting first, or
  latitude first.
- `axis_mapping` says which way each of the file's three axes points. A glTF file is x east, y up,
  z south; a projected GeoTIFF or a USD file is x east, y north, z up. A reader never assumes an
  engine's convention for a file that states its own.
- `coordinate_mode` is `absolute` (the coordinates are full values in `horizontal_crs`) or `offset`
  (they are displacements from the site origin, below).
- `pixel_convention` and `row_order` are for rasters only: whether a pixel value describes its cell
  or the point at its centre, and whether row 0 is the northern or the southern edge.
- `coordinate_epoch` is a decimal year, present where the CRS is dynamic or the realisation
  matters, and absent otherwise.

**No file relies on a compound EPSG code alone.** A CRS is named in exactly one of three forms: a
`code` such as `EPSG:5703`, a PROJJSON object, or a WKT2 string. The form is chosen per CRS, and the
horizontal and the vertical CRS are separate references, so a reader gets each from its own field
and never has to take one apart from a compound code. Datums that have no authority code yet ride
PROJJSON or WKT2 and arrive as a `code` later, which changes a value and not a field.

⛔ **The frame's value sets fail closed.** An axis direction, an axis order, a coordinate mode, a
pixel convention or a row order the reader does not know is a frame it cannot read, which is a file
it cannot place: refuse the file by name. An unknown key beside the known ones is tolerated, as a
later minor may add one.

**The site origin.** `location.site_origin` is readable by any host, unlike a host block. It is one
point: its latitude and longitude, its height above the ellipsoid, and its height in the order's
vertical datum, which `vertical_datum` names. The difference of the two heights is the datum offset
at the site. It is computed once and applied verbatim; a reader never re-derives it (§6). Two
optional members add to it: `geodetic_crs`, for a reader that needs more than WGS 84 to the metre,
and `projected`, the same point in a projected CRS with a height, so that a reader places every
offset file by arithmetic. The six members of `projected` come together or the block is invalid.

**How glTF and USD offsets relate to it.** glTF and USD files hold 32-bit floats, which lose
precision at full projected coordinates, so their frames say `coordinate_mode: offset`. Each
coordinate is then a displacement from the site origin, in the file's own axes (`axis_mapping`) and
its own unit (`unit_to_metres`). Where `projected` is present, a reader adds the file's x and y to
`projected.x` and `projected.y`, and its z to `projected.z`, whose height `projected.z_reference`
names (ellipsoidal, or the vertical datum), and never reprojects. The offset is in the file's unit and
the origin in `projected.unit`, and §6 applies: the unit is read from each, never assumed alike.
Where `projected` is absent the origin is a latitude, longitude and heights only, and a reader
reprojects that one point once to place the file. An `absolute` file, a GeoTIFF for example, needs
no site origin; its own `horizontal_crs` places it. None of this changes §4.2: a host is handed only
files in its own frame, and converts nothing.

**The neutral PNG mappings.** A PNG rendition's entry may carry `value_mapping`, which says how to
read a pixel back as data outside any host block. It states what the Unreal host block's heightmap
and UE-ready raster mappings state, for any reader, and those copies stay through 1.x. Its
`encoding` decides what else it must carry:

- a 16-bit grayscale needs `min` (the value pixel code 0 stands for), `max` (the value 65535 stands
  for) and `to_value`, the decoding formula in words, and may add `value_units`, `datum`,
  `nodata_value` and `quantization_step`;
- a mask needs `true_value` and `false_value`, the two pixel codes;
- an indexed layer needs `classes`, either the list of pixel codes in use or an object from code to
  class name, and a reader accepts both. The class codes are the legend and are never rescaled;
- an RGBA raster needs `bands`, naming the four channels in order;
- an 8-bit grayscale passthrough carries `identity: true`, as its codes are the data.

⛔ A reader that cannot decode an `encoding` skips the file and says so. It never guesses at what a
pixel means.

**The three licensing fields.** Each entry may carry `odbl_role`, `license` and `sources`.

- `odbl_role` is one of `database` (an ODbL Derivative Database, licensed ODbL), `produced-work`
  (rasterized, meshed, extruded or baked output, which is not ODbL-licensed) and `none` (no ODbL
  source contributes). Every file is exactly one of them.
- `license` is what the file ships under: an SPDX licence expression, a single id or ids joined by
  `AND`, `OR` or `WITH`, whose ids are SPDX ids or `LicenseRef-` ids. Free text is not a licence. A
  `produced-work` entry never names ODbL here. A compound expression lives only in the manifest,
  because the STAC Item beside it carries a single id per asset.
- `sources` lists the upstream sources the file was made from, each with a `name` and a `license`
  that is always a single id, never an expression, and optionally its registry id, its landing page
  and `license_unconfirmed`, true when the id is the best reading but the publisher has not confirmed
  it. The list is empty when the platform alone authored the file.

⛔ **A missing role reads as `database`**, the stricter duty, and so does a role the reader does not
know. An entry without `odbl_role` is not a `none`, and a reader that wants the lighter reading must
hold the role that earns it.

### 6.9 The AutoCAD host block: one host, three parts (from 1.18.0)

AutoCAD and Civil 3D are one host to the manifest. The host id is `autocad`, and Civil 3D is a part
of that host rather than a second id: a plugin running in plain AutoCAD and one running in Civil 3D
read the same `hosts.autocad` subtree and nothing else (§4.2). The block hands the plugin everything
it needs to place an order, so the plugin applies values and derives none. It carries three parts,
each with its own pointer and its own readiness verdict:

- **`hosts.autocad.file_frame`** states the frame the block's files share, as §4.2 defines it. Every
  coordinate-carrying file the block points at is in it, and the producer checks that before it
  publishes. It follows the delivery wherever a projected CRS in the delivery unit exists, and is a
  local frame about the origin where none does.
- **`hosts.autocad.contours`** names the published contour linework (`Surface/Contours.dxf`, §6.7),
  one file under two pointers. It is present only when the file is in the block's `file_frame`, and
  has the shape of `hosts.revit.contours`: `horizontal_frame` (`absolute_projected`, or offsets about
  the origin), `vertical_reference: absolute`, `horizontal_units` for X and Y and `vertical_units`
  for the heights, **stated apart**, `vertical_datum` and the file's `sha256`. A reader takes both
  units from the pointer, never from the drawing, which states one unit for all of it.
- **`hosts.autocad.surface`** is the terrain source for a Civil 3D surface: one file that Civil 3D
  builds a surface from, with `path`, `format`, `sha256`, and its frame stated beside the path as the
  contours state theirs (`horizontal_frame`, `horizontal_units`, `vertical_units`,
  `vertical_reference`, `vertical_datum`). The `format` is the pointer's own word for the file, and a
  reader that does not know it declines the surface and names the format; it never guesses. A surface
  is Civil 3D's, and plain AutoCAD has no object to place it as.
- **`hosts.autocad.aerial`** is the aerial image with its insertion already worked out. Beside the
  image's `path`, `format`, pixel `width` and `height` and `sha256`, an `insertion` object states
  where and how the image goes in the drawing, in the block's `file_frame` and its horizontal unit:
  the `x` and `y` of the image's lower-left corner, the ground `width` and `height` of the whole
  image (not of a pixel), and `rotation_deg`. A plugin inserts the image at those numbers. It does not
  work an insertion point or a scale out of an extent, a pixel size or a CRS, and it never
  reprojects the image: an image the block does not show to be in the frame is absent, with a verdict
  that says why (§6.4).

**Readiness, a verdict for each part.** `hosts.autocad.readiness` states `contours`, `surface` and
`aerial`, each as the §6.1 verdict: `present`, and when not, the stated `reason`, which a reader
echoes and never matches on. A bundle built before 1.18.0 carries no `hosts.autocad` block, and a
reader says so rather than reading another host's block (§4.2).

The `surface` verdict carries one member more, `requires`, naming the host part that can place the
file. For a surface it is `civil3d`. This is what lets a plain AutoCAD reader say the surface is
unavailable in this host from its own block alone: a reader that does not hold the named part,
or does not recognise the name, reports the surface as unavailable here ("needs Civil 3D") and
imports nothing from it. It does not open another host's block to find out, and it does not treat the
pointer's presence as a promise that its own host can use the file. `requires` is on the verdict
whether or not the file shipped, so the answer is the same on an order whose surface is absent.

What a host does with a part it cannot show to be in its frame is the host standard's (`HPS-52`,
`HPS-53`), not this document's.

## 7. The sidecar manifest

The vault publishes a versioned copy of the manifest beside the zip, so that a listing can show a
bundle's version, size and digest without downloading and unzipping it.

The sidecar is the same document plus one block — `bundle`, carrying the zip's own sha256, size and
filename. **That block is sidecar-only.** It never appears inside the zip's own
`Metadata/manifest.json`; a consumer MUST NOT require it, and MUST NOT read its absence from an
in-zip manifest as an error.

## 8. Reading a bundle, start to finish

The whole procedure, for a consumer holding a zip:

1. **Extract and parse `Metadata/manifest.json`.**
2. **Check the version.** Its JSON type tells you the era — a number is the integer pre-history, a
   string is MPB semver. Compare the major component, and refuse an unknown higher major gracefully
   (§11, [`compatibility.md`](compatibility.md)).
3. **Decide materialisation** from the neutral signals (§6.2). If the bundle carries no payload for
   your host, read the top-level facts, then report the readiness reasons rather than an empty
   import.
4. **Locate each file** by reading its `layout` pointer or its per-artifact `path` and extracting
   exactly that entry, resolved inside your extraction root (§3).
5. **Verify integrity** per §5 — fail closed where the schema requires a hash; skip, and say so,
   where it does not.
6. **Place the geometry** by applying your host block's published values verbatim (§6), reading each
   unit from the field that describes the thing you are placing.
7. **Honour the licensing obligations.** The `licensing` and `attribution` blocks state which
   families carry which terms; the platform's own [licensing](https://mantle.place/licensing) and
   [attributions](https://mantle.place/attributions) pages are the authority on the text those terms
   require, and nothing in this repository restates them. From 1.14.0, `file_entries` also states
   each file's `odbl_role`, `license` and `sources` (§6.8), and a missing role reads as `database`.

If a step above is under-specified for a bundle you hold, that is a spec bug worth an issue. If it
is under-specified in a way the corpus does not catch, it is a corpus bug too — see
[`conformance.md`](conformance.md).

## 9. MPB is a profile: canonical and courtesy formats

**A profile, never an encoding.** Mantle Place does not invent a way to store data, and MPB does not
define a file format. Every file in a bundle is in a format somebody else already specified and
maintains. What MPB adds is the layer above the files: the manifest, the placement rules (§6, §6.8)
and the licence and source facts of every file (§6.8). A team writing a producer or a reader needs a
library for each format it touches, and never a parser for anything MPB-specific except the manifest.

### 9.1 The admission test

A format is **canonical**, which is to say promised by this spec, only if it passes all three of:

1. **An open specification**: anyone can read the format's specification without a licence
   agreement, an account or a fee.
2. **Royalty-free use**: reading and writing the format needs no royalty and no patent licence.
3. **Two independent implementations**: at least two implementations that share no code and no
   owner, so that no single party can change what the format means.

A format that fails any of the three is never canonical, however widely it is used.

### 9.2 The canonical list

| Format | What a bundle carries in it |
| --- | --- |
| **COG** (Cloud Optimized GeoTIFF) | Rasters: elevation, imagery, the derived surfaces, land cover |
| **GeoPackage** | Vector layers and the public records, with attributes |
| **glTF 2.0** | Meshes: the terrain, extruded buildings |
| **OpenUSD** | The packaged site: the Context file and the Features file (`.usdz`) |
| **IFC**, named as **IFC 4.0.2.1 (IFC4 ADD2 TC1)** | The site model |

The IFC row names one published release on purpose. IFC 4.3 is allowed in a later release of this
spec, as an addition (§11) and not a replacement, so a reader that supports only IFC 4.0.2.1 is
never broken by it.

What "promised" obliges. For as long as 2.x lasts (§11), a canonical format stays on the list. A
reader that reads every canonical format can read the data content of every bundle, because the
producer ships each kind of data content (rasters, vector layers, meshes, the packaged site, the
site model) in a canonical format wherever it ships that content at all. A reader MUST NOT need a
courtesy format to read that data content.

Four kinds of file are exempt from that rule, and only these: the **documents** (the Site Brief and
the plan sheets, PDF and SVG); the **drawings** (`Drawings/Linework.dxf` and the contour linework
`Elevation/Contours.dxf`), which exist as drawings for CAD tools; the **point tables**
(`Elevation/SurfacePoints.csv` and `LandCover/TreePoints.csv`), which are point lists for tools that
read CSV; and the **LandXML and DXF encodings of the surface** (`Elevation/Surface.landxml`,
`Elevation/Surface.dxf`), whose surface is carried canonically by the elevation COG,
`Elevation/DEM.tif`, in the same folder. A bundle's exempt files
are courtesy files, and nothing in the bundle's data content depends on them.

### 9.3 The courtesy list

Every other file in a bundle is a **courtesy format**: shipped and described in the manifest, never
promised. The courtesy formats of this release are:

- **DXF**: the linework, the contours, the surfaces and the feature layers. DXF is a courtesy
  format everywhere, whatever it carries, so no content has to be argued into an exception to the
  admission test.
- **Shapefile**, which Civil 3D and CityEngine read where they do not read GeoPackage.
- **LandXML**, which Revit and Civil 3D read. The LandXML standard is no longer maintained; it
  stays only because those tools read it.
- **OBJ**, with its `.mtl` and texture, for tools that read no glTF.
- **CSV**, the point tables.
- **The PNG renditions** of a raster, and the **16-bit heightmap**.
- **The quantized-mesh tileset** of the terrain.
- **The PDF and SVG sheets**: the Site Brief and the plan sheets.

A courtesy format stays in the producer's catalogue only while a named tool that curators use reads
it natively and reads no canonical format for the same content. When no such tool is left, the
format is dropped from the catalogue, in a release that records it in the
[changelog](changelog.md). A reader MUST NOT fail a bundle because a courtesy file it once used is
absent, and reads the manifest's pointers (§3) to learn which files exist.

## 10. The 2.0 layout

MPB 2.0 restructured the archive once so that it can be held steady. The layout below is what a 2.0
producer writes. The pointer doctrine (§3) is unchanged and stronger for it: a consumer still finds
every file through a manifest pointer value, and the layout is for the people who unzip.

### 10.1 What a 2.0 bundle contains

The root holds the README and the folders, and nothing else. This tree is the default download of a
new order at its finest level. It is informative: the pointers decide which of these a given bundle
holds, and a bundle holds only what the AOI and its sources produced.

```
README.md
Metadata/   manifest.json  stac-item.json
Site/       Brief.pdf  Model.ifc  Context.usdz  Features.usdz
Elevation/  DEM.tif  Heightmap.png  Hillshade.tif .png  Slope.tif .png  Aspect.tif .png
            SteepSlopes.gpkg  Contours.dxf  Surface.landxml .dxf  SurfacePoints.csv
            Terrain.glb .obj .mtl .jpg  Terrain/ (quantized-mesh tileset)
Imagery/    Imagery.tif  Imagery.png .pgw .prj  NDVI.tif .png
LandCover/  LandCover.tif .png  LandCoverAreas.gpkg .shp .dxf  CanopyHeight.tif .png
            WaterMask.tif .png  MaterialWeights.tif  MaterialWeights-1To4.png
            MaterialWeights-5To8.png  TreePoints.csv
Features/   StylizedMap.tif .png  Buildings.glb
            BuildingFootprints, Roads, RoadsDraped, RoadSurfaces, Water, LandUse: each .gpkg .shp .dxf
Records/    FloodZones, Parcels, Zoning, Wetlands, Soils, Hydrography, Contamination,
            CriticalHabitat, HistoricPlaces, ProtectedAreas: each .gpkg .dxf
Drawings/   Linework.dxf  SitePlan-Small.pdf .svg  SitePlan-Large.pdf .svg
```

Outside that default download, or only on some deliveries, a bundle can also hold the lighter levels
of a surface or model (`Terrain-Med.glb`, `Context-Min.usdz`), the engine-frame variants, only where
the order's frame differs from the engine's (`TreePoints-Engine.csv`, `Imagery-Engine.png`), the
projected-CRS twins of the vector layers, only where a layer is not already in the order's CRS
(`RoadsDraped-Projected.gpkg`), and the Shapefile geometry splits (`Water-Lines.shp` with its
sidecars).

Four files carry most of what an outsider needs:

- **`Metadata/manifest.json`** is the machine entry point (§1), and the only file found by
  convention.
- **`Metadata/stac-item.json`** is the same bundle as a STAC 1.1.0 Item, generated from the manifest,
  for tools that already catalogue assets. Its asset keys match the manifest's pointer keys and its
  hrefs are relative. It is a convenience and never an authority: where it and the manifest differ,
  the manifest is right, and a reader that needs a frame, a role or a licence reads it from the
  manifest.
- **`Site/Context.usdz`** is the site as one handover-safe model: the terrain, extruded buildings,
  roads, water and land use as meshes grouped by class, baked trees, and the imagery as textures. It
  holds only Produced Work content (§10.4) and no curves, instancing or feature ids. Its up axis is Z,
  its unit is the metre and its default prim is `/Site`. The `mpb:` georeference on `/Site` is the
  file's frame, and the file stores its coordinates as offsets from the site origin (§6.8).
- **`Site/Features.usdz`** is the vector data of the same site as curves, points and polygons with
  their attributes, in the Context file's coordinates, and composes onto it as a sublayer. Its role
  is `database`, and its layer metadata carries the ODbL notice.

### 10.2 The folder allowlist

⛔ **Normative: the folders of a 2.0 bundle are exactly these eight.** `Metadata`, `Site`,
`Elevation`, `Imagery`, `LandCover`, `Features`, `Records` and `Drawings`.

- **A producer MUST put no file at the root but `README.md`, and MUST use no top-level folder
  outside the list.** The only way to add a folder is a new release of this spec (§11).
- **A consumer MUST NOT depend on it.** The allowlist binds the producer. A consumer finds a file by
  its pointer (§3), so a folder it has never heard of is no reason to refuse a bundle.
- **A validator warns on a folder outside the list.** It is a warning because the consumer rule above
  holds either way. A validator ignores the entries `__MACOSX` and `.DS_Store` that archive tools
  add on their own.

Each folder is named for what is in it, as a noun a curator would say. `Features/` holds the shared
vector layers, rasterised map and building meshes; `Records/` holds the public-record layers, flat
and named by the labels the Site Brief uses; `Site/` holds the files that describe the whole site and
need no prefix for it. The land cover polygons are `LandCoverAreas`, not `LandCover`, because they
are a separate vector product: they are processed differently from `LandCover.tif` and may be
licensed differently, so they cannot share its basename (rule 1 below).

**Licensing shows per file, never by folder.** A file's terms are its `odbl_role`, `license` and
`sources` (§6.8), and the bundle's README lists them in a table. No folder means "ODbL".

### 10.3 The six naming rules

⛔ **Normative: a 2.0 producer follows all six.**

1. **One noun per basename.** A basename names one thing, as a PascalCase noun a curator would say,
   and the extension is the format. Two files that share a basename in one folder are
   **renditions**: the same data in another encoding, at the same extent, with values possibly
   quantized and never resampled or reprojected. Different data never shares a basename anywhere in
   the bundle. (`Surface.dxf` and `Surface.landxml` are one surface in two encodings.)
2. **Plural for countable things, singular for mass nouns.** `Roads`, `Parcels` and `TreePoints`
   count things; `Imagery`, `LandCover` and `Water` do not.
3. **Variant qualifiers are hyphenated, in a fixed order: subset, then frame, then level.** A
   **variant** exists only for a frame or a format that a whole class of tools needs, never for one
   plugin; it is named for what it is and sits beside the file it varies. No dots, underscores or
   spaces.
   - Subsets: `-Small`, `-1To4`, `-Lines`. A subset is a naming qualifier and never a storage part.
   - Frames: `-Engine` and `-Projected`. A file with `-Engine` is in the frame a fixed-frame engine
     host places files in (§6.4, §6.6): the AOI's metric UTM grid, in metres, with heights in the
     engine content's datum. It is written only for an order whose delivered frame differs, beside
     the file in the order's frame. A `-Projected` file is a vector layer's twin for a reader that
     works in a projected CRS: the same tables, columns and types in the order's projected CRS and
     unit, with absolute coordinates, and the manifest names each twin in
     `layout.projected_variants`. A layer already in that CRS has no twin.
   - Levels: `-Raw`, `-Med`, `-Min`. A coarser level of a surface or model is a separate file, because
     none of the shipped formats holds levels portably. `Terrain-Med.glb` follows `Terrain.glb`.
4. **Fixed names keep their convention.** A name that software or a convention looks up keeps it:
   `README.md`, `Metadata/manifest.json`, `Metadata/stac-item.json`, and the names inside a format's
   own structure (tileset internals, Shapefile sidecars, an OBJ's material and texture, world-file
   and `.prj` sidecars). `Metadata/manifest.json` is fixed so that a reader written for an older
   major can still open it, read its version and refuse a newer major (§11).
5. **Character set.** Names are ASCII, from `[A-Za-z0-9._-]`, and no two entries of one archive
   differ only by case, so that the bundle extracts the same on a case-sensitive and a
   case-insensitive file system.
6. **Case matching.** Writers use the exact case. Readers match a manifest path to an archive entry
   regardless of case.

### 10.4 Produced Works and ODbL files

No file is both a Produced Work and an ODbL file. The Context file and the plan sheets are Produced
Works at full quality, so a curator can hand them on with a credit line. The Features file, the
vector layers built from ODbL sources and `Linework.dxf` are ODbL files, kept complete, and they
carry their notice and the licence link inside the file (GeoPackage metadata, the DXF header, USD
layer metadata). A file no ODbL source contributes to has the role `none`. The README says which is
which; the manifest says it for a machine (`odbl_role`, §6.8).

## 11. Stability, and a higher major

**Within 2.x nothing breaks.** The full promise is [`compatibility.md`](compatibility.md) §8: a
2.x consumer reads every 2.x bundle, and a 2.x producer's bundle stays readable by a consumer
written against an earlier 2.x. For this document it means that the canonical list (§9.2) and the
folder allowlist (§10.2) only grow, and that the naming rules (§10.3) never change meaning.

**A higher major is refused gracefully.** A reader opens `Metadata/manifest.json` first, which is
why rule 4 fixes its name, reads `version` and nothing else of the document, and compares the parsed
major (compatibility §1, §3). If the major is higher than any it supports, it stops: it does not
read a pointer, extract an artifact or import part of the bundle. It tells the user the bundle's
version, the versions it supports, and what to do about it, which is to update the software. A 1.x
reader refuses a 2.0 bundle in exactly this way, and a 2.x reader refuses a 3.0 bundle the same.

## 12. The bundle's media type

A bundle is a zip and keeps the `.zip` extension, because `.mpb` belongs to other applications. Its
media type is:

```
application/vnd.mantleplace.bundle+zip
```

A server that serves a bundle SHOULD send that type, and a file that is a bundle SHOULD be offered to
a reader under it. The `+zip` suffix says a generic zip reader can still open the file. The type is
not registered with IANA yet; it follows the vendor-tree form so that the name is stable if it is
registered later, and a reader MUST NOT refuse a bundle because it arrived as `application/zip` or
as `application/octet-stream`. What makes a file a bundle is `Metadata/manifest.json` inside it.
