"""The reader leg of the conformance gate: the `reader` corpus group.

A `reader` case says what a correct reader finds in one synthetic bundle, as language-neutral data
(`corpus/README.md`, "The reader group"). The case file names the bundle by the `bundle` group's case
file that describes it, and holds the `result` a reader must reach on it: the version it read, where
it found the manifest, the download's level, each file it found with its family, its frame and the
count the catalog states, and each file it skipped and why. A reader in any language builds the
bundle, reads it, and compares.

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
#: The members the manifests of the fixtures state these in, until the 3.x schema is served
#: (`mantleplace_mpb.layout3` names the first two; `not_held` and `count` follow them).
CATALOG = "file_entries"
LEVEL = "level"
NOT_HELD = "not_held"
COUNT = "count"

LEVELS = ("Max", "Med", "Min")
WHY = ("not-held", "file-missing")
REFUSALS = ("major-ahead",)
#: A family and the one frame its coordinates are in (format.md section 4).
FRAME_OF = {"georeferenced": "order", "host-ready": "site"}
_GEOREFERENCED = frozenset({"tif", "gpkg", "dxf", "landxml", "ifc"})
_HOST_READY = frozenset({"glb", "usdz", "obj", "png", "csv"})
#: What a 2.x file's own frame statement says about its family.
_FAMILY_OF_MODE = {"absolute": "georeferenced", "offset": "host-ready"}

_RESULT_KEYS = ("outcome", "major", "version", "manifestPath", "level", "refusal", "files", "skipped")
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
    if tuple(result) != _RESULT_KEYS:
        return [f"`result` has the members {', '.join(result) or 'none'}, expected exactly "
                f"{', '.join(_RESULT_KEYS)}, in that order"]
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
    if outcome == "refused" and _is_int(major) and major <= NEWEST_MAJOR:
        problems.append(f"a refusal is of a major above {NEWEST_MAJOR}, not {major}")
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


def _expected_file(path: str, entry: object, major: int) -> dict:
    if major >= 3:
        family = family_of_3(path)
        count = entry.get(COUNT) if isinstance(entry, dict) else None
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
        return []
    major = result["major"]
    problems: list[str] = []
    if result["level"] != (manifest.get(LEVEL) if major >= 3 else None):
        problems.append(f"`result.level` is {result['level']!r}, but the manifest states "
                        f"{manifest.get(LEVEL) if major >= 3 else None!r}")
    catalog = manifest.get(CATALOG)
    if not isinstance(catalog, dict):
        return problems + [f"the manifest has no `{CATALOG}` catalog to read the files from"]
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
    if held_back is not None and not isinstance(held_back, dict):
        return problems + [f"the manifest's `{NOT_HELD}` is not an object"]
    expected_skips = [{"path": path, "why": "file-missing", "reason": None}
                      for path in sorted(catalog) if path not in files]
    for path, entry in (held_back or {}).items():
        reason = entry.get("reason") if isinstance(entry, dict) else None
        if path in catalog or path in files or not isinstance(reason, str) or not reason:
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
