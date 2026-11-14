<!-- SPDX-License-Identifier: CC-BY-4.0 -->
<!-- SPDX-FileCopyrightText: Mantle Place LLC -->

# Conformance

**A conforming reader passes the corpus, and a conforming bundle passes the bundle check.** Those two sentences are the whole of this document; the rest says what they mean precisely enough to argue about.

## 1. A spec release is three artifacts, versioned together

| Artifact | Where | What it settles |
| --- | --- | --- |
| The **schema** | `https://mantle.place/.well-known/schemas/bundle-manifest/` | What a manifest may contain |
| The **prose** | this directory | What the tree and the manifest are for, and what a reader owes them; for 3.x, also the file table ([`format.md`](format.md) §6) |
| The **corpus** | [`tools/manifest-conformance/corpus/`](../tools/manifest-conformance/corpus/) | What correct behaviour looks like, executably |

The schema and the corpus are **normative**, and so is the 3.x file table: it is the list a producer's registry is checked against. The rest of the prose describes.

They ship as a set. The corpus declares the manifest version its fixtures are written against — a case written for a later additive minor declares its own — and each declaration is machine-checked against the fixtures themselves: a corpus whose stated version and actual fixtures disagree fails the gate before any host suite sees it. That is what makes "conforming to MPB *x.y.z*" a claim with a referent instead of a feeling.

## 2. What the corpus is

Language-neutral test vectors, as JSON: accept shapes, reject shapes, derived placement expectations with explicit tolerances, and known-answer tables. Every Mantle Place host plugin runs them against its parser, and so can yours.

The mechanics — the index format, the case fields, the `accept` / `reject` / `vector` verdicts, the tolerances, how a case is scoped to one host — are documented once, in the [corpus README](../tools/manifest-conformance/corpus/README.md), and are not restated here.

These vectors began as inline C++ literals inside one plugin's test suite. That is a perfectly good test suite and a completely unusable specification: a second implementer could read them only by reading C++, and would re-derive them slightly differently. The bugs that follow — a null digest read as "corrupt" rather than "unknown", a false northing dropped in the southern hemisphere — are all silent, and they produce a plugin that works on the developer's bundle and misplaces a customer's site by kilometres. Lifting the vectors into one language-neutral artifact is what makes "the second host agrees with the first" something a test asserts rather than something a reviewer hopes.

## 3. What conformance requires

A reader claims a set of **groups**, for each major it reads, and is bound by all of them. A reader of 3.x and 2.x claims the cases of both.

⛔ **Every case in a claimed group must be driven.** Partial coverage inside a group is not conformance. A group whose cases each exercise a different code path invites a suite that dispatches by id and quietly skips the case added last month — so a conforming suite tracks which ids it drove and fails on any it did not. A reviewer cannot see a missing branch in a four-hundred-line test.

**A reader need not claim every group.** A manifest-first implementation claims the manifest group and adds others as those layers land. Not claiming a group is honest; claiming it and skipping cases is not.

**A case scoped to one host is skipped by every other host.** This scope exists for the 2.x host blocks; a 3.x manifest has none, so a 3.x case binds every reader. A case that carries no scope binds everyone — the version gate, the major told apart by the manifest's place, and the catalog among them — and may not declare an expectation only one host could compute.

⛔ **The corpus reader is itself under test.** Alongside the corpus proper is a self-test set of deliberately broken fixtures — a malformed case file, an orphan, a duplicate id, an expectation key declared with the wrong JSON type, an expectation no reader may consume. A reader that *accepts* any of them has failed. The corpus proper proves your parser; the self-test proves the thing running it, and a reader that silently reads past an expectation it does not understand will pass a corpus full of assertions it never made.

## 4. Which groups a third-party reader needs

The corpus covers more than this spec does, because it also holds our plugins to the platform's service contract.

- **`manifest`, `digest`, `projection`, `cache`** — the portable half. These are the groups this spec governs, and the ones a third-party bundle reader claims. `projection` binds a 2.x reader only: a 3.x reader never projects ([`format.md`](format.md) §4).
- **`bundle`** — whole synthetic bundles, each described as JSON, with the verdict and the finding codes a bundle validator must reach on it. It holds a validator, not a reader, so no reader claims it; [`mantleplace-mpb`](../tools/mantleplace-mpb/README.md) (`mpb validate`) is the validator it holds today. The 3.x bundle requirements (§7) are its cases.
- **`vault`, `auth`, `platform`, `client`** — the Mantle Place vault's REST and OAuth behaviour, and the Platform API's Quote, order and client-version answers. First-party plugin obligations, not interchange. A reader that only opens bundles from disk does not claim them, and is fully conforming without them.

## 5. Proving it, and the two pins that record it

The offline gate lives at [`tools/manifest-conformance/`](../tools/manifest-conformance/) and needs only Python and its standard library:

```bash
python -m unittest discover -s tools/manifest-conformance   # offline: corpus integrity + self-test
python tools/manifest-conformance/check_manifest_conformance.py
```

The second form also reaches the network, to compare what each host is verified against with what the platform has actually published. It runs on every pull request here as a required check.

Two numbers record where a reader stands, and they are deliberately separate:

- **The corpus pin** — the manifest version the fixtures are written against, declared once by the corpus itself. It moves with the readers' floor, not with every minor: an additive minor arrives as a case carrying its own version.
- **The per-host pin** — [`verified-against.json`](../tools/manifest-conformance/verified-against.json), one entry per host, recording the version that host's parser was actually **exercised** against. Registering is a claim of evidence, not of tolerance; raising a number means the tests were updated. Each entry also names where that host's version floor lives, so the gate can read the floor out of a C++ header, a C# constant or a Python module without knowing which is which.

The two are split because hosts move at different speeds. A pin moves per host; a floor moves in lockstep, because one shared corpus cannot state a rule in absolute versions and be right for two different floors at once. A practical consequence: a retired version joins the corpus' reject set only once **every** registered host has cleared it. The reject set waits for the slowest host, and that is a correctness property rather than a courtesy.

## 6. Changing the corpus

The corpus is **maintainer-owned**, and that is load-bearing: a forked private copy is exactly the drift it exists to prevent. A reader that needs a new case proposes it here, by pull request.

Every case states its own reason. A fixture nobody can explain is deleted the first time it is inconvenient, usually by the person it would have saved.

If you build a consumer against this spec and find a behaviour the corpus does not pin, that is the most useful bug report this project can receive. It means two implementations could disagree while both stay green, and it is worth an issue whether or not you send the case with it.

## 7. A conforming 3.x bundle

A producer's 3.x bundle conforms when the bundle check finds every requirement below met. The corpus's `bundle` group is where each is checked, one case per requirement; the [file-by-host table](file-by-host.md) records where four of them came from.

**The tree.**

- The zip holds exactly the tree of [`format.md`](format.md) §1: the two root files, the seven folders, no subfolders, and no file the file table (§6 there) does not list. A file the bundle does not hold is listed in the manifest with its reason.
- Every name follows the naming rules (§2 there), and each file has the same name in storage as in the zip.
- No file that left with 2.x is present ([`format.md`](format.md) §12): no Shapefile, no SVG, no NDVI file, no STAC Item, no geographic or projected GeoPackage twin, no points file of the surface, no file with a level, frame or subset suffix, no RAW level, and no PNG copy of slope, aspect, hillshade, NDVI, canopy or land cover.

**Units and frames.**

- On an Imperial order and on a Metric order alike, every georeferenced file is in the order's CRS and units, and every host-ready file is in the local site frame in metres. The manifest states both frames once.
- Every DXF is in the order's CRS and units, the four Basemap DXFs included; a DXF in longitude and latitude, or with no unit, fails.

**The files themselves.**

- `Terrain.obj` is written Y-up, in metres, with texture coordinates, and names `Terrain.mtl`; the Readme states its unit.
- `Terrain.mtl` names `../Imagery/Imagery.png` as its diffuse map, the texture coordinates of `Terrain.obj` address the pixels of that file at the same level, and no other texture ships.
- `Imagery.tif`'s fourth band is written as near-infrared, never declared alpha.
- Every vector layer except the tree points and the contours is a `.gpkg` and a `.dxf` holding the same features and attributes, in 3D where the data has heights; the tree points are a `.gpkg` and a `.csv`, and the contours are a `.dxf` alone. Every DXF carries its attributes, written by the producer's writer.
- Each raster's data type, scale and no-data value are the ones the file table states. Slope, aspect and canopy height are each within half a stored unit of the source; elevation and imagery are equal to the source pixel for pixel.
- `Roads.csv` and `Trees.csv` carry the columns [`format.md`](format.md) §6 names.

**Levels.**

- Each order's three downloads hold the same names. The host-ready files halve and quarter by the fixed steps, and the georeferenced files are byte-identical across the three.
- The Med and Min `Trees.csv` are the first rows of the Max file.
- The manifest states the download's level once, and the zip's name carries it.

**The manifest and the zip.**

- `Manifest.json` is at the root, validates against the 3.x schema, has one catalog with an entry for every file held, stating a count for every file whose row in the file table gives one (§6 of [`format.md`](format.md); a row that says `None` has no count) and a sha256 for every file except `Manifest.json` itself, which cannot carry its own hash, and carries no per-host block, no commit, image path, budget, threshold or build parameter, and no Extra.
- Every member is DEFLATE-compressed, or stored where compression does not shrink it; the zip opens in the standard zip readers and every member verifies against its catalog digest.

**A parcels fixture.** The `Parcels` pair is checked on a fixture that has one, since many orders have no parcel source.
