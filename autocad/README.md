# Mantle Place for AutoCAD

The AutoCAD plugin: plain AutoCAD and Civil 3D, 2025, 2026 and 2027, as one plugin. Civil 3D's part
loads only when Civil 3D is running.

**Status:** the plugin's tree and its packaging. The shell loads and says what it is
(`MANTLEPLACEABOUT` names the version, the .NET it runs on and whether the Civil 3D part is on); it
does not import a Mantle Place bundle yet. Which features each plugin supports, beside every other
host, is the [host support matrix](../docs/host-support.md). Agents: read [`CLAUDE.md`](CLAUDE.md)
first.

## How it is built

- **Two builds.** AutoCAD and Civil 3D 2025 and 2026 run .NET 8; 2027 runs .NET 10. The shell is built
  for each runtime against that runtime's AutoCAD API, and the bundle carries both. `PackageContents.xml`
  names the build each year loads, so a curator copies one folder and never chooses.
- **Shared source, no shared assembly.** Revit's host-free `Core`, `Client` and `PlatformApi`
  sources are compiled into `MantlePlace.AutoCad.Core` by include. Nothing ships that two plugins
  depend on.
- **Civil 3D is its own assembly.** `MantlePlace.AutoCad.Civil3d` is the only project that references
  Civil 3D's API, and the shell loads it only when `AeccLand.dbx` is a loaded module of the process.
  The record of what ran, per year, is [`docs/api-record.md`](docs/api-record.md).
- **One version.** `<Version>` in [`Directory.Build.props`](Directory.Build.props) is read by the
  assemblies, the manifest, the zip name and the release tag.

## Build and test

The suite needs no AutoCAD install and no network. Choose the framework: `dotnet run` without `-f`
cannot, and fails.

```bash
dotnet run --project autocad/tests/MantlePlace.AutoCad.Core.Tests/MantlePlace.AutoCad.Core.Tests.csproj -f net8.0
dotnet run --project autocad/tests/MantlePlace.AutoCad.Core.Tests/MantlePlace.AutoCad.Core.Tests.csproj -f net10.0
```

It runs the shared conformance corpus over the compiled-in sources, plus the AutoCAD bundle manifest
and the Civil 3D presence decision.

Building the shell and the Civil 3D part needs the Autodesk assemblies. `AutoCadApiDirNet10`
(default `C:\Program Files\Autodesk\AutoCAD 2027`) is the install the .NET 10 build compiles
against; `AutoCadApiDirNet8` (default `...\AutoCAD 2025`) is the one for the .NET 8 build and must
be a **.NET 8** install: an install updated to .NET 10 fails the .NET 8 build with `CS1705`. Override
either with `-p:AutoCadApiDirNet8=...` or the packaging scripts' `-ApiDirNet8`.

## Package and install

```powershell
powershell -NoProfile -File autocad/tools/Package-MantlePlaceAutoCad.ps1
```

writes `MantlePlace-AutoCad-<version>.zip` under `autocad/dist/`. The zip holds one Autodesk
application plugin bundle, `MantlePlaceAutoCad.bundle`. To install, copy that folder into
`%APPDATA%\Autodesk\ApplicationPlugins`, or double-click `Install.cmd`; step by step in the
package's `README.txt` ([`packaging/README.txt`](packaging/README.txt)).

For development, `tools/Deploy-MantlePlaceAutoCad.ps1` builds and installs the bundle to the same
folder. It **refuses** while a Mantle Place AutoCAD bundle it did not install is present in an
`ApplicationPlugins` folder (for example one installed from the Autodesk storefront), naming the
folder, because AutoCAD would load the plugin twice. Remove that bundle first.

## Releasing

Its own track, tagged `autocad-<version>` with no `v`
([ADR 0001](../docs/adr/0001-per-host-release-tracks.md)); the first release is `autocad-1.0.0`.
Packaging needs the Autodesk assemblies, so it runs privately. The gate is the plugin loading in
AutoCAD and Civil 3D 2025, 2026 and 2027.
