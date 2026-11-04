# Mantle Place for AutoCAD

The AutoCAD plugin: plain AutoCAD and Civil 3D, 2025, 2026 and 2027, as one plugin. Civil 3D's part
loads only when Civil 3D is running.

**Status:** the plugin's tree and its packaging. The shell loads, signs you in, lists and searches
your vault, tells you when an order arrives, and imports an order sent from the web vault into the
active drawing, at the order's own coordinates and units. Which features each plugin supports, beside every
other host, is the [host support matrix](../docs/host-support.md). Agents: read [`CLAUDE.md`](CLAUDE.md)
first.

## Commands

The bundle puts a **Mantle Place** panel on the ribbon's **Plug-Ins** tab (named **Add-ins** from
AutoCAD 2025), from a partial customization file. F1 over any of its buttons opens this page. Each
command can also be typed at the command line. The panel carries the Mantle Place monogram and no
other logo.

| Button | Command | What it does |
| --- | --- | --- |
| Sign In | `MANTLEPLACESIGNIN` | Opens your browser to sign in; AutoCAD never sees your password. A small window waits for the browser and stays out of the way. Signing in here signs you in for every Mantle Place plugin on this machine. |
| Sign Out | `MANTLEPLACESIGNOUT` | Drops the session and forgets the stored credential. |
| Vault | `MANTLEPLACEVAULT` | The bundles you keep, with a search box, which of them are on this machine, removing a local copy, and reporting a problem with one. Needs you signed in. There is no Prepare or download in AutoCAD yet: an order comes into a drawing as an export sent from the web vault. |
| Send Feedback | `MANTLEPLACEFEEDBACK` | The Mantle Place feedback form in your browser. It needs no account. Only this plugin's version and your AutoCAD year go with the link: nothing from your drawing, and nothing about you. |
| About | `MANTLEPLACEABOUT` | Which build is loaded, whether the Civil 3D part is on, whether you are signed in, and a link to the [privacy page](https://mantle.place/privacy). |

**Notices.** When an order turns up in your vault that you did not ask this plugin to look at, a small
notice appears in a corner of AutoCAD's window; clicking it opens the vault on that bundle.

**Exports from the web.** While you are signed in, this AutoCAD appears as a destination in your web
vault. Sending an export to it fetches the bundle, checks it, and opens a window saying where it is.
Pressing **Import** places the whole order in the active drawing in one go, and the window then says
what went in and what did not, with each reason. Closing the window without importing ends the export
as cancelled.

**What an import places.** Everything comes from the order's AutoCAD block in the bundle, and nothing
is worked out in AutoCAD:

- **Contours**, as polylines on the `MantlePlace-Contours` layer, each at its published height.
- **The aerial image**, on the `MantlePlace-Aerial` layer, at the corner, size and rotation the bundle
  publishes. The image is a file AutoCAD links to; it stays in the order's folder in the bundle cache.
- **Units** are the order's: coordinates in its X/Y unit and heights in its height unit, as the bundle
  states them. The drawing's own unit setting is not read or changed.
- **Where it lands.** In a drawing with no geographic location, the order goes in at its own
  coordinates and a notice says so. In a drawing whose geographic location is in another coordinate
  system, the import refuses, names both, and places nothing. In Civil 3D the drawing's coordinate
  zone is checked the same way, by the EPSG code AutoCAD gives for it. The plugin never sets the
  location or the zone.
- **The surface, in Civil 3D only.** Civil 3D builds a TIN surface from the order's LandXML, on the
  `MantlePlace-Surface` layer. In plain AutoCAD, and in Civil 3D started as AutoCAD, the import says
  the surface needs Civil 3D and places the rest.
- **What is skipped.** A part the bundle does not carry is skipped with the bundle's own reason. Public-record
  layers and site linework are not in the order's AutoCAD block, so they are not imported.
- **Checked first.** Every file is matched to its published digest before anything is drawn; a
  mismatch places nothing.

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

It runs the shared conformance corpus over the compiled-in sources, plus the AutoCAD bundle manifest,
the partial customization file, the host identity the platform is told, the export receipt, the About
text and the Civil 3D presence decision.

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
