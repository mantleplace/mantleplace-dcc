---
name: adr-0028-unreal-plugin-editor-import-and-tests
description: Decisions held in comments in the Unreal plugin's editor module and its automation tests, recorded when the comments were removed: bundle and manifest reading, import identity and wipe safety, settings, terrain and tree-points handling, the local tile server, sign-in and materialize polling, and test gate thresholds. Read before changing the editor import path, its settings, or the tests that guard them.
status: accepted
---

# 28. The Unreal plugin editor module and its tests: decisions recorded from code

Date: 2026-11-21

## Status

Accepted. Recorded from the code comments that held these decisions, when the comments were removed; each decision was already in force in the code and none is new.

## Context

These decisions lived in comments in the Unreal plugin's editor module (`unreal/MantlePlace/Source/MantlePlaceEditor`), in its public headers and in its automation tests. Comments are being removed from the code, so the decisions and their reasons are recorded here.

## Decisions

### Bundle and manifest reading

**The importer's by-pointer zip reads rely on the engine zip reader matching entries case-insensitively.** UE 5.8 `ZipArchiveReader` hashes and compares keys ignoring case, which Python's `zipfile` does not do, so a bundle whose entries are in another case still reads.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceBundleLayoutTest.cpp

**Manifest versions are compared with the version helper, never with `FString` less-than.** Both are strings, and lexicographic comparison sorts the pre-history version 19 above 1.0.0, which turns a stale fixture's skip into a hard failure.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceImportLiveTest.cpp

**The conformance corpus's pinned manifest version must never fall below this parser's floor.** Hosts repin independently and the corpus may be newer, but a corpus below the floor makes every accept case a document the parser refuses, and many confusing assertions fail.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceImportManifestTest.cpp

**A retired terrain block that reappears in a bundle is ignored, not resurrected as a fallback.** Reading it would turn the clean break into dual-parsing, and a regression test guards this.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceImportManifestTest.cpp

**The mesh rotation maps mesh east to world +Y and mesh south to world -X, without mirroring.** Interchange lands glTF with East on +X and South on +Y. A determinant of +1 is what distinguishes this from the old mirrored workaround.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceImportManifestTest.cpp

### Textures and landscape

**A 16-bit coverage raster uses `TC_Grayscale` on a `TSF_G16` source and must not be simplified to `TC_HalfFloat`.** That pair is the only combination that reaches `PF_G16`, and the 10-bit mantissa of half float cannot represent uint16 values above 2048.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceCoverageRasterLogicTest.cpp

**Both 8-bit landings of an all-gray indexed raster are accepted, each with its own correct compression setting.** The same bytes may land as BGRA8 under Interchange or as G8 under the legacy factory.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceCoverageRasterLogicTest.cpp

**The landscape drape sets `LandscapeMaterial` by reflection, and a tripwire test checks the property name ahead of time.** `EditorSetLandscapeMaterial` is not exported from the Landscape module. Reflection is invisible to the compiler, so an engine rename would build cleanly and fail in the user's session.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceDrapeTripwireTest.cpp

**Landscape components are read through `ULandscapeInfo`, not `ALandscape::LandscapeComponents`.** In a World Partition world the components live on streaming proxies and the actor's own array is empty, so the latter silently measures nothing.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceImportLiveTest.cpp

### Settings

**Plugin settings are project-level `defaultconfig`, checked in, and never per-user. Name templates are deliberately not offered.** Developers with different content roots in one project would import one order to different places and never clean each other's copies.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Public/MantlePlaceEditorSettings.h

**Each setting has a separate, tested pure `Usable` rule that falls back to the shipped default.** `ClampMin` and `ClampMax` meta only constrain the Project Settings UI, so a value edited into `DefaultGame.ini` by hand, or carried from an older plugin version, reaches the code unchecked.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Public/MantlePlaceEditorSettings.h, unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceEditorSettingsTest.cpp

**An unusable generated content root falls back to the default instead of failing.** The importer creates and force-deletes a directory beneath that root, so a malformed root must never be resolved creatively.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Public/MantlePlaceEditorSettings.h

**Default foliage meshes are engine `BasicShapes` content.** Engine content is licence-clean in every project and carries no licence beyond the engine's.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Public/MantlePlaceEditorSettings.h

**The panel uses only the engine's own editor typefaces and ships no font files, and every font role must resolve to the engine default font.** The plugin then carries no third-party content, so Fab's Third Party Software form does not apply.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceEditorStyleFontsTest.cpp

### Import identity, naming and wipe safety

**Unusable import identities are refused, not sanitised.** Each identity becomes a path segment of a force-deleted directory, so rewriting it would invent an identity and could silently redirect the delete.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceImportNamingTest.cpp

**The import tag carries the full identity, not the eight-character truncation.** Re-import matches on the tag, and a truncated match could destroy the actors of a colliding order.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceImportNamingTest.cpp

**Every import subfolder must lie strictly beneath the import root.** The re-import wipe force-deletes only the root, so an escaping subfolder would be silently orphaned.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceImportNamingTest.cpp

**Single-actor labels must be distinct, and none may be a prefix of another.** The stale-actor sweep compares some labels by equality and one by `StartsWith`, and would otherwise delete the wrong actor.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceImportNamingTest.cpp

**Callers run a provenance check before deleting anything and do not trust the short identity form.** Identities that share their first eight characters produce the same label and folder.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceImportNamingTest.cpp

**The outliner folder's top segment is fixed and does not follow the configurable content root.** A folder-borne marker would otherwise vanish for exactly the studios that change the root.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceImportNamingTest.cpp

**Asset-name derivation treats forward-slash and backslash paths identically.** Bundles are produced on Linux and imported on Windows, so both separators reach it.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceImportNamingTest.cpp

**The identity is left out of leaf asset names.** The folder already carries it, and repeating it made material instance names unreadable in the material picker.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceImportNamingTest.cpp

**The import is one undoable transaction, so assets are not renamed inside it.** `FAssetRenameManager` deletes the old object, then `ObjectTools` resets the whole undo buffer and the half-built transaction goes with it, which makes Ctrl+Z do nothing.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceImportLiveTest.cpp

### Test gates and thresholds

**Live-test skips are errors under CI (keyed on the `CI` variable) and warnings locally.** A warn-and-pass skip looks identical to a gate that ran and passed, and an orientation gate once shipped unable to run on CI without anyone noticing.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceImportLiveTest.cpp

**The placement gate's mean-offset limit is tight, at 10 cm.** A datum, post or flip error shifts the mean while terrain noise averages out, and real DEM cells agreed within 1.5 cm.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceLiveGateLogic.h

**The placement gate's RMS limit is loose, at 100 cm, and there is no per-point bound.** `SurfacePoints.csv` is a decimated set that does not match DEM cells, so single points can sit metres off on steep cells, and a per-point bound would fail correct imports.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceLiveGateLogic.h

**The reimport save test holds a strong reference to the old saved mesh on purpose.** This reproduces the live editor, where a counted reference blocks force-delete. Without it, garbage collection hides the defect.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceReimportSaveTest.cpp

**Test levels mount under the plugin's own `/MantlePlace/` root, with three nested mount points.** World Partition files actors under the first path segment, and `/Temp` is neither scanned nor readable for descriptors.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceTestMount.h

**Roof points that lie under the ground are judged against the drawn ground from Landscape height data, never against the collision under test.** Buildings on slopes are levelled to their lowest ground, so ground can lie over a roof.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceRoofTraceLogic.h

**Terrain collision may sit 2 cm off the drawn ground.** That is the same slack every hit is held to, about seven times the larger offset measured on a 3.0 bundle, and far tighter than the old relief-based 99.7 cm bound.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceRoofTraceLogic.h

**At most 1 percent of roof points may lie under the drawn ground.** That is about six times the measured 0.15 percent, and a bundle past it has buildings set into the ground wholesale.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceRoofTraceLogic.h

### Import timing record

**The import timing record carries no threshold, target or expected column.** The plugin measures and reports only, and judging what a good timeline is belongs to the consumer of the file.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceImportTimingTest.cpp

**The timing record emitter is opt-in and writes nothing without a configured directory.** Scattering JSON files into a curator's project by default would be an unwanted surprise.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceImportTimingTest.cpp

### Local tile server and terrain streaming

**The local tile server refuses any path containing two dots, even for legitimate file names.** The rule is deliberately crude so that a traversal cannot pass through a mistake in the path arithmetic.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceLocalTileServerLogicTest.cpp

**Terrain tiles are served with a gzip content-encoding declared and are never re-compressed.** The tile writer stores them gzipped on disk.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceLocalTileServerLogicTest.cpp

**Cesium streaming is download-to-own.** A loopback server (127.0.0.1) serves only the user's local bundle copy, nothing streams from the platform, and it complements rather than replaces native-asset import.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Public/MantlePlaceImporterLibrary.h

**The staging record is a sibling of the staging directory, never inside it.** The staging directory is served over HTTP, so a bookkeeping file inside it would be servable content.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceStreamStagingTest.cpp

**Staging directories use the full identity, unlike the content tree, so eight-character collisions do not share one.** A collision would make the availability rewrite serve wrong tiles.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceStreamStagingTest.cpp

### Credit lines

**Credit lines stored as actor tags are split into numbered parts when long, and rejoined whole on read.** An `FName` holds at most 1023 characters and asserts beyond that.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceOdblLogicTest.cpp

### Tree points

**Tree-points CSV columns are read by header name.** The manifest publishes names, not order. Position keying would put a height into a crown radius, and exact-header keying would drop the layer.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceTreePointsLogicTest.cpp

**A drifted column header is its own outcome, distinct from a count mismatch.** The importer skips the layer for one and fails the import for the other, and conflating them misreports bundles.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceTreePointsLogicTest.cpp

**A tree-points file's frame belongs to the file, so one row off the landscape refuses the whole layer instead of dropping that row.** Keeping the rest would place a file with contrary evidence.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceTreePointsLogicTest.cpp

**The stated CRS is compared byte-for-byte with the georeference string.** Deciding that two spellings name one CRS is a derivation, and the producer writes both from the same string.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceTreePointsLogicTest.cpp

**An empty `ground_z` means the DEM had no data, and rows carry an explicit flag for it.** A Z of zero would read as sea level and pass off as a measured elevation.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Private/Tests/MantlePlaceTreePointsLogicTest.cpp

### Sign-in and the vault import orchestrator

**The editor owns one auth session, in an editor subsystem, not in the vault panel widget.** Per-tab sessions each held their own access token, wrote the same refresh token file and left sign-out inconsistent.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Public/MantlePlaceAuthSubsystem.h

**Session restore from the stored refresh token runs when the vault surface opens, not at editor startup.** An editor the user never points at the vault then makes no auth request at all.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Public/MantlePlaceVaultImportOrchestrator.h

**`OnImportStepsNative` fires before any import work.** A complete local zip imports inside `StartLocalImport` itself, so a window opened afterwards would open on an already finished run.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Public/MantlePlaceVaultImportOrchestrator.h

**Tagged-widget lookup validates a live widget path and reports tags on dormant switcher pages as not found.** A stale but plausible rectangle from cached paint geometry costs tooling the most downstream.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Public/MantlePlaceAutomationLibrary.h

### Materialize polling

**Materialize polling keeps the requested token set, and completion means those tokens are delivered and no job is in flight.** The status endpoint returns a delivery-state document with no status word.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Public/MantlePlaceVaultImportOrchestrator.h

**A flag records whether a materialize run was observed in flight.** "No job and something outstanding" means both "row not visible yet" and "run ended without building this", and only the first is fixed by polling.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Public/MantlePlaceVaultImportOrchestrator.h

**Materialize re-picks are bounded at one.** The platform answers 409 once a run is running and expects a re-pick on completion, but a second run that leaves the same tokens outstanding means it cannot build them, and looping must stop.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Public/MantlePlaceVaultImportOrchestrator.h

**Only one cloud materialize runs per import, and the check comes before recovering a downloaded-but-incomplete bundle.** A zip the platform cannot complete then fails on the importer's manifest gate instead of looping generate, download, generate.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Public/MantlePlaceVaultImportOrchestrator.h

**The first materialize start of a vault-row import is read as a route.** A complete order answers no-op and downloads Unreal's view. An on-demand order answers with a job and Prepare follows. Later starts are Prepare's.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Public/MantlePlaceVaultImportOrchestrator.h

**View delivery knowledge is true after a no-op start and false after a 503 `delivery_state_unknown`.** That value decides whether a whole-archive answer to a view request is importable.
Source: unreal/MantlePlace/Source/MantlePlaceEditor/Public/MantlePlaceVaultImportOrchestrator.h
