# Mantle Place for Revit

The Revit plugin — host #2, and the Host Plugin Standard's first test outside Unreal. Revit is
maximally unlike Unreal (.NET, document-transactional, BIM semantics), which is exactly why it goes
second: it debugs the standard.

**Status:** all four layers landed — manifest reader, import core, browser sign-in, vault client and
bundle cache — and the last unchecked box is ticked: sign-in → pick bundle → import completed end to
end on a licensed install in **2025.4, 2026.5 and 2027.2** on 2026-09-15, which is the release floor
below. The local-zip path stays as the permanent fallback.

**Supported hosts:** Revit **2025, 2026 and 2027**, from one `net8.0-windows` build. See
[Build and test](#build-and-test) for why the compile target is the oldest of the three.

## What it does today

`Mantle Place ▸ Account ▸ Sign In` opens your system browser (`HPS-05` — never an embedded webview,
never a password field in Revit), captures the redirect on a `127.0.0.1` loopback listener bound
before the browser opens, and exchanges the authorization code with PKCE `S256`. The refresh token
is stored per-OS-user with DPAPI; the access token is memory-only and never written (`HPS-15`). A
machine with no secure store degrades to memory-only auth and the dialog says so, rather than
writing the token somewhere less safe (`HPS-16`).

**The Account button is the session, not a command.** It is one split button whose face reads
`Sign In` when you are signed out, `Signing In…` — disabled — while a browser round-trip or a stored
grant is being renewed, and `Signed In` once you are, with your address on the tooltip. Signed in,
clicking the face opens About Mantle Place; the dropdown carries your address, `Sign Out`,
`Open mantle.place`, and that About dialog, which names the build you are running, the Revit it is
running in, and where each import wrote its log. `Sign Out` is offered only when there is a session
to drop — including a stored one you are not currently signed in to, because both hosts share one
credential per Windows user.

`Mantle Place ▸ Bundles ▸ Vault` lists the bundles you own, prepares their Revit deliverables,
downloads them and imports. It is **modeless**: a build can take ten minutes and Revit stays usable
throughout. Closing the window is not cancelling — only the Cancel button cancels; the ETL job keeps
running and reopening the browser rejoins it rather than queueing a second (`HPS-24`).

Downloads are written to `bundle.zip.part`, hashed, and renamed over `bundle.zip` only once they
verify (⛔`HPS-26`). Nothing is ever evicted automatically: a purchased bundle stays until you press
`Remove Download` (`HPS-44`).

**Every window this plugin opens is headed by the mark and its purpose** — `Vault`, `Sign In`,
`Bundle Import` —
and their button faces are Title Case, the same rule the ribbon follows and for the same reason. The
brand orange appears on exactly one control in the whole plugin, the vault browser's `Import`;
everything else keeps Revit's own chrome, because an add-in that paints its own windows stops looking
like part of the host. The words, the colour and which render of the mark a header slot gets are
`WindowLabels`, `BrandPalette` and `MarkRenders` in the pure core, so the suite checks them without a
Revit licence.

`Mantle Place ▸ Bundles ▸ Import Bundle` takes a bundle you already downloaded and:

- refuses anything below manifest **MPB 1.0.0**, and anything whose MAJOR is above the line it
  reads, naming re-download and plugin-update respectively rather than dual-parsing (`HPS-31`).
  The integer pre-history (v7–v19) is below the floor as a family: a bundle cut before the MPB
  re-baseline is not merely old, it is written in a dialect this reader does not speak;
- builds the terrain from the TIN in `Surface/Surface.dxf` — its vertices are placed adaptively,
  dense on slopes and sparse on flats, where `Surface/SurfacePoints.csv` is a perfectly regular
  lattice whose cells are cocircular and therefore degenerate to triangulate, so the triangulator
  picks slivers and fans by tie-break. It is also the cheaper of the two: on the bundle this was
  measured against, 75,203 TIN vertices against the grid's 80,940.
  ⚠ **This is not what made the imported ground read as faceted, and an earlier version of this
  list said it was.** The mosaic outlived the move to the TIN. `Toposolid.Create` takes points and
  re-triangulates, so a toposolid is a triangulated mesh whatever the vertex source is — the faceting
  is Revit mapping the drape per face, and the only thing that changes it is the smooth-shading
  setting below, which the import turns on and anchors the photograph for;
- falls back to the points file when the DXF is missing, or when the bundle publishes no origin to
  reduce its absolute coordinates against — the points file is already local, so it needs none — and
  falls back again to linking the DXF as CAD when neither surface can be built. Whichever tier is
  used, it says which and why;
- **is repeatable.** A second import of the same bundle recognises the ground it built — the terrain
  carries a stamp naming the order and the surface build, the way the site-boundary subdivisions
  already do — and reuses it instead of laying a second terrain on top of the first. Where the ground
  came from an *earlier build* of the same order it stops and names the toposolid to delete rather
  than either stacking or deleting: a curator's buildings and views may be standing on it, and this
  plugin does not remove ground it did not create in that run. A toposolid it does not recognise —
  yours, another order's, or one from an import that predates the stamp — is left alone and reported.
  See [ADR 0004](../docs/adr/0004-revit-terrain-identity.md);
- copies every building in the site model (`Site/Site.ifc`) into the project as its own Generic
  Model element — the platform's own extrusion, not one rebuilt here — so each can be selected,
  hidden or coloured, and a renderer's live link sees it as model geometry. The site model's context
  terrain is left out: the terrain is the toposolid. Each building carries
  `Mantle Place Building {stem}/{build}/{GlobalId}` in Comments and follows the terrain's
  reuse / refuse / create table, so a second import of the same build creates nothing, and buildings
  from an earlier build are refused with the prefix to delete. Linking the site model is still a row
  in the import window's checklist, and it starts unticked; the version-qualified companion `.rvt` is
  written only when it is linked. See
  [ADR 0012](../docs/adr/0012-context-buildings-come-from-the-site-model.md);
- sets the survey point / shared coordinates from `hosts.revit.georeference.origin.projected` —
  this host's own block — falling back to `delivery.local_origin` on a bundle whose own block
  publishes no usable origin (`HPS-33`);
- sets the project's **site location** — Manage ▸ Location, which is what places the sun in every
  view and renderer — from the latitude and longitude in `hosts.revit.georeference.origin`, verbatim,
  and from nowhere else. The time zone is the bundle's `location.time_zone`, written
  after the coordinates, because Revit recalculates the zone from the longitude whenever they change
  and a zone derived from longitude is derivation. Revit takes only UTC−12 to UTC+12, so a zone east
  of +12 (Tonga, Samoa, Kiritimati, the Chatham Islands) wraps by a day: +13 goes in as −11,
  the same clock time one calendar day later. Revit's daylight-saving switch cannot be set by an
  add-in, so the log says whether the zone observes it. A bundle that publishes no zone leaves the
  project's own in place, and the log names the zone set or kept;
- makes one 3D view, **Mantle Place Site Context**, and one view filter of the same name that
  matches every element whose Comments begin with `Mantle Place` — everything an import stamped —
  across every model category that has Comments. The filter goes on the new view with no override,
  ready to hide, halftone or recolour the site context in any view it is added to. A second import
  finds both by name and leaves them as they are;
- draws the road centrelines from the `road_splines` vector layer as DirectShape linework, drapes
  the `land_use` boundaries onto the terrain as toposolid subdivisions, and places the trees from
  `Landcover/TreePoints.csv` at their published height and crown radius — the three rows that closed
  the Forma Site Design Add-In parity gap. Each tree is an instance of one Planting family,
  **`Mantle Place Tree`**, with the published numbers in its `Tree Height` and `Crown Radius`
  instance parameters (not `Height`: every Planting family already has a built-in *type* parameter
  of that name) and its row's stamp in Comments. The family ships inside the add-in; when it cannot
  be loaded, the step builds the same trees as DirectShapes on the Planting category and the log
  says why. A family already in the project is used as it stands and never reloaded over, so a
  curator's edits to it survive a re-import — and a newer build's family reaches that project only
  when the curator reloads it. One family only — a shrub or any other foliage type waits for the
  platform to publish one, and the add-in never guesses a type from a height. **No render
  substitution is set, and the add-in never sets one:** linking the family to a renderer's tree
  asset is the curator's step, in that renderer's own tool — and in Twinmotion it costs the
  published sizes. Checked in **Twinmotion 2026.2 from Revit 2027, September 2026**: with a tree
  asset set on the type's *Twinmotion Substitution*, every tree in the forest renders at that
  asset's own size, and raising the family's built-in type `Height` does not move it, so a published
  5 m tree and a 30 m one come out identical. Without a substitution the family renders as modelled,
  each tree at its own published height and crown. Two more things a curator meets there: Twinmotion
  applies a substitution only when **Enable Substitution** was ticked in the import that brought the
  model in — setting one afterwards and synchronising into a scene imported without it does nothing
  — and it goes on drawing the Revit geometry beside the substituted assets, which is a second
  forest until the Revit trees are hidden in its scene graph. All three are positioned from the same
  published origin as the survey point, and a bundle whose origin is in a CRS they cannot be brought
  into is **skipped with that reason** rather than placed ~2000 km out;
- cuts the `land_cover` polygons into the terrain as subdivisions too, the same way and stamped
  under their own kind. `land_cover` is a different layer from `land_use`, not a second name for it,
  and it is the one carrying the physical subtype — forest and its like;
- cuts the `water` layer's **water bodies** and the `road_polygons` layer's **road surfaces** into
  the terrain as subdivisions as well, after the two land layers and stamped under kinds of their
  own. Polygons only: the streams in `water` stay out, because widening a centreline into an area is
  deriving what nobody published. A road surface comes in **flat**, at the terrain's own surface —
  recessing it is issue 158. These two keep their **holes**: a water body's islands and a merged road
  network's city blocks are cut out of the subdivision rather than becoming subdivisions of their
  own, which is what the land layers do with an inner ring. Nothing is clipped against another layer
  and no layer wins where two meet — the published polygons overlap because the ground does, and
  Revit keeps them all, with the overlaps counted in the log. `road_polygons` is derived from `road`
  and is **best-effort**: where a bundle carries the roads and not the surfaces, the log says the
  surfaces were not derived for that order rather than reporting an area without roads;
- names each subdivision's drape material with the **renderer keyword** for its published `subtype`
  — `Mantle Place Site Imagery {stem} grass`, and so on — so Enscape grows 3D grass on it without
  anything being renamed by hand. The photograph stays; the keyword rides on the name, last, in the
  renderer's own word order (`tall grass`, never `grass tall`). The table from subtype to keyword is
  `RendererKeywords` in the pure core; a subtype it does not name, and a hole cut out of a polygon,
  get no keyword. The water bodies and the road surfaces take their word from the **layer** instead —
  `water` and `asphalt` — because a reservoir, a pond and a swimming pool are one material and the
  road surfaces are merged per class before they are published. Those two are plain material names
  first: Enscape documents `water` as one of the words it reads, nothing documents `asphalt` to any
  renderer, and no renderer effect is claimed as verified. Enscape growing grass on a keyworded
  material has not been watched yet;
- drapes `Imagery/Drape.png` over the terrain as a real-world-scaled material texture — the last
  parity row — on a **duplicated** toposolid type, so the project's own type is
  never repainted. The rectangle the image is pinned to is not taken on trust: the only extent this
  host may read is undeclared by the published schema, so it is used only when the image's own pixel
  grid times `imagery.gsd_m` reproduces it, and refused with a stated reason when it does not;
- turns on Revit 2025's **toposolid smooth shading** and **anchors the photograph for it**. The
  faceting is not lighting: under flat shading Revit maps a real-world-scaled bitmap per face, in each
  face's own plane, so every triangle carries its own slice of the image and the ground reads as a
  mosaic that no view style, sun setting or self-illumination touches. Smooth shading maps it
  continuously — but measures `texture_RealWorldOffset` from the **element's bounding-box corner**
  rather than from the project origin, which is undocumented and is why a drape written for the
  origin renders as four quarters meeting at a cross the moment smoothing is on. Measured by
  exporting one view under both settings and matching every region against the published
  photograph: anchored to the corner, the photograph sits within 1.6 m of the truth everywhere, on
  smooth ground. So the import turns the setting on **first**, reads it back, and writes the offsets
  for the renderer that will draw them — the terrain from its corner, and every
  subdivision from its own, each with its own material, since one material carries one offset. The
  log says which origin each was written for and names the ribbon switch
  (Massing & Site ▸ Model Site ▸ Toposolid Smooth Shading) with what turning it off will do to the
  imagery. The setting is project-wide, so the log names the documented costs too (surface patterns
  stop drawing, paint and graphic overrides are ignored), and the plugin never turns it off. Where
  Revit refuses the setting, the photograph is anchored to the origin instead and the log says to
  import again after turning smoothing on by hand;
- writes the manifest's `attribution.sources[]` into a drafting view named **Mantle Place
  Attribution** — one line per source, its attribution text, licence text and licence URL exactly
  as the manifest gives them — ready to place on a sheet. The plugin makes no licensing decision of
  its own; it copies what the manifest says. Beside it, Project Information carries a record of the
  order, the build, the manifest version, the sources and the note's text, which is how a re-import
  finds the note this one wrote: a later build of the same order rewrites that note, an unchanged one
  leaves it alone, and any other note — one edited by hand, or another order's — is left as it is,
  with a fresh one added beneath it. See
  [ADR 0011](../docs/adr/0011-revit-provenance-record-and-attribution-note-identity.md);
- refuses to import anything at all when an artifact's bytes do not match the `sha256` its own
  manifest publishes, before a single element is created (⛔`HPS-26`);
- tells you what it did **not** import and why, using the manifest's own
  `hosts.revit.readiness.<path>.reason` where there is one (`HPS-36`).

The Bundles panel carries a **slide-out** — the small unfold arrow at its foot — holding the two
commands that are not ways to get content in. `Probe Terrain` measures what this project and a
bundle would give the terrain importer, trying every way of placing it and rolling every attempt
back, so nothing in the project changes; use it when a bundle import is refused and the log does not
say enough. `Logs` shows the most recent import or probe log in File Explorer. `Probe Terrain`
used to sit on the panel face beside `Vault` and `Import Bundle`, where a first-time user read a
diagnostic as a third way to import.

**Neither log has a fixed home**, which is the whole reason `Logs` exists: both writers put
their file beside the zip they were handed, so a bundle downloaded from your vault logs inside its
own order folder under `%LOCALAPPDATA%\MantlePlace\bundles\`, and a zip you opened from somewhere
else logs beside that zip. `Logs` selects the newest log under the cache, and where there is
none it opens the cache and says that a zip from elsewhere logged elsewhere. The About dialog on the
Account button reaches the same place through the same function, so the two can never disagree.

**An import brings in what you tick.** Both ways in — `Import Bundle` and the vault window's
`Import` — open the modeless `Bundle Import` window on a checklist headed `Include`: one box for each
layer the bundle carries (`Terrain`, `Context Buildings`, `Site Model`, `Road Centrelines`,
`Land Use Subdivisions`, `Land Cover Subdivisions`, `Water Subdivisions`, `Road Subdivisions`,
`Trees`, `Imagery Drape`), all ticked but
`Site Model`: that row links the site model, whose buildings `Context Buildings` has already copied
in, and ticking both shows every building twice. Nothing runs until `Import` is pressed. Every kind of subdivision and the drape need the terrain, so unticking `Terrain` disables
them and says `Needs Terrain` beside each; ticking it again gives back what they were. A layer left
out creates nothing, and the log says it was left out by choice. The shared coordinates, the site
location and the attribution are not layers and are written whatever is ticked. Leaving out the drape also builds the
terrain on the project's own ground type rather than the imagery one. Closing the window before
`Import` imports nothing and leaves the last run's log as it was.

**An import is staged, and shows itself.** Once `Import` is pressed the window lists the chosen
steps, marks each one `Waiting`, `Importing`, `Done`, `Failed`, `Cancelled` or `Not Run`, and counts
the context buildings and the trees in as they go. The import runs one step, or one chunk of 200
buildings or trees, per `ExternalEvent` raise, so Revit repaints between them and **Cancel** is
honoured at the next boundary. Whatever committed before the cancel stays, and importing the same
bundle again reuses the terrain, the context buildings, the site model link, the subdivisions, the
road centrelines, the trees and the site context view and filter it finds and creates only what is
missing. Opening the site model to copy from is one call and is not counted: the window shows the
building count once it is open. Road centrelines drawn by a build that predates their stamp carry none, so the first import
after upgrading draws them once more; delete the older set by hand. The log's last line names the
steps that completed and those that never ran. What no window can show is the inside of one
commit: the terrain and the
subdivisions are one commit each — and so is the drape's retype, which only a ground built before the
terrain took the imagery type still needs — Revit reports "not responding" while one runs, and a
Cancel pressed then takes effect when it finishes. Closing the window while it runs is a cancel.

Setting `MANTLEPLACE_BUNDLE_ZIP` names the zip up front and skips the file picker, so the import
runs unattended from a Revit journal or a tester script. An unattended run raises no dialog and opens
no window — it imports every layer, runs synchronously and writes `<zip>.mantleplace-import.log`
beside the bundle instead, because a `TaskDialog` or a modeless window during journal playback is
never dismissed.

⛔ **Load the add-in by hand once after every deploy, before the first unattended run.** The
assemblies are unsigned, so the first time Revit loads a *newly built* shim it raises
**Security — Unsigned Add-In**, once per Revit version. Journal playback does not answer it, and
the failure does not look like a failure: Revit carries on **without the add-in loaded**, the
journal runs to its end and reports `finished journal file playback`, the `Jrn.RibbonEvent` lines
name ribbon buttons that were never created, and nothing happens — no import, no log, no error.
Answering **Always Load** once per version is remembered for that build; the next build prompts
again, because the trust is per assembly and the assembly changed. Getting past it is a click, and
there is no way to do it from a journal — which is the same reason the release gate below is a
person launching all three Revits rather than a script.

⛔ **And the click only counts if that Revit then exits normally.** The answer is written on
shutdown, so a session you kill — which is the obvious thing to do to a playback that has stopped
dead behind the dialog — loses it, and the next launch prompts again for the same assembly. That
reads exactly like the answer not having been remembered. So the sequence is: launch Revit, answer
**Always Load**, quit it, and only then start the playback. Measured 2026-09-16 on Revit 2026,
same assembly either way: killed after answering, the next launch prompted; closed after answering,
it did not.

The ribbon events a playback journal needs, for reference — the tab, panel and button names come
from `MantlePlaceApplication`:

```
Jrn.RibbonEvent "Execute external command:CustomCtrl_%CustomCtrl_%Mantle Place%Bundles%MantlePlaceImportLocalBundle:MantlePlace.Revit.Addin.ImportLocalBundleCommand"
Jrn.RibbonEvent "Execute external command:CustomCtrl_%CustomCtrl_%Mantle Place%Bundles%MantlePlaceProbeTerrain:MantlePlace.Revit.Addin.TerrainProbeCommand"
Jrn.RibbonEvent "Execute external command:CustomCtrl_%CustomCtrl_%Mantle Place%Bundles%MantlePlaceOpenLogs:MantlePlace.Revit.Addin.OpenLogsCommand"
```

**A slide-out item addresses the same as a panel-face one**, so moving `Probe Terrain` off the face
changed nothing above. `RibbonPanel.AddSlideOut()` does not create a named container: every item on
the panel stays a child of the panel. You do not have to guess, and you should not — Revit reports
each button's parent as it registers it, into the journal every session writes:

```
Added pushbutton Id: 6424, name: MantlePlaceProbeTerrain, ... parentId: CustomCtrl_%Mantle Place%Bundles
Added pushbutton Id: 6425, name: MantlePlaceOpenLogs,     ... parentId: CustomCtrl_%Mantle Place%Bundles
```

**A split button's dropdown is the opposite, and the same journal says so** — those items *are*
nested, which is what makes the slide-out's flatness worth stating rather than assuming:

```
Added pushbutton Id: 6421, name: MantlePlaceAbout, ... parentId: CustomCtrl_%CustomCtrl_%Mantle Place%Account%MantlePlaceAccount
```

Measured 2026-09-16 in Revit 2026, on the build that first carried the slide-out. Worth writing down
because the control path is the only handle a journal has on a button — and because the read that a
slide-out adds a level to it is a reasonable one that happens to be wrong, as the Account line just
above shows it would have been for a dropdown.

⚠ **Read off the add-in's load, not off a click.** The intent was to settle this by replaying the
recorded probe journal and watching the button fire, and that leg did not complete: every attempt
stopped at startup on `processShellCommand` and `TaskDialog "Failed to open file."`, with Revit
taking the journal as a document to open rather than a script to play, and no `Jrn.` line in it ever
executing. A hand-written journal starting at `Dim Jrn` is the suspect — a recorded journal carries a
`Jrn.Directive` preamble ahead of that line — but this is not established, so do not trust a
hand-written playback file until somebody does establish it. **The registration lines above are the
better evidence anyway**: they are Revit reporting the parent it actually assigned, they appear
before any click, and a `Jrn.RibbonEvent` that fires only tells you a command ran, never what the
button looked like or where it hung.

### Holes in a subdivision

`Toposolid.CreateSubDivision` takes a list of curve loops, and what it does with more than one of
them is undocumented. Measured 2026-09-19 in **Revit 2025 (25.4.60.9), 2026 (26.5.0.55) and 2027
(27.2.0.39)**, on a toposolid built from a 121-point grid, one subdivision per case, each in its own
transaction:

| Loops in the call | What came back |
| --- | --- |
| Outer, one inner | One subdivision, sketch profile of 2 loops, up-facing area within 0.03 % of outer − inner |
| Outer, one inner wound the same way as the outer | Identical — the winding is not read |
| Outer, two inner | One subdivision, 3 loops, area within 0.03 % of outer − both |
| Two disjoint outers | One subdivision covering both, 2 loops |
| One outer (control) | One subdivision, 1 loop |

Every case committed with no failure message of any severity, in all three versions. So an outer
loop with its inner loops **is** one subdivision with the holes left out of it, and that is how the
water bodies and the road surfaces are cut (`GroundCuts`). It is also why nothing in this tree asks
which way round a published ring is wound.

**Where two subdivisions cover the same ground, neither is on top.** Measured in the same Revit
2027, on an import that cut all four layers into one 74,852-point terrain — 23 land-use, 10
land-cover, 1 water and 4 road subdivisions, the roads cut last and over everything. The land-use,
land-cover and water polygons are a real order's; no bundle on the machine published a
`road_polygons` layer yet, so the road surfaces came from a hand-written one added to a scratch copy
of that bundle. They exercise the add-in, and say nothing about what the platform publishes. Revit posted an
overlap warning for each pair (15, 96, 3 and 26 places as the four steps ran) and kept every
subdivision. A ray straight down over six road/land overlaps met a **land-cover** subdivision first
every time, the road surface last or not at all, with every surface inside 0.02 ft (6 mm) of the
others: they are coincident, and the cut order does not decide what draws. So a renderer keyword is
never a way to paint one published polygon over another — where the curator needs one surface to
win, they choose it in the model.

### Authoring the tree family

`src/MantlePlace.Revit.Addin/Families/MantlePlaceTree.rfa` is authored by code, not by hand:
`TreeFamilyAuthoring.cs` builds it from Revit's own `Metric Planting.rft` through the Family API —
the two instance parameters, three formula parameters for the trunk and the crown's tip in the
proportions `TreeFamily` owns, a trunk extrusion and a crown blend whose ends and radii are labelled
to them — then flexes it three times, places one instance in a scratch project through the tree step's own
placement code, and measures all three against the numbers that drove them. It is the one binary
the tree step adds, and it is re-run, never edited, when the proportions change:

It has no button. It runs at Revit's startup when an environment variable names a folder:

1. Build the add-in, deploy it, and set `MANTLEPLACE_AUTHOR_TREE_FAMILY` to a **neutral folder** such
   as `C:\MantlePlace` in the environment Revit is started from.
2. Start **Revit 2025** — a family saved by a Revit loads only in that Revit and later ones. Once
   Revit has initialised, the folder holds `Mantle Place Tree.rfa` and
   `Mantle Place Tree.authoring.log`; nothing else is touched, and there is no button.
3. Commit the file as `Families/MantlePlaceTree.rfa` only if the log's last line says every
   measurement agreed.

⛔ **Revit writes the folder a file was saved in, and the saving Revit's user name, into the file.**
Neither shows in the Family Editor and both are public once pushed. The run refuses to call a
family ready when the folder is under a user profile, or when it was saved by any Revit but 2025; the user name — for a Revit signed in to an
Autodesk account, that account's name — it reports, for whoever commits the file to judge.

## Layout

```
src/MantlePlace.Revit.Core/    pure logic — no Revit API, no I/O, no NuGet; net8.0
src/MantlePlace.Revit.Client/  I/O that is not Revit — HTTP, cache, zip, secrets; net8.0
src/MantlePlace.Revit.Addin/   the Revit shim — transactions, ribbon; net8.0-windows
tests/MantlePlace.Revit.Core.Tests/   headless suite over Core AND Client; net8.0 + net10.0
```

Each layer is a **triad** (`HPS-02`): an impure host shim, a pure logic core, and a headless test of
the core. All protocol behaviour lives in the core, which is constructible and testable with Revit
not installed. That is what lets the conformance suite run on a hosted runner
(`.github/workflows/ci-revit-tests.yml`).

**Why three assemblies and not two.** The shim was carrying two different kinds of impure: "talks to
Revit" and "talks to the disk and the network". Only the first needs a licensed install to exercise,
and CI cannot build the shim at all — so folding the second into it left ⛔`HPS-26` (write to
`.part`, verify, rename) resting on `agent-review` alone, even though the rule names
`automation-test` as its second enforcer. Split out, `MantlePlace.Revit.Client` builds and runs on
the hosted runner like anything else.

## Build and test

**The shim compiles against Revit 2025's API — the oldest version supported, not the newest
installed.** Autodesk's .NET floors are Revit 2025 and 2026 on **.NET 8** and Revit 2027 on
**.NET 10**, and one `net8.0-windows` assembly built against the 2025 API loads in all three. The
reverse does not hold and the compiler says so: a net8.0 project referencing Revit _2027_'s
`RevitAPI.dll` fails with `CS1705`. So the referenced API version is what pins the supported range,
and aiming `RevitApiDir` at a newer install silently drops the older hosts — which is why
[`Directory.Build.props`](./Directory.Build.props) defaults it to 2025 and the shim errors with one
sentence when that install is missing.

Revit 2024 is deliberately out of range: it is .NET Framework 4.8, where `System.Text.Json` is a
NuGet package, and the tree takes no packages.

```bash
# The pure core, the client and the conformance suite, on both runtimes. No Revit needed.
dotnet run --project tests/MantlePlace.Revit.Core.Tests/MantlePlace.Revit.Core.Tests.csproj -f net8.0
dotnet run --project tests/MantlePlace.Revit.Core.Tests/MantlePlace.Revit.Core.Tests.csproj -f net10.0

# Everything, including the add-in shim. Needs Revit 2025 installed.
dotnet build MantlePlace.Revit.slnx

# Build against a different Revit's API — narrows what the output can load into:
dotnet build MantlePlace.Revit.slnx -p:RevitApiDir="C:\Program Files\Autodesk\Revit 2026"
```

If the SDK was installed per-user (`dotnet-install.ps1 -InstallDir "$env:USERPROFILE\.dotnet"`),
put that directory on `PATH` for the session — the machine-wide `dotnet` will report the pinned SDK
as missing.

## Loading it into Revit

```powershell
# Close Revit first -- a loaded add-in DLL is file-locked.
./tools/Deploy-MantlePlaceRevit.ps1
```

Builds the solution and installs `MantlePlace.addin` plus the assemblies into every supported
per-version add-ins folder, printing the timestamp of what it wrote:

```
%APPDATA%\Autodesk\Revit\Addins\2025\
%APPDATA%\Autodesk\Revit\Addins\2026\
%APPDATA%\Autodesk\Revit\Addins\2027\
```

`-Configuration Release`, `-RevitVersions 2025`, and `-SkipBuild` narrow it. Copying by hand works
too, and the script does nothing you could not do with Explorer — but hand-copying is how a machine
ends up running a plugin months older than the source tree. That failure is silent and it reads
exactly like a code bug: a fix present in `git` and absent in the symptom, with a file timestamp as
the only tell. **When a bug reproduces against code that already contains its fix, check the
deployed timestamp before anything else.**

The version now travels with the build — `<Version>` in [`Directory.Build.props`](./Directory.Build.props)
is its one home, the deploy script prints it, and it opens both the import log and the terrain probe
report. The SDK appends the source commit to it, and the build appends `-dirty` when anything under
`revit/` had uncommitted changes, so the string reads `0.1.0+<sha>` or `0.1.0+<sha>-dirty`: a
stranger's log says which build they are running without anyone having to ask, and two dev builds a
day apart no longer claim to be the same code.

### The install is a single slot, and it says what it holds

Revit loads one Mantle Place per process — the manifest carries one `ClientId`, and Revit refuses a
duplicate — so with several worktrees there is still one install, and it has to say where it came
from. The deploy script writes **`MantlePlace.install.json`** beside the manifest: the commit, the
branch, the worktree, whether that tree was dirty, and when. **About**, under the Account button,
reads it back and shows version, commit, branch and tree on one screen, so a screenshot proves the
build. The stamp records; it never refuses. A deploy from a feature branch is a preview, printed as
one, and the install returns to `main` when a session deploys after a merge — the rule is in root
[`CLAUDE.md`](../CLAUDE.md#local-installs), and the model in
[`tools/local-install/`](../tools/local-install/README.md).

```powershell
# Is the add-in in Revit the tree? One line per install, exit 0 for current or preview.
./tools/Check-RevitInstall.ps1
```

It compares the stamped commit against `origin/main`, counting only commits that touch `revit/`,
and says `current`, `preview`, `stale` (with the count and the command), or `unverified` (no stamp,
a dirty tree, or a commit this clone has never seen). It fetches first; `-NoFetch` skips that.

### Hot Reload, and what it cannot reach

```powershell
# Build, install, and start Revit 2027 ready for Hot Reload.
./tools/Deploy-MantlePlaceRevit.ps1 -Launch
```

`-Launch` starts Revit (`-LaunchVersion`, 2027 by default: the one host where a .NET 8 assembly runs
under a .NET 10 runtime, so the leg that shows a difference first) with
`DOTNET_MODIFIABLE_ASSEMBLIES=debug`. Attach Visual Studio or Rider to that `Revit.exe`, edit a
method body in `Core` or `Client`, and Hot Reload applies it to the running add-in — no deploy, no
restart. That covers the behaviour behind the buttons. It does not cover the buttons: `OnStartup`
ran once, and a ribbon change is a deploy and a restart, the same as a new type or a changed
signature. A Revit started from the Start menu has no such variable and simply cannot be hot-reloaded.

`MantlePlace.addin` names the assembly without a path, so Revit resolves it beside the manifest.

**One build, three hosts, and that is a claim to be tested rather than assumed.** Before a release,
drop the same output into each folder above, launch that Revit, and confirm the ribbon appears and an
import completes. The 2027 leg is the one that matters most: it is the only one where a .NET 8
assembly is loaded by a .NET 10 runtime. Check the ribbon in **both UI themes** while you are there —
Revit has had two since 2024, the icons are picked per theme, and the failure mode is a panel of
invisible buttons rather than an error.

## Ribbon imagery

Every button on the tab carries an image, and the images are **embedded resources** reached by pack
URI rather than loose files beside the DLL — the build action in the shim's `.csproj` is `Resource`
and not `EmbeddedResource`, because a pack URI resolves against the WPF resource table and
`EmbeddedResource` does not populate it. Nothing has to be copied at packaging time, and there is no
way to ship a release whose ribbon is blank because a folder did not get zipped.

The Account face draws the Mantle Place mark, which comes from a private vector source through
[`tools/brand-assets/`](../tools/brand-assets/) ([ADR 0009](../docs/adr/0009-host-assets-render-the-monogram.md)).
The four command buttons draw glyphs rendered from MIT-licensed SVGs committed in the tree, by

```powershell
./tools/Render-RibbonIcons.ps1   # from revit/, like the two scripts beside it
```

which is run by hand when a glyph changes and by nothing else. Both the sources and the reasoning —
which icon, which colours, which orange and why only one — are
[`src/MantlePlace.Revit.Addin/Resources/src/README.md`](./src/MantlePlace.Revit.Addin/Resources/src/README.md).

**Which file each button is handed is a pure decision and lives in the core**, so the whole of it is
asserted on a machine with no Revit licence: `RenderSizes` picks a render for a display scale,
`MarkRenders` and `RibbonGlyphs` name the file, and the headless suite reads `Resources/` in both
directions — every name the plugin can ask for is a file that is there, and every file that is there
is one something asks for. That last check is the only thing standing between a hand-run render
script and a button that is silently blank.

## Packaging a release

```powershell
./tools/Package-MantlePlaceRevit.ps1
```

Builds `Release`, assembles `MantlePlace-Revit-<version>.zip` into `dist/`, and prints its sha256.
Inside: a `Contents` folder holding everything Revit loads, with `README.txt`, `Install.cmd` and
`Deploy-MantlePlaceRevit.ps1` above it. `Contents` is Autodesk's own bundle folder name, so shipping
this as a `.bundle` later is adding a `PackageContents.xml` rather than rearranging the archive.

`Deploy-MantlePlaceRevit.ps1` ships in the zip **verbatim** — one script, two callers, reached with
`-PayloadDirectory`. That is deliberate and it is the only test coverage an installer can have here:
CI can never run it, because CI can never build this add-in.

The packaging materials live in [`packaging/`](./packaging/). `README.txt` leads with a manual copy
and offers the script second — see [ADR 0005](../docs/adr/0005-release-installs-are-copy-first.md)
before "fixing" that, because it contradicts the deploy script's own advice on purpose.

**Packaging is not the gate.** The zip proves the plugin compiles. What proves it works is the
three-host check above, run against *that zip* rather than a `bin/` folder, plus `ci-revit-tests`
green on both target frameworks. `MANTLEPLACE_BUNDLE_ZIP` skips the file picker, so each leg can be
driven unattended rather than clicked. The release track is `revit-<version>` — no `v`, and not
Unreal's number ([ADR 0001](../docs/adr/0001-per-host-release-tracks.md)).

A release published with paths that failed the gate says so in its body, precisely, rather than
quietly. The floor below which there is no release at all: the ribbon loads in all three, and one
import completes end to end in all three.

## Conformance

`revit` is registered in
[`tools/manifest-conformance/verified-against.json`](../tools/manifest-conformance/verified-against.json)
from this tree's first commit (`HPS-38`), with its version floor declared as a path plus a regex
(`HPS-39`). The floor has one home: `ManifestVersions.MinSupportedManifestVersion`. Moving or
renaming it means editing that entry in the same commit, or the gate goes red.

The suite reads [`corpus/index.json`](../tools/manifest-conformance/corpus/README.md) at run time
rather than transcribing vectors into C# literals (`HPS-40`). The reader's failure modes are
normative, and each was verified by making it fail on purpose:

- a corpus that cannot be found **fails** the suite; it never skips;
- a claimed group resolving to zero cases fails, and so does a case naming a missing vector file;
- an `expectations` key this host does not recognise fails it too, so a platform-side assertion
  cannot silently bind nobody;
- and a key it _does_ recognise, declared with the wrong JSON type, fails as well. That last one is
  a step past the Unreal reference: tracking which keys were actually read — rather than which are
  on an allow-list — is what catches a corpus typo that would otherwise assert nothing while still
  counting as covered.

Cases carrying another host's `appliesTo` are skipped. Today that binds Revit to the
**18 host-invariant `manifest` cases** — the version gate with its pre-history ladder and semver
near-misses, the base-bundle partial parse, the top-level `vector` pointers and the
materialization signals — plus this host's **own three
`appliesTo: "revit"` cases** (`manifest.revitArtifactHashes`, `manifest.revitOwnGeoreference`,
`manifest.reject.revitHashMissing`), which pin the `hosts.revit` manifest block described under
"The contract gap" below.

`revit` claims **all six groups**: `manifest`, `auth`, `vault`, `cache`, `digest`, `projection`.

**`projection` is claimed for one thing only**, per `HPS-45`: the WGS84 lon/lat → UTM forward
projection behind the `vector` layers (roads, site boundaries) — see `GeoProjection.cs` and
`ProjectionConformanceTests.cs`. Placement is different: the survey point is applied verbatim from
the manifest, and this host computes no easting, northing or zone of its own for it (`HPS-33`).

### Vector cases, and the one thing `HPS-46` does not reach

The asserted-keys rule binds `expectations` keys. A case whose `expect` is `vector` declares none —
the file itself is the payload — and every case in `auth`, `cache` and `digest` is a vector. So a
suite that drives row 0 of an eleven-row table passes, and the coverage ratchet records the case as
covered.

`VectorDocument` applies the same idea one level down: every leaf value in a vector file must be
read by a typed assertion, or the case fails **with the path**. Prose keys are exempt by an
enumerated list, which the proposed `HPS-46` amendment replaces with a naming convention. This is
this host's own stricter reading until that lands, it costs other hosts nothing, and it is proven by
`VectorDocumentSelfTests` making it fail on purpose.

**The same gap exists one level down again, inside `expectations`, and is NOT closed here.** The
asserted-keys rule binds top-level keys, so `vault.list.fullAndLegacy`'s `items` is asserted but the
per-row keys inside it are on the host's honour. This suite now reads every one of them. The one it
did not — `items[].tierLabel`, which `DeriveTierLabel` derives from the presence of `glb` and so can
only ever name the reference host — was a host-specific key on a host-invariant case, and has moved
to its own `appliesTo: "unreal"` case that this host skips as a unit. The nested gap itself is
still open.

## Configuration

**Nothing is required.** Sign-in and token refresh both reach mantle.place on compiled-in routes,
so a curator installs the add-in and signs in with no file to obtain and nothing to configure.

Refresh used to be identity-provider-direct only, which meant a machine without the project URL and
anon key could sign in once and then lose the session at access-token expiry — reporting a
misconfiguration naming a file that no packaging step produced. That path is gone: refresh goes to
the broker, always, so the route nearly every install actually uses is also the one exercised in
development, and there is no packaging-time secret in the auth path at all.

One optional file remains: `%LOCALAPPDATA%\MantlePlace\config.json`, overrides layered over the
compiled defaults when it is present, and nothing installs it. Every key in it is an override for one
of those defaults (`webLoginUrl`, `tokenEndpointUrl`, `refreshEndpointUrl`, `apiBaseUrl`,
`loopbackPorts`, `callbackPath`, `signInTimeoutSeconds`) and is how a dev build points at a
non-production stack. `loopbackPorts` is the `HPS-06a` override: leave it out and the OS assigns
the sign-in callback port, which is what keeps sign-in clear of Windows' shifting reserved ranges
and lets Revit and Unreal be signed in at the same time. Absent or malformed, the file is ignored
and the defaults stand — this is read during Revit's add-in load, where throwing costs the ribbon
button and explains nothing.

## The contract gap, and how it closed

The plugin will not derive placement values, and it will not read another host's block to get them
(`HPS-33`). Through v18 the packager computed the AOI centroid's easting/northing/EPSG in
every Revit emitter and then **discarded it** — it survived only in `mesh.origin` and
`unreal.georeference.origin`, neither of which is Revit's to read, and in `delivery.local_origin`,
which is emitted on the `local_ft` tier alone. So on every other tier there was nothing this host
was allowed to apply, and the model was imported not georeferenced at all.

**v19 closed it, and the fix was host-side.**
`revit.georeference.origin.projected` publishes the origin addressed to this host by name, and the
reader takes it as the primary source with `delivery.local_origin` as the fallback — own block wins
where both are present, which is what makes reading it a complete fix with no coordinated pipeline
release. Two traps sit under that:

- the origin's `linear_unit` is the **origin's**, not the artifacts'. They differ on the foot tiers,
  where the origin is State Plane feet and the files are international feet, so the unit travels
  with the coordinates rather than being assumed metric;
- `grid_rotation_deg` is zero by construction today — the emitters reproject per-vertex, so
  meridian convergence is already absorbed — but it is **read**, not assumed. The shared corpus case
  `manifest.revitOwnGeoreference` states a non-zero one on purpose, because a fixture that agreed
  with the old hardcoded zero would have been passed by it.

## Where the rules live

- Cross-host, binding: the Host Plugin Standard (`HPS-NN` rule IDs throughout this tree).
- Revit-specific: this folder. Import semantics — what a toposurface is, which topo path wins — are
  this host's own and deliberately not cross-host (`HPS-03`, `DOC-06`).
- Agent onboarding: [`CLAUDE.md`](./CLAUDE.md).
