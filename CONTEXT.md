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

**Stamp**:
The identity a host writes onto something it created so its next import recognises it. It names the
order and, for the terrain, the build — never the element id, which identifies a run rather than a
document.
_Avoid_: tag, marker, label, key
