---
name: adr-0030-unreal-plugin-auth-vault-and-cesium
description: Decisions recorded from comments in the Unreal plugin's runtime module, conformance corpus reader, Cesium streaming script and install tools. Covers sign-in and token rotation, manifest version handling, vault and materialize responses, corpus strictness, Cesium frame and clipping, and packaging. Read before changing any of these.
status: accepted
---

# 30. Unreal plugin runtime, corpus reader and Cesium streaming: decisions recorded from code

Date: 2026-11-21

## Status

Accepted. Recorded from the code comments that held these decisions, when the comments were removed; each decision was already in force in the code and none is new. The locked-row decision (a locked Extra is shown as a locked row) is superseded by [ADR 0034](0034-no-plugin-upsells.md).

## Context

These decisions lived in comments in the Unreal plugin: the runtime module's public headers (auth, vault, landcover, coverage rasters), the conformance corpus reader used by its tests, the Cesium streaming Python script, the plugin packaging filter and the local install tools. Comments are being removed from the code, so the decisions and their reasons are recorded here.

## Decisions

### Sign-in and the auth system

**The auth system compiles in the public mantle.place routes, with no packaging-time secret and no identity-provider-direct path, and the auth subsystem owns the session.** Sign-in then works without configuration, and the session is not held by a Blueprint child.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceAuthSystemBase.h

**The host query parameter on the login URL is display-only.** It is not a credential, is allowlisted and is never validated against a client registry, so it must not be signed or treated as a client id.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceAuthSystemBase.h

**LoopbackPorts stays empty so the OS picks a free port per session.** Fixed ports collide with Windows Hyper-V reserved ranges, which move across reboots, and with a second host signing in at the same time.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceAuthSystemBase.h

**A free loopback port is found by binding a throwaway socket to port 0.** FHttpListener asserts on port 0, so the HTTP server cannot be asked to choose one.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceAuthSystemBase.h

**The loopback port is bound once per process and reused.** FHttpServerModule keeps every listener until module shutdown, so a new OS-assigned port per sign-in would leak one socket each.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceAuthSystemBase.h

**Callers join an in-flight refresh instead of starting another.** Two renewals would make the second present a refresh token the first already rotated away.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceAuthSystemBase.h

**A refresh or restore discards the stored token only on a definitive platform rejection, and keeps it on a transient failure; sign-in is always non-definitive.** No stored credential is at stake in a sign-in.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceAuthSystemBase.h

**The refresh token actually sent is remembered, and when the stored token differs from the one in memory the stored one is the freshest.** A difference means the other host (Revit) rotated the shared token since this host last read it, so a rejection of the sent token concerns a superseded token, not the session.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceAuthSystemBase.h

**Only one rotation-loss retry is allowed per successful renewal.** Two hosts rotating the shared refresh token could otherwise ping-pong indefinitely.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceAuthSystemBase.h

**Session tokens are a plain in-memory struct, not a USTRUCT, and are never serialized or written to disk.** Raw JWTs are then not exposed to Blueprint or the reflection system.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceAuthTypes.h

### Manifest version handling

**A manifest version is kept and passed as a raw string spanning both families (the integer era, such as 19, and semver, such as 1.0.0), never parsed into a number for display or caching.** In the cache an empty value means the sidecar reported nothing, which is not the same as reporting something old. Listings span both eras, and parsing would show a pre-history bundle's integer version as 0. The version is displayed, not gated on, and the conformance corpus's pinned version is likewise returned verbatim so callers compare through the total order.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceBundleCacheTypes.h, unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceVaultTypes.h, unreal/MantlePlace/Source/MantlePlaceRuntime/Public/Tests/MantlePlaceConformanceCorpus.h

**The minimum supported manifest version is one constant, read by the importer, the bundle cache and the conformance tool (which regexes the header).** Bundles below it are rejected and re-procured, never dual-parsed, because older integer-versioned bundles use an unreadable dialect.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceVaultTypes.h

**The reader refuses a manifest whose major version exceeds the newest it speaks, with an update-the-plugin message, instead of parsing best-effort.** A new major promises that something the reader relies on may have changed meaning.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceVaultTypes.h

**A version that is not strict MAJOR.MINOR.PATCH (absent, integer, partial, pre-release) is invalid and refused, and components reject signs, decimal points and leading zeros.** Coercing to a number such as 0 would only work by accident. IsNumeric accepts some of these forms, but semver and the platform's publisher and gate forbid them, so accepting them would admit versions the contract says do not exist.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceVaultTypes.h

**Every non-semver version string (integer-era, absent or malformed) sorts below any semver floor.** Callers then never need to know which era a stored version came from, notably cache sidecars written before the re-baseline.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceVaultTypes.h

**An unparseable floor constant makes the below-floor check return false, enforcing nothing, rather than true.** A cache invalidating every entry over a mistyped constant is a far worse failure than lapsed enforcement.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceVaultTypes.h

### Coverage rasters, extent and foliage

**Coverage rasters record their orientation.** They keep the source map's row order through the texture factory, while heightmap and weight planes are transposed onto the Landscape grid, and a mismatched sample looks plausibly wrong.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceCoverageRasterTypes.h

**UMantlePlaceCoverageRasterData lives in the Runtime module.** UAssetUserData is serialized into the texture, and a payload class in an editor-only module would fail to load in a cooked build.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceCoverageRasterTypes.h

**An Extent box at exactly (0,0) is refused as unlocated, and rotation is ignored.** Sending it would place the request in the Gulf of Guinea, and the curator's W,S,E,N extent is axis-aligned.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceCuratorExtentLogic.h

**Unknown foliage_type values read as Tree, and the type is never derived from height or crown.** The platform's vocabulary only grows, and a tree is what the layer is otherwise made of.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceLandcoverTypes.h

**The tree point row's property names are the DT_TreePoints column contract, and a rename is a deliberate documented change.** PCG graphs bind to the columns by name. The row type lives in Runtime so it loads in cooked builds.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceLandcoverTypes.h

**The tree scatter element runs on the game thread only.** It reads rasters and meshes as UObjects from their source data.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceTreeScatterSettings.h

**The tree scatter element is not cacheable.** A re-import replaces the textures under the same names, so a cached PCG result would outlive the data it was built from.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceTreeScatterSettings.h

### Vault client and responses

**GetPresignedBundleUrl exists so callers never spell the whole-bundle token themselves.** The logic header is private to Runtime, and the last workaround hardcoded the platform's deprecated, ambiguous glb alias.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceVaultClient.h

**Token renewals are capped at one per successful vault response.** A platform answering 401 to everything would otherwise loop renew-and-replay forever, each renewal costing a network round trip.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceVaultClient.h

**A locked Extra is shown as a locked row and never requested; a row that does not say is treated as not locked.** The platform refuses a request for a locked Extra with HTTP 403 and code extra_locked.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceVaultTypes.h

**Sidecar fields on legacy bundles are null, meaning unknown, each with a has-flag companion; when the flag is false the value is unknown and the sha256 integrity check is skipped rather than failed.**
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceVaultTypes.h

**A presigned download URL is a direct R2 GET valid for 24 hours, so it is minted fresh for every import and never cached past its expiry.**
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceVaultTypes.h

**Files the platform refused as not in the bundle are held against the downloaded manifest.** A file the manifest lists but the platform refused is a platform fault, not a layer the site lacks.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceVaultTypes.h

**A materialize start has five wire shapes and four outcomes, and two successes name no job id; a missing job id is not a failure.** Treating it as one left the Revit host unable to import bundles with nothing left to build.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceVaultTypes.h

**A 503 with code delivery_state_unknown is not a refusal; it means try the download, which the view response settles.** The same answer also covers complete orders. The client sets this outcome from status and code, never from a body.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceVaultTypes.h

**Requested tokens the platform will never produce are reported as unproducible and stepped over, not treated as failures.** Waiting for one would wait forever.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceVaultTypes.h

**The Outstanding field lists what a new materialize request must still ask for.** The status document alone cannot distinguish a job row not yet visible (wait) from a joined run that ended without building the tokens (re-pick).
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/MantlePlaceVaultTypes.h

**Every Platform API request is created through FMantlePlaceClientHttp::CreateRequest so it carries the client header; only non-Platform calls may bypass the factory, marked on their line.** A text check enforces this because CI never compiles the plugin.
Source: unreal/tools/test_platform_requests_carry_client_header.py

### Conformance corpus reader

**The corpus reader fails, never skips, when the corpus is missing in CI or when a declared expectation key is never asserted.** A silent skip or an allow-list would let conformance suites pass green asserting nothing.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/Tests/MantlePlaceConformanceCorpus.h

**Expectation accessors use strict JSON type checks and record a key as asserted only on a successful typed read.** Unreal's TryGet*Field coerces across types, and an accessor returns false both for undeclared and for mistyped keys, so a mistyped expectation must read as nothing and be flagged as unasserted. A typed read records an explicit null as a value but yields nothing, and a wrong type records nothing, so mistyped and unasserted expectations fail identically. Nested accessors follow the same rule, because coercion would let a numeric status read as a string, assert happily and mark the path read, which an allow-list could not notice.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/Tests/MantlePlaceConformanceCorpus.h

**Nested expectation paths below the top level are tracked separately.** Tracking only top-level keys cannot tell a host asserting one leaf of a row from one asserting all of them.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/Tests/MantlePlaceConformanceCorpus.h

**Leaf collection prunes documentation subtrees but counts empty containers and explicit nulls as leaves.** Otherwise an assertion such as formats being empty, or a null value, could go unread for free.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/Tests/MantlePlaceConformanceCorpus.h

**The corpus search starts from the plugin's own base directory, not the project directory.** The plugin may be mounted as a submodule under a consuming project where no project ancestor carries the corpus.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/Tests/MantlePlaceConformanceCorpus.h

**The appliesTo field is read with strict string typing.** TryGetStringField coerces numbers and would scope a case for Unreal but not for hosts with strict JSON readers, a divergence the gate must prevent.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/Tests/MantlePlaceConformanceCorpus.h

**A missing corpus skips tests in any packaged plugin regardless of CI, but in a source tree skips only when CI is unset.** Releases do not ship the corpus, and a CI run without it means the layout moved.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/Tests/MantlePlaceConformanceCorpus.h

**Row manifest versions are the one field read in either shape (semver string or pre-history integer).** The platform widens the vault list's manifestVersion on its own schedule, so this is the contract's exception to strict typing.
Source: unreal/MantlePlace/Source/MantlePlaceRuntime/Public/Tests/MantlePlaceConformanceCorpus.h

### Plugin packaging and local install

**The packaged plugin carries no LICENSE, TRADEMARK or other licence or policy files, and they must not be added back.** The storefront refuses them beside its own licence, and the unreal-naming tools enforce this.
Source: unreal/MantlePlace/Config/FilterPlugin.ini

**A detached head at origin/main counts as main, not a preview.** The consuming project pins commits rather than branches, so a detached checkout on main is the normal current state.
Source: unreal/tools/Check-UnrealInstall.ps1

**The refresh script checks out the ref detached and refuses a dirty slot.** A branch checked out inside the submodule orphans commits on the next submodule update, and checking out over uncommitted changes loses edits.
Source: unreal/tools/Refresh-UnrealInstall.ps1

### Cesium streaming script

**Each tileset actor is yawed by CESIUM_TO_WORLD_YAW to reach the Mantle Place North-East-Up frame.** Cesium Unreal uses an East-South-Up frame and exposes no knob to change it, and a wrong sign mirrors instead of rotating.
Source: unreal/MantlePlace/Content/Python/mantleplace_cesium_stream.py

**The tileset yaw pivots on the actor origin, which equals the georeference origin and the AOI centroid, and must be verified in a real editor session.** In a pythonscript commandlet set_actor_rotation silently no-ops.
Source: unreal/MantlePlace/Content/Python/mantleplace_cesium_stream.py

**unreal.Rotator is always built with keyword arguments.** Its positional order is roll, pitch, yaw, unlike C++ FRotator, so a positional 90 in the middle tips the globe onto its side.
Source: unreal/MantlePlace/Content/Python/mantleplace_cesium_stream.py

**The module sets the georeference origin from the bundle manifest and never trusts the level's value.** The world-frame yaw pivots at the georeference origin, and a hand-set origin would swing the streamed patch around the globe.
Source: unreal/MantlePlace/Content/Python/mantleplace_cesium_stream.py

**The origin is read only from hosts.unreal.georeference.origin; the retired top-level unreal block is deliberately not consulted.** This matches the C++ reader, and a fallback quietly turns a clean break into dual-parsing.
Source: unreal/MantlePlace/Content/Python/mantleplace_cesium_stream.py

**The georeference origin height is the ellipsoid (0), not the ground height.** Level Z is orthometric and the bundle's tiles carry the same metres, so an origin at ground height would sink every Cesium overlay by the ground elevation.
Source: unreal/MantlePlace/Content/Python/mantleplace_cesium_stream.py

**Raster overlay components are added through the SubobjectDataSubsystem, the editor's own Add Component path.** UE 5.8 does not expose AActor.add_component_by_class to Python.
Source: unreal/MantlePlace/Content/Python/mantleplace_cesium_stream.py

**The streamed tileset is clipped to the AOI, and the clip polygon takes the tileset's yaw and local-space points.** ctb-tile writes flat zero-height world-spanning root tiles that render a sea-level shell, and local-space points keep Cesium's inverse transform exact.
Source: unreal/MantlePlace/Content/Python/mantleplace_cesium_stream.py

**The AOI clip runs after the world-frame yaw is applied.** The polygon overlay bakes the tileset's transform at creation, so clipping first would bake identity and put the kept region 90 degrees off the AOI.
Source: unreal/MantlePlace/Content/Python/mantleplace_cesium_stream.py

**The clip overlay and the imagery overlay are refreshed (re-added to the tileset) after their properties are set.** Each activates with empty properties at creation, so without the refresh the clip ignores its polygon list and the terrain streams untextured.
Source: unreal/MantlePlace/Content/Python/mantleplace_cesium_stream.py

**Zip members are matched case-insensitively, with the exact name tried first.** MPB readers and the engine's zip reader match case-insensitively while Python's zipfile matches exactly, and a bundle never holds two entries differing only by case.
Source: unreal/MantlePlace/Content/Python/mantleplace_cesium_stream.py

**The pending terrain tiles fetcher is held in a module global.** This stops it being garbage collected while the asynchronous download runs.
Source: unreal/MantlePlace/Content/Python/mantleplace_cesium_stream.py
