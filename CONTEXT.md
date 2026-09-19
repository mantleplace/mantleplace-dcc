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

**Context building**:
One native element a host creates from the site model, per building in it. Selectable, renderable
and stamped, where a linked site model is none of those.
_Avoid_: building, massing, generic model, context geometry

**Stamp**:
The identity a host writes onto something it created so its next import recognises it. It names the
order and, for the terrain and the tree points, the build — never the element id, which identifies a run
rather than a document.
_Avoid_: tag, marker, label, key
