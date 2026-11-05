---
name: autocad-host-onboarding
description: Onboarding for the AutoCAD plugin (plain AutoCAD and Civil 3D, 2025/2026/2027) - the two builds from one source, the Revit sources compiled in by include, the Core / Addin / Civil 3D part split, the frame kind (order-frame, HPS-54), the one version home, and the traps (CI never builds the shell, the Civil 3D part is its own assembly). An index; the detail is in `autocad/README.md` and `autocad/docs/`. Read first for any change under `autocad/`.
---

# Mantle Place for AutoCAD - agent onboarding

Read the repo root [`CLAUDE.md`](../CLAUDE.md) first. This file is an index: each rule is stated
here in a line, and its detail lives in the document it links.

## Identity

- **Hosts:** Autodesk AutoCAD **and Civil 3D**, 2025, 2026 and 2027. One plugin, one release track
  (`autocad-<version>`, [ADR 0001](../docs/adr/0001-per-host-release-tracks.md)); Civil 3D is
  AutoCAD with extra modules, so plain AutoCAD is the base and Civil 3D is a part loaded on top.
- **Two builds, not one.** 2025 and 2026 run .NET 8 and 2027 runs .NET 10, and AutoCAD's managed API
  assemblies are compiled per runtime, so a `net8.0-windows` assembly built against 2027's API fails
  with `CS1705` and a `net10.0-windows` one cannot load in 2025. The shell is built twice
  (`net8.0-windows` against 2025's API, `net10.0-windows` against 2027's) and the bundle carries
  both; `PackageContents.xml` names the one each year loads. Never build the `net8` one against a
  newer install. What ran, per year, is [`docs/api-record.md`](docs/api-record.md).
- **SDK:** pinned in [`global.json`](./global.json).
- **Frame:** AutoCAD is an **order-frame host** (`HPS-54`) - the content it places, and the point
  it places it against, are in the **delivery CRS and its linear unit**, metric or foot, so the
  frame is a per-order fact. A file built in the AOI's metric UTM zone for a fixed-frame host is
  not in this frame on a State Plane delivery, and is skipped with a stated reason (`HPS-53`),
  never reprojected. The host block this plugin reads is its own, and the planner places that
  first (`HPS-52`).
- **Version:** one home, `<Version>` in [`Directory.Build.props`](Directory.Build.props). The
  assemblies, `PackageContents.xml`, the zip name and the release tag all derive from it; the
  packaging script stops if a build disagrees.

## Layout and the split that matters

```
src/MantlePlace.AutoCad.Core/     PURE. No AutoCAD API, no NuGet. net8.0 + net10.0. Compiles Revit's Core,
                                  Client and PlatformApi sources by include, plus its own decisions.
src/MantlePlace.AutoCad.Addin/    IMPURE and AutoCAD. The shell: commands, the app entry point. Both -windows TFMs.
src/MantlePlace.AutoCad.Civil3d/  IMPURE and Civil 3D. The only assembly that references Civil 3D's API.
tests/MantlePlace.AutoCad.Core.Tests/   Headless, over Core. net8.0 + net10.0.
tools/                            Packaging, deploy, the manifest writer, the Civil 3D spike.
```

- **Shared source, no shared assembly.** The host-free Revit sources (`Core`, `Client`,
  `PlatformApi`) are compiled into `MantlePlace.AutoCad.Core` by `Compile Include` with a
  `LinkBase`, so the next .NET host takes shipped code (`HPS-43`) and nothing ships as a dependency
  of two plugins. Edit them in `revit/`; the AutoCAD build picks the change up. Do not copy them.
- **Civil 3D is its own assembly, loaded behind a presence test.** Only `MantlePlace.AutoCad.Civil3d`
  references Civil 3D's API, so plain AutoCAD never resolves it. The shell loads it only when
  `Presence.Decide` says Civil 3D is loaded (`AeccLand.dbx` is a module of the process; the evidence
  is the spike in [`docs/api-record.md`](docs/api-record.md)). The decision is in `Core`, pure and
  tested; only the observations and the load are in the shell.
- **Put logic in `Core`.** If you cannot assert it without launching AutoCAD, it is in the wrong
  assembly. CI cannot build the shell or the part (it needs the Autodesk assemblies), so anything
  there is covered by review and by a real launch alone.

## The standard binds this folder

The Host Plugin Standard is normative; rules are cited by `HPS-NN` id in the code. This host
registered in [`verified-against.json`](../tools/manifest-conformance/verified-against.json) from its
first commit (`HPS-38`/`39`) and claims every corpus group (`HPS-41`). The `manifest` group runs under
the `autocad` key: its `hosts.autocad` cases drive the import plan (`AutoCadImportPlanner`), and the
host-invariant cases drive the shared reader the plan reads through. A case added to the group is a
key this suite must assert, or it fails.

## Packaging and install

- The release zip is an **Autodesk application plugin bundle** (`MantlePlaceAutoCad.bundle`), so the
  same package serves the Autodesk storefront and a hand copy. `PackageContents.xml` comes from
  `AutodeskBundleManifest.Build(version, years, BundleHost.AutoCad)` in Revit's `Core` - the one
  manifest function, with a host parameter - written by `tools/WriteBundleManifest`.
- **One way in.** `tools/Deploy-MantlePlaceAutoCad.ps1` refuses, naming the folder, while a Mantle
  Place AutoCAD bundle it did not install is present in an Autodesk `ApplicationPlugins` folder:
  AutoCAD would load the plugin twice.
- Packaging needs the Autodesk assemblies of both runtimes, so a release is built privately. A
  release is gated by a real launch in each of 2025, 2026 and 2027 (a .NET 8 host run is the
  release check, not part of this tree's CI).

## Host identity, windows and the ribbon

- **The platform is told `autocad`, from one place.** `HostIdentity.Id` in Revit's `Core` is `revit`
  there and `autocad` here (`MANTLEPLACE_AUTOCAD`, defined by `MantlePlace.AutoCad.Core.csproj`); the
  sign-in route, the `Mantle-Place-Client` header, the connected-host presence and the feedback
  `source` all read it. A shared source that spells `revit` out makes this plugin a second Revit to the
  platform.
- **Windows are copied from Revit's, not shared.** The Addin's WPF windows (sign-in, vault browser,
  notice) are built in code and read the monogram from embedded resources (`ResourceImages`), linked
  from Revit's `Resources` so no binary is committed twice. The vault browser has no Prepare, download
  or import yet: an order comes into a drawing through an export sent from the web vault.
- **The ribbon panel is built in code, on a `Mantle Place` tab of the plugin's own** (`HPS-57`), as
  Revit's is, in Civil 3D and plain AutoCAD alike. `PlugInsPanel` (Core) lists the commands, in
  `HPS-51`'s order, and decides the tab (`Place`: the plugin's tab, made when the ribbon lacks it);
  `PluginRibbon` (shell) makes the tab and adds the panel, and adds it again after each workspace
  change without a second tab or panel. The host's Add-ins tab is left alone. Not a partial CUIX: its
  tab is its own group's, so the host shows it as a second "Add-ins" tab
  ([`docs/api-record.md`](docs/api-record.md)). F1 is each button's tooltip help source. The
  storefront's guidance has pointed apps at the Add-ins tab; the way back is the one choice in
  `PlugInsPanel.Place` ([`docs/releases.md`](../docs/releases.md)).
- **An import is one press, planned in Core.** `AutoCadImportPlanner` decides everything from the
  `hosts.autocad` block: what is placed, where, in which units, what is skipped and why, and whether
  the drawing's geographic location refuses the import. The shell (`OrderImporter`) observes the
  location (`DrawingObservations`), verifies every file before drawing anything, and applies the plan
  verbatim. The window an export from the web opens (`AutoCadExportImportHost`) has the Import button;
  closing it before the press ends the export as cancelled.
- **Civil 3D adds the zone check and the surface, through its part alone.** When the presence test
  lets the part load, it reads the drawing's zone code (never sets it), which the shell resolves to an
  EPSG code through AutoCAD's own library for the plan to compare, and it builds the TIN surface from
  the block's LandXML (`TinSurface.CreateFromLandXML`). Part off, the plan is plain AutoCAD's.

## Commands

- **Build and test** -> [`README.md` > Build and test](./README.md#build-and-test), the one home.
  A `dotnet run` on the suite without `-f` cannot choose a framework and fails outright.
- **Quote paths with spaces** - `C:\Program Files\Autodesk\...`.
- **Names are spelled out** - `mantleplace`, never `mp`; no AEC or AECO abbreviations anywhere.
