# Generated content — where it lands, and how a re-import stays safe

What the Unreal plugin writes into a user's project beyond the names themselves. The naming
standard — the asset prefix table and the `MP_` actor-label carve-out — stays in
[`unreal/CLAUDE.md`](../CLAUDE.md), next to each other, as
[ADR 0003](../../docs/adr/0003-naming-authority-and-mp-prefix.md) records; this file holds the rest
of what that file's Naming section used to.

## Where generated content lands

```
<ContentRoot>/<identity>/Imagery          the drape texture and its material instance
<ContentRoot>/<identity>/Mesh             the terrain static mesh
<ContentRoot>/<identity>/Buildings        the buildings static mesh
<ContentRoot>/<identity>/CoverageRasters  slope, water, canopy and their like, as textures
<ContentRoot>/<identity>/Landcover        one layer info per paint layer, the tree points table, and
                                          the foliage scatter's PCG graph when one was asked for
<ContentRoot>/Layers                      one Data Layer asset per layer family, and one HLOD layer for
                                          terrain and one for buildings, shared by every import
```

- `<ContentRoot>` defaults to `/Game/MantlePlace` and is a **project** setting —
  `UMantlePlaceEditorSettings`, Project Settings → Plugins → Mantle Place, `config=Game`, checked
  into `DefaultGame.ini`. Deliberately not per-user: two developers with different roots in one
  project produce two copies of the same import that neither one's re-import will clean up. An
  unusable value falls back to the default rather than failing the import, because the plugin
  creates and force-deletes a directory beneath it and a malformed root is not something to resolve
  creatively.
- ODbL content (made from `database` files, a missing role included) goes in the order's `ODbL` sub-folder
  (`MantlePlace/<identity>/ODbL`) and, in World Partition, the shared `DL_ODbL` Data Layer as well;
  `MantlePlaceOdblLogic` decides, and `Prepare for handover` acts on the tags `Claim` writes.
- `<identity>` identifies the *order*, not the build that produced it. See
  [ADR 0002](../../docs/adr/0002-import-identity.md) — this is the part most likely to be got wrong, and
  getting it wrong duplicates a user's landscape silently.
- **The identity does not repeat in leaf asset names.** The folder already carries it.
- The **outliner** folder is `MantlePlace/<identity>`, fixed, and does *not* follow the content-root
  setting. Outliner folders have no project layout policy to collide with, and keeping it fixed keeps
  one predictable marker for support.

## Paint layers are not ours to name

A paint layer's name is what a landscape material's `LandscapeLayerBlend` nodes bind to, so it is the
platform's material name verbatim (`HPS-33`). Conforming those names to the prefix table in `unreal/CLAUDE.md` would be
deriving a published value locally, which the boundary rule refuses. The legitimate half is already
done: the *asset* is `LI_<material>` while the layer's own name stays exactly as delivered.

## One place, not every call site

Every generated name and package path comes from `MantlePlaceImportNaming`. No `/Game/` literal, no
prefix literal and no subfolder literal at a call site — a convention with no single point of
definition rots at the first patch that does not know about it, and a naming regression compiles
cleanly.

Two things check it. `MantlePlace.Import.Naming` asserts the exact strings the module produces, and
`tools/unreal-naming/check_generated_names.py` refuses a `/Game/` literal, a bare prefix, an `MP_`
label, a subfolder path segment or an inline identity truncation anywhere else. The second exists
because the first only runs where an engine does — a line added at a call site would otherwise reach
`main` unchallenged. A line that genuinely needs one can carry `// naming-gate: allow <reason>`; the
reason is required.

## Re-import, and the two things that make it safe

Re-importing an order **replaces** its content: the destination directory is force-deleted and
rebuilt, because Interchange re-creates source-named assets. Two things stand in front of that
delete, and neither is optional.

One thing follows it. The force-delete leaves freeing each asset to garbage collection, and an asset
something still holds stays in memory at its own path, no longer public. The import would import
into it, and the level could not save the actor that references it: in a saved level the buildings
were gone after a reopen. So whatever the delete leaves behind is moved into the transient
package before the import runs (`RetireDeletedLeftovers`).

**A provenance record**, written outside the content tree under the project's `Saved` directory. The
folder is named by the *truncated* identity, so two orders sharing eight characters name one folder;
the record holds the full identity, and a mismatch refuses instead of deleting. It lives outside the
content because it has to be readable at the moment the plugin is deciding whether to delete that
content — loading an asset inside a folder about to be force-deleted is the hazard documented at the
delete itself. Its absence is also how content from 0.3.0 and earlier is recognised: that content is
refused, with its path, rather than adopted or silently replaced.

**An actor tag**, `mantleplace_import=<identity>`, carrying the full identity. This, not the label,
is what the stale-actor sweep matches. Labels are user-editable, and matching on them meant a user
who renamed an actor in the outliner broke their own next re-import and got a second landscape on
top of the first.

## World Partition levels

In a World Partition level the landscape is split, after it is built, into `ALandscapeStreamingProxy`
actors on the grid the **Landscape grid size** project setting names (components per proxy per axis,
the engine's default of 2), as the engine's own New Landscape tool does. A non-partitioned level is
not split and imports exactly as before.

- **Every proxy carries the import tag**, and the sweep also takes every proxy the parent landscape
  holds, tagged or not, destroying proxies before their parent.
- **A re-import loads the prior import's actors in unloaded regions, then replaces them with the
  rest.** Before the sweep it reads the level's World Partition descriptors, which exist for loaded
  and unloaded actors alike, and loads every unloaded one of the prior import: a tagged actor, a
  streaming proxy of a prior landscape (by its landscape grid GUID, tagged or not), and a PCG
  partition actor whose cell overlaps a prior foliage scatter. The sweep destroys them with the loaded
  ones, and what was loaded for it is released afterwards, so the loaded regions are as the user left
  them. Nothing outside the prior import is loaded, except what one of its actors references.
- **If one of them cannot be loaded, the re-import refuses and changes nothing**, with the message the
  first version gave for any unloaded actor: what it did load is unloaded again, so the sweep is never
  partial. An actor a re-import destroyed is not one of them: its World Partition descriptor lingers,
  reading as unloaded, until the next save deletes its package, so the check skips a descriptor whose
  package is in memory, dirty and holds no live actor. A second re-import before saving is therefore
  allowed.
- **Every other imported actor is still kept always loaded** (not spatially loaded). The cost is that
  those actors do not stream. The terrain mesh and the buildings are the exception: they stream,
  because an always-loaded actor is never represented by an HLOD, and the re-import loads them as
  above. Since the re-import can load any unloaded actor, the sweep no longer needs the rest to stay
  loaded, but other parts of the plugin still do: Prepare for handover finds the ODbL actors it removes
  and the credits it keeps by walking the loaded actors, and the sweep finds which unloaded PCG
  partition actors hold a prior scatter from the scatter's loaded volume. A road or a volume that
  streamed would be missed by both while its cell is unloaded. Those walks have to read descriptors
  before the rest can stream.

### Data Layers, one per layer family

Every artifact handler declares its layer family (terrain, coverage, buildings, roads, foliage,
public records), and in a World Partition level every actor it spawns, a landscape's streaming
proxies included, is assigned to that family's Data Layer. The Data Layers outliner then shows and
hides, loads and unloads, a whole family at once. A layer is made the first time an import spawns an
actor of its family, so no empty layer appears; a non-partitioned level gets none and is unchanged.

- **Per family, shared across orders, not per order.** Hiding `Buildings` hides every imported
  area's buildings, so two areas toggle together, and the outliner holds one row per family however
  many orders the level holds. The asset is `DL_<family>` in `<ContentRoot>/Layers`, beside the order
  folders and never inside one, so no re-import's force-delete can reach a layer another order uses.
- **A wipe leaves the layers.** It destroys its order's actors, which takes them out of every layer;
  the assets and the level's instances of them stay for the next import.
- **Runtime layers, initially Activated.** A game run loads and shows the import exactly as before
  layers existed, and a game can stream a family by changing the layer's runtime state.
- Like everything else here, the asset is never saved by the plugin; saving the level offers it.

### The foliage scatter, partitioned per cell

The scatter ([`foliage-table-contract.md`](foliage-table-contract.md)) is opt-in: an import that did
not ask for it spawns nothing for it. When asked, it spawns one PCG volume over the AOI, labelled
`MP_Foliage_<identity>`, tagged and claimed like every other imported actor, in the foliage Data
Layer.

- **In a World Partition level its PCG component is partitioned** on the PCG grid, so generation runs
  per cell into PCG partition actors, and those take the volume's Data Layer: the foliage family
  shows, hides and streams with its layer. The volume is claimed before its graph is assigned,
  because a partition actor takes the layer the volume has when the actor is made.
- **A non-partitioned level generates on the volume itself**, with the same graph, and gets no
  partition actor. That is the decided behaviour, not a fallback: the scatter is the same either way,
  and only its streaming differs.
- **The wipe sweeps what it generated.** Before the volume is destroyed its generated instances are
  cleaned up, and every partition actor that held them and now holds nothing else is destroyed. A
  partition actor another PCG component still generates on is left to it.
- **A re-import regenerates on the same partition actors.** The emptied ones are kept until the new
  scatter has claimed its cells, and only those it did not claim are destroyed, after the import. In
  a saved level, PCG keeps a destroyed partition actor's cell on record until the next save, and
  makes no partition actor on a recorded cell, so destroying them first left the re-import with no
  scatter at all. The same record means one sequence still loses it: a re-import without the scatter
  destroys them, and a re-import with it before the level is next saved makes no scatter. Saving,
  then importing again, brings it back.
- **An unloaded cell refuses the re-import.** The sweep reaches loaded partition actors only, and a
  partition actor carries no import tag, so a re-import counts every unloaded one whose cell
  overlaps the prior volume's footprint (a cell that only touches its edge does not count) and
  refuses, changing nothing, exactly as for any other unloaded actor of the prior import. Loading
  the regions that hold the foliage, then importing again, is the remedy.

### HLODs, for terrain and buildings

In a World Partition level the landscape's streaming proxies and the terrain mesh are put in the
`HLOD_Terrain` layer and the buildings in `HLOD_Buildings`, so a distant view of a large import
shows simplified stand-ins for the cells that are not loaded. Roads, foliage, coverage and records
get no HLOD layer: at the distance an HLOD stands in, they read as noise.

- **Per family, shared across orders**, like the Data Layers, and beside them in `<ContentRoot>/Layers`.
  Both are simplified-mesh layers: a bundle's terrain and buildings are a few large unique meshes,
  which instancing and merging do not reduce. A landscape builds through its own HLOD builder.
- **The level streams them.** Since 5.8 an HLOD layer counts only when a runtime partition's HLOD
  setup lists it, so the import adds both to the main partition's first HLOD setup (World Settings
  → Runtime Partitions → HLOD Partitions), making one from the main partition when the level has none.
- **The import never builds HLODs; building is a prompted step.** The engine's HLOD build reads the
  saved level and leaves out every unsaved actor, and the plugin saves nothing, so a build run by
  the import would include none of it. The import log says so and names the two ways to build, after
  File → Save All: the editor's **Build → Build HLODs**, or `MantlePlaceImporterLibrary.BuildHLODs`
  (`build_hlods` from Python), which refuses an unsaved level, runs the engine's builder with no
  dialog, saves nothing, and reports the HLOD actor count and the build's cost in the import log.
  Offline, the documented invocation is
  `UnrealEditor-Cmd.exe <project> <map> -run=WorldPartitionBuilderCommandlet -Builder=WorldPartitionHLODsBuilder -AllowCommandletRendering`.
- **Measured cost.** On a reference bundle (a 491 MB order: a 1009 x 1009 landscape split into 16
  proxies, a buildings mesh and 8 paint layers), on a Ryzen 9 5900X with 128 GB and an RTX 3080 Ti
  shared with another editor run, the import took 24.9 s and the HLOD build 393.6 s for 17 HLOD
  actors: about 15 s for the 16 terrain HLODs and 379 s for the one buildings HLOD, which simplifies
  and bakes the whole buildings mesh. At sixteen times the import, the build is too slow to run
  unasked even once the level is saved; it stays a step the user chooses.
