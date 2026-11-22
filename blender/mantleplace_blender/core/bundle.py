# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC

from __future__ import annotations

import json
import re
from dataclasses import dataclass, field

SUPPORTED_MAJOR = 2

CONTEXT_KEY = "context_usdz"
FEATURES_KEY = "features_usdz"
FIXED_NAMES = {CONTEXT_KEY: "Site/Context.usdz", FEATURES_KEY: "Site/Features.usdz"}

ANCHOR_KEY = "mantleplace_site_origin"

ORIGIN_UNITS = {"m": 1.0, "ft": 0.3048, "usft": 1200 / 3937}
_AXIS_ORDERS = frozenset({"east-north", "north-east"})
_COORDINATE_MODES = frozenset({"absolute", "offset"})
_DIRECTIONS = frozenset({"east", "west", "north", "south", "up", "down"})
_BLENDER_AXES = {"x": "east", "y": "north", "z": "up"}
_SEMVER = re.compile(r"^(\d+)\.(\d+)\.(\d+)(?:[-+].*)?$")


class BundleRefused(Exception):
    pass


@dataclass(frozen=True)
class Georeference:
    crs: dict
    x: float
    y: float
    z: float
    unit: str
    z_reference: str
    latitude: float
    longitude: float
    ellipsoidal_height_m: float
    datum_height_m: float
    vertical_datum: str

    @property
    def crs_code(self) -> str | None:
        code = self.crs.get("code")
        return code if isinstance(code, str) else None


@dataclass(frozen=True)
class Plan:
    version: str
    context_path: str
    features_path: str | None
    georeference: Georeference | None
    digests: dict = field(default_factory=dict)
    notes: tuple = ()


def read_major(manifest: dict) -> int:
    version = manifest.get("version") if isinstance(manifest, dict) else None
    if not isinstance(version, str):
        era = "an integer version from before MPB semver" if isinstance(version, int) else "no readable version"
        raise BundleRefused(
            f"This bundle has {era}; this plugin reads MPB {SUPPORTED_MAJOR}.x bundles only.")
    match = _SEMVER.match(version)
    if match is None:
        raise BundleRefused(
            f"This bundle's version {version!r} is not an MPB version; this plugin reads MPB "
            f"{SUPPORTED_MAJOR}.x bundles only.")
    major = int(match.group(1))
    if major > SUPPORTED_MAJOR:
        raise BundleRefused(
            f"This bundle is MPB {version}, and this plugin reads MPB {SUPPORTED_MAJOR}.x. "
            "Update the plugin to import it.")
    if major < SUPPORTED_MAJOR:
        raise BundleRefused(
            f"This bundle is MPB {version}, which carries no Context file; this plugin reads MPB "
            f"{SUPPORTED_MAJOR}.x. Download the bundle again from your vault to get a "
            f"{SUPPORTED_MAJOR}.x copy.")
    return major


def plan_import(manifest: dict, include_features: bool = True) -> Plan:
    read_major(manifest)
    notes: list[str] = []
    layout = _object(manifest.get("layout"))
    listed = set(_object(manifest.get("file_entries"))) | set(_object(manifest.get("files")))

    context = _locate(layout, CONTEXT_KEY, listed, notes)
    if context is None:
        raise BundleRefused(
            "This bundle carries no Context file (layout.context_usdz), so there is no site to "
            "import.")

    features = None
    if include_features:
        features = _locate(layout, FEATURES_KEY, listed, notes)
        if features is None:
            notes.append("This bundle carries no Features file, so the site comes in without its "
                         "vector features.")

    entries = _object(manifest.get("file_entries"))
    context_frame, frame_problem = _frame(entries, context)
    if features is not None:
        features_frame, problem = _frame(entries, features)
        if problem is None and context_frame is not None and not _same_crs(
                features_frame["horizontal_crs"], context_frame["horizontal_crs"]):
            problem = (f"{features} is in {_crs_text(features_frame['horizontal_crs'])}, not the "
                       f"Context file's {_crs_text(context_frame['horizontal_crs'])}")
        if problem is not None:
            notes.append(f"Features left out: {problem}; it would not line up with the site.")
            features = None

    georeference = None
    if frame_problem is not None:
        notes.append(f"Site origin not applied: {frame_problem}.")
    else:
        georeference, problem = _georeference(manifest, context, context_frame)
        if problem is not None:
            notes.append(f"Site origin not applied: {problem}.")

    files = _object(manifest.get("files"))
    digests = {}
    for path in (context, features):
        if path is not None:
            digest = _object(files.get(path)).get("sha256")
            digests[path] = digest.lower() if isinstance(digest, str) else None

    return Plan(str(manifest["version"]), context, features, georeference, digests, tuple(notes))


def scene_properties(georeference: Georeference) -> dict:
    g = georeference
    properties: dict = {
        ANCHOR_KEY: {
            "crs": g.crs_code or _crs_text(g.crs),
            "x": g.x,
            "y": g.y,
            "z": g.z,
            "unit": g.unit,
            "z_reference": g.z_reference,
            "latitude": g.latitude,
            "longitude": g.longitude,
            "ellipsoidal_height_m": g.ellipsoidal_height_m,
            "datum_height_m": g.datum_height_m,
            "vertical_datum": g.vertical_datum,
        }
    }
    if g.unit == "m" and g.crs_code is not None:
        properties.update({"SRID": g.crs_code, "crs x": g.x, "crs y": g.y,
                           "latitude": g.latitude, "longitude": g.longitude})
    return properties


def wrapper_stage(context_file: str, features_file: str | None) -> str:
    layers = [features_file, context_file] if features_file else [context_file]
    listed = ",\n".join(f"        {_asset(path)}" for path in layers)
    return ("#usda 1.0\n"
            "(\n"
            '    defaultPrim = "Site"\n'
            "    metersPerUnit = 1\n"
            '    upAxis = "Z"\n'
            "    subLayers = [\n"
            f"{listed}\n"
            "    ]\n"
            ")\n")


def _asset(path: str) -> str:
    path = path.replace("\\", "/")
    return f"@@@{path}@@@" if "@" in path else f"@{path}@"


def _object(value: object) -> dict:
    return value if isinstance(value, dict) else {}


def _locate(layout: dict, key: str, listed: set, notes: list) -> str | None:
    if key in layout:
        value = layout[key]
        if value is None:
            return None
        if not isinstance(value, str) or not value:
            raise BundleRefused(f"layout.{key} is not a path.")
        return _safe(value, key)
    fixed = FIXED_NAMES[key]
    if fixed in listed:
        notes.append(f"layout.{key} is missing from this manifest; {fixed} was used because the "
                     "manifest lists it.")
        return fixed
    return None


def _safe(path: str, key: str) -> str:
    parts = path.replace("\\", "/").split("/")
    if (path.startswith(("/", "\\")) or re.match(r"^[A-Za-z]:", path) or ".." in parts
            or "" in parts):
        raise BundleRefused(f"layout.{key} points outside the bundle ({path!r}); refusing it.")
    return path


def _frame(entries: dict, path: str) -> tuple[dict | None, str | None]:
    raw = _object(entries.get(path)).get("frame")
    if not isinstance(raw, dict):
        return None, f"{path} states no frame in file_entries, so nothing says where it sits"
    for key in ("horizontal_crs", "unit_to_metres", "axis_order", "axis_mapping", "coordinate_mode"):
        if key not in raw:
            return None, f"{path}'s frame has no {key}"
    unit = raw["unit_to_metres"]
    if isinstance(unit, bool) or not isinstance(unit, (int, float)) or unit <= 0:
        return None, f"{path}'s frame states unit_to_metres {unit!r}, which is not a length"
    if raw["axis_order"] not in _AXIS_ORDERS:
        return None, f"{path}'s frame states an unknown axis_order {raw['axis_order']!r}"
    if raw["coordinate_mode"] not in _COORDINATE_MODES:
        return None, f"{path}'s frame states an unknown coordinate_mode {raw['coordinate_mode']!r}"
    mapping = _object(raw["axis_mapping"])
    if any(mapping.get(axis) not in _DIRECTIONS for axis in ("x", "y", "z")):
        return None, f"{path}'s frame states an unknown axis_mapping {raw['axis_mapping']!r}"
    if {axis: mapping[axis] for axis in ("x", "y", "z")} != _BLENDER_AXES:
        return None, (f"{path}'s axis_mapping is {mapping!r}, not x east, y north, z up, and this "
                      "plugin turns no axes")
    if _crs_key(raw["horizontal_crs"]) is None:
        return None, f"{path}'s frame names no readable horizontal_crs"
    return raw, None


def _georeference(manifest: dict, path: str, frame: dict) -> tuple[Georeference | None, str | None]:
    if frame["coordinate_mode"] == "absolute":
        return None, (f"{path} states absolute coordinates, so its own CRS places it and no site "
                      "origin applies")
    origin = _object(manifest.get("location")).get("site_origin")
    if not isinstance(origin, dict):
        return None, "the manifest carries no location.site_origin to measure the offsets from"
    projected = origin.get("projected")
    if not isinstance(projected, dict):
        return None, ("location.site_origin has no projected point, and placing the site from "
                      "latitude and longitude would take a reprojection, which this plugin never does")
    if not all(_number(projected.get(k)) for k in ("x", "y", "z")):
        return None, "location.site_origin.projected has no numeric x, y and z"
    if projected.get("unit") not in ORIGIN_UNITS:
        return None, (f"location.site_origin.projected.unit {projected.get('unit')!r} is not one "
                      f"of {', '.join(ORIGIN_UNITS)}")
    if not isinstance(projected.get("z_reference"), str):
        return None, "location.site_origin.projected names no z_reference"
    if not _same_crs(projected.get("crs"), frame["horizontal_crs"]):
        return None, (f"the site origin is in {_crs_text(projected.get('crs'))} and {path} is in "
                      f"{_crs_text(frame['horizontal_crs'])}; relating them would take a "
                      "reprojection, which this plugin never does")
    for key in ("latitude", "longitude", "ellipsoidal_height_m", "datum_height_m"):
        if not _number(origin.get(key)):
            return None, f"location.site_origin.{key} is not a number"
    return Georeference(
        crs=dict(projected["crs"]), x=float(projected["x"]), y=float(projected["y"]),
        z=float(projected["z"]), unit=projected["unit"], z_reference=projected["z_reference"],
        latitude=float(origin["latitude"]), longitude=float(origin["longitude"]),
        ellipsoidal_height_m=float(origin["ellipsoidal_height_m"]),
        datum_height_m=float(origin["datum_height_m"]),
        vertical_datum=str(origin.get("vertical_datum", ""))), None


def _number(value: object) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def _crs_key(crs: object) -> object:
    if not isinstance(crs, dict):
        return None
    if isinstance(crs.get("code"), str):
        return ("code", crs["code"].strip().upper())
    if isinstance(crs.get("projjson"), dict):
        return ("projjson", json.dumps(crs["projjson"], sort_keys=True))
    if isinstance(crs.get("wkt2"), str):
        return ("wkt2", " ".join(crs["wkt2"].split()))
    return None


def _same_crs(a: object, b: object) -> bool:
    key = _crs_key(a)
    return key is not None and key == _crs_key(b)


def _crs_text(crs: object) -> str:
    if isinstance(crs, dict) and isinstance(crs.get("code"), str):
        return crs["code"]
    return "a CRS stated without a code" if isinstance(crs, dict) else "no stated CRS"
