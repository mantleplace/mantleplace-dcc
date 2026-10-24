<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- SPDX-FileCopyrightText: Mantle Place LLC -->

# mantleplace-mpb

Two things for anyone who works with a Mantle Place Bundle (MPB), in pure Python: the standard
library and nothing else, so no GDAL, no PROJ and nothing compiled.

- `mpb validate` checks a bundle and says pass or fail, with every reason. It is the conformance
  check a producer, a reader's author or a CI job runs on a bundle.
- `read_bundle` opens a bundle and gives every file with its frame and its ODbL role, and the
  offset and axis transform that relate two files of one frame. It never reprojects.

```bash
pip install mantleplace-mpb
mpb validate site-bundle.zip             # a zip, a folder, or either inside one wrapper folder
mpb validate site-bundle.zip --json      # the machine-readable report
```

The package version is the MPB version it was tested against: `1.14.0` is held to the conformance
corpus written for MPB 1.14.0, and a `.postN` release changes the package and not the suite.
`mpb --version` and `mantleplace_mpb.__version__` report it.

| Exit | Meaning                                                                               |
| ---- | ------------------------------------------------------------------------------------- |
| 0    | conformant: no error (warnings are reported; `--strict` makes them fail)               |
| 1    | not conformant: each reason is a finding in the report                                 |
| 2    | could not check: the input is unreadable, or its schema could not be fetched or trusted |

## What it checks

The format is [`spec/format.md`](https://github.com/mantleplace/mantleplace-dcc/blob/main/spec/format.md); this tool checks a bundle against it and
against the published schema, and restates neither.

- **The archive.** A zip, a folder, or either with the bundle inside one wrapper folder.
  `__MACOSX/` and `.DS_Store` are skipped wherever they sit. A zip entry that is not a plain
  relative path inside the bundle is refused and never read.
- **The schema.** The manifest is held to the schema of the version it **declares**, fetched from
  the published series at `https://mantle.place/.well-known/schemas/bundle-manifest/`. The
  version must be listed in the series' freeze ledger, and the fetched bytes must match the
  sha256 the ledger records for them. The JSON Schema evaluator is built in and refuses any
  keyword it does not evaluate, so a schema it cannot fully check is "could not check", never
  "pass".
- **A staged version, when asked.** A release can be served for readers before it is frozen, so
  the ledger does not name it yet. `--staged` holds a manifest that declares one to the served
  schema instead of failing it as unpublished, and the report says so (`schema.staged`, at info).
  Only the next release can be staged: a version below the ledger's newest is unpublished either
  way.
- **The pointer rule.** Every `layout` path (each string value; `mesh_formats` lists format
  names, not files), every per-artifact `path` and every `file_entries` key must name a file or folder in the bundle, by a plain relative path that stays inside it.
  `layout.manifest` must name the manifest. In a bundle stored as parts, a file another part holds
  is reported, not failed.
- **sha256, where present.** Each stated hash is checked against the bytes, and a mismatch fails
  by file name. A pointer with no hash of its own takes the one the manifest's format tables
  state for the same path; a file with no hash anywhere is counted as unknown, not corrupt.
- **Frames** (`file_entries.<path>.frame`). A frame is whole (its CRS, unit, axis order, axis
  mapping and coordinate mode, and for a raster its pixel convention and row order) and coherent:
  no raster fields on a non-raster, no geoid or vertical unit without a vertical CRS, no two axes
  along one line, and an offset file only where `location.site_origin` is there to measure from.
  A file with no spatial frame states `{"placed": false}` and nothing else.
- **ODbL roles** (`file_entries.<path>.odbl_role`). A vector or tabular data file (`.gpkg`,
  `.shp`, `.geojson`, `.dxf`, `.csv` and the like) is never a Produced Work, and a file whose role
  is `none` lists no ODbL source. A missing role reads as `database` and is a warning, as is a
  file with no entry at all.
- **The 2.0 layout and names** (`format.md` §10.2, §10.3), for a manifest that declares 2.x and
  only then; a 1.x bundle is held to its own version. These are the `names` check:

  | Code | Severity | Rule |
  | --- | --- | --- |
  | `layout.folder-unlisted` | warning | a top-level folder outside the eight |
  | `layout.root-file` | warning | a file at the root other than `README.md` |
  | `name.basename` | error | rule 1: a basename is a PascalCase noun, and the extension, lowercase, is the format |
  | `name.basename-shared` | error | rule 1: one basename in two folders names two things |
  | `name.qualifier` | error | rule 3: a qualifier joined by `_` or `.`, or not a capitalised word or number |
  | `name.qualifier-order` | error | rule 3: qualifiers run subset, frame (`Engine`, `Projected`), level (`Raw`, `Med`, `Min`) |
  | `name.charset` | error | rule 5: a name outside ASCII `[A-Za-z0-9._-]` |
  | `name.case-collision` | error | rule 5: two entries that differ only by case |
  | `name.case-mismatch` | error | rule 6: a pointer that names an entry only regardless of case |

  The allowlist binds the producer and no consumer depends on it, so it warns. A pointer is
  matched regardless of case, as a 2.0 reader matches it, so a case mismatch is the writer's
  error and not a missing file. Rules 1 and 3 name the files of a folder: the fixed names
  (`README.md`, `Metadata/manifest.json`, `Metadata/stac-item.json`) and the files inside a
  format's own folder, such as a tileset's, keep their own names (rule 4). Rule 2, plural or
  singular, is a matter of meaning that no validator reads.

It never reprojects, and it derives no placement value: a frame is checked for being whole and
self-consistent, never recomputed.

## The report

`--json` prints one object: the input and the form it was read as, the declared manifest
version and the schema it was held to, the `verdict` (`pass`, `fail` or `error`) and its
`exitCode`, a status per check, the sha256 counts, and `findings`. Each finding has a `check`, a
stable `code` (`pointer.missing`, `sha256.mismatch`, `frame.partial`, …), a `severity` (`error`,
`warning` or `info`), a `message`, and where it applies the JSON Pointer into the manifest and the
bundle path it is about.

## Reading a bundle

```python
from mantleplace_mpb import read_bundle

with read_bundle("site-bundle.zip") as bundle:          # a zip, a folder or a wrapper folder
    for f in bundle.files:                              # every file, the manifest and README included
        print(f.path, f.role, f.frame.coordinate_mode if f.frame else f.frame_problem)

    mesh = bundle.placement("Site/Terrain.glb")
    mesh.axis_transform                                 # 3x3: file x, y, z onto east, north, up
    mesh.offset                                         # the site origin the file is measured from
    mesh.to_enu_metres((10.0, 5.0, 20.0))               # that point, east/north/up, in metres

    move = bundle.transform("Site/Terrain.glb", "Elevation/DEM.tif")
    move.apply((10.0, 5.0, 20.0))                       # the same point in the raster's coordinates
```

- **Every file, with its frame and role.** A `BundleFile` carries `path`, `size`, `frame`, `role`,
  `license` and `sources`. A file the manifest has no entry for has no frame and reads as
  `database`; so does a role the reader does not know (`declared_role` keeps what was written).
- **A frame the reader cannot read is not guessed.** An axis, mode or order outside the known
  sets, a missing field, or two axes on one line leaves `frame` as `None` and `frame_problem`
  saying why. The file stays in the listing; `placement` refuses it.
- **Within one frame, the offset and axis transform.** `placement(path)` gives the file's axis
  transform, its units and, for an `offset`-mode file, the site origin as its `offset`.
  `transform(source, destination)` relates two files of one frame by axes, units and that origin,
  added.
- **Never a reprojection.** Files whose horizontal CRS, vertical CRS, geoid or epoch differ are in
  different frames: `transform` raises `FrameMismatch`. An origin stated only as latitude and
  longitude, or in another CRS than the file's, leaves `offset` as `None` with `offset_problem`
  saying so. CRS references are compared as written, so a `code` and a WKT2 string for one CRS are
  two frames to this reader.
- **Reading is not validating.** The reader opens the manifest and the archive and fetches
  nothing. Run `mpb validate` first when the bundle is not yours.

## Offline

`--schema-dir DIR` reads `<version>.json` and `frozen.lock.json` from a directory instead of the
network, with the same ledger check when the ledger is there. The schemas are not copied into
this repository: the published series is the one source.

## The conformance corpus

The validator is held to the `bundle` group of the shared corpus in
[`tools/manifest-conformance/corpus/`](https://github.com/mantleplace/mantleplace-dcc/blob/main/tools/manifest-conformance/corpus/README.md): each case
describes a synthetic bundle as JSON, and the runner builds it as a folder, a zip and a wrapped
zip and requires the same verdict and the same finding codes from all three.

```bash
mpb corpus tools/manifest-conformance/corpus
python -m unittest discover -s tools/mantleplace-mpb/tests     # offline, no install needed
```

## Releasing

A release is a tag. `python tools/mantleplace-mpb/release_check.py` holds the package version to
the newest MPB version the corpus declares a case for, a reject case aside (the tests run it on
every pull request), and `release_check.py --tag mpb-<version>` holds the tag to the package.
Pushing the tag `mpb-<version>` runs `.github/workflows/release-mpb.yml`: it tests, builds, checks
the distributions and publishes them to PyPI through trusted publishing, with no token stored
anywhere. To install from a checkout instead: `pip install ./tools/mantleplace-mpb`.
