---
name: adr-0027-unreal-editor-plugin-import-and-landscape-behavior
description: Decisions in the Unreal editor plugin's import path, recorded from code comments: import timing, undo transactions, provenance, Cesium streaming, landscape and mesh import, level frame anchor, local tile server, manifest parsing, tree points, vault import and World Partition re-import. Read before changing how the Unreal importer orders its steps, parses a bundle, or serves tiles.
status: accepted
---

# 27. Unreal editor plugin import path: decisions recorded from code

Date: 2026-11-21

## Status

Accepted. Recorded from the code comments that held these decisions, when the comments were removed; each decision was already in force in the code and none is new.

## Context

These decisions lived in comments in the Unreal editor plugin's private sources (the import timing, importer library, landscape, mesh, level frame, local tile server, manifest, tree points, vault import and World Partition code under `unreal/MantlePlace/Source/MantlePlaceEditor/Private/`). Comments are being removed from the code, so the decisions and their reasons are recorded here.

## Decisions

### Import timing

**Import timing is always on, with phase names in one vocabulary and nesting measured.** Performance bars come from measurement, drifting names break comparison between runs, and summing nested phases once printed 144 percent.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceImportTiming.h

**Nesting depth is measured by open and close calls paired by scope guards, not declared by the caller.** The shader stall is recorded from a deeply nested frame whose call site cannot know it sits inside an artifact phase.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceImportTiming.h

**An unbalanced phase close is clamped rather than checked.** A timing call must never be the reason an import dies, and a check would trade a wrong number for a lost import.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceImportTiming.h

**Summed rows use an accumulator that opens no nesting level and carries no detail.** The row count then does not depend on the bundle, and varying detail does not give a summed row a new identity on each import.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceImportTiming.h

**The current-import timeline pointer is resolved once on construction and held.** Looking it up twice could close a level that was never opened and stamp later rows one level too shallow.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceImportTiming.h

**The inside-transaction timing guard is declared immediately after the transaction.** Locals tear down in reverse order, so the guard is destroyed just before the transaction; declared earlier, it would measure nothing.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceImporterLibrary.cpp

**The machine-readable timeline record carries no thresholds or expected values.** The plugin is world-readable, and a committed bar would permanently state an acceptable cost on hardware the repository does not describe.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceImportTiming.h

**The record schema version is bumped only when a field changes meaning, not when one is added.** Adding the depth field did not bump it, so consumers needed no re-pinning.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceImportTiming.h

**Timeline records are written only when an environment variable names a directory.** A plugin that scattered JSON into a curator's project unasked would earn bug reports about an unwanted folder.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceImportTiming.h

### Undo transaction and editor state

**Mesh and glTF assets are imported, and named, before the import's undo transaction opens, and are never renamed afterwards. The asset wipe runs before the imports.** Renaming embedded glTF assets inside the transaction purges the undo buffer (measured, it purged a 500-actor import), and a wipe after the imports would delete what was just imported. Names come from DestinationName via ImportNameFor.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceImporterLibrary.cpp, unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceMeshImporter.cpp, unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceMeshImporter.h

**Content Browser sync is suppressed by a scoped RAII CVar override.** The Interchange completion path honours the CVar over the caller's bSyncToBrowser=false, and the override is editor-wide, so it must be restored on every exit.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceImporterLibrary.cpp

**Editor-state restores use a named scope object, not paired Set and Reset calls or ON_SCOPE_EXIT.** A later early return would silently leak the change into the editor session, and an unnamed macro local cannot be asserted by tests.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceScopedRestore.h

**The synchronous import runs in a modal slow-task window that ticks Slate only while it is the active modal.** Ticking Slate delivers every queued click, and a click on the level mid-import would land inside the undo transaction.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Slate/SMantlePlaceImportWindow.h

### Provenance

**The provenance record is written on success or cancel only, never for content that was never created.** A stray record would refuse a legitimate order's import, and a cancelled import's content needs a record so it can be completed.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceImporterLibrary.cpp

**A provenance record is accepted only when it carries this order's full import identity.** Records are keyed on the short identity, so two orders can share a key, and accepting one would let an order adopt another's extent.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceLevelExtent.cpp

### Cesium streaming and the local tile server

**The local Cesium stream server outlives the stream call, and its destructor touches only a router it already holds, never the module.** Cesium keeps fetching tiles after the call returns, and static teardown after module unload must be safe.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceImporterLibrary.cpp

**Cesium stream artifacts are staged once per bundle, and a staged bundle is reused only when both the full import identity and the manifest digest match; any mismatch re-stages.** The directory is scratch, a truncated identity can collide and serve another bundle's tiles, and an order's identity is stable across rebuilds, so only the digest detects a changed bundle.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceImporterLibrary.cpp, unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceStreamStaging.h

**The staging directory is cleared, not merged into.** The availability rewrite declares every terrain file beneath it, so a previous build's leftover tiles would be declared available and served as this build's.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceImporterLibrary.cpp

**The terrain tiles Extra is identified by a digest of its own bytes.** The Extra carries no version promise, so its bytes say the same tiles where the order id says the same order.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceImporterLibrary.cpp

**The terrain tiles download is a plain HTTP request without the Platform API client header.** The minted storage link carries its own signature and is not a Platform API call.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceTerrainTilesFetcher.cpp

**Stopping the local tile server unregisters only its preprocessor and never calls StopAllListeners.** Unreal offers only a global stop, which tore down the auth loopback callback server mid-sign-in and cleared the listeners-enabled flag that per-port bind checks depend on.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceLocalTileServer.cpp

**Tile responses are sent with Cache-Control no-store.** Successive streams reuse the same loopback URLs for different bundles, so Cesium's persistent request cache would otherwise serve a previous bundle's tiles for the new one.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceLocalTileServer.cpp

**The path containment check appends a slash to the root, so only paths strictly beneath it pass.** Without the slash, a sibling directory whose name merely starts with the root's name, or the root itself, would be accepted.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceLocalTileServerLogic.cpp

**Request paths containing two dots are refused outright, even legitimate file names.** The served tree is machine-generated, so nothing is lost, and traversal is stopped before any path arithmetic that could contain a mistake.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceLocalTileServerLogic.h

### Landscape import

**A landscape layer's name is the ETL's material name verbatim, never prettified or prefixed.** The landscape material's LandscapeLayerBlend nodes bind to layers by that exact name.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceLandscapeImporter.cpp

**The heightmap PNG is transposed, not row-flipped, so landscape X (north) indexes PNG rows and Y (east) indexes columns.** A straight row copy is a reflection that mirrored every bundle across the NE diagonal in Unreal's left-handed frame.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceLandscapeImporter.cpp

**The drape material is assigned before ALandscape::Import.** A landscape given a material after creation keeps rendering the default material until it is re-applied to a finalized landscape, because components are not rebuilt.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceLandscapeImporter.cpp

**In World Partition the landscape is split into streaming proxies after Import and before the drape rebuild, matching the engine's New Landscape order.** The parent holds no components afterwards, so the material rebuild must run after the split to reach every proxy.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceLandscapeImporter.cpp

**The drape sequence blocks to finish landscape shader compilation, then rebuilds material instances and flushes render commands, inside the import transaction.** Scene proxies snapshot component materials at construction, and a render-state recreate before shaders are ready leaves the default material.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceLandscapeImporter.cpp

**Weight bands are resampled nearest-neighbour, not bilinear.** The manifest does not say whether a png-8bit-rgba band is continuous or categorical, and deriving that locally breaks the thin-client rule. Bilinear returns only if the manifest publishes the distinction.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceLandscapeWeightsLogic.h

### Mesh import and collision

**Terrain mesh actors are spawned with a +90 degree yaw, a rotation and not a mirror.** Interchange lands glTF with East on +X and South on +Y, while the world frame has North on +X.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceMeshImporter.cpp

**Any glTF up axis other than Y is refused at parse.** Interchange converts Y-up to Z-up unconditionally, so a bundle declaring another axis would import silently rotated 90 degrees with no error anywhere.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceManifestJson.h

**Nanite meshes get a fallback that keeps every triangle, and mesh collision is set to complex-as-simple.** Complex collision is cooked from the reduced fallback, which sat up to 0.88 m off the drawn surface, and a bounded-error reduction saves almost nothing on large terrains. Interchange's convex hull would lid the terrain.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceMeshImporter.cpp, unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceMeshImporter.h

**Only one imported terrain collides: in Both mode the Landscape collides and the overlaid terrain mesh does not.** Bodies then rest on the surface the Landscape draws, not on whichever near-coincident surface is a few centimetres higher.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceTerrainCollisionLogic.h

### Georeference and level frame

**Cesium and the engine's GeoReferencing are read by reflection, looking up classes and functions by name, so the plugin takes no module dependency on either.** The plugin then builds and loads unchanged in projects without them, and an absent class means an absent source.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceLevelExtent.h

**Georeference converters are invoked through UObject::ProcessEvent, not AActor::ProcessEvent.** The actor version silently does nothing until the world's actors are initialized, such as in a loading level or throwaway world.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceLevelExtent.cpp

**The engine position is inverse-transformed by the Cesium georeference actor's transform before conversion.** Cesium reads positions in that actor's own frame, which equals the world frame only when the actor sits untransformed at the origin.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceLevelExtent.cpp

**The level's anchor is never stored; each placed import records its origin and offset, and origin minus offset yields the anchor.** Re-imports therefore recompute offsets, and the anchor clears itself when no placed import remains.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceLevelFrameLogic.h

**When no other placed import remains, a bundle whose own tag records a nonzero offset keeps that offset instead of becoming the anchor.** Otherwise deleting the anchor bundle would make the re-imported bundle jump to the world origin.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceLevelFrameLogic.cpp

**The maximum placed offset from the anchor is 100 km.** A 32-bit float stops resolving a centimetre beyond that, and the editor, physics and tools still use single precision, which would lose the seam the feature closes.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceLevelFrameLogic.h

**CRS strings are compared case-sensitively.** FString equality ignores case, and 'epsg:32613' is not the string the manifest publishes; only identical text counts as the same frame.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceLevelFrameLogic.cpp

### Manifest parsing

**The Imagery.pmtiles pointer layout.imagery is deliberately not parsed.** Cesium for Unreal has no PMTiles raster overlay, and a parsed field without a reader would advertise support the plugin does not have.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceManifestBlocks.h

**The drape alignment descriptor is a free string by contract, matched only on its prefix and never the whole value.** The divergent form embeds measured numbers, and a 'matches' claim the extents refute is refused at parse.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceManifestBlocks.h

**Declared payloads with no published digest stay in the integrity chain carrying an empty Sha256.** They are the source of the 'valid but unverified' report, and dropping them would make verified and unverified indistinguishable to callers.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceIntegrityLogic.h

**Credit tags longer than the FName limit are split into numbered part tags and never truncated.** An over-long FName is an assertion that takes the editor down, and the credit must stay the manifest's verbatim words.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceOdblLogic.h

### Tree points

**A tree file with a real share of points outside the landscape extent is refused whole, while a few edge points are trimmed and counted. A file in a frame other than the fixed metric UTM frame is Unplaceable and skipped with nothing converted.** A wrong frame such as foot State Plane yields plausible finite positions far away, and producers can state a frame wrongly, so the landscape-extent test stays as a backstop behind a stated frame.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceTreePointsLogic.cpp, unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceTreePointsLogic.h

**A tree-points frame owed by the manifest version (MPB 1.3.0 and later) that is missing is refused, never read as pre-1.3.0.** The version decides the bundle kind, and the absence of a key must not.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceTreePointsLogic.h

**Tree-points integrity uses two checks, and a count mismatch fails the whole import rather than skipping the layer.** The sha256 proves the bytes and the point count proves the rows survived the reader, since skipped malformed rows leave the digest matching. A subset would build a foliage scatter that reads as a sparse forest, not an error. A count of zero means unknown, and the frame check runs before the count.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceTreePointsLogic.h

### Vault import

**The level download starts on the next tick, not inside the Download click.** A kept level imports at once, and the import moves the window holding the clicked button into its modal host.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceVaultImportOrchestrator.cpp

**A complete order is fetched as Unreal's own view and imported with no polling or re-list, and a start with an unknown delivery proves nothing.** A whole archive in the answer is therefore not trusted.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceVaultImportOrchestrator.cpp

**When the platform reports nothing to build there is no job, so the importer goes straight to the re-list instead of polling.** Polling would sit on a waiting message for the whole budget and then time out.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceVaultImportOrchestrator.cpp

**For a joined materialize run the importer keeps this host's own required token set instead of the run's.** The platform's unreal keyword and the plugin's targeted tokens are maintained separately and have already disagreed.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceVaultImportOrchestrator.cpp

**A joined materialize run counts as observed from the start.** It was already going and may finish before the first poll, so its active job would never be seen and the import would wait for a finished job.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceVaultImportOrchestrator.cpp

**A pending status with nothing in flight is treated as ambiguous: once a run has been seen and layers remain outstanding, the importer re-picks them instead of polling.** Polling there previously froze a progress bar for ten minutes before a timeout.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceVaultImportOrchestrator.cpp

**After materializing, the vault list is re-fetched.** The bundle download verifies the sha256 fail-closed, and the pre-materialize list item's digest is stale.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceVaultImportOrchestrator.cpp

**The downloaded manifest is pre-flighted before the import is announced.** The listing's completeness flag has advertised completeness a base bundle lacked, so the downloaded bytes are the authority and an incomplete bundle is completed in the cloud.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceVaultImportOrchestrator.cpp

**Progress ticks with no message are not logged; the UI bar still receives every tick.** They arrive dozens per second and flooded the log, and the log records only phase transitions, which carry messages.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceVaultImportOrchestrator.cpp

**A UObject controller sits between the Slate vault panel and the orchestrator, and it roots the orchestrator chain.** Dynamic delegates need UObject handlers, which a Slate widget cannot be, and a widget does not keep UObjects alive.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceVaultPanelController.h

### World Partition re-import

**The cell-footprint overlap test is strict rather than using FBox2D::Intersect.** Intersect counts a shared edge, and a cell merely beside the AOI holds none of the scatter.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceWorldPartitionLogic.cpp

**The wipe destroys streaming proxies first and the landscape parent last.** Destroying the parent first leaves proxies pointing at a landscape info with no owner, which the engine's own CanDeleteLandscape refuses the other way round.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceWorldPartitionLogic.h

**Imported actors in a partitioned level are kept always loaded except streamed families, even though the wipe now loads them.** Handover and the wipe's scatter-footprint lookup iterate loaded actors only and would miss unloaded roads or scatter volumes.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceWorldPartitionLogic.h

**An unloaded PCG partition actor is tied to a prior foliage scatter only by its cell footprint overlapping a scatter volume.** It carries no import tag and is shared across components, and a wipe cannot clean an unloaded one.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceWorldPartitionLogic.h

**An actor descriptor whose package is in memory, dirty and holds no live actor belongs to an actor destroyed this session and is not counted.** The next save deletes it, and counting it would wrongly refuse a re-import.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceWorldPartitionLogic.h
