<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- SPDX-FileCopyrightText: Mantle Place LLC -->

# mantleplace-mpb

`mpb validate` checks a Mantle Place Bundle and says pass or fail, with every reason. It is the
conformance check a producer, a reader's author or a CI job runs on a bundle, and it is pure
Python: the standard library and nothing else, so no GDAL, no PROJ and nothing compiled.

```bash
pip install ./tools/mantleplace-mpb      # from a checkout of this repository
mpb validate site-bundle.zip             # a zip, a folder, or either inside one wrapper folder
mpb validate site-bundle.zip --json      # the machine-readable report
```

| Exit | Meaning                                                                               |
| ---- | ------------------------------------------------------------------------------------- |
| 0    | conformant: no error (warnings are reported; `--strict` makes them fail)               |
| 1    | not conformant: each reason is a finding in the report                                 |
| 2    | could not check: the input is unreadable, or its schema could not be fetched or trusted |

## What it checks

The format is [`spec/format.md`](../../spec/format.md); this tool checks a bundle against it and
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
- **ODbL roles** (`file_entries.<path>.odbl_role`). A vector or tabular data file (`.gpkg`,
  `.shp`, `.geojson`, `.dxf`, `.csv` and the like) is never a Produced Work, and a file whose role
  is `none` lists no ODbL source. A missing role reads as `database` and is a warning, as is a
  file with no entry at all.

It never reprojects, and it derives no placement value: a frame is checked for being whole and
self-consistent, never recomputed.

## The report

`--json` prints one object: the input and the form it was read as, the declared manifest
version and the schema it was held to, the `verdict` (`pass`, `fail` or `error`) and its
`exitCode`, a status per check, the sha256 counts, and `findings`. Each finding has a `check`, a
stable `code` (`pointer.missing`, `sha256.mismatch`, `frame.partial`, …), a `severity` (`error`,
`warning` or `info`), a `message`, and where it applies the JSON Pointer into the manifest and the
bundle path it is about.

## Offline

`--schema-dir DIR` reads `<version>.json` and `frozen.lock.json` from a directory instead of the
network, with the same ledger check when the ledger is there. The schemas are not copied into
this repository: the published series is the one source.

## The conformance corpus

The validator is held to the `bundle` group of the shared corpus in
[`tools/manifest-conformance/corpus/`](../manifest-conformance/corpus/README.md): each case
describes a synthetic bundle as JSON, and the runner builds it as a folder, a zip and a wrapped
zip and requires the same verdict and the same finding codes from all three.

```bash
mpb corpus tools/manifest-conformance/corpus
python -m unittest discover -s tools/mantleplace-mpb/tests     # offline, no install needed
```

The 2.0 naming, case and folder rules arrive with the 2.0 corpus cases.
