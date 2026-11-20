# Mantle Place for AutoCAD

The AutoCAD plugin: plain AutoCAD and Civil 3D, 2025, 2026 and 2027, as one plugin. Civil 3D's part
loads only when Civil 3D is running.

**Status:** the plugin's tree and its packaging. The shell loads, signs you in, lists and searches
your vault, tells you when an order arrives, and imports an order into the active drawing, at the
order's own coordinates and units: one sent from the web vault, or a bundle zip already on this computer. Which features each plugin supports, beside every
other host, is the [host support matrix](../docs/host-support.md). Agents: read [`CLAUDE.md`](CLAUDE.md)
first.

## Commands

The plugin puts a **Mantle Place** tab of its own on the ribbon, as Revit does, holding a **Mantle Place** panel;
the host's Add-ins tab is left as it is (Civil 3D and plain AutoCAD alike). F1 over any of its buttons opens this page. Each
command can also be typed at the command line. The panel carries the Mantle Place monogram and no
other logo.

| Button | Command | What it does |
| --- | --- | --- |
| Sign In | `MANTLEPLACESIGNIN` | Opens your browser to sign in; AutoCAD never sees your password. A small window waits for the browser and stays out of the way. Signing in here signs you in for every Mantle Place plugin on this machine. |
| Sign Out | `MANTLEPLACESIGNOUT` | Drops the session and forgets the stored credential. |
| Vault | `MANTLEPLACEVAULT` | The bundles you keep, with a search box, which of them are on this machine, removing a local copy, and reporting a problem with one. **Prepare** and **Import**: Import downloads the selected order, at the level chosen for an MPB 3.0 one, and imports it into the active drawing in the import window, in one press for an order already built whole. An order still to be prepared is handed to Prepare instead; press **Import** again once Prepare says it is ready. Needs you signed in. |
| Import Bundle | `MANTLEPLACEIMPORTBUNDLE` | Asks for a Mantle Place bundle zip on this computer and imports it into the active drawing in the import window, exactly as an export from the web is imported: the same checks, the same refusals, the same surface. It needs no account. |
| About Mantle Place | `MANTLEPLACEABOUT` | Which build is loaded, whether the Civil 3D part is on, whether you are signed in, and a link to the [privacy page](https://mantle.place/privacy). |
| Send Feedback | `MANTLEPLACEFEEDBACK` | The Mantle Place feedback form in your browser. It needs no account. Only this plugin's version and your AutoCAD year go with the link: nothing from your drawing, and nothing about you. |

**Notices.** When an order turns up in your vault that you did not ask this plugin to look at, a small
notice appears in a corner of AutoCAD's window; clicking it opens the vault on that bundle.

**Exports from the web.** While you are signed in, this AutoCAD appears as a destination in your web
vault. Sending an export to it fetches the bundle, checks it, and imports the whole order into the
active drawing in the import window, the one Revit uses. AutoCAD offers no choice of layers, so a 2.x
order's import begins as the window opens; an MPB 3.0 order that offers levels opens on the level
control first (below).

**Bundle formats: MPB 3.0 beside 2.x.** The plugin reads MPB 3.0 bundles and MPB 2.x bundles side by
side, by every way in: the vault, an export from the web, and Import Bundle. A 2.x bundle imports
from its AutoCAD block, as before, except that in Civil 3D the drawing-units check below now applies
to it too. A 3.0 bundle's parts are found by name in its manifest's
catalog, and a part it does not hold is shown as skipped, naming the file, with the manifest's reason
where it gives one. Which 3.0 file each step reads, and what a 3.0 import leaves out, are in the
[1.2.0 release notes](docs/release-notes-1.2.0.md).

**The level control.** An MPB 3.0 order comes in one of three levels, **Max**, **Med** and **Min**, and
the level is chosen before the download, for the whole order. In the Vault window, selecting a 3.0
order shows the control under the buttons, each level with its download size and the platform's
recommendation preselected; **Import** downloads the chosen level. An export of a 3.0 order sent from
the web vault opens the import window on what the import will do, with the same control and **Import**
to press: choosing another level there downloads it and reopens the window on it before anything is
drawn. A 2.x order offers no level.

**The import window.** Every import, from the web or from Import Bundle, runs in the **Bundle Import**
window: it lists the steps (**Verify Files**, then each part the order places) with each one's state,
the step in flight and its **Elapsed** clock. **Cancel** stops the import between steps, never inside
one: what was placed stays in the drawing, and the steps it never reached read **Not Run**. When it is
over the window says what went in and what did not, with each reason, and so does the command line. A
second import waits until the first is over; an export sent from the web meanwhile waits its turn.

**Import Bundle.** A bundle zip you already have (downloaded from the web vault, copied from a
colleague, or the bundle cache's copy of one sent to this AutoCAD) goes in with **Import Bundle**. Pick
the zip and the whole order is imported into the active drawing in the import window; when it is over
the same words the window shows are written beside the zip in a file ending `.mantleplace-import.log`,
which is what a script waits for, since the steps run after the command returns. A file that is not a Mantle Place bundle is refused and nothing is
placed. With `FILEDIA` set to 0, or from a script, the command asks for the zip's path on the command
line instead of opening a dialog. With `MANTLEPLACE_BUNDLE_ZIP` set to a zip's path in AutoCAD's
environment, as for the Revit plugin's Import Bundle, the command asks nothing and imports that zip.

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
  the surface needs Civil 3D and places the rest. Civil 3D converts a LandXML file to the drawing's
  own drawing units (feet or metres) as it reads it, so the surface is built only in a drawing whose
  drawing units are the order's: in any other the import says which units the drawing is in, which
  the order is in, and places the rest. The plugin never changes a drawing's units; set them under
  Drawing Settings, Units and Zone, and import again.
- **Public-record layers**, such as parcels and zoning, as polylines, each record on its own
  `MantlePlace-Records-<name>` layer.
- **Site linework**, as polylines under the `MantlePlace-Linework` layer, each on a layer named for the
  one the drawing puts it on (`MantlePlace-Linework-Roads`).
- **What is skipped.** A part the bundle does not carry is skipped with the bundle's own reason, and the
  rest goes in. A bundle made before the AutoCAD block carried public-record layers and site linework
  names neither, and the import says so. A drawing holding something other than polylines (a hatch, an
  arc) is skipped by name rather than placed in part. That is per record layer: a record file holding
  points (the contamination and historic-places layers) is left out by name and the other record
  layers still go in.
- **Checked first.** Every file is matched to its published digest before anything is drawn; a
  mismatch places nothing.

## How it is built

- **Two builds.** AutoCAD and Civil 3D 2025 and 2026 run .NET 8; 2027 runs .NET 10. The shell is built
  for each runtime against that runtime's AutoCAD API, and the bundle carries both. `PackageContents.xml`
  names the build each year loads, so a curator copies one folder and never chooses.
- **Shared projects, each plugin its own copy.** The host-neutral `Core`, `Client` and `PlatformApi`
  projects live in [`shared/`](../shared/README.md); `MantlePlace.AutoCad.Core` references them,
  and the plugin ships its own copy of their assemblies. Nothing is installed once for two plugins.
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
the ribbon panel and the tab it goes on, the host identity the platform is told, the export receipt, what
Import Bundle refuses and that it reaches the same importer as an export, through the shared
import window, the import's steps and Cancel between them, the About
text and the Civil 3D presence decision.

Building the shell and the Civil 3D part needs the Autodesk assemblies. `AutoCadApiDirNet10`
(default `C:\Program Files\Autodesk\AutoCAD 2027`) is the install the .NET 10 build compiles
against; `AutoCadApiDirNet8` (default `...\AutoCAD 2025`) is the one for the .NET 8 build and must
hold **.NET 8** assemblies: one that references .NET 10 fails the .NET 8 build with `CS1705`. Override
either with `-p:AutoCadApiDirNet8=...` or the packaging scripts' `-ApiDirNet8`.

Once 2025 and 2026 have taken their .NET 10 update, and the Civil 3D Object Enabler updates that go
with it, an installed copy no longer serves, so the .NET 8 directory is assembled by hand. It needs
this layout:

| Path in the directory | Where a .NET 8 copy comes from |
| --- | --- |
| `accoremgd.dll`, `acdbmgd.dll`, `acmgd.dll` and the other AutoCAD assemblies | The `inc` folder of the ObjectARX 2025 SDK release that is not marked .NET 10 |
| `ACA\AecBaseMgd.dll`, `C3D\AeccDbMgd.dll` | The original Civil 3D 2025 Object Enabler (2025.0, early 2024); the ObjectARX SDK does not carry them |

The SDK is a self-extracting archive that 7-Zip opens without running it. Autodesk Account offers only
the latest Object Enabler; on a machine that installed Civil 3D 2025 through Autodesk's installer, the
original is often still in its package cache under `C:\Autodesk\WI`, as a `pkg.c3doe.tar` whose
`.adix` is a zip holding both DLLs. Check what a candidate references before using it: one whose
`System.Runtime` reference is 10.0 is an updated copy.

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
