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
