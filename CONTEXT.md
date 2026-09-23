# Mantle Place DCC Plugins

The shared language of this repository: the plugins that let a designer browse the Mantle Place vault
and import bundles inside the application they already work in.

This file is a **glossary and nothing else**. It says what terms mean, never how anything is built —
implementation lives in the code, the contract lives in the published schema, and the format lives in
[`spec/`](spec/). A term earns a place here when two people have used the same word for different
things, or different words for the same thing.

## Language

### Hosts

**DCC host**:
A digital content creation application a designer works in, and which this repository ships a plugin
for. Currently Unreal Engine and Revit.
_Avoid_: application, editor, target, integration

**Host plugin**:
The Mantle Place plugin for one DCC host. Each is written natively for its host and implements the
same behaviour; they share no code, only a contract and a conformance corpus.
_Avoid_: client, connector, adapter

**Curator**:
The person working in a DCC host who brings bundles into their project through a host plugin. Not
necessarily the customer who placed the order.
_Avoid_: user

### Brand and marks

**Mark**:
The square, full-bleed orange tile carrying the white `mp` monogram. This is *the* Mantle Place
mark, in every host and at every size. Unqualified "the logo" has meant this and the roundel to
different people, which is how two hosts came to ship two different marks.
_Avoid_: logo, icon, brand mark, tile

**Monogram**:
The white lowercase `mp` letterform inside the mark. Nameable on its own, because a host may render
it without the tile and extrude around it.
_Avoid_: letterform, initials, glyph

**Roundel**:
The superseded round treatment of the monogram. Not a synonym for the mark; saying one when you mean
the other is the confusion this entry exists to end.
_Avoid_: circular logo, round mark, the old logo

**Lockup**:
The mark set beside the wordmark. A composition of the two, never either one alone.
_Avoid_: logo, header logo, brand block

**Wordmark**:
The words "mantle place" set as brand typography, with no mark beside them.
_Avoid_: logotype, text logo

**Glyph**:
A flat monochrome command icon on a host's toolbar or ribbon, drawn in that host's own style. A
glyph says what a command does, where the mark says whose it is.
_Avoid_: icon, symbol, button image

**Vignette**:
The explanatory picture a host shows beside or beneath a command's long description, saying what the
command *produces*. Third of three and named apart from the other two on purpose: a glyph says what
a command does and lives on the button, a mark says whose it is, and a vignette answers the question
neither can — what will be in my project afterwards.
_Avoid_: tooltip image, screenshot, illustration, preview

### Sessions and identity

**Editor session**:
One run of a DCC host — from the moment the application launches to the moment it exits. Bounded by
the process, not by the user.
_Avoid_: session (unqualified), instance, run

**Auth session**:
The signed-in state held in memory during one editor session: who the user is and the credentials the
plugin currently holds for them. It begins when a stored credential is restored or a sign-in
completes, and it ends with the editor session. There is at most one per host process.
_Avoid_: session (unqualified), login, logged-in state

**Grant lifetime**:
The window over which a stored refresh token stays usable, independent of any editor session. This is
the thing that survives an application restart, and the reason a user does not sign in every morning.
An auth session is reconstructed from it; the two are not the same and do not expire together.
_Avoid_: session length, token lifetime, expiry

**Machine identity**:
The one Mantle Place account signed in on a machine, per OS user, shared by every host plugin on it.
Signing in inside one host signs the user in for all of them; signing out anywhere signs them out
everywhere.
_Avoid_: profile, account (when the machine-local credential is what is meant)

### Credentials

**Access token**:
The short-lived credential presented to the vault on each call. Held in memory for the life of an
auth session and never written anywhere.
_Avoid_: JWT, bearer, token (unqualified)

**Refresh token**:
The long-lived credential exchanged for a new access token. The only credential that is stored, and
the thing whose usability the grant lifetime describes.
_Avoid_: token (unqualified), key

**Secret store**:
The per-OS-user facility a host plugin stores the refresh token through. A machine without one
degrades to an auth session that cannot outlive its editor session, and says so, rather than storing
the credential somewhere less safe.
_Avoid_: keychain, credential cache, token cache

**Definitive rejection**:
An answer from the platform that a refresh token is permanently unusable — revoked, superseded or
expired. It ends the grant lifetime, and the stored credential is discarded because retrying it can
never succeed.
_Avoid_: auth error, 401, failure

**Transient failure**:
Any other reason a refresh did not succeed — no network, a service interruption, a machine waking
from sleep. It says nothing about the grant lifetime, and the stored credential is kept, because
retrying it later is expected to work.
_Avoid_: auth error, failure, outage

### The vault

**Vault**:
The user's collection of bundles held by the Mantle Place platform. Reaching it is the only thing in
any host plugin that requires being signed in.
_Avoid_: library, catalogue, cloud

**Vault browser**:
The in-host surface that lists the vault and starts an import. The only surface that requires an auth
session.
_Avoid_: panel, browser, window

**Prepare**:
Asking the platform to build an order's bundle for this host, and fetching the bundle once it is
built. The platform's API calls the same request a materialize.
_Avoid_: materialize, build

**Unannounced order**:
An order available in the vault that the curator has not yet been told about on this machine —
neither by a notice nor by seeing it listed in the vault browser. Not the same as an order that has
not been prepared.
_Avoid_: new order, unseen order, unread order

**Bundle**:
A published unit of real-world site data a host plugin imports. What one is, and what it contains, is
described in [`spec/`](spec/).
_Avoid_: package, asset, dataset, download

**Bundle import**:
Bringing a bundle the user already holds into the open document. Deliberately requires no account, no
sign-in and no server call — it is not a vault operation and must never acquire one.
_Avoid_: load, ingest, sync

### Orders and builds

These two were one word for a long time, and a host plugin that keys anything on the wrong one
duplicates a user's content instead of replacing it.

**Order**:
What a customer asked the platform for. Stable: the same order rebuilt and re-delivered is still that
order, and it is what a user thinks of as owning.
_Avoid_: purchase, request, job

**Job**:
One run of the platform's pipeline that produces a bundle. Rebuilding an order produces a new job, so
a job identifies a *build* and never a thing a customer owns.
_Avoid_: build, run, materialization, order

**AOI**:
The area of interest an order covers.
_Avoid_: region, extent, tile, site

### Units and frames

"An imperial bundle" has meant a bundle whose every file is in feet, and it has meant an order whose
customer chose feet. Neither is what ships: the same bundle carries files in more than one frame, on
purpose, and a reader who assumes one frame per bundle places something in the wrong one.

**Unit system**:
The customer's choice on an order — metric or imperial. It says what the customer asked to work in,
and it does not by itself say what unit any one file is in.
_Avoid_: units (unqualified), imperial bundle, metric bundle

**Delivery tier**:
The published name for what an order's unit system resolved to on its AOI — which grid the delivery
is stated on, and in which linear unit. Two imperial orders can land on different delivery tiers, and
on one of them no foot grid exists, so the origin and the files are stated in different units.
_Avoid_: imperial tier, foot tier, State Plane bundle

**Delivery CRS**:
The projected coordinate reference system an order's delivery names, where it names one. It belongs
to the delivery, not to every file in the bundle: each file states its own frame, and a fixed-frame
host's content need not be in it.
_Avoid_: bundle CRS, project CRS, the EPSG

**Frame**:
What one file's coordinates are stated in — a coordinate reference system, or an offset from a stated
origin, and a linear unit. A frame belongs to a file, never to a bundle and never to a host block.
The word has also been used for the delivery CRS and for whether a file's coordinates are absolute
or offsets; each of those is one part of a frame.
_Avoid_: bundle units, coordinate system (for a whole bundle), delivery frame

**Host frame**:
The coordinate reference system a DCC host's origin is stated in on one order, which is where the
content made for that host lands. It is the host's, not its files': each file the host block points
at still states its own frame.
_Avoid_: plugin units, import units

**Fixed-frame host**:
A DCC host with one native unit, so its host frame does not vary with the order's unit system.
Unreal is one: the content made for it is metric on an imperial order.
_Avoid_: metric host, metric-only plugin

**Order-frame host**:
A DCC host whose host frame follows the order's delivery instead of staying fixed. Revit is one.
_Avoid_: imperial host, unit-aware plugin

### What a bundle carries

**Manifest**:
The document inside a bundle that declares what shipped and the pre-derived values a host must apply.
The published schema, not this repository, is its authority.
_Avoid_: metadata, index, descriptor

**Landscape layer block**:
One named division of a bundle's landscape data — the material weights, and the coverage rasters
beside them. Its name is a manifest key.
_Avoid_: layer

**Paint layer**:
One named material a landscape surface is painted with, carried as a weight channel. Its name comes
from the platform and a host applies it exactly as given, because that name is what a landscape
material binds to. A paint layer is not a landscape layer block; the paint layers all live inside one
of them.
_Avoid_: layer, landscape layer, material layer, landcover class

**Band legend**:
The fixed order of the paint layers, published in the manifest. It says which weight channel is which
material, so a host never infers that from a filename.
_Avoid_: channel map, material list, band order

**Coverage raster**:
A single-purpose raster describing the ground — slope, water, canopy and their like — delivered for a
host to use as data. Distinct from the paint layers, which describe how the ground looks.
_Avoid_: mask, layer, overlay

**Drape**:
Imagery laid over terrain as its surface appearance.
_Avoid_: overlay, texture, basemap

**Site model**:
The bundle's IFC of building massing and context terrain, one element per building. Say site model
for the artifact; the buildings a host makes from it are context buildings. Unqualified "buildings"
has meant this, the building mesh and the building footprints to different people.
_Avoid_: buildings, the IFC, site file, massing

**Tree point**:
One published point of the bundle's tree layer — a position, a ground height, a height and a crown
radius — whatever its foliage type. A shrub is a tree point; the name is the manifest's, and a host
does not rename what a pointer names.
_Avoid_: planting point, tree (for a point of any foliage type), vegetation point

**Foliage type**:
The platform's closed vocabulary for what a tree point is — a tree, a shrub. A host maps a value to
a family and never infers one from height or crown. "Tree" and "shrub" name the foliage type and
nothing wider.
_Avoid_: species, tree type, vegetation class

**Shrub (foliage type)**:
A tree point the platform classified as a shrub. It is a plant, at a position, with a height and a
crown radius, and a host gives it its own family. Say *shrub foliage type* wherever the ground sense
below could be meant.
_Avoid_: bush, scrub, shrub (unqualified, where both senses are in play)

**Shrub (land cover)**:
A published land-cover polygon whose subtype is `shrub` — a *ground surface*, which a host paints
with a material named for tall growth. It is scrubby ground, not a plant, and the polygon says
nothing about what stands on it: a tree point of either foliage type can sit inside one. Two
platform vocabularies happened on the same word and neither is ours to rename, which is why both are
here.
_Avoid_: shrubland, shrub layer, shrub (unqualified, where both senses are in play)

**Planting (Revit)**:
Revit's own category for plants, and the one row of Revit's import checklist that brings in the tree
points of every foliage type. It is the host's noun for a host construct, not a Mantle Place word:
the layer is the tree layer and each point is a tree point, in every host.
_Avoid_: planting (for the layer or a point), planting point, vegetation

**Published contours**:
The contour linework a bundle ships: one flat line per contour, at the elevation the file states and
the interval the order was built with. It is fixed, and does not follow later edits to a terrain.
_Avoid_: contours (unqualified), contour lines

**Flood zone**:
One published flood-hazard area from a public flood map, carrying that map's own zone and subtype
exactly as the map states them. A host shows it as context; it is never a determination about a
site, which only the authoritative map panel makes.
_Avoid_: flood overlay, floodplain (for a zone), flood layer

**Steep ground**:
The published area steeper than a threshold the platform chose and states beside it. The threshold
is the platform's policy, not a host's; a host shows the area and its stated threshold, and never
draws its own from the slope raster.
_Avoid_: steep slope, steep-slope overlay, slope polygons

### What a host builds from a bundle

**Terrain**:
The ground surface a host builds from a bundle's surface artifact, and the thing an import owns. In
Revit it is one toposolid; in Unreal it is the landscape. One bundle has exactly one.
_Avoid_: topo, toposurface, mesh, DEM

**Ground**:
A toposolid's *role* in a Revit document — one that is not another toposolid's subdivision. Every
terrain is a ground; a project can hold grounds this import did not make, and counting them is how a
duplicate terrain is detected. Say ground when the question is "what kind of toposolid is this", and
terrain when the question is "whose surface is this".
_Avoid_: main toposolid, base toposolid, alternate

**Subdivision**:
A toposolid cut into a ground, carrying its own surface, its own material and its own contour lines.
A bundle's land-use, land-cover, water and road-surface polygons all become subdivisions; a ground
can hold many, and they may overlap one another because the published polygons do. One can have
holes in it — a water body's islands, a road network's city blocks. Say subdivision whatever it was
cut from — where it came from is the polygon's business, not the element's.
_Avoid_: district, region, site-boundary polygon, sub-toposolid

**Toposolid contours**:
The contour lines Revit draws on a toposolid from its own surface, at an interval Revit's own
settings choose. They follow the surface as it is edited; published contours do not.
_Avoid_: contours (unqualified)

**Context building**:
One native element a host creates from the site model, per building in it. Selectable, renderable
and stamped, where a linked site model is none of those.
_Avoid_: building, massing, generic model, context geometry

**Hazard plan**:
Revit's flat plan of one bundle's build where its flood zones and steep ground are drawn, apart from
the terrain and the model a visualiser renders. A later build gets its own; a plan a curator may
have annotated is never redrawn.
_Avoid_: hazard overlay, flood view, analysis view

**Zone key**:
The key drawn inside a hazard plan naming each flood zone and steep-ground threshold that plan shows,
in the published wording. Distinct from the band legend, which orders paint layers.
_Avoid_: legend, hazard legend

**Stamp**:
The identity a host writes onto something it created so its next import recognises it. It names the
order and, for the terrain and the tree points, the build — never the element id, which identifies a run
rather than a document.
_Avoid_: tag, marker, label, key
