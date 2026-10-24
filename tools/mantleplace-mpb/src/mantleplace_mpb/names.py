# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""The 2.0 layout: the folder allowlist (`format.md` §10.2) and the naming rules (§10.3).

They bind a producer, and a consumer never depends on them: a reader finds every file by its
pointer. So a folder outside the allowlist is a warning, as the spec says, and so is a file at
the root other than the README, for the same reason. A broken naming rule is an error: a 2.0
producer follows all six.

What a machine can check, rule by rule:

1. One noun per basename: the basename is PascalCase and the extension, lowercase, is the format
   (`name.basename`); and one basename names one thing, so it never appears in two folders
   (`name.basename-shared`). Two files with one basename in one folder are renditions.
2. Plural or singular is a matter of meaning, which no validator can read.
3. Qualifiers are hyphenated (`name.qualifier`) and run subset, then frame, then level
   (`name.qualifier-order`). The frames and levels are named; any other qualifier is a subset.
4. The fixed names, and the names inside a format's own structure (a tileset folder, a
   Shapefile's sidecars), are exempt from rules 1 and 3.
5. ASCII from `[A-Za-z0-9._-]` (`name.charset`), and no two entries differ only by case
   (`name.case-collision`), checked over every file and folder.
6. A reader matches a manifest path regardless of case; a writer uses the exact case. That one is
   checked where the pointers are (`name.case-mismatch`, in `checks.check_pointers`).

`__MACOSX` and `.DS_Store` never reach here: the archive drops them as it opens the bundle.
"""

from __future__ import annotations

import re

from .archive import MANIFEST_PATH, Bundle
from .report import Report

#: The eight folders of a 2.0 bundle, in the spec's order.
FOLDERS = ("Metadata", "Site", "Elevation", "Imagery", "LandCover", "Features", "Records",
           "Drawings")
ROOT_FILES = frozenset({"README.md"})
#: Names that software or a convention looks up (rule 4).
FIXED_NAMES = frozenset({"README.md", MANIFEST_PATH, "Metadata/stac-item.json"})

#: The frame qualifiers: a fixed-frame engine's grid, and a projected-CRS reader's twin.
FRAMES = frozenset({"Engine", "Projected"})
#: The level qualifiers, finest first.
LEVELS = frozenset({"Raw", "Med", "Min"})
_SUBSET, _FRAME, _LEVEL = 0, 1, 2

_SEGMENT = re.compile(r"^[A-Za-z0-9._-]+$")
_HEAD = re.compile(r"^[A-Z][A-Za-z0-9]*$")
_QUALIFIER = re.compile(r"^[A-Z0-9][A-Za-z0-9]*$")
_EXTENSION = re.compile(r"^[a-z0-9]+$")
_SEMVER = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")


def is_layout_2(version: object) -> bool:
    """Whether a manifest's `version` declares the 2.x layout: a semver string of major 2."""
    if not isinstance(version, str):
        return False
    found = _SEMVER.match(version)
    return found is not None and found.group(1) == "2"


def check_names(bundle: Bundle, report: Report) -> None:
    report.ran.append("names")
    files = bundle.files
    _check_charset(files, report)
    _check_case_collisions(files, sorted(bundle.folders), report)
    _check_allowlist(files, report)
    _check_basenames(files, report)


def _check_charset(files: list[str], report: Report) -> None:
    for path in files:
        bad = [segment for segment in path.split("/") if not _SEGMENT.match(segment)]
        if bad:
            report.add("names", "name.charset", "error",
                       f"{path!r} holds {', '.join(repr(b) for b in bad)}: names are ASCII from "
                       "[A-Za-z0-9._-], so the bundle extracts the same everywhere", file=path)


def _check_case_collisions(files: list[str], folders: list[str], report: Report) -> None:
    groups: dict[str, list[str]] = {}
    for entry in folders + files:
        groups.setdefault(entry.casefold(), []).append(entry)
    for entries in groups.values():
        if len(entries) > 1:
            names = sorted(entries)
            report.add("names", "name.case-collision", "error",
                       f"{' and '.join(names)} differ only by case, so a case-insensitive file "
                       "system extracts them as one", file=names[0])


def _check_allowlist(files: list[str], report: Report) -> None:
    unlisted: set[str] = set()
    for path in files:
        if "/" not in path:
            if path not in ROOT_FILES:
                report.add("names", "layout.root-file", "warning",
                           f"{path} sits at the root, where a 2.0 bundle holds only README.md "
                           "and the folders; a reader still finds it by its pointer", file=path)
        elif path.split("/", 1)[0] not in FOLDERS:
            unlisted.add(path.split("/", 1)[0])
    for folder in sorted(unlisted):
        report.add("names", "layout.folder-unlisted", "warning",
                   f"{folder}/ is not one of the eight folders of a 2.0 bundle "
                   f"({', '.join(FOLDERS)}); a reader still finds its files by their pointers",
                   file=folder)


def _check_basenames(files: list[str], report: Report) -> None:
    folders_by_basename: dict[str, set[str]] = {}
    for path in files:
        parts = path.split("/")
        # Rules 1 and 3 name the files of a folder. A file deeper down sits inside a format's
        # own structure (a tileset), whose names are the format's (rule 4); so are the fixed names.
        if len(parts) != 2 or path in FIXED_NAMES or not _SEGMENT.match(parts[1]):
            continue
        folder, name = parts
        basename, _, extensions = name.partition(".")
        problem = _basename_problem(basename, extensions)
        if problem is not None:
            code, why = problem
            report.add("names", code, "error", f"{path}: {why}", file=path)
            continue
        folders_by_basename.setdefault(basename, set()).add(folder)
    for basename, folders in sorted(folders_by_basename.items()):
        if len(folders) > 1:
            where = ", ".join(f"{f}/" for f in sorted(folders))
            report.add("names", "name.basename-shared", "error",
                       f"the basename {basename} names files in {where}: two files share a "
                       "basename only as renditions in one folder, and different data never "
                       "shares one", file=f"{sorted(folders)[0]}/{basename}")


def _basename_problem(basename: str, extensions: str) -> tuple[str, str] | None:
    """`(code, why)` for the first naming rule `basename` + `.extensions` breaks, or None."""
    chain = extensions.split(".") if extensions else []
    if not chain:
        return "name.basename", "it has no extension, and the extension is the format"
    if any(not _EXTENSION.match(part) for part in chain[:-1]):
        return "name.qualifier", ("a qualifier is joined by a dot; qualifiers are hyphenated, and "
                                  "only the format's extension follows a dot")
    if not _EXTENSION.match(chain[-1]):
        return "name.basename", "the extension is the format, written in lowercase"
    if "_" in basename:
        return "name.qualifier", "qualifiers are hyphenated, never joined by an underscore"
    head, *qualifiers = basename.split("-")
    if not _HEAD.match(head):
        return "name.basename", "a basename is a PascalCase noun"
    if any(not _QUALIFIER.match(q) for q in qualifiers):
        return "name.qualifier", "each hyphenated qualifier is a capitalised word or a number"
    ranks = [_FRAME if q in FRAMES else _LEVEL if q in LEVELS else _SUBSET for q in qualifiers]
    repeated = ranks.count(_FRAME) > 1 or ranks.count(_LEVEL) > 1
    if repeated or ranks != sorted(ranks):
        return "name.qualifier-order", ("qualifiers run subset, then frame "
                                        f"({', '.join(sorted(FRAMES))}), then level "
                                        f"({', '.join(sorted(LEVELS))}), each at most once")
    return None
