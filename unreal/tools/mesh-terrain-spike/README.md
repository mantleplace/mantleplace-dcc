# Mesh Terrain spike harness

The evidence behind [ADR 0016](../../../docs/adr/0016-unreal-mesh-terrain-is-parked-until-5-9.md),
kept so the question can be re-asked at UE 5.9 by re-running it. It is **not part of the plugin**:
nothing here is compiled by `MantlePlace.uplugin`, and the plugin takes no Mesh Partition dependency.
Only the source is kept. The project files are written out below rather than committed, so no
`.uproject` or `.Build.cs` sits inside a consuming project's plugin scan.

## What it measures

One automation test, `Spike.MeshTerrainSpike.Run`, in a World Partition level, chosen by
`SPIKE_MODE`:

| `SPIKE_MODE` | What it does |
| --- | --- |
| `importer` | Imports a 16-bit PNG through `FHeightmapImporter`, moves the MegaMesh to the Landscape path's frame (yaw +90, AOI centred on the origin, base at `z_min`) and measures every seventh vertex against the surface the Landscape builds from the same heightmap |
| `grid` | Builds each section's grid itself, a vertex on every sample, through `SpawnBaseModifier`, and measures the same way; with `SPIKE_CHANNELS=1` it also adds four weight layers, declares four channels on a definition, and compiles a material that samples them |
| `convert` | Imports a glb through Interchange and replays the Convert tool's accept step on its static meshes, comparing bounds before and after |

`SPIKE_PREVIEW=1` then runs the Nanite preview build and times it. Each run logs `SPIKE` lines:
time, editor memory added, sections, vertices, the largest horizontal misregistration in samples,
and the largest and RMS height error in centimetres.

## Inputs

All placement values come from the bundle's manifest, read by hand, so the harness applies them as
the plugin would:

| Variable | Value |
| --- | --- |
| `SPIKE_PNG` | the bundle's `hosts.unreal.heightmap` PNG, extracted |
| `SPIKE_EAST_CM`, `SPIKE_NORTH_CM` | the AOI's east and north spans, as `GetAoiSizeUeCm()` gives them |
| `SPIKE_ZMIN_CM`, `SPIKE_ZRANGE_CM` | `uint16_mapping.z_min_m`, and `z_max_m - z_min_m`, in centimetres |
| `SPIKE_SECTIONS` | sections a side (the importer caps the total at 4096) |
| `SPIKE_GLB`, `SPIKE_GROUND_CM` | `convert` only: the glb, and the ground height its origin is placed at |

The worst case needs no bundle: any 8129-square 16-bit grey PNG will do. The ADR's figures used a
synthetic one, which is never committed.

## Running it

In a scratch project outside every repository, on UE 5.8 (or the version being re-evaluated):

1. `<Name>.uproject` with one Editor module and the `MeshPartition`, `CQTest` and `Interchange`
   plugins enabled.
2. An Editor target naming that module.
3. `Source/<Name>/<Name>.Build.cs` with these private dependencies: `Core`, `CoreUObject`, `Engine`,
   `UnrealEd`, `Json`, `MeshPartition`, `MeshPartitionEditor`, `GeometryCore`, `GeometryFramework`,
   `MeshConversion`, `MeshDescription`, `StaticMeshDescription`, `ImageWrapper`, `RHI`, `RenderCore`,
   `CQTest`, `AssetTools`, `MaterialEditor`. On 5.8 it also needs
   `PrivateIncludePaths.Add(Path.Combine(EngineDirectory, "Source/Runtime/Engine/Internal"))`: that
   line is the ADR's first re-entry test, and on 5.9 the build is tried **without** it first.
4. This file as `Source/<Name>/Private/MeshTerrainSpike.cpp`. Build with
   `Engine/Build/BatchFiles/Build.bat <Name>Editor Win64 Development -Project=<path>.uproject -WaitMutex`.
5. Set the variables above and run
   `UnrealEditor-Cmd.exe <path>.uproject -ExecCmds="Automation RunTests Spike" -unattended -nopause -nosplash -RenderOffscreen -testexit="Automation Test Queue Empty" -log`.
