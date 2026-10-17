---
name: adr-0016-unreal-mesh-terrain-is-parked-until-5-9
description: The Unreal host does not import to UE 5.8's Experimental Mesh Terrain (Mesh Partition, "MegaMesh"); the Landscape stays its only terrain target, and the question is reopened at UE 5.9 against stated re-entry tests. Holds the spike's measured evidence (module linkage, placement fidelity, worst-case cost, Convert-Mesh, weight channels) and the data-contract request a later implementation needs. Read before adding a Mesh Partition dependency, before reaching for FHeightmapImporter, and before reopening Mesh Terrain at 5.9.
status: accepted
---

# 16. Unreal Mesh Terrain is parked until UE 5.9

Date: 2026-10-17

## Status

Accepted. A no-go for UE 5.8, to be re-evaluated at UE 5.9 against the re-entry tests below.

## Context

UE 5.8 ships **Mesh Terrain**: a Mesh Partition actor (a "MegaMesh") built from base sections that
non-destructive modifiers edit, compiled to Nanite static meshes, partitioned along a grid, and
painted through named **weight channels** declared on a Mesh Partition definition. It supports
overhangs, caves and non-uniform resolution, which a Landscape cannot. It is **Experimental**: the
four plugins that enable it (MeshPartition, MeshPartitionWater, PCG MeshPartition Interop, Mesh
Terrain Mode) carry the engine's Experimental flag.

A timeboxed spike asked whether this host could offer it as an opt-in terrain target beside the
Landscape. It ran on UE 5.8.0 from a scratch editor module in a World Partition level, against a
production bundle's heightmap (2017 samples a side, about 2 km square) and a synthetic heightmap at
the platform's worst case (8129 samples a side). The harness, and how to re-run it, is in
[`unreal/tools/mesh-terrain-spike/`](../../unreal/tools/mesh-terrain-spike/README.md).

### What the spike found

**Module linkage: it links, but only past an engine wall.** `FHeightmapImporter`,
`UMeshPartitionEditorComponent::SpawnBaseModifier`, `UMeshProviderModifier` and
`AMeshPartition::SetMeshPartitionDefinition` are exported from `MeshPartitionEditor` and
`MeshPartition`. But Mesh Partition's own public headers include an engine **Internal** header
(`MaterialCache/MaterialCacheVirtualTexture.h`), which the build tool hides from every module outside
the engine. A plugin compiles against Mesh Partition only by adding the engine's `Internal` directory
to its include path by hand, against the engine's stated boundary. Past that, two settings an import
needs are private and writable only through reflection: the definition's channel list
(`ChannelMap.ChannelDescs`) and its material. The channel bake
(`FChannelTextureRenderer::GenerateSectionMeshChannels`) is private too, and runs only inside the
engine's own builds.

**Placement and scale: exact, but not through `FHeightmapImporter`.** The importer reads PNG only,
8- or 16-bit grey (the bundle's is 16-bit), spawns the MegaMesh at the origin, centres the mesh there, and maps a
sample `u` to `u / 65535` of the mesh's height, which is exactly the bundle's published
`uint16_mapping`. Its frame is the glb's: columns run east and rows run south, so the same yaw +90
the mesh path already applies puts North on +X and East on +Y, and sections follow the MegaMesh
actor when it is moved. The AOI's extents then match the Landscape's to the centimetre. The heights
do not: the importer samples the heightmap **cell-centred** with bilinear interpolation, so each
vertex takes its height from up to half a sample away, and it extrapolates at the edges. On the
production bundle that is a 3.4 cm RMS and 160 cm worst-case height error against the Landscape the
same bundle builds. Building each section's grid in the plugin instead, with a vertex on every
sample, and handing it to `SpawnBaseModifier`, matched the Landscape surface with **zero** error.

**Worst-case cost.**

| Case | Step | Time | Editor memory added |
| --- | --- | --- | --- |
| Production bundle, 2017², 16×16 sections | `FHeightmapImporter` | 1.9 s | 0.9 GB |
| Production bundle, 2017², 16×16 sections | Plugin-built grid | 7.0 s | 1.0 GB |
| Production bundle, 2017², 16×16 sections | Preview build (Nanite) | 3.1–3.8 s | 5.2–5.7 GB in all |
| Worst case, 8129², 64×64 sections | `FHeightmapImporter` | 26 s | 18 GB |
| Worst case, 8129², 64×64 sections | Plugin-built grid, zero height error | 105 s | 17 GB |
| Worst case, 8129², 64×64 sections | Preview build (Nanite) | Unfinished at 31 min, stopped | Peaked at 56 GB in all |

The worst-case preview build never returned: the editor stayed inside one frame for the whole
31-minute timebox, unresponsive, and was stopped. The plugin-built grid ran on one thread in the
harness, where the importer builds sections in parallel, so its time is an upper bound. The importer
caps a MegaMesh at 4096 sections, which the worst case reaches at 127 quads a section. Its only
memory-bounded mode **saves and unloads** each batch of sections as it goes; this plugin never saves
what it imports, so without that mode the whole worst-case mesh stays in memory.

**Convert-Mesh: viable programmatically, and not needed.** The editor's Convert tool is a modelling
tool, but its accept step is thirty lines over exported calls: copy the static mesh to a dynamic
mesh, recentre it, `SpawnBaseModifier`, move the MegaMesh to the actor's transform. Replayed on a
production `Terrain.glb` (one mesh of 501,035 vertices and about a million triangles), the
conversion took 4 s and the MegaMesh's bounds matched the static mesh's exactly; its preview built in
3.1 s, and then an engine ensure fired inside Mesh Partition's static-mesh transformer. It adds
nothing the heightmap path lacks for terrain, and `Buildings.glb` is not terrain: buildings stay static meshes.

**Weight channels: per-vertex data is programmatic; the declaration is not public.**

| Piece | Programmatic? | How |
| --- | --- | --- |
| Per-vertex weights for `material_weights`' four bands | Yes | Named weight layers on each section's dynamic mesh, one per published material name; the planes the Landscape path already resamples map one-to-one onto a plugin-built grid's vertices |
| Declaring the channels on the definition | Only by reflection | `ChannelMap.ChannelDescs` is private and has no setter; the Mesh Terrain editor mode is the supported way |
| Assigning the definition's material | Only by reflection | `Material` is private on the definition |
| A Mesh Terrain material sampling the channels | Yes | Mesh Partition Resource and Channel Sample expressions, created and wired in code; the spike's material compiled with no reported errors |
| The drape | Needs a new material | The shipped drape material reads `LandscapeLayerCoords`, which exists only on a Landscape; a MegaMesh section carries the AOI's 0-to-1 UV instead, so the drape needs a parallel material reading that |
| Baking channels to the section textures | Engine-internal | Runs inside the preview and compile builds; the bake call is private |

## Decision

**The Unreal host does not import to Mesh Terrain on UE 5.8. The Landscape stays its only terrain
target, and Mesh Terrain is re-evaluated at UE 5.9.** The plugin takes no dependency on any Mesh
Partition module and claims no Mesh Terrain support anywhere.

The weight of the decision is the linkage, not the cost. Every Mesh Terrain import would rest on an
include path the engine closes to plugins, two private properties written through reflection, and a
hard dependency that makes every consuming project enable an Experimental plugin, under APIs the
engine says will change. The measured costs and fidelity are acceptable on the right path, so they do
not decide it.

**Reopen at 5.9 when all of these hold**, each checked by re-running the spike harness on 5.9:

- Mesh Partition's public headers compile in a plugin module **without** the `Internal` include path.
- A public way to declare a definition's channels and set its material, without reflection.
- Mesh Partition is no longer Experimental, or the founder accepts an Experimental dependency for an
  opt-in target, with an engine-version pin and a stated churn expectation.

## Considered options

- **Go now, against 5.8, behind an opt-in.** Rejected for the reasons above: the opt-in would hide
  the target from users, not the dependency from the build.
- **Go now, through `FHeightmapImporter`.** Rejected on fidelity alone: half a sample of horizontal
  error in every height is a placement error, and this host applies placement verbatim.
- **A separate optional module or companion plugin holding the Mesh Partition dependency**, so a
  project without the Experimental plugins still builds. The right shape for a later go, and the one a
  5.9 implementation should take; it does not remove the `Internal` include path or the reflection.

## Consequences

- **When Mesh Terrain is implemented, it builds the grid itself.** One section per published tile,
  a vertex on every heightmap sample, handed to `SpawnBaseModifier`; never `FHeightmapImporter`. Its
  orientation is the mesh path's yaw +90, and the live orientation gate is adapted to read a MegaMesh.
- **It imports unsaved**, as everything here does, so the worst case costs about 18 GB of editor
  memory before the preview build, and on 5.8 that build did not finish. A 5.9 implementation
  re-measures both and states the cost, or finds a bounded path.
- **Data-contract request, for the platform.** A Mesh Terrain import needs three values the bundle
  does not publish as such, and the plugin will not work them out:
  - **The section grid**: how many sections a side. The importer requires it and caps it at 4096 in
    all; it is a tiling decision, as the Landscape's published component layout already is.
  - **The MegaMesh transform**, in UE units, published the way `landscape_transform` is: the east
    and north spans, the vertical range (the full `uint16` span in centimetres) and the actor height.
    Today they could only be computed from `landscape_transform` and `uint16_mapping`.
  - **The section build settings**, the Nanite and level-of-detail hints a Mesh Partition definition
    carries; the spike used the engine's defaults.
- **No release note mentions Mesh Terrain**, because nothing ships.
