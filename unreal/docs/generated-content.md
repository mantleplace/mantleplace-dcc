# Generated content — where it lands, and how a re-import stays safe

What the Unreal importer writes into a user's project beyond the names themselves. The naming
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
<ContentRoot>/<identity>/Landcover        one layer info per paint layer, plus the tree points table
<ContentRoot>/Layers                      one Data Layer asset per layer family, shared by every import
```

- `<ContentRoot>` defaults to `/Game/MantlePlace` and is a **project** setting —
  `UMantlePlaceEditorSettings`, Project Settings → Plugins → Mantle Place, `config=Game`, checked
  into `DefaultGame.ini`. Deliberately not per-user: two developers with different roots in one
  project produce two copies of the same import that neither one's re-import will clean up. An
  unusable value falls back to the default rather than failing the import, because the importer
  creates and force-deletes a directory beneath it and a malformed root is not something to resolve
  creatively.
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

**A provenance record**, written outside the content tree under the project's `Saved` directory. The
folder is named by the *truncated* identity, so two orders sharing eight characters name one folder;
the record holds the full identity, and a mismatch refuses instead of deleting. It lives outside the
content because it has to be readable at the moment the importer is deciding whether to delete that
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
- **Every other imported actor is kept always loaded** (not spatially loaded), so the sweep, which can
  only see loaded actors, always sees it. The cost is that those actors do not stream.
- **A re-import refuses, changing nothing, when any actor of the prior import is in an unloaded
  region.** Loading the regions that hold it, then importing again, is the remedy; a sweep that went
  ahead would leave the unloaded actors behind beside the new import.

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
- Like everything else here, the asset is never saved by the importer; saving the level offers it.
