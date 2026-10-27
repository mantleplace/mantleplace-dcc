# Mantle Place for Blender

A Blender add-on that imports a Mantle Place bundle's site in one step: `Site/Context.usdz`, with
`Site/Features.usdz` composed over it as a sublayer, placed at the site origin the bundle's manifest
publishes. It reads a bundle you already have on disk; it does not sign in or list your vault.

It is the smallest reader of the open bundle format outside the Unreal and Revit plugins, and it is
written to be read: every rule it applies is cited to [`spec/format.md`](../spec/format.md).

Licensed under [Apache 2.0](../LICENSE).

## What it needs

- **Blender 4.2 or later.** It uses Blender's own USD import; nothing else is installed. It is tested
  in Blender 4.5 and 5.2.
- **A bundle of MPB 2.0 or later that carries `Context.usdz`.** An older bundle is refused with its
  version named; download it again from your vault to get a 2.x copy.

## Install

1. Get the add-on as a zip. From a clone of this repository, build it with Blender:

   ```bash
   blender --command extension build --source-dir blender/mantleplace_blender --output-dir .
   ```

   That writes `mantleplace_blender-<version>.zip`.
2. In Blender, open **Edit > Preferences > Get Extensions**, open the menu at the top right, choose
   **Install from Disk**, and pick the zip.
3. The add-on is enabled on install. It is listed under **Add-ons** as *Mantle Place*.

## Use

1. **File > Import > Mantle Place Bundle (.zip)**.
2. Pick the bundle's `.zip`, or the `Metadata/manifest.json` of a bundle you already extracted.
3. Leave **Features** ticked to bring in the vector data (roads, water, land use and the rest as
   curves and polygons); untick it for the site model alone.
4. Press **Import Bundle**.

The site arrives as one collection, `Site`: the terrain with its imagery, the buildings, roads, water,
land use and trees from `Context.usdz`, and, when ticked, the features from `Features.usdz`. Textures
are packed into the `.blend`, so the bundle can be moved or deleted afterwards.

**Anything the import leaves out is said, by name, in Blender's status bar and its Info log**: a
Features file in another frame than the site, a file whose sha256 does not match its manifest, a site
origin that cannot be applied, and why.

**Send Feedback**, under **Help** and in the add-on's preferences, opens the feedback form on
mantle.place in your browser. It sends the add-on's version and Blender's, and nothing else.

## Where the site lands

The scene's world origin **is** the site origin. The geometry keeps the offsets the files store, in
metres, Z up, and the scene records the point those offsets are measured from:

| Scene custom property | What it holds |
| --- | --- |
| `mantleplace_site_origin` | the whole of `location.site_origin` as the manifest states it: the projected `x`, `y`, `z`, its CRS, `unit` and `z_reference`, the latitude and longitude, both heights, and the vertical datum |
| `SRID`, `crs x`, `crs y`, `latitude`, `longitude` | the same point in the keys Blender's GIS add-ons read for a scene's origin, so a later GIS import into this scene lands on the site; written only when the origin is in metres |

So an absolute coordinate is the origin plus the offset: a vertex at `(12.5, -40.0, 3.2)` is at
easting `crs x + 12.5`, northing `crs y - 40.0`, and height `z + 3.2` in the `z_reference` the
origin names.

**Why the site is not moved to its full coordinates.** Blender stores object positions as 32-bit
floats, which at a projected northing of four million metres are only good to half a metre. That is
the reason the format stores offsets at all (spec §6.8), and moving the site would throw away what
the format kept.

**What it never does.** It does not reproject, convert a unit, or shift a height between datums. Where
the site origin is missing, is in another CRS than `Context.usdz`, or `Context.usdz` states a frame
the add-on cannot read, the site still comes in, at the origin, and the import says the origin was not
applied and why. That is the thin-client rule every Mantle Place plugin keeps: the platform derives
placement values, and a plugin applies them verbatim.

## Licences in the bundle

`Context.usdz` is a Produced Work and can be handed on with a credit line. `Features.usdz` is an ODbL
database: its notice and the licence link are in its own USD layer metadata, and the bundle's README
says what publishing work made from it obliges you to do. The [licensing](https://mantle.place/licensing)
and [attributions](https://mantle.place/attributions) pages are the authority on that text.

## Build and test

The add-on is the triad every Mantle Place plugin is built as:

| Part | Where | Needs Blender |
| --- | --- | --- |
| the pure core: version, pointers, frames, site origin, the composed stage, the feedback link, zip and folder reading | [`mantleplace_blender/core/`](mantleplace_blender/core/) | no |
| the host shim: operators, menus, preferences, Blender's USD import | [`mantleplace_blender/shim.py`](mantleplace_blender/shim.py) | yes |
| the headless test of the core | [`tests/`](tests/) | no |

The core suite runs anywhere with Python 3.11 or later, and in CI on every pull request:

```bash
python -m unittest discover -s blender/tests -t blender
```

The end-to-end check runs the add-on inside Blender with no window, against a real bundle you hold:
it imports with and without Features, checks the curves, the packed textures, the offsets and the
recorded origin, and checks that a newer major is refused. No bundle is committed to this repository,
so it is run by hand:

```bash
blender --background --factory-startup --python-exit-code 1 \
    --python blender/tests/headless/run_headless.py -- path/to/bundle.zip
```

## Working on it

The add-on Blender loads is a copy in Blender's extension folder, one per Blender version, not this
tree (`HPS-50`). [`tools/Install-BlenderAddon.ps1`](tools/Install-BlenderAddon.ps1) copies the tree
there for every Blender from 4.2 and stamps the copy with its commit, branch and dirty state;
[`tools/Check-BlenderInstall.ps1`](tools/Check-BlenderInstall.ps1) reads the stamp back and says
whether the copy is current with `main`.

The inner loop:

- **Live:** nothing. Blender does not watch an add-on's files.
- **Reload Scripts** (F3, *Reload Scripts*) re-imports the add-on after an install, which picks up a
  change to the core or the shim's operator bodies.
- **Restart Blender** after a change to what registers: a new operator, property, menu entry or
  preference, or `blender_manifest.toml`. The first install also needs the add-on enabled once in
  **Edit > Preferences > Add-ons**.
