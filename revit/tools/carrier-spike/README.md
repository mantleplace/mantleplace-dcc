# Vector carrier spike

The prototypes and the probe behind [`revit/docs/vector-carrier.md`](../../docs/vector-carrier.md),
the record of which shared file the Revit plugin reads its vector layers from, so the Revit-only
GeoJSON can go, and of where a linked site IFC lands. A developer tool for this machine's Revit:
nothing here ships, and CI neither builds nor runs it (Revit is never on a hosted runner, and the
GeoPackage reader needs the Windows SQLite).

| Path | What it is |
|---|---|
| `Readers/GeoPackageLayerReader.cs` | prototype: one GeoPackage table to the `SiteFeature` list the plugin takes, through `WinSqlite` and `GeoPackageGeometry` |
| `Readers/WinSqlite.cs` | P/Invoke over the `winsqlite3.dll` Windows ships: no NuGet, no native binary beside the plugin |
| `Readers/GeoPackageGeometry.cs` | the GeoPackage blob header and WKB, to lines and rings |
| `Readers/DxfLayerReader.cs` | prototype: one DXF layer to the same list, attributes from `MANTLEPLACE` extended data |
| `Readers/CarrierFeatures.cs` | the one place a carrier's parts and attributes become `SiteFeature`s, with `SiteVectorReader`'s rules |
| `Readers/CarrierComparison.cs` | reads a bundle through all three carriers and tables the differences against GeoJSON |
| `Tests/` | headless tests of both readers against `SiteVectorReader`, and `--compare` over a real bundle |
| `Probe/` | the scratch plugin a real Revit runs, one mode per launch |
| `Invoke-CarrierProbe.ps1` | launches that Revit quietly, waits for the probe, stops it, prints `result.md` |
| `make_carriers.py` | writes both carriers from one extracted bundle, standing in for the pipeline |

## Running it

```powershell
# 1. Both carriers from an extracted bundle (GDAL's ogr2ogr on PATH, and pip install ezdxf).
#    When PROJ complains about another installation's proj.db, point PROJ_DATA at GDAL's own projlib.
python make_carriers.py <extracted bundle> <carrier folder>

# 2. The readers, headless, on both runtimes the plugin meets; then the comparison over the real files.
cd Tests
dotnet run -f net8.0
dotnet run -f net10.0
dotnet run -f net8.0 -- --compare <extracted bundle> <carrier folder>

# 3. In a real Revit, one launch per mode: geojson, gpkg, dxf, ifc.
pwsh ./Invoke-CarrierProbe.ps1 -Year 2027 -Mode gpkg -Bundle <extracted bundle> -Carriers <carrier folder> -WorkRoot <folder outside the repo>
```

The extracted bundle needs `Metadata/manifest.json`, `Site/Vector/*.geojson`, `Site/Site.ifc` and
`Vector/Building.geojson`. Keep it and the work root outside the repository: no bundle, carrier or
run folder is ever committed (the root `CLAUDE.md`, "Binaries").

## What a probe launch does

- **`geojson`, `gpkg`, `dxf`:** reads every layer through that carrier first, cold, so the first read
  pays for the JIT and, for the GeoPackage, for loading `winsqlite3.dll` into Revit's process; then
  reads every layer through all three for the parity table; then places each layer as one
  `DirectShape` of lines in a fresh imperial project whose survey point is the manifest's origin, set
  as the plugin sets it, and reads back each bounding box and one vertex's shared coordinates.
- **`ifc`:** converts `Site/Site.ifc` with `OpenIFCDocument` at reference intent, as the plugin does,
  and reports where the converted model's project position, survey point and contents are; then links
  it into a fresh project with that survey point, origin to origin (the plugin's placement) and by
  shared coordinates, and reports each placement against the GeoPackage footprints. Every failure
  Revit raises is recorded in its own words and the warnings let through, so no dialog waits.

The driver keeps the timing harness's rules ([`../timing/README.md`](../timing/README.md)): one Revit at
a time, refused while any is running; a temporary manifest in
`%APPDATA%\Autodesk\Revit\Addins\<year>\`, removed once the probe starts and again when the run ends;
Revit launched minimised through WMI; only the unsigned add-in prompt answered (Load Once); and only
the Revit it launched is stopped. The probe is inert unless that driver launched the Revit it is in.
