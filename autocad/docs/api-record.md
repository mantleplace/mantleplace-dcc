# AutoCAD API record: what has run

The compiler proves that a member exists and how it is shaped; it says nothing about what AutoCAD or
Civil 3D does when the member runs. This file records which calls the AutoCAD plugin will make that
have **run inside a real host**, in which years, and how. It starts with the Civil 3D spike
([`../tools/civil-3d-spike/`](../tools/civil-3d-spike/README.md)), run before any plugin code was
written. Add to it when an import proves or disproves a call.

Each entry is **worked**, **did nothing** (no exception, no effect) or **threw**, per year. The spike
ran on 2026-11-03 on the build machine, with these installs, all on .NET 10.0.12:

| Year | AutoCAD | Civil 3D | Runs |
|---|---|---|---|
| 2027 | 26.0.118 (2027.1) | 13.9.1308 (2027.1) | console and window, both launchers |
| 2026 | 25.1.179 (2026.1.2) | 13.8.1873 (2026.2.3) | console, both launchers; window, Civil 3D |
| 2025 | 25.0.189 (2025.1.4) | 13.7.1519 (2025.2.7) | console, both launchers |

"Console" is `accoreconsole.exe`, "window" is `acad.exe`, and each was started with its Start menu
launcher's switches: Civil 3D's (`/ld AecBase.dbx`, the Civil 3D profile, `/product C3D`) or the
"as AutoCAD" one (`/product ACAD`), over the same install folder.

## The presence test

**The plugin loads its Civil 3D part only when `AeccLand.dbx`, Civil 3D's land module, is a loaded
module of the process** (`GetModuleHandle("AeccLand.dbx")` is not null). That is the test in
`Presence.Decide`, and it is the one observation that held in every run, console and window alike:

| Observation | Civil 3D, window | Civil 3D, console | "As AutoCAD", either |
|---|---|---|---|
| `AeccLand.dbx` loaded | true | true | false |
| `AecBase.dbx` loaded | true | true | false |
| Aec modules in `SystemObjects.DynamicLinker.GetLoadedModules()` | 89 to 91 | 23 to 26 | 0 |
| `AeccDbMgd` a loaded managed assembly | true | **false** | false |
| `PRODUCT` | `AutoCAD` | `AutoCAD` | `AutoCAD` |
| `C3D\AeccDbMgd.dll` on disk beside the host | true | true | **true** |

- **Files on disk say nothing.** The "as AutoCAD" launcher runs the same `acad.exe` over the same
  folder, so every installed-file test reads true there.
- **`PRODUCT` says `AutoCAD` in Civil 3D too**, so it cannot decide.
- **`AeccDbMgd` is not loaded in Civil 3D's console** until something calls into it, so a managed
  assembly test would turn the Civil 3D part off there. In the window it was loaded by the time the
  probe ran.
- **`AecBase.dbx` alone is not Civil 3D**: it is the shared base of the AutoCAD verticals, which load
  it too.
- **Decide at first use, not at load.** The probe ran from a startup script, after the host had
  finished starting. Whether `AeccLand.dbx` is already loaded when a bundle's assembly initialises at
  startup was not tested, so the plugin decides when a command first needs the Civil 3D part.
- **A wrong verdict degrades, it does not crash.** Loading the Civil 3D part under the "as AutoCAD"
  launcher threw `FileNotFoundException` for `AeccDbMgd` (2025, 2026 and 2027, console; 2027,
  window), the probe caught it, and the host carried on.

## Calls

| Call | 2025 | 2026 | 2027 |
|---|---|---|---|
| `CivilApplication.ActiveDocument` | worked | worked | worked |
| `CivilDocument.Settings.DrawingSettings.UnitZoneSettings` | worked | worked | worked |
| `SettingsUnitZone.CoordinateSystemCode`, read | worked | worked | worked |
| `SettingsUnitZone.GetAllCodes()` | worked | worked | worked |
| `SettingsUnitZone.GetCoordinateSystemByCode(code)` | worked | worked | worked |
| `SettingsUnitZone.CoordinateSystemCode`, set (the probe's fixture, never the plugin) | worked | worked | worked |
| `TinSurface.Create(Database, string)` | worked | worked | worked |
| `SurfaceDefinitionDEMFiles.AddDEMFile(path)`, ASCII grid and GeoTIFF | worked | worked | worked |
| `SurfaceDefinitionDEMFiles.AddDEMFile(path, code, false, 0)`, ASCII grid and GeoTIFF | worked | worked | worked |
| `TinSurface.Rebuild()`, `GetGeneralProperties()` | worked | worked | worked |

Neither the zone route nor the surface route failed in any year, so neither is cut from the plugin's
1.0. What the calls did:

- **A fresh drawing's zone reads `.`**, not an empty string. `.` is also the first code
  `GetAllCodes()` returns: it is Civil 3D's "no coordinate system". `ZoneCheck` treats it as no zone
  and refuses, saying so.
- **The codes are Civil 3D's own library codes, not EPSG codes, and they are irregular.** California
  zone III in US feet is `CA83IIIF`, with no hyphen, beside `CA83-IVF` for zone IV. `CA83-IIIF` is not
  a code: `GetCoordinateSystemByCode` and the setter both threw `ArgumentException` ("Value does not
  fall within the expected range"). The library holds 9,607 codes in 2025, 9,714 in 2026 and 9,715 in
  2027. So the code the plugin compares must reach it already in Civil 3D's spelling, and the
  comparison is exact: the plugin never maps one spelling to another.
- **Reading and comparing never changed the zone.** Every run read the code again after each
  comparison and after the surfaces, and it was unchanged.
- **`AddDEMFile(path)` places the grid as written**, in its own coordinates, whatever the drawing's
  zone.
- **The overload transforms into the drawing's zone, expressed in the drawing's unit.** A DEM in
  `UTM83-10` metres, added to a metric drawing whose zone is `CA83IIIF` (a US-foot zone), landed at
  California zone III coordinates **in metres**: all four extents matched, to the centimetre, a GDAL
  transform of the grid's corner points to the metre-based zone III frame. Elevations came through
  unchanged. A drawing in feet
  was not run, so whether the overload also scales elevations there is still open.
- **A GeoTIFF's points sit half a cell from the ASCII grid's.** From one grid, the ASCII grid's
  points start at the first cell's centre (15 m in from the corner) and the GeoTIFF's at its corner.
  Which file the bundle names for the surface therefore moves the surface by half a cell, and the
  plugin should be checked against the format the bundle actually ships.

## Called by the plugin

The import's Civil 3D half makes three calls the spike did not run when it was first recorded. The
plugin's live check ran each of them in Civil 3D 2027 (window, Civil 3D launcher) on 2026-11-04, from
the installed bundle, importing a cached Texas Central (US foot) order whose manifest was given an
AutoCAD block for the run; 2025 and 2026 are still unproven.

| Call | Why the plugin makes it | 2027 |
|---|---|---|
| `GeoCoordinateSystem.Create(zoneCode).EPSGcode` | The block states the order's frame as an EPSG code and Civil 3D's zone as its library code, so the zone is compared through AutoCAD's own library and never through a spelling the plugin maps. | worked: `NSRS11.TX-CF` gave 6578 and `TX83-NCF` gave 2276, so the order went in under the first and was refused under the second |
| `TinSurface.CreateFromLandXML(db, name, path, surfaceName)` | The block's terrain source is LandXML (format.md §6.9), which `AddDEMFile` does not read. The three-parameter overload is obsolete. | worked: 75,302 points, placed as written (US-foot coordinates and heights, the LandXML's own extents), with and without the drawing's zone set |
| `CivilDocument.GetSurfaceIds()` | The surface's name is unique in the drawing, so a second import of an order gets a numbered name. | worked: the second import into one drawing built `...-2` |

- **Setting the zone sets the geographic location.** In a drawing whose zone the run set, the plan
  read the drawing's geographic location as that same code, so the geolocation check and the zone
  check agreed.
- **The bundle's load path.** The shell loaded at startup from `%APPDATA%\Autodesk\ApplicationPlugins`
  once `PackageContents.xml` gave each year its own `Components` group: with all three years'
  requirements in one group the autoloader registered the bundle and loaded nothing. An unsigned
  build there raises AutoCAD's "Unsigned Executable File" prompt (two of them under `SECURELOAD` 1).

## The ribbon panel

Read live on 2026-11-04 in 2027, window mode, from the installed bundle, in Civil 3D (Civil 3D
launcher) and in plain AutoCAD (the same install's AutoCAD launcher); 2025 and 2026 are unproven.

- **The ribbon names a tab `MENUGROUP.UID`.** Civil 3D's Add-ins tab is `CIVIL.ID_Civil3DAddins`
  and plain AutoCAD's is `ACAD.RBN_00012112`. Civil 3D's workspaces show its own tab and not
  AutoCAD's. The plugin named both while its panel went on the host's tab; the way back to it is the
  one choice in `PlugInsPanel.Place`.
- **A partial CUIX cannot put a panel on either.** The plugin's first partial file gave its tab the
  UID `RBN_00012112`; the host read it as `MANTLEPLACE.RBN_00012112`, a second "Add-ins" tab beside
  its own, in both hosts. The profile also held a copy of the partial in its `Support` folder, listed
  in the main file's partial menus, which still loaded after the bundle stopped naming it;
  `CUIUNLOAD MANTLEPLACE` removes it from a machine that ran that build.
- **A panel built in code on the host's tab works.** In both hosts the panel sat on the host's
  Add-ins tab beside App Manager, with all five buttons, their monogram, tooltip and help topic, and
  the ribbon held one Add-ins tab. Pressing About ran `MANTLEPLACEABOUT` and opened its window. A
  round trip to another workspace (Planning and Analysis in Civil 3D, 3D Modeling in AutoCAD) and
  back left the panel where it was, once.
- **It now goes on a tab of the plugin's own.** `HPS-57` asks every host with a ribbon for a
  `Mantle Place` tab, as Revit has, so `PlugInsPanel.Place` names `MANTLEPLACE_TAB` (title and name
  `Mantle Place`) and the shell makes it when the ribbon lacks it; the host's Add-ins tab is no
  longer named. The tab is created in code, so its identifier is the plugin's own and the host does
  not prefix it with a menu group (the partial CUIX's tab was read as `MANTLEPLACE.RBN_00012112`).
  A rebuild that keeps the tab and drops the panel puts the panel back, and a second
  `WorkspaceLoaded` adds neither a tab nor a panel. The headless tests pin the decision; the live
  ribbon was read for the new tab in Civil 3D 2027 on 2026-11-14 (below).

## The extended probe (2026-11-05)

The probe was run again with the zone compare and the LandXML surface added, in a window
(`acad.exe`) with the Civil 3D launcher's switches, on the same three installs, all on .NET 10.0.12.
In every year Civil 3D loaded (`Civil3dLoaded`) and the same 22 calls worked:

| Call | 2025 | 2026 | 2027 |
|---|---|---|---|
| Zone compare, fresh drawing (`.`) against the order's zone | refused, no zone | refused, no zone | refused, no zone |
| Zone compare, matching zone | proceed | proceed | proceed |
| Zone compare, other zone | refused, mismatch | refused, mismatch | refused, mismatch |
| `Database.GeoDataObject`, fresh drawing | worked (none) | worked (none) | worked (none) |
| `Database.GeoDataObject`, after the fixture zone | worked | worked | worked |
| `GeoCoordinateSystem.Create(code)`, a library code | worked | worked | worked |
| `GeoCoordinateSystem.Create(".")` | threw | threw | threw |
| `TinSurface.CreateFromLandXML(db, name, path, style)`, US feet with and without `<CoordinateSystem>`, metres | worked | worked | worked |

- **The compare never changed the zone.** Read again after each compare and after the surfaces, it
  was unchanged.
- **`GeoCoordinateSystem.Create` maps a library code to its EPSG code**: `CA83IIIF` to 2227,
  `CA83-IVF` to 2228 and `UTM83-10` to 26910. On `.` it throws `eNotApplicable`, so it must be called
  only after the zone check has refused "no zone".
- **`CreateFromLandXML` places the points as written.** The US-foot file landed in the same extents
  with and without its `<CoordinateSystem>` element, so that element does not move the surface.
- **The first window runs hung as the headless ones had**, with no result after 600 s. They ran once
  the run folder sat on a path with no 8.3 short name and no other Mantle Place bundle was installed;
  which of the two was the cause was not isolated.

## The vault send and import, live (2026-11-14)

A live Civil 3D 2027 run on the build machine (window, Civil 3D launcher, .NET 10), with the plugin
1.0.0 built from `main` and installed as its `MantlePlaceAutoCad.bundle`, with nobody at the screen.
The plugin started signed in to a local development platform; three exports of one ready order
(manifest 2.2.0, a `hosts.autocad` block in a metric UTM frame) were sent from the web vault to the
drawing's connected host, and Import was pressed in the window each one opened. Each step is
**worked**, **did nothing** or **threw**:

| Step | Civil 3D 2027 | What showed it |
|---|---|---|
| 1. Vault send: the Download step lists the AutoCAD host and a send reaches it | worked | the presence entry named this AutoCAD and its year; each of the three sends opened the plugin's window |
| 2. One-press import from the received export | worked | the export ended imported on the web, shown as in the drawing; aerial, contours, public records, linework and surface placed on `MantlePlace-*` layers |
| 3. Zone check | worked | under another system's zone the import refused, named both systems and placed nothing; under the order's own zone it went in; the zone, units and geographic location were never written |
| 4. Terrain surface | worked | a TIN surface built from the bundle's LandXML, on `MantlePlace-Surface`; a second import of the same order built a numbered surface |

The calls the import made, each recorded from the drawing read back:

| Call | 2027 |
|---|---|
| Presence test: `AeccLand.dbx` a loaded module | worked |
| Civil 3D part loaded by reflection, behind the verdict | worked |
| `UnitZoneSettings.CoordinateSystemCode`, read | worked |
| `Database.GeoDataObject`, then `GeoLocationData.CoordinateSystem` | worked |
| `GeoCoordinateSystem.Create(code).EPSGcode` | worked |
| `Document.LockDocument()` | worked |
| `LayerTable.Add` | worked |
| `RasterImageDef` (image dictionary, `Load()`) | worked |
| `RasterImage` (`Orientation`, `AssociateRasterDef`) | worked |
| `RasterImage.TransformBy` (rotation) | n/a: the order's aerial is not rotated |
| `Polyline.AddVertexAt`, `Elevation`, `AppendEntity`: contours, public records, site linework | worked |
| `CivilDocument.GetSurfaceIds()` | worked |
| `TinSurface.CreateFromLandXML(db, name, path, surfaceName)` | worked |
| `TinSurface.GetGeneralProperties()`, `.Layer` | worked |

- **The `Mantle Place` tab, live.** The same run read the ribbon: one `Mantle Place` tab, the
  plugin's one panel on it and on no other tab, its five buttons in order (Sign In, Sign Out, Vault,
  About, Send Feedback), and F1 on each opening the plugin's help page at its topic.
- **The drawing's unit is not consulted.** The order went in at the units its block states, and the
  window's report said so.
- **Only the plugin's own load prompt** ("Unsigned Executable File", answered Load Once) appeared; no
  other dialog, and Civil 3D never took the foreground.
- **The local-bundle run was not made.** The plugin has no entry point that imports a bundle already
  on disk: an order reaches a drawing only as an export sent from the web vault, so every import above
  came through a send. The entry point, and the local-bundle run it allows, are tracked in [their own issue](https://github.com/mantleplace/mantleplace-dcc/issues/635).

## What this does not yet cover

- **A local-bundle import.** The plugin has no entry point for a bundle already on disk (above); [tracked here](https://github.com/mantleplace/mantleplace-dcc/issues/635).
- **A second workspace load of the `Mantle Place` tab**, live. The tab and its buttons were read on a
  running Civil 3D 2027 (above); plain AutoCAD 2027 and a round trip to another workspace were not.
- **A drawing in feet**, with a foot-based zone and a metric DEM.
- **The plugin's own load path in 2025 and 2026**, and in plain AutoCAD; 2027's is above.
- **A .NET 8 build.** Every install here, 2025 and 2026 included, runs .NET 10 after its updates, and
  its API assemblies reference .NET 10, so a .NET 8 project cannot compile against them (`CS1705`).
  The probe builds each year against that year's installed API, at the framework its own runtime
  configs name. Whether a .NET 8 assembly built against the original 2025 API loads in these updated
  hosts was not tested.
