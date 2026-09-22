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
the contract; a consumer MUST NOT parse it.

A **sidecar manifest** is also published beside the zip in the vault, carrying the same document
plus one extra block — see [§7](#7-the-sidecar-manifest). Inside the zip,
`Metadata/manifest.json` is the manifest.

## 2. Modality folders are human-facing, and machines never navigate by them

The top-level folders are one-word nouns naming a **modality**, not a tool: `Basemap`, `Imagery`,
`Elevation`, `Surface`, `Site`, `Mesh`, `Vector`, `Landcover`, `Flood`, `Metadata`. The vocabulary
is a frozen allowlist on the producing side — growing it is a deliberate, reviewed act — and it is
frozen so that a curator opening the zip finds the same words next year.

The naming rule inside a folder: **basename is artifact identity, extension is format.**
`Surface.dxf` and `Surface.landxml` are one artifact in two formats. A compound basename appears
only where a file must still describe itself after being copied out of its folder.

**None of this is a machine contract.** Folder names are human-facing convention and are free to
evolve; they have already been reorganised once, and a pointer-driven consumer needed no change to
survive it. Which brings us to the rule that matters most in this document.

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
`name` and one row per delivered format. A consumer finds a layer by its `name` and then a file by
its format row. It MUST NOT guess a layer from a file name (§3). It also MUST NOT fall back to a
format it cannot read when the one it wants is missing. The corpus pins both:
`manifest.roadSplinesGeojsonWins` puts a decoy layer beside the one being selected, and
`manifest.roadSplinesGpkgOnly` offers only a format the reader does not take.

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
geometry type rather than on the layer. Every layer's coordinates are geographic, and the block
says so: `vector.crs` is `EPSG:4326` whenever the set shipped. §6's one projection exception
applies to all of them — and a host whose own block carries the layer in its own frame (§6.5)
reads that copy instead.

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
  `georeference.crs_projected`, and its `units` and `horizontal_units` are `m`, by schema. On a
  metric delivery that is `Landcover/TreePoints.csv`, and one file carries both pointers. On any
  foot delivery it is `Landcover/TreePointsMetric.csv`: the same trees, `x` and `y` reprojected into
  the metric UTM grid and `ground_z` re-sampled in metres, every other column carried verbatim.
- The producer chooses between the two by comparing the frames the files state with the host's, and
  never by the delivery tier. Where neither file is in the Unreal frame — a foot delivery built
  before 1.3.0, until its next rebuild — the block carries no `foliage_points` at all, rather than a
  pointer at a foot file.
- `sha256` and `point_count` on the pointer are the named file's, not the delivered file's; the two
  files hold the same trees, so `point_count` agrees, and the hashes differ.

What a host does with a tree-point file it cannot show to be in its frame — refuse it, name why, and
never convert it — is the host standard's (`HPS-52`, `HPS-53`), not this document's.

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
   ([`compatibility.md`](compatibility.md)).
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
   require, and nothing in this repository restates them.

If a step above is under-specified for a bundle you hold, that is a spec bug worth an issue. If it
is under-specified in a way the corpus does not catch, it is a corpus bug too — see
[`conformance.md`](conformance.md).
