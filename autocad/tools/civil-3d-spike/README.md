# Civil 3D spike

A throwaway probe of the Civil 3D calls the AutoCAD plugin will rest on: reading the drawing's
coordinate zone and comparing it with the order's, building a TIN surface from a DEM through
`AddDEMFile` and its coordinate-system overload and from a LandXML file through `CreateFromLandXML`,
and telling Civil 3D loaded in the process from
Civil 3D installed with its "as AutoCAD" launcher running. What it found is the plugin's API record,
[`../../docs/api-record.md`](../../docs/api-record.md). A developer tool for this machine's Civil 3D:
nothing here ships, and CI neither builds nor runs it (no hosted runner has AutoCAD).

| Path | What it is |
|---|---|
| `Shared/ZoneCheck.cs` | the zone comparison: proceed on the order's code, refuse on any other or on none, never set it |
| `Shared/Presence.cs` | the presence test the plugin will use, and what else the probe observes |
| `Probe/` | the AutoCAD-only half: the `MANTLEPLACEC3DPROBE` command, which observes presence and loads `Civil/` only on a Civil 3D verdict |
| `Civil/` | the Civil 3D half, the one assembly that references Civil 3D: the zone calls and the surfaces |
| `Tests/` | headless tests of the two decisions, on .NET 8 and .NET 10 |
| `Invoke-Civil3dProbe.ps1` | builds both halves against the year's install, writes a synthetic DEM, runs the host, prints `result.md` |

## Running it

```powershell
# The decisions, headless.
cd Tests
dotnet run -f net8.0
dotnet run -f net10.0

# In a real host, one run per year and launcher; the work root sits outside the repository.
powershell -NoProfile -File ./Invoke-Civil3dProbe.ps1 -Year 2027 -Mode civil3d -WorkRoot <folder>
powershell -NoProfile -File ./Invoke-Civil3dProbe.ps1 -Year 2027 -Mode as-autocad -Force -WorkRoot <folder>
powershell -NoProfile -File ./Invoke-Civil3dProbe.ps1 -Year 2026 -Mode civil3d -Shell gui -WorkRoot <folder>
```

- **`-Mode`** takes the launcher's own switches: `civil3d` loads `AecBase.dbx` with the Civil 3D
  profile and `/product C3D`, `as-autocad` passes `/product ACAD` to the same install.
- **`-Shell console`** (the default) runs `accoreconsole.exe`, with no window. **`-Shell gui`** runs
  `acad.exe` minimised; it needs a signed-in licence, and the script stops the host once the probe
  reports.
- **`-Force`**, on a run whose presence test says no, loads the Civil 3D half anyway, to record what
  a wrong presence test would do.
- **`-Zone`, `-MismatchZone` and `-DemZone`** are Civil 3D coordinate-system codes. The defaults are a
  State Plane zone, its neighbour and the UTM zone the synthetic DEM is written in.

The DEM is synthetic, a 40 by 40 grid of 30 m cells written by the script as an ESRI ASCII grid and,
when GDAL's `gdal_translate` is on `PATH`, as a GeoTIFF too. No real data is used or committed.

## What a run does

1. **Presence.** From the AutoCAD-only half: whether `AeccDbMgd` is a loaded assembly, whether
   `AeccLand.dbx` and `AecBase.dbx` are loaded modules, what `DynamicLinker` lists, what `PRODUCT`
   says, and whether Civil 3D's files are on disk. `Presence.Decide` gives the verdict.
2. **Zone.** Reads `CoordinateSystemCode`, compares it with `-Zone` and reads it again. Then, as a
   fixture only and in a drawing never saved, sets the order's zone, so that a match and a mismatch can
   both be compared; the plugin never sets it.
3. **Zone codes in EPSG.** For the fresh drawing's code and each code given, what
   `GeoCoordinateSystem.Create` says it is, and whether the drawing has a geographic location before
   and after the fixture sets the zone.
4. **Surface.** For each DEM, one TIN surface through `AddDEMFile(path)` and one through
   `AddDEMFile(path, code, false, 0)`, each rebuilt and read back: points, extents and elevations.
   Then one TIN surface through `CreateFromLandXML` from each synthetic LandXML file: a 3 by 3 grid
   in California zone III US feet with and without a `<CoordinateSystem>` element, and one in UTM
   zone 10 north metres.

Each call is recorded as worked, did nothing or threw, in the host's own words. The host is launched
with the variables the probe reads, and the probe is inert without them.

AutoCAD loads an unsigned assembly only from a trusted folder, and the console answers the
secure-load prompt with "do not load" without saying so: its only message is "Unable to load ...
assembly". The script's host script therefore appends the run's build folder to `TRUSTEDPATHS` for the
`NETLOAD` alone and restores the profile's own value on the next line, before the probe runs.
