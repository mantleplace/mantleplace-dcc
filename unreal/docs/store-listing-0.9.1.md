---
name: unreal-store-listing-0-9-1
description: Draft Fab listing text for Mantle Place for Unreal Engine, release 0.9.1 - title, short and long description, what is new, technical details, and the reviewer instructions. Read before editing the Fab listing or cutting unreal-0.9.1; it describes only what 0.9.1 does, and the founder approves every word.
---

# Fab listing text, Unreal 0.9.1

**Draft for the founder's approval.** Each field below is the text to paste into the matching Fab
field. It describes only what the plugin does at 0.9.1; where the README or the release notes
([`release-notes-0.9.1.md`](release-notes-0.9.1.md), and [`release-notes-0.9.0.md`](release-notes-0.9.0.md)
for what an MPB 3.0 bundle imports) hold a fact, this file words it for a store page and does not become
a second home for it. Fab's submission form shows each field's limit and a live count; check every field
there before pasting, and the form wins over this file.

## Title

Mantle Place - Real-World Terrain, Imagery, Buildings, Roads and more

(The live title lists "Land Cover" last; this one ends "and more" like the other storefront listings.
Keep the live title if the form's limit or the store's rules say otherwise.)

## Short description

Bring a real site into an Unreal Engine level: terrain, aerial imagery, buildings, roads, trees and
more, from a bundle you ordered or downloaded. A bundle import needs no account.

## Long description

Mantle Place turns open geospatial data for any site into a bundle: one download holding the terrain,
aerial imagery, buildings, roads, land cover and more for the area you chose. This plugin brings that
bundle into Unreal Engine as a level you keep: one Landscape with the aerial imagery draped on it,
placed at the site's real position, plus the other things the bundle carries.

**What an import brings in**

- A Landscape built from the bundle's elevation, with the imagery draped on its footprint.
- The land-cover weights painted as Landscape layers.
- Buildings as a static mesh.
- Road centrelines as one typed spline actor per road.
- Tree points as a data table you can feed to PCG, and, if you turn it on in the project settings,
  a PCG foliage scatter.
- From an MPB 2.x bundle, its coverage rasters (water, land cover and their like) as data textures
  with their value mappings.
- Public-record layers (parcels, zoning, flood zones and more) drawn as one toggleable overlay of lines
  each, where the bundle states a frame the plugin places in.
- The bundle's credits, written into the import log and the project.
- And more, depending on what the bundle holds.

**Watch it work.** A **Bundle Import** window lists each step in order, with its state and how long it
has run, and a **Cancel** that stops the import between steps.

**MPB 3.0 beside 2.x.** The plugin reads MPB 3.0 bundles and MPB 2.x bundles side by side; a 2.x bundle
imports as it did before.

**Choose a level before you download.** For an MPB 3.0 order in your vault, the import window offers
the fidelity levels Max, Med and Min, each with its download size and the platform's recommendation
preselected, so you download only the level you want.

**Streamed terrain for 3.0 orders.** With Cesium for Unreal installed, the plugin can stream the same
bundle beside the imported copy, to compare. For an MPB 3.0 order it fetches the terrain tiles the
platform lists beside the bundle the first time you stream it (a signed-in session is needed for
that fetch).

**Credit and sharing.** Content made from OpenStreetMap-derived data is kept apart and tagged, and
**Tools > Mantle Place > Prepare for handover** makes a level safe to share and adds the credit text.

**Yours to keep.** The bundle is downloaded to you, and the import is ordinary Landscape, mesh, spline
and data assets in your project. Importing the same order again replaces the previous import rather
than adding a copy. The plugin saves nothing itself: use **File > Save All**.

**Account and sign-in.** A bundle import needs no account and no sign-in. Ordering a bundle needs a
mantle.place account.

**Support.** Report a problem or send feedback at `https://mantle.place/feedback`. It needs no account.

## What is new since 0.8.1

- Imports MPB 3.0 bundles, beside MPB 2.x ones.
- A Bundle Import window with a step list, elapsed time and Cancel between steps.
- For an MPB 3.0 order, choose Max, Med or Min before the download.
- Public-record layers are drawn as toggleable overlays on the Landscape.
- Terrain for a streamed 3.0 order is fetched from the order's terrain tiles.
- Imported terrain collides where it is drawn, in every import mode.
- A 3.0 order downloaded at a level now brings its public-record layers with it.
- Tree points that lie on the Landscape's edge now place, and a 3.0 road table with no name column
  now reads.

## Technical details

- Compatibility: Unreal Engine 5.8.
- Platform: Windows 64-bit. The plugin is editor-side; its modules are `MantlePlaceRuntime` and
  `MantlePlaceEditor`, both Win64.
- Network: signing in and the vault need an internet connection; importing a bundle from disk does not.
- Engine plugins it depends on: Interchange, PCG, SQLiteCore (all Epic's own, enabled by the plugin).
- Cesium for Unreal is optional, used only for the stream-to-compare path; it is not required to import.
- Distribution: a Fab Source zip, compiled by Epic.

## Reviewer instructions

1. Open `https://mantle.place/mpb-samples` and download one of the sample bundle zips. Do not
   unzip it.
2. Install the plugin, open an Unreal Engine project in the version the listing names, accept the
   prompt to enable it, then restart when asked.
3. Choose **Window > Mantle Place**.
4. Press **Browse...**, pick the zip you downloaded, leave the mode at *Landscape*, and press **Import**.
5. The **Bundle Import** window lists the steps; wait until each reads `Done`. The level now holds one
   Landscape with the imagery draped on it, and the buildings and roads of the sample where it ships them.
6. To keep the result, use **File > Save All**.

No sign-in or account is used in these steps. The sample bundles carry OpenStreetMap-derived data under
the ODbL; each file holds its notice. The samples page serves MPB 2.0 bundles, so this walk-through
exercises the 2.x path; the MPB 3.0 path needs an order from an account.
