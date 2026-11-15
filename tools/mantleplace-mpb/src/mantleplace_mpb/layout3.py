# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""The 3.x tree (`format.md` §1, §2, §6, §12), its one level (§8, §9.3) and its catalog (§9.5).

These hold a manifest that declares 3.x, and only one. What each rule checks:

- **The tree.** Every file sits directly in one of the seven subject folders, and the root holds
  only `Readme.md` and `Manifest.json`. A deeper file is `tree.subfolder`; a third root file is
  `tree.root-file`; a 2.x file that 3.0 removed (§12), or a file in a format 3.0 dropped, is
  `tree.file-removed`. A file with a good name that the 3.0 file table does not list is
  `tree.file-unknown`, and a folder outside the seven `tree.folder-unknown`: errors in a bundle
  that declares 3.0, whose table this validator holds (`conformance.md` §7), and warnings in one
  that declares a later 3.x minor, which may add a file (§3) that a 3.0 reader still reads.
- **The names** (§2), for a name the table does not list: ASCII (`name.charset`), no number
  (`name.number`), no hyphen (`name.hyphen`), no suffix after a word of the tree, joined or not
  (`name.suffix`: `TerrainMed`, `Terrain_Med`, `Terrain.med.glb`), one word with its first letter
  capitalised (`name.word`) and a lower-case extension (`name.extension`). A tree name written in
  another case is `name.case-mismatch`: a reader matches regardless of case, and a writer uses the
  exact case. Two entries that differ only by case are `name.case-collision`.
- **One level.** The manifest states the download's level once, as one of `Max`, `Med` and `Min`:
  none is `level.missing`, two or more is `level.several`, anything else `level.unknown`.
- **The catalog.** One entry per file the download holds, keyed by its path: an entry with no file
  is `catalog.file-missing`, a file with no entry `catalog.entry-missing`, and no catalog at all
  `catalog.absent`. Every entry but the manifest's carries a sha256 (`catalog.sha256-missing`),
  which is verified (`sha256.mismatch`).

The 3.x schema owns the manifest's members: the catalog is `catalog` and the level `level`, as the
served 3.0.0 schema names them. A 3.x manifest that states only the 2.x member `file_entries` has no
catalog. These two names are read here and nowhere else.
"""

from __future__ import annotations

import hashlib
import re

from .archive import MANIFEST_PATH_3, Bundle, unsafe
from .report import Report
from .schema_subset import escape

#: The manifest members these checks read (see the module docstring).
CATALOG = "catalog"
LEVEL = "level"
#: The levels, coarsest last (§8).
LEVELS = ("Max", "Med", "Min")

#: The seven subject folders (§1).
FOLDERS = ("Elevation", "Imagery", "Landcover", "Materials", "Basemap", "Records", "Site")
ROOT_FILES = ("Readme.md", MANIFEST_PATH_3)

#: The file table of 3.0 (§6): sixty-nine files.
_RECORDS = ("Parcels", "Zoning", "Floodplain", "Wetlands", "Soils", "Heritage", "Conservation",
            "Habitat", "Hydrography", "Contamination")
TREE = frozenset(
    list(ROOT_FILES)
    + [f"Elevation/{n}" for n in (
        "Elevation.tif", "Elevation.png", "Terrain.glb", "Terrain.obj", "Terrain.mtl",
        "Terrain.dxf", "Terrain.landxml", "Slope.tif", "Slope.gpkg", "Slope.dxf", "Aspect.tif",
        "Hillshade.tif", "Contours.dxf")]
    + ["Imagery/Imagery.tif", "Imagery/Imagery.png"]
    + [f"Landcover/{n}" for n in (
        "Landcover.tif", "Landcover.gpkg", "Landcover.dxf", "Canopy.tif", "Trees.gpkg",
        "Trees.csv")]
    + [f"Materials/{n}.png" for n in (
        "Water", "Grass", "Forest", "Dirt", "Rock", "Sand", "Snow", "Built")]
    + [f"Basemap/{n}" for n in (
        "Basemap.tif", "Basemap.png", "Basemap.usdz", "Buildings.glb", "Roads.csv")]
    + [f"Basemap/{n}.{e}" for n in ("Buildings", "Roads", "Pavement", "Water", "Landuse")
       for e in ("gpkg", "dxf")]
    + [f"Records/{n}.{e}" for n in _RECORDS for e in ("gpkg", "dxf")]
    + ["Site/Site.usdz", "Site/Site.ifc", "Site/Site.dxf"])

#: The 2.x files 3.0 removed or renamed (§12), by their 2.x path. Matched regardless of case, so
#: `LandCover/WaterMask.tif` is caught under the 3.x folder `Landcover/` too.
REMOVED = frozenset(path.casefold() for path in (
    "README.md", "Metadata/manifest.json", "Metadata/stac-item.json",
    "Elevation/DEM.tif", "Elevation/Heightmap.png", "Elevation/Surface.dxf",
    "Elevation/Surface.landxml", "Elevation/SurfacePoints.csv", "Elevation/SteepSlopes.gpkg",
    "Elevation/Slope.png", "Elevation/Aspect.png", "Elevation/Hillshade.png",
    "Elevation/Terrain.jpg",
    "Imagery/Imagery.pgw", "Imagery/Imagery.prj", "Imagery/NDVI.tif", "Imagery/NDVI.png",
    "Imagery/Imagery-Engine.png",
    "LandCover/LandCover.png", "LandCover/LandCoverAreas.gpkg", "LandCover/LandCoverAreas.dxf",
    "LandCover/CanopyHeight.tif", "LandCover/CanopyHeight.png", "LandCover/WaterMask.tif",
    "LandCover/WaterMask.png", "LandCover/MaterialWeights.tif",
    "LandCover/MaterialWeights-1To4.png", "LandCover/MaterialWeights-5To8.png",
    "LandCover/TreePoints.csv", "LandCover/TreePoints-Engine.csv",
    "Features/StylizedMap.tif", "Features/StylizedMap.png", "Features/Buildings.glb",
    "Features/BuildingFootprints.gpkg", "Features/BuildingFootprints.dxf",
    "Features/RoadsDraped.gpkg", "Features/RoadsDraped.dxf", "Features/Roads.gpkg",
    "Features/Roads.dxf", "Features/RoadsDraped-Engine.gpkg", "Features/RoadSurfaces.gpkg",
    "Features/RoadSurfaces.dxf", "Features/Water.gpkg", "Features/Water.dxf",
    "Features/LandUse.gpkg", "Features/LandUse.dxf",
    "Records/FloodZones.gpkg", "Records/FloodZones.dxf", "Records/HistoricPlaces.gpkg",
    "Records/HistoricPlaces.dxf", "Records/ProtectedAreas.gpkg", "Records/ProtectedAreas.dxf",
    "Records/CriticalHabitat.gpkg", "Records/CriticalHabitat.dxf",
    "Site/Model.ifc", "Site/Context.usdz", "Site/Features.usdz", "Site/Brief.pdf",
    "Drawings/Linework.dxf", "Drawings/SitePlan-Small.pdf", "Drawings/SitePlan-Large.pdf",
    "Drawings/SitePlan-Small.svg", "Drawings/SitePlan-Large.svg",
))
#: Formats no 3.x file is in (§12): the Shapefile set, world and projection files, the plan sheets
#: and the OBJ's own texture. A file in one is removed wherever it sits.
REMOVED_FORMATS = frozenset({"shp", "shx", "dbf", "cpg", "sbn", "sbx", "prj", "pgw", "tfw",
                             "jgw", "pngw", "wld", "jpg", "jpeg", "pdf", "svg"})

#: The words of the tree, for spotting a suffix after one (`TerrainMed`).
_WORDS = sorted({p.rsplit("/", 1)[-1].split(".", 1)[0] for p in TREE} | set(FOLDERS),
                key=len, reverse=True)
_ASCII = re.compile(r"^[A-Za-z0-9._-]+$")
_WORD = re.compile(r"^[A-Z][a-z]+$")
_EXTENSION = re.compile(r"^[a-z]+$")


_SEMVER = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")


def major(version: object) -> int | None:
    """The major of a semver `version` string, or None."""
    found = _SEMVER.match(version) if isinstance(version, str) else None
    return int(found.group(1)) if found else None


def minor(version: object) -> int | None:
    """The minor of a semver `version` string, or None."""
    found = _SEMVER.match(version) if isinstance(version, str) else None
    return int(found.group(2)) if found else None


def check_tree(bundle: Bundle, report: Report, version: object = "3.0.0") -> None:
    """The tree and its names (`tree.*`, `name.*`), for a bundle that declares `version`."""
    report.ran.append("names")
    # The table is 3.0's: a later minor may list a file it does not (§3).
    unknown = "error" if minor(version) == 0 else "warning"
    folded = {path.casefold(): path for path in TREE}
    unknown_folders: set[str] = set()
    for path in bundle.files:
        parts = path.split("/")
        if path in TREE:
            continue
        if not _ASCII.match(path.replace("/", "")):
            report.add("names", "name.charset", "error",
                       f"{path!r} is not ASCII from [A-Za-z0-9._-]", file=path)
            continue
        if len(parts) > 2:
            report.add("names", "tree.subfolder", "error",
                       f"{path} sits in a subfolder; every file sits directly in one of the seven "
                       f"subject folders ({', '.join(FOLDERS)})", file=path)
            continue
        if path.casefold() in folded:
            report.add("names", "name.case-mismatch", "error",
                       f"{path} is {folded[path.casefold()]} in another case: a reader matches "
                       "regardless of case, and a writer uses the exact case", file=path)
            continue
        if path.casefold() in REMOVED or _extension(path) in REMOVED_FORMATS:
            report.add("names", "tree.file-removed", "error",
                       f"{path} is a 2.x file that 3.0 removed; format.md §12 names the 3.x file "
                       "it comes from and the step that derives it", file=path)
            continue
        if len(parts) == 1:
            report.add("names", "tree.root-file", "error",
                       f"{path} sits at the root, which holds only {' and '.join(ROOT_FILES)}",
                       file=path)
            continue
        folder, name = parts
        if folder not in FOLDERS:
            if folder.casefold() in {f.casefold() for f in FOLDERS}:
                report.add("names", "name.case-mismatch", "error",
                           f"{folder}/ is a subject folder in another case", file=path)
                continue
            problem = _name_problem(folder, folder=True)
            if problem is not None:
                code, why = problem
                report.add("names", code, "error", f"{folder}/: {why}", file=path)
                continue
            unknown_folders.add(folder)
        problem = _name_problem(name)
        if problem is not None:
            code, why = problem
            report.add("names", code, "error", f"{path}: {why}", file=path)
        elif folder in FOLDERS:
            report.add("names", "tree.file-unknown", unknown,
                       f"{path} is not in the 3.0 file table; only a later 3.x minor may add a "
                       "file, and a reader finds it by its catalog entry", file=path)
    for folder in sorted(unknown_folders):
        report.add("names", "tree.folder-unknown", unknown,
                   f"{folder}/ is not one of the seven subject folders ({', '.join(FOLDERS)})",
                   file=folder)
    _check_case_collisions(bundle, report)


def _extension(path: str) -> str:
    name = path.rsplit("/", 1)[-1]
    return name.rsplit(".", 1)[-1].lower() if "." in name else ""


def _name_problem(name: str, folder: bool = False) -> tuple[str, str] | None:
    """`(code, why)` for the first naming rule `name` breaks (§2), or None."""
    stem, dot, extension = name.partition(".")
    if any(c.isdigit() for c in stem):
        return "name.number", "a name carries no number"
    if "-" in stem:
        return "name.hyphen", "a name carries no hyphen"
    if "_" in stem or (not folder and "." in extension):
        return "name.suffix", ("a name carries no suffix: no level, unit, frame or CRS, joined by "
                               "an underscore or a dot")
    for word in _WORDS:
        if stem.startswith(word) and stem[len(word):][:1].isupper():
            return "name.suffix", (f"{word} carries the suffix {stem[len(word):]}; no name carries "
                                   "a level, a unit, a frame or a CRS")
    if not _WORD.match(stem):
        return "name.word", ("a name is one word, first letter capitalised, with no acronym or "
                             "second capital")
    if folder:
        return None if not dot else ("name.word", "a folder name has no extension")
    if not dot or not _EXTENSION.match(extension):
        return "name.extension", "the extension is the format, in lower case"
    return None


def _check_case_collisions(bundle: Bundle, report: Report) -> None:
    groups: dict[str, list[str]] = {}
    for entry in sorted(bundle.folders) + bundle.files:
        groups.setdefault(entry.casefold(), []).append(entry)
    for entries in groups.values():
        if len(entries) > 1:
            names = sorted(entries)
            report.add("names", "name.case-collision", "error",
                       f"{' and '.join(names)} differ only by case, so a case-insensitive file "
                       "system extracts them as one", file=names[0])


def check_level(manifest: dict, report: Report) -> None:
    """The download's one level (`level.*`)."""
    report.ran.append("level")
    where = f"/{LEVEL}"
    if LEVEL not in manifest:
        report.add("level", "level.missing", "error",
                   "the manifest states no level; a download holds one level, and the manifest "
                   "states it once", pointer=where)
        return
    level = manifest[LEVEL]
    if isinstance(level, list) and len(level) > 1:
        report.add("level", "level.several", "error",
                   f"the manifest states {len(level)} levels ({', '.join(map(str, level))}); a "
                   "download holds exactly one level of each file", pointer=where)
    elif level not in LEVELS:
        report.add("level", "level.unknown", "error",
                   f"the level {level!r} is not one of {', '.join(LEVELS)}", pointer=where)


def check_catalog(manifest: dict, bundle: Bundle, report: Report) -> None:
    """One catalog entry per file, each with a sha256 that matches (`catalog.*`, `sha256.*`)."""
    report.ran.append("catalog")
    report.ran.append("sha256")
    raw = manifest.get(CATALOG)
    if not isinstance(raw, dict):
        report.add("catalog", "catalog.absent", "error",
                   "the manifest has no catalog, so no file of the download is listed",
                   pointer=f"/{CATALOG}")
        return
    files = {path.casefold(): path for path in bundle.files}
    named: set[str] = set()
    for path, entry in sorted(raw.items()):
        where = f"/{CATALOG}/{escape(path)}"
        if unsafe(path):
            report.add("pointers", "pointer.unsafe", "error",
                       f"{path!r} is not a plain relative path inside the bundle; a reader "
                       "resolves every path inside its extraction root and refuses one that "
                       "escapes it", pointer=where, file=path)
            continue
        actual = path if bundle.has_file(path) else files.get(path.casefold())
        if actual is None:
            report.add("catalog", "catalog.file-missing", "error",
                       f"the catalog lists {path}, but the download does not hold it; a file it "
                       "does not hold is listed as not held, with its reason", pointer=where,
                       file=path)
            continue
        named.add(actual)
        if actual != path:
            report.add("names", "name.case-mismatch", "error",
                       f"the catalog lists {path}, but the bundle holds {actual}: a reader matches "
                       "the two regardless of case, and a writer uses the exact case",
                       pointer=where, file=actual)
        if actual == bundle.manifest_path:
            continue  # The manifest cannot carry its own hash (§6).
        stated = entry.get("sha256") if isinstance(entry, dict) else None
        if not isinstance(stated, str):
            report.hashes["unknown"] += 1
            report.add("catalog", "catalog.sha256-missing", "error",
                       f"the entry for {path} states no sha256; every entry but the manifest's "
                       "carries one", pointer=where, file=path)
            continue
        digest = hashlib.sha256()
        with bundle.open(actual) as stream:
            for chunk in iter(lambda: stream.read(1 << 20), b""):
                digest.update(chunk)
        if digest.hexdigest() == stated.lower():
            report.hashes["verified"] += 1
        else:
            report.hashes["mismatched"] += 1
            report.add("sha256", "sha256.mismatch", "error",
                       f"{path} has sha256 {digest.hexdigest()}, but the catalog states {stated}; "
                       "a reader fails closed on it", pointer=f"{where}/sha256", file=path)
    for path in bundle.files:
        if path not in named:
            report.add("catalog", "catalog.entry-missing", "error",
                       f"{path} is in the download but not in the catalog; the catalog has one "
                       "entry per file the download holds", file=path)
