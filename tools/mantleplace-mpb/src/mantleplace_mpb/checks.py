# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""The checks past the schema: the pointer rule, sha256, file frames and ODbL roles.

Each reads the manifest defensively (the schema check runs first, but a manifest that fails it
is still checked here) and records findings on the report. None of them derives a placement
value: a frame is checked for being whole and self-consistent, never reprojected or recomputed.
"""

from __future__ import annotations

import hashlib
import re
from collections.abc import Iterator

from .archive import MANIFEST_PATH, Bundle, unsafe
from .report import Report
from .schema_subset import escape

#: Top-level blocks whose `path` values are not pointers into this archive: `completeness`
#: records the paths a check looked for and did NOT find; `files`, `view` and `bundle` describe
#: storage and the sidecar; `platform` is outside the contract; the source pointers name
#: upstream records. `layout` and `file_entries` are read on their own terms below.
_NOT_POINTER_BLOCKS = frozenset({"completeness", "files", "view", "bundle", "platform",
                                 "source_pointers", "source_pointers_unavailable", "layout",
                                 "file_entries"})

#: Extensions by what the bytes hold. A raster, a mesh or a rendered sheet is rasterized,
#: meshed, extruded or baked output, which is what a Produced Work is; a vector or tabular data
#: file keeps its features recoverable, so it is never one.
RASTER = frozenset({"tif", "tiff", "png", "jpg", "jpeg", "webp", "jp2"})
MESH = frozenset({"glb", "gltf", "obj", "usd", "usda", "usdc", "usdz", "fbx", "ifc", "stl", "ply",
                  "terrain"})
RENDERED = frozenset({"pdf", "svg"})
SIDECARS = frozenset({"mtl", "pgw", "tfw", "jgw", "pngw", "wld", "prj", "ovr", "aux"})
DATA = frozenset({"gpkg", "shp", "shx", "dbf", "cpg", "sbn", "sbx", "geojson", "fgb", "csv",
                  "tsv", "dxf", "dwg", "kml", "kmz", "gml", "landxml", "parquet", "sqlite"})
PRODUCED_WORK_FORMATS = RASTER | MESH | RENDERED | SIDECARS
#: Files that carry coordinates, so a file entry for one is expected to state its frame.
GEOSPATIAL = RASTER | MESH | (DATA - {"shx", "dbf", "cpg", "sbn", "sbx"})

#: A frame written whole states these; a raster's also states how its pixels sit.
FRAME_CORE = ("horizontal_crs", "unit_to_metres", "axis_order", "axis_mapping", "coordinate_mode")
FRAME_RASTER = ("pixel_convention", "row_order")
_AXIS_LINE = {"east": "east-west", "west": "east-west", "north": "north-south",
              "south": "north-south", "up": "up-down", "down": "up-down"}
_ODBL = re.compile(r"[Oo][Dd][Bb][Ll]")


def extension(path: str) -> str:
    name = path.rstrip("/").rsplit("/", 1)[-1]
    return name.rsplit(".", 1)[-1].lower() if "." in name else ""


def _walk_paths(node: object, where: str) -> Iterator[tuple[str, dict]]:
    """Every object below `node` that carries a string `path`, with its JSON Pointer."""
    if isinstance(node, dict):
        if isinstance(node.get("path"), str):
            yield where, node
        for key, value in node.items():
            yield from _walk_paths(value, f"{where}/{escape(key)}")
    elif isinstance(node, list):
        for i, value in enumerate(node):
            yield from _walk_paths(value, f"{where}/{i}")


def pointers(manifest: dict) -> list[tuple[str, str]]:
    """(JSON Pointer, archive path) for every pointer value the manifest publishes."""
    found: list[tuple[str, str]] = []
    layout = manifest.get("layout")
    if isinstance(layout, dict):
        # A string value is a pointer. A list is not: the one the schema publishes,
        # `mesh_formats`, lists format names (`glb`, `obj`), and each format's file has its own pointer.
        found.extend((f"/layout/{escape(key)}", value) for key, value in layout.items()
                     if isinstance(value, str))
    for key, value in manifest.items():
        if key not in _NOT_POINTER_BLOCKS:
            found.extend((f"{where}/path", obj["path"])
                         for where, obj in _walk_paths(value, f"/{escape(key)}"))
    entries = manifest.get("file_entries")
    if isinstance(entries, dict):
        found.extend((f"/file_entries/{escape(path)}", path) for path in entries)
    return found


def _outside_view(manifest: dict, path: str) -> bool:
    """Whether a bundle stored as parts says another part holds `path`, so this archive may not."""
    view, files = manifest.get("view"), manifest.get("files")
    if not isinstance(view, dict) or not isinstance(files, dict):
        return False
    parts = view.get("parts")
    entry = files.get(path)
    return isinstance(parts, list) and isinstance(entry, dict) and entry.get("part") not in parts


def check_pointers(manifest: dict, bundle: Bundle, report: Report, fold_case: bool = False) -> None:
    """Every pointer names a file or folder in the bundle.

    With `fold_case` (a 2.0 bundle), a pointer is matched to an entry regardless of case, as a
    2.0 reader matches it (format §10.3, rule 6), and a match only regardless of case is the
    writer's error, `name.case-mismatch`, rather than a missing file.
    """
    report.ran.append("pointers")
    layout = manifest.get("layout")
    if isinstance(layout, dict) and isinstance(layout.get("manifest"), str) \
            and layout["manifest"] != MANIFEST_PATH:
        report.add("pointers", "pointer.manifest-self", "error",
                   f"layout.manifest names {layout['manifest']!r}, but the manifest was read from "
                   f"{MANIFEST_PATH}", pointer="/layout/manifest", file=layout["manifest"])
    for where, path in pointers(manifest):
        if unsafe(path):
            report.add("pointers", "pointer.unsafe", "error",
                       f"{path!r} is not a plain relative path inside the bundle; a reader resolves "
                       "every pointer inside its extraction root and refuses one that escapes it",
                       pointer=where, file=path)
        elif bundle.has_file(path) or bundle.has_folder(path):
            continue
        elif fold_case and (actual := bundle.resolve(path, fold_case=True)) is not None:
            report.add("names", "name.case-mismatch", "error",
                       f"the manifest names {path}, but the bundle holds {actual}: a reader "
                       "matches the two regardless of case, and a writer uses the exact case",
                       pointer=where, file=actual)
        elif _outside_view(manifest, path):
            report.add("pointers", "pointer.outside-view", "info",
                       f"{path} is held by a part this archive does not carry", pointer=where,
                       file=path)
        else:
            report.add("pointers", "pointer.missing", "error",
                       f"{path} is named by the manifest but is not in the bundle", pointer=where,
                       file=path)


def check_hashes(manifest: dict, bundle: Bundle, report: Report, fold_case: bool = False) -> None:
    """Verify every sha256 the manifest states for a file this bundle holds.

    A file named only by a pointer with no hash of its own takes the hash the manifest's format
    tables state for the same path. A file with no hash anywhere is counted as unknown: a missing
    optional hash is not a corrupt file, and the report says which it was rather than claiming a
    verification it did not make.
    """
    report.ran.append("sha256")
    stated: dict[str, set[str]] = {}
    for key, value in manifest.items():
        if key in _NOT_POINTER_BLOCKS:
            continue
        for _, obj in _walk_paths(value, f"/{escape(key)}"):
            if isinstance(obj.get("sha256"), str):
                stated.setdefault(obj["path"], set()).add(obj["sha256"].lower())
    named = {path for _, path in pointers(manifest)} - {"README.md", MANIFEST_PATH}
    for path in sorted(named | set(stated)):
        actual = None if unsafe(path) else bundle.resolve(path, fold_case=fold_case)
        if actual is None or not bundle.has_file(actual):
            continue
        hashes = stated.get(path)
        if not hashes:
            report.hashes["unknown"] += 1
            continue
        if len(hashes) > 1:
            report.add("sha256", "sha256.conflict", "error",
                       f"the manifest states {len(hashes)} different sha256 values for {path}",
                       file=path)
            continue
        digest = hashlib.sha256()
        with bundle.open(actual) as stream:
            for chunk in iter(lambda: stream.read(1 << 20), b""):
                digest.update(chunk)
        expected = next(iter(hashes))
        if digest.hexdigest() == expected:
            report.hashes["verified"] += 1
        else:
            report.hashes["mismatched"] += 1
            report.add("sha256", "sha256.mismatch", "error",
                       f"{path} has sha256 {digest.hexdigest()}, but the manifest states {expected}; "
                       "a reader fails closed on it", file=path)


def _entries(manifest: dict) -> dict[str, dict]:
    entries = manifest.get("file_entries")
    if not isinstance(entries, dict):
        return {}
    return {path: entry for path, entry in entries.items() if isinstance(entry, dict)}


def check_frames(manifest: dict, report: Report) -> None:
    report.ran.append("frames")
    location = manifest.get("location")
    has_origin = isinstance(location, dict) and isinstance(location.get("site_origin"), dict)
    for path, entry in sorted(_entries(manifest).items()):
        ext = extension(path)
        where = f"/file_entries/{escape(path)}/frame"
        frame = entry.get("frame")
        if isinstance(frame, dict) and frame.get("placed") is False:
            continue  # 2.0: a file with no spatial frame says so, and states nothing else.
        if not isinstance(frame, dict):
            if "frame" not in entry and ext in GEOSPATIAL:
                report.add("frames", "frame.absent", "warning",
                           f"the entry for {path} states no frame, so no reader can place the file "
                           "from the neutral part of the manifest", pointer=where[: -len("/frame")],
                           file=path)
            continue
        missing = [k for k in FRAME_CORE if k not in frame]
        if ext in RASTER:
            missing += [k for k in FRAME_RASTER if k not in frame]
        if missing:
            report.add("frames", "frame.partial", "error",
                       f"the frame of {path} lacks {', '.join(missing)}; a frame is written whole "
                       "or not at all", pointer=where, file=path)
        if ext in MESH | DATA | RENDERED:
            stray = [k for k in FRAME_RASTER if k in frame]
            if stray:
                report.add("frames", "frame.raster-only-field", "error",
                           f"{path} is not a raster, but its frame states {', '.join(stray)}",
                           pointer=where, file=path)
        if "vertical_crs" not in frame:
            if "geoid_model" in frame:
                report.add("frames", "frame.geoid-without-vertical-crs", "error",
                           f"the frame of {path} names a geoid model but no vertical CRS for its "
                           "heights to be in", pointer=where, file=path)
            if "vertical_unit_to_metres" in frame:
                report.add("frames", "frame.vertical-unit-without-vertical-crs", "error",
                           f"the frame of {path} states a vertical unit but no vertical CRS",
                           pointer=where, file=path)
        if frame.get("coordinate_mode") == "offset" and not has_origin:
            report.add("frames", "frame.offset-without-site-origin", "error",
                       f"{path} holds offsets from the site origin, but the manifest carries no "
                       "location.site_origin to measure them from", pointer=where, file=path)
        mapping = frame.get("axis_mapping")
        if isinstance(mapping, dict):
            lines = [_AXIS_LINE.get(mapping.get(axis)) for axis in ("x", "y", "z")]
            if None not in lines and len(set(lines)) < 3:
                report.add("frames", "frame.axis-mapping-degenerate", "error",
                           f"the axis mapping of {path} puts two axes along one line "
                           f"({mapping.get('x')}, {mapping.get('y')}, {mapping.get('z')})",
                           pointer=f"{where}/axis_mapping", file=path)


def check_roles(manifest: dict, bundle: Bundle, report: Report, fold_case: bool = False) -> None:
    """Every file's ODbL role, against its format and its sources.

    A missing role reads as `database`, the stricter duty, so it is reported rather than
    refused while the field is optional.
    """
    report.ran.append("roles")
    raw = manifest.get("file_entries")
    if not isinstance(raw, dict):
        report.add("roles", "role.not-stated", "info",
                   "the manifest carries no file_entries, so no file states its ODbL role; every "
                   "file reads as database")
        return
    entries = _entries(manifest)
    for path, entry in sorted(entries.items()):
        where = f"/file_entries/{escape(path)}"
        ext = extension(path)
        role = entry.get("odbl_role")
        if "odbl_role" not in entry:
            report.add("roles", "role.absent", "warning",
                       f"the entry for {path} states no odbl_role; it reads as database",
                       pointer=where, file=path)
        if role == "produced-work":
            if ext in DATA:
                report.add("roles", "role.extension-mismatch", "error",
                           f"{path} is a .{ext} file, which keeps its features recoverable; it "
                           "cannot be a Produced Work", pointer=f"{where}/odbl_role", file=path)
            elif ext not in PRODUCED_WORK_FORMATS:
                report.add("roles", "role.extension-unknown", "warning",
                           f"{path} claims produced-work, and .{ext or '(none)'} is not a format "
                           "this validator knows to be rasterized, meshed or rendered",
                           pointer=f"{where}/odbl_role", file=path)
        if role == "none":
            sources = entry.get("sources")
            odbl = [s.get("name", "?") for s in sources if isinstance(s, dict)
                    and isinstance(s.get("license"), str) and _ODBL.search(s["license"])] \
                if isinstance(sources, list) else []
            if odbl:
                report.add("roles", "role.sources-mismatch", "error",
                           f"{path} states odbl_role none, but lists an ODbL source "
                           f"({', '.join(odbl)})", pointer=f"{where}/odbl_role", file=path)
    def fold(value: str) -> str:
        return value.casefold() if fold_case else value

    stated = {fold(p) for p in raw if isinstance(p, str)}
    folders = [fold(p) for p in raw if isinstance(p, str) and p.endswith("/")]
    for path in bundle.files:
        if path in ("README.md", MANIFEST_PATH) or fold(path) in stated:
            continue
        if any(fold(path).startswith(folder) for folder in folders):
            continue
        report.add("roles", "role.unstated", "warning",
                   f"{path} has no file entry, so its ODbL role is unstated; it reads as database",
                   file=path)
