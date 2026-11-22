---
name: adr-0026-unreal-editor-importer-decisions-from-code
description: Decisions recorded from code comments in the Unreal editor importer (MantlePlaceEditor Private sources) covering artifact failure policy, handler order, undo, coverage textures, drape, Data Layers, foliage scatter, HLOD, manifest and axis handling, import identity and the re-import wipe. Read before changing the importer, its naming, its wipe or its texture settings.
status: accepted
---

# 26. The Unreal editor importer: decisions recorded from code

Date: 2026-11-21

## Status

Accepted. Recorded from the code comments that held these decisions, when the comments were removed; each decision was already in force in the code and none is new.

## Context

These decisions lived in comments in the Unreal editor importer, the private sources of the `MantlePlaceEditor` module under `unreal/MantlePlace/Source/MantlePlaceEditor/Private/`. Comments are being removed from the code, so the decisions and their reasons are recorded here. Related records: [ADR 0002](0002-import-identity.md), [ADR 0003](0003-naming-authority-and-mp-prefix.md) and [ADR 0006](0006-unreal-declines-elevation-bounds-target-crs.md).

## Decisions

### Artifact failure policy and handler seam

**Building massing imports whenever it is present, regardless of Mode, and a building failure is logged rather than failing the import.** The terrain is the primary deliverable. A sha256 mismatch still fails closed in the integrity pre-check.
Source: MantlePlaceArtifactBuildings.cpp

**Coverage rasters warn and skip per raster, unlike the fail-closed weights path.** A missing analysis channel costs a texture, not a wrong Landscape. Byte integrity stays fail-closed.
Source: MantlePlaceArtifactCoverageRasters.cpp

**An import selection is a set of excluded artifacts, not an enum or a flag list.** New artifacts are then included by default and a selection UI writes checkboxes into the set. Only terrain alternatives are excluded by Mode, and foliage scatter is opt-in.
Source: MantlePlaceArtifactHandler.h

**Artifact handlers are a thin adapter seam over the existing free-function implementations, not a framework.** There is no discovery or reflection. The registry is an explicit list in import order, and decisions stay in separate Logic units.
Source: MantlePlaceArtifactHandler.h

**The handler order is a contract.** Handlers share state through the import context by running order: the drape handler builds its material before the landscape adopts it and leaves it for later geometry handlers, each geometry handler leaves its actor for the drape's coverage report, and the log reads in this order.
Source: MantlePlaceArtifactHandler.h

**Every imported actor is labelled, tagged and filed through the single Claim function.** A new actor type then cannot forget the import tag that the stale-actor sweep of a re-import matches on.
Source: MantlePlaceArtifactHandler.h

**Mesh readiness is read only from `hosts.unreal.readiness`; the retired top-level `dcc_readiness` is not consulted.** A fallback would quietly turn a clean break into dual parsing, and the version gate already refuses such bundles.
Source: MantlePlaceArtifactTerrainMesh.cpp

**In Both mode the terrain mesh over a Landscape built by this import is an overlay with collision off, so bodies rest on the Landscape.** This relies on the Landscape handler running first. If the Landscape fails, the mesh remains as the ground.
Source: MantlePlaceArtifactTerrainMesh.cpp

### Foliage tree points and scatter

**A tree-point count that disagrees with the payload fails the import instead of skipping the layer.** The parsed rows would be a silent subset, and the scatter would read as a sparse forest rather than an error.
Source: MantlePlaceArtifactFoliagePoints.cpp

**A drifted column contract or a file in the wrong frame is a named skip of the tree-points layer, not a failure.** Terrain, imagery and buildings are then not reported failed, and nothing scatters a forest far off site.
Source: MantlePlaceArtifactFoliagePoints.cpp

**The PCG foliage graph is authored in code at import, not shipped as a binary asset.** A diff reviews it whole, and each order's graph carries that order's own table and rasters.
Source: MantlePlaceFoliageScatter.h

**The foliage volume is spawned without a graph and claimed before Generate, and the scatter volume is claimed before its graph is assigned.** A partitioned component creates partition actors as soon as it has a graph, and each takes the volume's Data Layers as they are at that moment.
Source: MantlePlaceArtifactFoliageScatter.cpp, MantlePlaceFoliageScatter.h

**Generated PCG instances are cleaned up immediately, not deferred.** The volume is destroyed next, and deferred cleanup would run on a missing component and leave instances on the partition actors.
Source: MantlePlaceFoliageScatter.cpp

**PCG partition actors are cleaned of generated content first and destroyed only once empty.** They are shared by every partitioned component on the same cell.
Source: MantlePlaceFoliageScatter.h

### Undo transactions

**RF_Transactional is set on the foliage data table after RowStruct, not passed to NewObject.** A transactional object built inside an open transaction is serialized with no RowStruct, which makes UDataTable::Serialize log an error.
Source: MantlePlaceArtifactFoliagePoints.cpp

**Prepare work runs before the import's undo transaction opens.** A glTF import renames what it brings, and a rename inside the transaction takes the whole import off the undo stack.
Source: MantlePlaceArtifactHandler.h

**Imported assets (textures, the imagery texture and the rest) are named on the way in through DestinationName and never renamed afterwards.** A rename inside the import's transaction makes ObjectTools reset the whole undo buffer, which leaves Ctrl+Z doing nothing for the level's actors.
Source: MantlePlaceCoverageRasters.cpp, MantlePlaceDrape.cpp, MantlePlaceImportNaming.h

### Coverage raster textures

**Coverage raster texture settings are applied after the import, never before, and are keyed off the format the engine landed, not the manifest's encoding.** UTextureFactory overwrites its own compression settings from the loader's suggestion as it starts, so a factory configured up front is silently ignored. The landed format and the manifest can disagree (a 16-bit PNG with a tRNS chunk decodes as RGBA), and a contradiction is refused. TC_Grayscale is chosen over the lossy TC_HalfFloat.
Source: MantlePlaceCoverageRasters.cpp, MantlePlaceCoverageRasterLogic.h, MantlePlaceCoverageRasterLogic.cpp

**Coverage raster textures are never sRGB.** The importer force-enables sRGB for 16-bit sources on the assumption of colour, which puts a gamma curve between sample and value and picks a Grayscale sampler on a data raster.
Source: MantlePlaceCoverageRasterLogic.cpp

**Coverage raster textures use no mipmaps.** Averaging neighbouring samples invents class codes absent from the legend and blends real values with the nodata sentinel.
Source: MantlePlaceCoverageRasterLogic.cpp

**Coverage rasters never use virtual texture streaming.** AutoVTSize would virtualise only the imagery-grid ndvi raster, and one of seven textures would need a different sampler node in materials.
Source: MantlePlaceCoverageRasterLogic.cpp

**Categorical and mask rasters are point-sampled, and continuous channels keep the default filter.** Interpolating between class codes produces values the source never claimed.
Source: MantlePlaceCoverageRasterLogic.cpp

**A 16-bit raster must land as TSF_G16 with TC_Grayscale, the only combination that reaches PF_G16 with all 16 bits.** Any other landed format means the PNG was not 16-bit grayscale, likely because a tRNS chunk forced RGBA, and it would silently yield G8.
Source: MantlePlaceCoverageRasterLogic.cpp

**The three 8-bit encodings may land in different formats depending on the import path, so compression settings follow the landed format.** The legacy factory collapses gray RGBA to G8, and Interchange leaves BGRA8.
Source: MantlePlaceCoverageRasterLogic.cpp

**An 8-bit gray raster uses PF_G8 uncompressed with sRGB false.** The engine promotes an sRGB G8 to BGRA8 and then de-gammas the sample in the shader.
Source: MantlePlaceCoverageRasterLogic.cpp

**Indexed rasters use TC_VectorDisplacementmap, the uncompressed BGRA8 path, and the code is recovered as the round of the red sample times 255.** That path has no block compression to smear class codes and no default sRGB.
Source: MantlePlaceCoverageRasterLogic.cpp

**Coverage raster orientation is stated explicitly as north-up row-major.** UTextureFactory keeps the PNG's U=East, V=South order, unlike the transposed heightmap, so a consumer sampling beside the Landscape must swizzle.
Source: MantlePlaceCoverageRasters.cpp

### Data Layers

**Data Layers are per family and shared across all imports in the project, not per order.** Imported areas then toggle together, and a wipe never deletes a layer another import uses. The layer assets live in a Layers folder beside the order folders, outside any order's force-deleted folder.
Source: MantlePlaceDataLayerLogic.h, MantlePlaceImportNaming.h

**New Runtime Data Layer instances are set to Activated initially.** A Runtime layer defaults to Unloaded, which would make the import vanish from every game run.
Source: MantlePlaceDataLayers.cpp

### Drape and landscape material

**LandscapeQuadsXY counts are swapped relative to the manifest's grid axes.** After the transpose, the landscape's local X indexes heightmap rows (north), while the manifest's ComponentCountX is the easting axis.
Source: MantlePlaceDrape.cpp

**The landscape material is assigned by inlining the full Pre/PostEditChange cycle on the named property, and assignment fails closed, changing nothing, when reflection cannot find the property.** EditorSetLandscapeMaterial is not exported, and freshly created landscapes need the material before Import. An unassigned drape is a visible, reportable miss, which is better than a wrong or silent assignment.
Source: MantlePlaceDrape.cpp, MantlePlaceDrape.h

**An automation tripwire test asserts that the landscape material property resolves by reflection.** Reflection makes an engine rename invisible to the compiler, so a rename turns the suite red instead of crashing a user.
Source: MantlePlaceDrape.h

**The drape material applies scale and offset in landscape-grid order first, then swizzles and flips into imagery order last.** The imagery is north-up with V growing southward, scale and offset cannot express the swizzle and the one-minus flip, and flipping earlier would be wrong by one minus scale minus twice offset.
Source: MantlePlaceImportManifest.cpp, MantlePlaceImportManifest.h

**Alignment shapes are matched by prefix up to the first word only.** The ETL may re-punctuate the free-string text, and matching further would turn a typographic edit into a behaviour change.
Source: MantlePlaceDrapeAlignmentLogic.cpp

**Measured numbers in the alignment text are read relative to an anchor word, not by position.** Rewording the divergent sentence then reads as absent and falls back to the verbatim string, instead of silently swapping coverage and overshoot.
Source: MantlePlaceDrapeAlignmentLogic.cpp

**The extent slack is one 1% constant shared by the alignment refusal and the coverage warning.** They then cannot drift apart. The reference bundle's imagery is about 0.1% short and truthfully declares that it matches.
Source: MantlePlaceDrapeAlignmentLogic.h

**The importer refuses only a bundle that claims imagery matches the heightmap while its own rectangles disagree beyond the slack; every other claim is believed and merely reported.** That case imports looking correct but is silently mis-draped.
Source: MantlePlaceDrapeAlignmentLogic.h

### Editor UI

**The plugin ships no font files and draws with the engine's own editor typefaces.** A bundled third-party font would need to sit under Source/ThirdParty and be declared on Fab's Third Party Software form.
Source: MantlePlaceEditorStyle.cpp

**The vault list view style is set to FSlateNoResource.** STableViewBase::OnPaint fills the whole list rect with the engine's opaque default ListView background, which transparent row brushes cannot remove.
Source: MantlePlaceEditorStyle.cpp

**Pill buttons use the no-radius rounded-box constructor (half-height radius).** A huge fixed radius is degenerate in the shader, collapsing coverage so neither fill nor outline draws and buttons render as bare text.
Source: MantlePlaceEditorStyle.cpp

**Button vertical padding is one fixed constant off the margin scale.** Slate has no height primitive, and pill height equals label height plus twice the padding, so the padding must stay uniform across every button.
Source: MantlePlaceEditorStyle.h

**The auth subsystem cancels a pending sign-in explicitly on shutdown rather than leaving it to BeginDestroy.** A waiting sign-in holds a bound loopback route and a timeout ticker, and garbage collection may run much later.
Source: MantlePlaceAuthSubsystem.cpp

**Widget geometry for automation is taken from FindPathToWidget, which arranges from the window down.** Widgets on a dormant SWidgetSwitcher page, whose cached paint geometry is stale but nonzero, then correctly count as unreachable.
Source: MantlePlaceAutomationLibrary.cpp

### HLOD

**HLOD builds are prompted and never run by the import.** The engine build omits actors whose packages are unsaved and the importer saves nothing, and a build measured six and a half minutes against a 25 s import.
Source: MantlePlaceHLODLogic.h

**Both terrain and building HLOD layers use the simplified-mesh type.** A bundle's terrain and buildings are a few large unique meshes that instancing and merging do not reduce.
Source: MantlePlaceHLODLogic.h

**The HLOD layer is registered in the main partition's first HLOD setup.** Since UE 5.8 a layer counts only when a runtime partition's HLOD setup lists it, otherwise an actor is built into no HLOD at all.
Source: MantlePlaceHLODs.cpp

### Manifest, axes and frame

**The projected-to-UE axis mapping has one home: North to +X and East to +Y.** Unreal's world is left-handed, and East to +X is a reflection that mirrors the scene and looks like a 90 degree rotation.
Source: MantlePlaceImportManifest.cpp

**AOI spans take the manifest's y values for UE X, through one accessor.** The manifest's x axis names easting and y northing, while UE X is North, and a half-done swap hides as an aspect-ratio error on anisotropic scales.
Source: MantlePlaceImportManifest.cpp

**glTF-sourced meshes get a plus 90 degree yaw rather than mirroring.** Interchange lands them East on +X and South on +Y, measured against the heightmap, and the mapping to world is a pure rotation.
Source: MantlePlaceImportManifest.h

**The level offset is horizontal only.** Every height this host places is already absolute, so a vertical offset would count the elevation twice.
Source: MantlePlaceImportManifest.cpp

**EPSG is parsed but unused by design.** The contract places the AOI in a flat frame at world origin and deliberately instantiates no AGeoReferencingSystem or large-world coordinates in v1.
Source: MantlePlaceImportManifest.cpp

**The vault order id is the join key that maps a downloaded zip back to its order.** The per-rebuild ETL job id is deliberately not used because it changes on every rebuild.
Source: MantlePlaceImportManifest.cpp

**The manifest version is read as a string and parsed as semver, never coerced through a number.** An absent, integer or partial version is then refused by decision, not by an accident of falling to zero.
Source: MantlePlaceImportManifest.cpp

**Any minor of a supported major reads, and an unknown higher major is refused gracefully, naming the version.** A major signals changed meaning, and the message must distinguish too new from too old.
Source: MantlePlaceImportManifest.cpp

**A missing `hosts.unreal` block does not mean unmaterialized.** The web side can materialize for another host only, so neutral signals decide. Either way the import is refused, while the top-level Cesium fields stay parsed.
Source: MantlePlaceImportManifest.cpp

**Manifest string members are read only when they are JSON strings.** The engine's TryGetStringField also converts numbers into strings and would let a mistyped manifest value pass.
Source: MantlePlaceBundleReaderLogic.cpp

### Naming and import identity

**Every generated name and package path is built in the one import-naming unit.** The naming standard drifts when spelled at call sites, and nothing in CI builds the plugin, so only a script gate checks it.
Source: MantlePlaceImportNaming.h

**Asset prefixes are tested most-derived first.** MaterialInstanceConstant is a UMaterialInterface, and testing the base class first would name every instance with the M_ prefix.
Source: MantlePlaceImportNaming.cpp

**The layer info asset is prefixed, but the paint layer's name inside it stays the platform's material name verbatim.** Landscape material LandscapeLayerBlend nodes bind to that name.
Source: MantlePlaceImportNaming.h

**An unreachable enumerator returns a named subfolder, not empty.** A forgotten enumerator then cannot resolve to the import root itself, where the re-import wipe would reach the whole import.
Source: MantlePlaceImportNaming.cpp

**Import identity is the order id, else a hash of the manifest, never the job id.** Keying on a rebuild-varying job created duplicate folders and landscapes while the wipe looked at an unused path.
Source: MantlePlaceImportNaming.h

**Identities with characters outside letters, digits and three separators, and identities that are empty or contain path tricks, are refused rather than stripped or sanitised.** The identity is a package-path segment of a force-deleted directory. Stripping would invent an identity nobody chose, and an empty identity would make the delete reach every import.
Source: MantlePlaceImportNaming.cpp, MantlePlaceImportNaming.h

**Disambiguation of the eight-character short identity by the full identity is deliberately not built.** Collisions are detected by the provenance record and refused, nothing else keyed on the short identity loses data, and no collision has occurred.
Source: MantlePlaceImportNaming.h

**Re-import matches the import actor tag carrying the full identity, not the user-editable label.** Renaming an actor used to silently break re-import and stack duplicate landscapes. Absence of the tag also marks legacy content.
Source: MantlePlaceImportNaming.h

**Identity comparison is case-sensitive.** Order ids and hex digests are case-significant as delivered, and treating identities that differ only in case as equal would invent an equivalence nobody stated.
Source: MantlePlaceImportProvenance.cpp

**Credit lines are written through CreditTags, never as one FName per line.** A line longer than FName's length limit triggers an assertion.
Source: MantlePlaceImportOrchestration.cpp

### Re-import wipe and integrity

**A content root must have a mount point and at least one segment beneath it, with no relative segments.** The importer force-deletes beneath it, and an empty or bare slash root would reach everything.
Source: MantlePlaceImportNaming.h

**The prior-asset wipe is guarded by a provenance record read from outside the content tree, kept in Saved data and not as an asset in the content folder.** Two identities sharing eight characters name the same folder, and the force-delete would destroy another order's content. Loading an asset that is about to be deleted purges the undo buffer.
Source: MantlePlaceImportOrchestration.cpp, MantlePlaceImportProvenance.h

**The nothing-to-import refusal is decided before the destructive wipe.** Otherwise a prior import's still-referenced assets would be force-deleted to produce nothing.
Source: MantlePlaceImportOrchestration.h

**The editor selection is cleared before the force-delete.** Deleting a selected or referenced asset drives pivot updates over a stale typed-element selection and trips an "Element type ID 0 not registered" ensure.
Source: MantlePlaceImportOrchestration.cpp

**Objects the force-delete could not free are moved to the transient package.** Otherwise the glTF import reuses them with cleared public flags, and the level can no longer save the actor referencing them.
Source: MantlePlaceImportOrchestration.cpp

**Actor labels are read before destruction.** Destroying a landscape also destroys its streaming proxies, which would make the labels unreadable afterwards.
Source: MantlePlaceHandover.cpp

**Landscape streaming proxies are destroyed before their parent landscape.** Destroying the parent first leaves the proxies registered to a landscape info with no owner.
Source: MantlePlaceImportOrchestration.cpp

**Imported actors are kept always loaded so loaded-actor walks see them, but landscapes and HLOD families stream instead.** An always-loaded actor is never in an HLOD, and re-import loads unloaded actors before its wipe.
Source: MantlePlaceImportOrchestration.cpp

**Emptied PCG partition actors are kept for the re-import to generate on.** In a saved World Partition level a destroyed actor's cell stays recorded until save, and the subsystem will not generate on it.
Source: MantlePlaceImportOrchestration.h

**A payload with no published digest is skipped rather than passed, and a payload the archive was cut without is skipped too.** Valid-but-unverified is a third state and not corruption. Verification walks each handler's declared list.
Source: MantlePlaceImportOrchestration.h

### Import timing

**The integrity timing's read row is read-and-inflate.** TryReadFile is a disk read plus zlib inflate, so a large number there is not necessarily I/O. Read and hash totals are one type, recorded together in run order.
Source: MantlePlaceImportOrchestration.cpp

**Timing rows are recorded by an explicit call, not by destructors.** Destructors fire in reverse declaration order and would print the hashing row above the reading row that fed it.
Source: MantlePlaceImportOrchestration.cpp

**The running timeline is a file-static, not a parameter.** Other translation units can then report into it without timing arguments on unrelated signatures. It is safe because imports are synchronous, and the scope guard restores the outer timeline.
Source: MantlePlaceImportTiming.cpp
