---
name: building-a-reader-for-mpb-3
description: How to build the reader for a new host from a page instead of from another host's code — telling a 2.x bundle from a 3.0 one, finding files in the catalog by their promised names, the two file families and their frames, the level, what to ask the platform for, the nine actions every Mantle Place plugin supports, how to run the corpus reader cases against your reader, and the checklist before a first release. Read before starting a plugin or a third-party reader for any host, and before writing a rule that tells a reader where a file is.
---

# Building a reader for MPB 3.0

For someone building the Mantle Place plugin for a new host, or a reader of their own. It says what a
reader of a 3.0 bundle does, in the order it does it, and what a Mantle Place plugin does on top.

**This page links; it does not restate.** The format is [`spec/`](../spec/README.md), whose schema,
corpus and file table decide. What a plugin must *do* with it is the [Host Plugin Standard](host-plugin-standard.md),
cited by `HPS-NN`. If this page and either of those disagree, this page is the bug.

What this page is not: a project skeleton for a new host. It tells you what to build, not how a host
project is laid out.

Two readers use it, and the page says which is which:

- **A reader of bundles from disk.** It needs no account, no token and no network call
  ([`spec/README.md`](../spec/README.md#scope)). Sections 1 to 5 and 8 are all it needs.
- **A Mantle Place plugin.** It does everything the reader does, and adds sign-in, the vault, the
  common core and the obligations of the standard. It needs every section.

## 1. Tell 2.x from 3.0

**The majors are told apart by where the manifest is** ([`compatibility.md`](../spec/compatibility.md) §1).

1. `Manifest.json` at the root of the zip is a 3.x bundle ([`format.md`](../spec/format.md)).
2. Otherwise `Metadata/manifest.json` is a 2.x or 1.x bundle ([`format-2.md`](../spec/format-2.md)).
3. A zip with neither is not a bundle.

Then read the manifest's `version`. A **JSON string** is MPB semver and a **JSON number** is the integer
pre-history; tell them apart by the JSON type and never coerce one into the other, and compare the
parsed major, never the string ([`compatibility.md`](../spec/compatibility.md) §1).

**Refuse a higher major before you extract a file.** Read `Manifest.json`, parse the major, and if it is
above the highest you support, stop and tell the user the bundle's version, the versions you support and
that the software needs updating. A crash, a silent partial import or an attempt anyway are all wrong
([`compatibility.md`](../spec/compatibility.md) §3, [`format.md`](../spec/format.md) §14).

**Reading 2.x as well is your choice.** A reader written for 3.x SHOULD also read 2.x, because a 2.x
bundle is never repackaged and curators keep them; Mantle Place's own plugins do. The floor is policy,
not spec ([`compatibility.md`](../spec/compatibility.md) §3, §5). A Mantle Place plugin declares its
floor in one place (`HPS-31`).

**What changed for a 2.x reader.** In 2.x a file was found through a pointer in a per-host block of the
manifest. In 3.0 there is no such block and no pointer: a file is found by its promised name in the
catalog (section 2). Every rule in the standard that sends a reader to a pointer or a host block is
marked 2.x only, and a 3.0 reader skips it; the table at the top of the standard says which.

## 2. The catalog

`Manifest.json` is the one file found by its name before anything else is read. Every other file is
found by **its promised name in the manifest's one catalog** ([`format.md`](../spec/format.md) §9.1,
§9.5, §9.7, §11).

- **A name the catalog lacks is a file this download does not hold,** and the manifest lists every
  promised file it does not hold with its reason ([`format.md`](../spec/format.md) §9.6). An absent file
  is not an error.
- **Show the stated reason; never match on it.** Reasons grow, and a reader that branches on the ones it
  knows shows nothing for the one it does not.
- **A file the catalog names and the zip lacks** is reported and skipped, never fatal, and every file the
  reader does find is still read ([reader group](../tools/manifest-conformance/corpus/README.md#the-reader-group-reader)).
- **A file in the zip the catalog does not name** is not found and is not an error: ignore unknown
  files, columns and fields ([`compatibility.md`](../spec/compatibility.md) §3). A minor release may add a file, and a reader
  that finds it by its catalog name needs no change.
- **A new host needs nothing added to the manifest** ([`format.md`](../spec/format.md) §9.7).
- **Names are the contract.** Every file's name and place are promised for the life of 3.x
  ([`format.md`](../spec/format.md) §3). The tree is seven subject folders with no subfolders, and the
  file table (§6 there) is the list of what can be in one.
- **Match names regardless of case; resolve every path inside your extraction root** and reject an
  entry that escapes it ([`format.md`](../spec/format.md) §2, §10).
- **Never parse `Readme.md`.** It is for a person ([`format.md`](../spec/format.md) §1).
- **Read tables by header name, never by position,** and ignore a column you do not know
  ([`format.md`](../spec/format.md) §5).
- **Verify each file against its sha256, and fail closed on a mismatch,** naming the file. Write a
  download to a temporary path, verify it, and promote it by rename
  ([`format.md`](../spec/format.md) §9.8; `HPS-26`). `Manifest.json` is the one file that carries no hash.
- **A file whose stated encoding you cannot decode is skipped, and you say so.** Never guess what a pixel
  means ([`format.md`](../spec/format.md) §7).

The catalog states each file's count (rows, features, triangles or pixels) so you can estimate an import
before starting it. Report the count the catalog states and never count a file yourself.

## 3. Two file families, and their frames

Every data file is in one of two families, and **its extension says which**
([`format.md`](../spec/format.md) §4).

| Family | Extensions | Coordinates | Unit |
| --- | --- | --- | --- |
| Georeferenced | `tif`, `gpkg`, `dxf`, `landxml`, `ifc` | Absolute, in the order's CRS and vertical datum | The order's |
| Host-ready | `glb`, `usdz`, `obj`, `png`, `csv` | Offsets in the local site frame | Metres |

`Terrain.mtl` is in neither family. The manifest states both frames once ([`format.md`](../spec/format.md) §9.4):
the order's projected CRS, vertical CRS, geoid and units, and the site origin with its own datum. A host
picks the family it can place. The same data in another format shares its name and differs only by
extension ([`format.md`](../spec/format.md) §2), so `Trees.gpkg` and `Trees.csv` are the same trees in the two
families (the `.gpkg` is the same trees as the Max `.csv`, [`format.md`](../spec/format.md) §6). You never convert a format to read a file you already have an importer for ([`format.md`](../spec/format.md) §3).

Four rules carry the placement, and each is the kind of failure that imports successfully and is wrong:

- **Apply the published values verbatim.** The site origin, the frames, each raster's rectangle and each
  value scale are computed once by the producer. Multiply them into your host's convention, and do
  nothing else ([`format.md`](../spec/format.md) §4; `HPS-33`).
- **Never project.** Every georeferenced file is already in the order's CRS and every host-ready file in
  the local site frame, so a 3.0 reader has no projection to perform ([`format.md`](../spec/format.md) §4).
- **Fail closed on a unit, a datum or an axis you do not understand,** naming the value. A linear unit
  misread by a factor puts a site off by the whole ratio ([`format.md`](../spec/format.md) §4).
- **Know the axes of each format.** glTF and OBJ are Y-up, OpenUSD is Z-up, a CSV carries `x`, `y` and a
  height in the frame's axes, and a PNG raster has row 0 at its north edge ([`format.md`](../spec/format.md) §4).

A named skip is the way to refuse content you cannot place: say which content you did not place and why,
bring in everything else, and never reproject, rescale or shift a datum to reconcile a mismatch
(`HPS-53`).

## 4. The level

A bundle is built at one of three levels, **Max, Med and Min**, and a download holds exactly one level of
each file under the same name and path ([`format.md`](../spec/format.md) §8).

- **The manifest states the download's level once,** and the zip's name carries it. A file's name never does.
- **Host-ready files carry the level and georeferenced files do not.** A `glb`, `usdz`, `obj`, `png` or
  `csv` is built at each level, and a `tif`, `gpkg`, `dxf`, `landxml` or `ifc` is byte-identical in all three.
- **A level omits or coarsens; it never moves, invents or reclassifies.** Med is half of Max and Min a
  quarter, in linear resolution for a grid and in count for a mesh or a set of rows.
- **The level the platform recommends is the platform's answer to your host, not a fact in the manifest.**
  A request that names no level receives Max.

A Mantle Place plugin lets the curator choose a level for a download, with each level's size shown and
the recommended level preselected (`HPS-59`).

## 5. What to request from the platform

A reader of bundles from disk requests nothing. A Mantle Place plugin talks to the Platform API, whose
authority is the spec the platform serves at [`https://mantle.place/api/v1/openapi.json`](https://mantle.place/api/v1/openapi.json)
(a vendored copy sits beside the shared .NET client under [`shared/`](../shared/README.md)). This page
names what to ask for, never how the wire is shaped.

- **Sign in** through the system browser ([`platform-auth-contract.md`](platform-auth-contract.md);
  `HPS-04` … `HPS-13`), and identify your client in the header the spec names. The platform uses it to
  work out the recommended level for your host, and answers a plugin too old with a
  client-update-required answer (corpus group `client`).
- **List the vault.** For a 3.0 bundle a listing row carries the level the platform recommends for the
  asking host and site, the size and counts of all three levels, and the bundle's Extras. A size or a
  recommendation is the platform's, applied verbatim (`HPS-59`, `HPS-18`, `HPS-20`).
- **Download one level, and the files you import.** The 3.0 download request names one level (none means
  Max) and the promised files wanted by their names. There is **no file list per host,** so a plugin
  names the files it imports and a new host needs no platform change. `Manifest.json` always comes.
  Mint a link per download, never reuse one past its expiry, and state the payload you want in the request
  (`HPS-29`, `HPS-49`).
- **Extras are separate downloads** beside the bundle: the Site Brief, the plan sheets and the terrain
  tiles. They carry no version promise, and the manifest names none of them
  ([`format.md`](../spec/format.md) §1, §9.9).
- **The 3.0 form of these requests may be published ahead of the behaviour.** Read the served spec for
  whether it is live before you depend on it.

Everything here is a first-party plugin's concern. The service contract is not part of the interchange
format ([`compatibility.md`](../spec/compatibility.md) §6).

## 6. The common core

Every Mantle Place plugin supports **nine actions, in this order, with the words the standard fixes**
(`HPS-58`):

| # | The action | The rule |
| - | ---------- | -------- |
| 1 | Sign in | `HPS-04` … `HPS-13`, `HPS-57` |
| 2 | List the vault | `HPS-18`, `HPS-20`, `HPS-21`, `HPS-55` |
| 3 | Choose a level | `HPS-59` |
| 4 | Download | `HPS-18`, `HPS-26`, `HPS-27` |
| 5 | Import, through the import window | `HPS-60` |
| 6 | Import a zip from disk, with no account | `HPS-61` |
| 7 | Refuse a manifest version out of range | `HPS-31` |
| 8 | Show credits and notices | `HPS-62` |
| 9 | Import again | `HPS-63` |

The words each action takes on screen are the shared vocabulary (`HPS-51`). The core is a floor: a
feature only your host can offer is **host-only,** is listed as such beside the core, and takes your host's
own noun ([`host-support.md`](host-support.md#the-common-core), [ADR 0019](adr/0019-plugins-share-a-common-core-a-dotnet-home-and-corpus-cases.md)).
The same page holds a column for each host, and a new host adds its own.

## 7. The shape a host plugin takes

Four layers (auth, vault client, bundle cache, manifest consumption), each a triad of an impure host
shim, a pure logic core and a headless test of the core (`HPS-01`, `HPS-02`). The import layer carries no
geoprocessing stack and re-implements no conversion the platform already did (`HPS-03`). The pure cores
test with no host application running (`HPS-42`). A .NET host builds the shared, host-neutral code from
[`shared/`](../shared/README.md) rather than copying it.

## 8. Run the corpus reader cases against your reader

The `reader` group in the [conformance corpus](../tools/manifest-conformance/corpus/README.md#the-reader-group-reader)
is the answer sheet for a reader of either major. For one synthetic bundle it says which files a correct
reader finds, each with its family, its frame and the count the catalog states, which files it skips and
why, the level, and the refusal of a major above 3. It is data, so a reader in any language reaches the
same result.

For each case of group `reader` in the corpus's `index.json`:

1. **Read the case file,** and the `bundle` case file it names under `bundle/`.
2. **Build the bundle** from that description: `files` maps each archive path to its content, so the
   corpus holds no sample data. Build it as a folder, a zip and a zip inside one wrapper folder; a reader
   reaches the same result on all three.
3. **Run your reader** on it.
4. **Compare** to the case's `result`: `outcome`, `major`, `version`, `manifestPath` and `level`. Compare
   `files` and `skipped` as sets keyed by `path`, every member of an entry exactly and a count as an integer.
5. For a case whose `expect` is `reject`, assert that your reader **refuses**, that its message contains
   `errorContains`, and that it had read `version`, `major` and `manifestPath` first.

Three things make the run mean something ([`conformance.md`](../spec/conformance.md) §3):

- **Drive every case in a group you claim,** and track which ids you drove so the suite **fails on one it
  did not run.** A reader that quietly skips the case added last month is not conforming.
- **Your corpus reader is itself under test.** `self-test/` holds deliberately broken fixtures, and a
  reader that accepts any of them has failed. A first-party plugin also fails on a declared key it never
  read (`HPS-46`).
- **A case with no scope binds you.** Scoping a case to one host exists for the 2.x host blocks, and a
  3.x manifest has none.

The groups a third-party reader claims, and which it does not, are in
[`conformance.md`](../spec/conformance.md) §4: the portable groups (`manifest`, `digest`, `cache`, and
`projection` for a 2.x reader only) and `reader`, never `vault`, `auth`, `platform` or `client`. A reader
that only opens bundles from disk is fully conforming without them. A plugin that signs in claims them,
because they are its obligations to the platform.

The offline gate checks that the corpus itself is sound, and needs only Python and its standard library
([`docs/agents/ci.md`](agents/ci.md#running-the-gates-locally)):

```bash
python -m unittest discover -s tools/manifest-conformance
python tools/manifest-conformance/check_manifest_conformance.py --mode pinned
```

If you find a behaviour the corpus does not pin, propose the case by pull request. The corpus is
maintainer-owned, and a forked private copy is the drift it exists to prevent ([`conformance.md`](../spec/conformance.md) §6).

## 9. The checklist before a first release

**The reader**

- [ ] Finds the manifest by its place, reads its version by its JSON type, and refuses a higher major
      before extracting anything (section 1).
- [ ] Finds every file by its promised name in the catalog, resolves each inside its extraction root, and
      reads no file the catalog does not name (section 2).
- [ ] Shows each not-held reason as stated and never branches on it.
- [ ] Verifies every sha256 and fails closed on a mismatch; writes to a temporary path and promotes by rename.
- [ ] Reads tables by header, reads an unknown `foliage_type` as `tree` and an unknown or missing ODbL
      role as an ODbL database ([`format.md`](../spec/format.md) §5, §9.8).
- [ ] Places georeferenced files by the order's CRS and host-ready files by the local site frame, applies
      every published value verbatim, never projects, and fails closed on a unit, datum or axis it does
      not know (section 3).
- [ ] Shows the credits and the licence terms the manifest states for each file ([`format.md`](../spec/format.md) §9.8; `HPS-62`).
- [ ] Reads the level from the manifest and never from a file name (section 4).
- [ ] Passes every case of each corpus group it claims, drives every id, and passes the self-test (section 8).

**The plugin**

- [ ] Supports the nine actions of the common core, in order, with the shared words, and adds its column
      to [`host-support.md`](host-support.md) (section 6).
- [ ] Is registered in [`verified-against.json`](../tools/manifest-conformance/verified-against.json) from its first commit, with its floor in one place (`HPS-38`, `HPS-39`).
- [ ] Imports a bundle zip from disk with no account (`HPS-61`).
- [ ] Has a Send Feedback action (`HPS-56`) and a tab or top-level menu named `Mantle Place` if the host
      has a ribbon or a menu (`HPS-57`).
- [ ] Has a local-install tool and a check script for its host (`HPS-50`).
- [ ] Is built as four layers of triads with pure cores that test without the host, and carries no
      geoprocessing stack (`HPS-01` … `HPS-03`, `HPS-42`).
- [ ] Has its own `CLAUDE.md` naming how the host builds and tests, and contains no logic that derives a
      placement value ([`CLAUDE.md`](../CLAUDE.md#the-boundary-that-keeps-this-client-thin)).

The sign-in flow and the secret stores are closed to outside patches, and a defect there is a private
report ([`SECURITY.md`](../SECURITY.md)). The rules in `HPS-04` … `HPS-17` are published so you can build
yours to them, not so you can change ours.
