"""The reader leg of the conformance gate: the `reader` corpus group.

A `reader` case says what a correct reader finds in one synthetic bundle, as language-neutral data
(`corpus/README.md`, "The reader group"). The case file names the bundle by the `bundle` group's case
file that describes it, and holds the `result` a reader must reach on it: the version it read, where
it found the manifest, the download's level, each file it found with its family, its frame and the
count the catalog states, each file it skipped and why, and for a 3.x bundle the two frames its manifest
states once (format.md section 9.4). A reader in any language builds the bundle, reads it, and
compares.

This module holds every case file to that shape, and to its bundle. Two checks, both offline:

- **The shape.** The keys, their types and the closed vocabularies are exactly the ones the README
  documents, so a reader's runner is written once against one shape; the lists are sorted and hold
  no path twice, so a comparison is by path and nothing else.
- **The answer.** The result is re-derived from the bundle's own manifest, with the rules the format
  text states, and must equal what the case file says. The answer sheet cannot drift from the
  fixture it is the answer for: editing the fixture, or the answer, without the other fails here.

Stdlib only, like the rest of the gate.
"""

from __future__ import annotations

import json
import re
from pathlib import Path

READER_GROUP = "reader"

#: The one place each major keeps its manifest (compatibility.md section 1).
MANIFEST_PATH_3 = "Manifest.json"
MANIFEST_PATH_2 = "Metadata/manifest.json"
#: The newest major this corpus's readers read; a manifest of a later one is refused.
NEWEST_MAJOR = 3
#: The members a 3.x manifest states these in, as the served 3.0.0 schema names them: the catalog,
#: an object from a path to its entry; the level; the files it does not hold, a list of
#: `{"path", "reason"}`; and an entry's count, `{"kind", "value"}`, whose `value` a reader reports.
CATALOG = "catalog"
LEVEL = "level"
NOT_HELD = "not_held"
COUNT = "count"
#: Where a 2.x manifest keeps its catalog.
CATALOG_2 = "file_entries"

LEVELS = ("Max", "Med", "Min")
WHY = ("not-held", "file-missing")
#: `major-ahead`: a major above the newest. `unreadable`: a major the reader reads, whose manifest
#: states a unit, a datum, an axis order or a frame it does not understand (format.md section 4).
REFUSALS = ("major-ahead", "unreadable")
#: A family and the one frame its coordinates are in (format.md section 4).
FRAME_OF = {"georeferenced": "order", "host-ready": "site"}
_GEOREFERENCED = frozenset({"tif", "gpkg", "dxf", "landxml", "ifc"})
_HOST_READY = frozenset({"glb", "usdz", "obj", "png", "csv"})
#: What a 2.x file's own frame statement says about its family.
_FAMILY_OF_MODE = {"absolute": "georeferenced", "offset": "host-ready"}

_RESULT_KEYS = ("outcome", "major", "version", "manifestPath", "level", "refusal", "files", "skipped")
#: A 3.x read also states the frames, between the level's group and the refusal's: right after `level`.
_RESULT_KEYS_WITH_FRAMES = _RESULT_KEYS[:5] + ("frames",) + _RESULT_KEYS[5:]
FRAMES = "frames"
_FRAME_KEYS = ("georeferenced", "local")
#: The axis orders and the one local axes statement the served 3.0.0 schema names.
_AXIS_ORDERS = ("east-north", "north-east")
_LOCAL_AXES = {"x": "east", "y": "north", "z": "up"}
#: The factors to metres a reader understands: the metre, the international foot, the US survey foot.
_KNOWN_FACTORS = (1.0, 0.3048, 1200 / 3937)
_FACTOR_TOLERANCE = 1e-9
_CRS_CODE = re.compile(r"^[A-Z]+:[0-9A-Za-z.]+$")
_FILE_KEYS = ("path", "family", "frame", "count")
_SKIPPED_KEYS = ("path", "why", "reason")
_SEMVER = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")


def _is_int(value: object) -> bool:
    return isinstance(value, int) and not isinstance(value, bool)


def family_of_3(path: str) -> str | None:
    """The family a 3.x file's extension states (format.md section 4); None for a file in neither."""
    extension = path.rsplit(".", 1)[-1].lower() if "." in path.rsplit("/", 1)[-1] else ""
    if extension in _GEOREFERENCED:
        return "georeferenced"
    return "host-ready" if extension in _HOST_READY else None


def _major(version: object) -> int | None:
    found = _SEMVER.match(version) if isinstance(version, str) else None
    return int(found.group(1)) if found else None


def _entries(label: str, rows: object, keys: tuple[str, ...]) -> tuple[list[dict], list[str]]:
    """The rows of `label` when each is an object with exactly `keys`, sorted by a unique path."""
    if not isinstance(rows, list):
        return [], [f"`result.{label}` is not a list"]
    problems: list[str] = []
    good: list[dict] = []
    for position, row in enumerate(rows):
        if not isinstance(row, dict) or tuple(row) != keys:
            problems.append(f"`result.{label}[{position}]` is not an object with exactly the members "
                            f"{', '.join(keys)}, in that order")
        elif not isinstance(row["path"], str) or not row["path"] or row["path"].startswith("/") \
                or ".." in row["path"].split("/") or "\\" in row["path"]:
            problems.append(f"`result.{label}[{position}].path` is not a plain relative path")
        else:
            good.append(row)
    paths = [row["path"] for row in good]
    if paths != sorted(paths) or len(set(paths)) != len(paths):
        problems.append(f"`result.{label}` is not sorted by path with each path once")
    return good, problems


def check_result_shape(result: object) -> list[str]:
    """The problems with `result` as a shape, one line each; empty when it is the documented one."""
    if not isinstance(result, dict):
        return ["`result` is not an object"]
    keys = _RESULT_KEYS_WITH_FRAMES if FRAMES in result else _RESULT_KEYS
    if tuple(result) != keys:
        return [f"`result` has the members {', '.join(result) or 'none'}, expected exactly "
                f"{', '.join(_RESULT_KEYS)}, in that order, with `frames` after `level` for a 3.x read"]
    problems: list[str] = []
    outcome, major, version = result["outcome"], result["major"], result["version"]
    if outcome not in ("read", "refused"):
        problems.append("`result.outcome` is not `read` or `refused`")
    if not _is_int(major) or major < 1:
        problems.append("`result.major` is not a positive integer")
    if _major(version) is None:
        problems.append("`result.version` is not a semver string")
    elif _is_int(major) and _major(version) != major:
        problems.append(f"`result.major` {major} is not the major of `result.version` {version}")
    if _is_int(major) and result["manifestPath"] != (MANIFEST_PATH_3 if major >= 3 else MANIFEST_PATH_2):
        problems.append(f"`result.manifestPath` is not where major {major} keeps its manifest")
    if result["level"] is not None and result["level"] not in LEVELS:
        problems.append(f"`result.level` is not one of {', '.join(LEVELS)}, or null")
    if (outcome == "refused") != (result["refusal"] is not None):
        problems.append("`result.refusal` is set when, and only when, the outcome is `refused`")
    elif result["refusal"] is not None and result["refusal"] not in REFUSALS:
        problems.append(f"`result.refusal` is not one of {', '.join(REFUSALS)}")
    if result["refusal"] == "major-ahead" and _is_int(major) and major <= NEWEST_MAJOR:
        problems.append(f"a refusal is of a major above {NEWEST_MAJOR}, not {major}")
    if result["refusal"] == "unreadable" and major != NEWEST_MAJOR:
        problems.append(f"an `unreadable` refusal is of a bundle of major {NEWEST_MAJOR}, not {major}")
    stated = FRAMES in result
    if stated != (outcome == "read" and _is_int(major) and major >= 3):
        problems.append("`result.frames` is stated when, and only when, a 3.x bundle is read")
    elif stated and not (isinstance(result[FRAMES], dict) and tuple(result[FRAMES]) == _FRAME_KEYS):
        problems.append(f"`result.frames` is not an object with exactly the members {', '.join(_FRAME_KEYS)}")
    if outcome == "read" and _is_int(major) and major > NEWEST_MAJOR:
        problems.append(f"a bundle of major {major}, above {NEWEST_MAJOR}, is refused and never read")

    files, found = _entries("files", result["files"], _FILE_KEYS)
    skipped, missed = _entries("skipped", result["skipped"], _SKIPPED_KEYS)
    problems += found + missed
    for row in files:
        where = f"`result.files` entry {row['path']}"
        if row["family"] is not None and row["family"] not in tuple(FRAME_OF):
            problems.append(f"{where} has a family that is not {' or '.join(FRAME_OF)}, or null")
        elif row["frame"] != FRAME_OF.get(row["family"]):
            problems.append(f"{where} has the frame {row['frame']!r}, which is not the one its "
                            f"family {row['family']!r} is in")
        if row["count"] is not None and (not _is_int(row["count"]) or row["count"] < 0):
            problems.append(f"{where} has a count that is not a non-negative integer or null")
    for row in skipped:
        where = f"`result.skipped` entry {row['path']}"
        if row["why"] not in WHY:
            problems.append(f"{where} has a `why` that is not one of {', '.join(WHY)}")
        elif (row["why"] == "not-held") != (isinstance(row["reason"], str) and bool(row["reason"])):
            problems.append(f"{where} states a reason when, and only when, it is `not-held`")
        elif row["why"] == "file-missing" and row["reason"] is not None:
            problems.append(f"{where} is `file-missing` and states no reason")
    both = {r["path"] for r in files} & {r["path"] for r in skipped}
    if both:
        problems.append(f"`result` both finds and skips {', '.join(sorted(both))}")
    if outcome == "refused" and (result["files"] or result["skipped"] or result["level"] is not None):
        problems.append("a refusal finds no file, skips none and states no level: the reader stops "
                        "after the version")
    return problems


def _is_number(value: object) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def _unit_problem(unit: object, where: str) -> str | None:
    """Why a linear unit is not one a reader understands, or None: a name, and a known factor."""
    if not isinstance(unit, dict) or not isinstance(unit.get("name"), str) or not unit["name"]:
        return f"{where} states no unit name"
    factor = unit.get("to_metres")
    if not _is_number(factor) or factor <= 0:
        return f"{where} states no positive to_metres factor"
    if not any(abs(factor - known) <= known * _FACTOR_TOLERANCE for known in _KNOWN_FACTORS):
        return f"{where} states the unit {unit['name']} ({factor} metres), which is not understood"
    return None


def _crs_problem(crs: object, where: str) -> str | None:
    """Why a CRS names nothing a reader can apply, or None: a code, PROJJSON or WKT2, each well formed."""
    if not isinstance(crs, dict):
        return f"{where} is not an object"
    if "code" in crs and not (isinstance(crs["code"], str) and _CRS_CODE.match(crs["code"])):
        return f"{where} names the code {crs['code']!r}, which is not an authority code"
    if "projjson" in crs and not isinstance(crs["projjson"], dict):
        return f"{where}.projjson is not an object"
    if "wkt2" in crs and not (isinstance(crs["wkt2"], str) and crs["wkt2"]):
        return f"{where}.wkt2 is not a string"
    if not any(form in crs for form in ("code", "projjson", "wkt2")):
        return f"{where} names no code, PROJJSON object or WKT2 string"
    return None


def _number_problem(frame: object, name: str, where: str) -> str | None:
    value = frame.get(name) if isinstance(frame, dict) else None
    return None if _is_number(value) else f"{where}.{name} is not a number"


def frames_refusal(frames: object) -> str | None:
    """Why a reader of the served 3.0.0 frames (format.md section 9.4) refuses them, or None.

    A unit it does not understand, an axis order it does not know, a datum that names nothing, and a
    local unit that is not the metre are refused; so is a member the frames need and lack.
    """
    if not isinstance(frames, dict):
        return "the manifest states no frames"
    order, local = frames.get("georeferenced"), frames.get("local")
    if not isinstance(order, dict) or not isinstance(local, dict):
        return "the manifest states no frames.georeferenced or frames.local"
    origin = local.get("origin")
    projected = origin.get("projected") if isinstance(origin, dict) else None
    if not isinstance(origin, dict) or not isinstance(projected, dict):
        return "frames.local.origin states no projected origin"
    checks = [
        _crs_problem(order.get("horizontal_crs"), "frames.georeferenced.horizontal_crs"),
        _crs_problem(order.get("vertical_crs"), "frames.georeferenced.vertical_crs"),
        _unit_problem(order.get("horizontal_unit"), "frames.georeferenced.horizontal_unit"),
        _unit_problem(order.get("vertical_unit"), "frames.georeferenced.vertical_unit"),
        None if order.get("axis_order") in _AXIS_ORDERS
        else f"frames.georeferenced.axis_order states {order.get('axis_order')!r}, not an axis order",
        _number_problem(order, "grid_rotation_deg", "frames.georeferenced"),
        _crs_problem(local.get("vertical_crs"), "frames.local.vertical_crs"),
        _crs_problem(projected.get("crs"), "frames.local.origin.projected.crs"),
        _unit_problem(projected.get("unit"), "frames.local.origin.projected.unit"),
        _unit_problem(local.get("unit"), "frames.local.unit"),
        None if local.get("axes") == _LOCAL_AXES else "frames.local.axes is not x east, y north, z up",
        _number_problem(local, "grid_rotation_deg", "frames.local"),
    ]
    checks += [_number_problem(origin, name, "frames.local.origin")
               for name in ("longitude", "latitude", "ellipsoidal_height_m", "height_m")]
    checks += [_number_problem(projected, name, "frames.local.origin.projected") for name in ("x", "y")]
    for frame, where in ((order, "frames.georeferenced"), (local, "frames.local")):
        geoid = frame.get("geoid_model")
        if geoid is not None and not isinstance(geoid, str):
            checks.append(f"{where}.geoid_model is not a name or null")
    local_unit = local.get("unit")
    if isinstance(local_unit, dict) and _is_number(local_unit.get("to_metres")) and local_unit["to_metres"] != 1:
        checks.append(f"frames.local.unit states {local_unit.get('name')} ({local_unit['to_metres']} metres), "
                      "but the local frame is in metres on every order")
    return next((problem for problem in checks if problem), None)


def expected_frames(frames: dict) -> dict:
    """The answer for a readable `frames`: the manifest's own words, with each `geoid_model` stated.

    Nothing is derived or renamed; a geoid the manifest names none for is `null`, so a reader that
    leaves it out and one that reports `null` give the answer sheet one shape.
    """
    answer = json.loads(json.dumps(frames))
    for member in _FRAME_KEYS:
        answer[member].setdefault("geoid_model", None)
    return answer


def _expected_file(path: str, entry: object, major: int) -> dict:
    if major >= 3:
        family = family_of_3(path)
        count = entry.get(COUNT) if isinstance(entry, dict) else None
        count = count.get("value") if isinstance(count, dict) else None
        count = count if _is_int(count) else None
    else:
        frame = entry.get("frame") if isinstance(entry, dict) else None
        mode = frame.get("coordinate_mode") if isinstance(frame, dict) else None
        family, count = (_FAMILY_OF_MODE.get(mode) if isinstance(mode, str) else None), None
    return {"path": path, "family": family, "frame": FRAME_OF.get(family), "count": count}


def check_result_against_bundle(result: dict, fixture: object) -> list[str]:
    """The problems with `result` as the answer for `fixture`, re-derived from its manifest."""
    files = fixture.get("files") if isinstance(fixture, dict) else None
    if not isinstance(files, dict):
        return ["the bundle's case file has no `files` object"]
    manifest = files.get(result["manifestPath"])
    if not isinstance(manifest, dict):
        return [f"the bundle holds no JSON manifest at {result['manifestPath']}"]
    if manifest.get("version") != result["version"]:
        return [f"`result.version` is {result['version']!r}, but the bundle's manifest declares "
                f"{manifest.get('version')!r}"]
    if result["outcome"] == "refused":
        if result["refusal"] == "unreadable" and frames_refusal(manifest.get(FRAMES)) is None:
            return ["`result` refuses the bundle as unreadable, but a reader reads its manifest's frames"]
        return []
    major = result["major"]
    problems: list[str] = []
    if major >= 3:
        refusal = frames_refusal(manifest.get(FRAMES))
        if refusal is not None:
            return [f"`result` reads the bundle, but a reader refuses its manifest's frames: {refusal}"]
        if result[FRAMES] != expected_frames(manifest[FRAMES]):
            problems.append("`result.frames` is not the frames the manifest states, with each geoid_model stated")
    if result["level"] != (manifest.get(LEVEL) if major >= 3 else None):
        problems.append(f"`result.level` is {result['level']!r}, but the manifest states "
                        f"{manifest.get(LEVEL) if major >= 3 else None!r}")
    member = CATALOG if major >= 3 else CATALOG_2
    catalog = manifest.get(member)
    if not isinstance(catalog, dict):
        return problems + [f"the manifest has no `{member}` catalog to read the files from"]
    stated = result["files"]
    expected = [_expected_file(path, catalog[path], major) for path in sorted(catalog) if path in files]
    if stated != expected:
        have = {row["path"]: row for row in stated}
        for row in expected:
            if have.get(row["path"]) != row:
                problems.append(f"`result.files` has {have.get(row['path'])!r} for {row['path']}, "
                                f"the catalog and the extension make it {row!r}")
        for path in sorted(set(have) - {row["path"] for row in expected}):
            problems.append(f"`result.files` finds {path}, which the catalog does not name or the "
                            "bundle does not hold")
    held_back = manifest.get(NOT_HELD) if major >= 3 else None
    if held_back is not None and not isinstance(held_back, list):
        return problems + [f"the manifest's `{NOT_HELD}` is not a list"]
    expected_skips = [{"path": path, "why": "file-missing", "reason": None}
                      for path in sorted(catalog) if path not in files]
    for row in held_back or []:
        path = row.get("path") if isinstance(row, dict) else None
        reason = row.get("reason") if isinstance(row, dict) else None
        if not isinstance(path, str) or path in catalog or path in files                 or not isinstance(reason, str) or not reason:
            problems.append(f"the manifest lists {path} as not held, but holds it, catalogues it or "
                            "states no reason")
        else:
            expected_skips.append({"path": path, "why": "not-held", "reason": reason})
    expected_skips.sort(key=lambda row: row["path"])
    if result["skipped"] != expected_skips:
        problems.append("`result.skipped` is not what the catalog and the manifest's not-held list "
                        f"make it: expected {json.dumps(expected_skips)}")
    return problems


def check_case(entry: dict, body: object, fixture: object) -> list[str]:
    """Failure lines for one `reader` case: its case file `body`, held to its bundle `fixture`."""
    case_id = entry["id"]
    problems: list[str] = []
    if not isinstance(body, dict):
        return [f"FAIL: reader case '{case_id}' ({entry['file']}) is not a JSON object."]
    members = [key for key in body if key != "$comment"]
    if members != ["bundle", "result"]:
        return [f"FAIL: reader case '{case_id}' ({entry['file']}) has the members "
                f"{', '.join(members) or 'none'}, expected exactly `bundle` and `result`."]
    problems += check_result_shape(body["result"])
    result = body["result"]
    if isinstance(result, dict) and not problems:
        expected = {"read": "accept", "refused": "reject"}[result["outcome"]]
        if entry["expect"] != expected:
            problems.append(f"index `expect` is {entry['expect']!r}, but the outcome "
                            f"`{result['outcome']}` is {expected!r}")
        problems += check_result_against_bundle(result, fixture)
    if "errorContains" in entry and (entry["expect"] != "reject"
                                     or not isinstance(entry["errorContains"], str)):
        problems.append("`errorContains` is a string on a `reject` case only")
    elif isinstance(result, dict) and result.get("refusal") == "unreadable":
        files = fixture.get("files") if isinstance(fixture, dict) else None
        manifest = files.get(result.get("manifestPath")) if isinstance(files, dict) else None
        stated = json.dumps(manifest.get(FRAMES)) if isinstance(manifest, dict) else ""
        if not isinstance(entry.get("errorContains"), str) or entry["errorContains"] not in stated:
            problems.append("`errorContains` names a value the manifest's frames state, the value the "
                            "reader refused")
    return [f"FAIL: reader case '{case_id}' ({entry['file']}): {problem}." for problem in problems]


def check_reader_cases(raw_cases: list[dict], corpus: Path) -> list[str]:
    """Failure lines for every `reader` case of the index; a case that cannot be read is one."""
    bundle_files = {c.get("file") for c in raw_cases if c.get("group") == "bundle"}
    failures: list[str] = []
    for entry in raw_cases:
        if entry.get("group") != READER_GROUP or not entry.get("file") or not entry.get("id"):
            continue
        try:
            body = json.loads((corpus / entry["file"]).read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue  # check_corpus has already reported a missing or unparseable file
        named = body.get("bundle") if isinstance(body, dict) else None
        if named not in bundle_files:
            failures.append(f"FAIL: reader case '{entry['id']}' names the bundle {named!r}, which is "
                            "not the case file of a `bundle` case in index.json.")
            continue
        try:
            fixture = json.loads((corpus / named).read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue  # likewise reported by the bundle case's own check
        failures.extend(check_case(entry, body, fixture))
    return failures
