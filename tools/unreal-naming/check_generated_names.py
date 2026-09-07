#!/usr/bin/env python3
"""Refuse a generated name or package path built anywhere but the Unreal naming module.

`unreal/CLAUDE.md` states the standard for what the Unreal importer names inside a user's project:
the asset prefixes, where generated content lands, and the actor-label form. ADR 0003 records why
that standard lives in this repository at all. Neither is an enforcer.

The standard is only a standard while it has one point of definition. Spelled at each call site it
drifts on the first patch that does not know about it -- which is how the tree arrived at four
prefixes in a table and three more invented inline, a content root respelled at one site and a
truncation respelled at eight. And a naming regression compiles cleanly: nothing in public CI
builds this plugin (a licensed engine on Windows, and a self-hosted runner on a public repository
would let a fork's pull request execute on the build machine), so neither the compiler nor a hosted
gate is going to notice. A user notices, in their outliner.

So this gate enforces the "nowhere else" half, which is the half a reviewer cannot reliably hold:

  * A `/Game/` package-path literal. The importer's destination directory is FORCE-DELETED on
    re-import, so a second place that builds it is a second thing that decides what gets deleted.
  * A `TEXT("MP_...")` actor-label literal. Re-import finds prior actors by matching these; a label
    built somewhere else drifts out of agreement with the matcher and re-import silently stops
    replacing, then starts stacking duplicates.
  * A bare asset-prefix literal -- `SM_`, `T_`, `M_`, `MI_`, `LI_`, `DT_`, `BP_`. Use the typed
    helper, so the prefix table has one row per type and one home.
  * A subfolder name used as a path segment (`/ TEXT("Imagery")`). Matched only next to the path
    operator, because the same words are legitimate UI strings elsewhere.
  * `.Left(8)` on a job or order id. The truncation is what makes two identities collide in one
    folder, and a folder that is force-deleted; it gets one definition, with the hazard documented
    on it.
  * An outliner folder path, and the import tag. The tag is what re-import matches on, so a second
    place that spells it is a second thing that decides which actors get destroyed.

What is deliberately NOT refused: names the importer READS rather than writes. A paint layer's name
arrives in the manifest and is applied verbatim (HPS-33), and the drape material template is shipped
plugin content addressed through the plugin's own mount point rather than `/Game/`. Both are inputs.

A line that genuinely needs one of these can end with `// naming-gate: allow <reason>`. The reason is
required -- a bare silencer is refused, because the next reader needs to know why.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]

# The tree this gate governs.
SCAN_ROOT = Path("unreal/MantlePlace/Source")
SCAN_SUFFIXES = (".cpp", ".h")

# The one file allowed to build these strings, and the tests that assert what it produces.
NAMING_MODULE = ("MantlePlaceImportNaming.cpp", "MantlePlaceImportNaming.h")
TEST_DIR_PART = "Tests"

ALLOW_RE = re.compile(r"//\s*naming-gate:\s*allow\s+\S")
BARE_ALLOW_RE = re.compile(r"//\s*naming-gate:\s*allow\s*$")

ASSET_PREFIXES = ("SM_", "MI_", "LI_", "DT_", "BP_", "M_", "T_")
SUBFOLDERS = ("Imagery", "Mesh", "Buildings", "CoverageRasters", "Landcover")

RULES: list[tuple[str, re.Pattern[str], str]] = [
    (
        "content-path",
        re.compile(r'TEXT\(\s*"/Game/'),
        'a "/Game/" package path - build it with MantlePlaceImportNaming::ImportRoot()',
    ),
    (
        "actor-label",
        re.compile(r'TEXT\(\s*"MP_'),
        'an "MP_" actor label - build it with MantlePlaceImportNaming::ActorLabel() '
        "or RoadSplineLabel()",
    ),
    (
        "asset-prefix",
        re.compile(r'TEXT\(\s*"(?:' + "|".join(re.escape(p) for p in ASSET_PREFIXES) + r")"),
        "a bare asset-name prefix - use the typed helper in MantlePlaceImportNaming "
        "(TextureName, StaticMeshName, LayerInfoName, ...)",
    ),
    (
        "subfolder",
        re.compile(r'/\s*TEXT\(\s*"(?:' + "|".join(SUBFOLDERS) + r')"\s*\)'),
        "a subfolder name as a path segment - use MantlePlaceImportNaming::SubfolderPath()",
    ),
    (
        "outliner-folder",
        # Matched at the CALL, not by the folder's spelling: "MantlePlace/..." is also a legitimate
        # on-disk cache subdirectory, and a rule that could not tell the two apart would be a rule
        # that gets switched off.
        re.compile(r"SetFolderPath\([^)]*TEXT\("),
        "an outliner folder built from a literal - use MantlePlaceImportNaming::OutlinerFolder()",
    ),
    (
        "import-tag",
        re.compile(r"mantleplace_import"),
        "the import tag - use MantlePlaceImportNaming::ImportTag(), which is what re-import "
        "matches on",
    ),
    (
        "identity-truncation",
        re.compile(r"(?:JobId|OrderId|Identity)\s*\.\s*Left\("),
        "an identity truncated inline - use MantlePlaceImportNaming::ShortIdentity(), which "
        "documents the collision hazard the truncation creates",
    ),
]


def is_exempt(path: Path) -> bool:
    """The naming module defines these strings; its tests assert them."""
    if path.name in NAMING_MODULE:
        return True
    return TEST_DIR_PART in path.parts


def files_to_scan(root: Path) -> list[Path]:
    scan_dir = root / SCAN_ROOT
    if not scan_dir.is_dir():
        return []
    found = [p for p in sorted(scan_dir.rglob("*")) if p.suffix in SCAN_SUFFIXES]
    return [p for p in found if not is_exempt(p)]


def check_file(path: Path, root: Path) -> list[str]:
    problems: list[str] = []
    rel = path.relative_to(root).as_posix()
    text = path.read_text(encoding="utf-8-sig", errors="replace")
    for lineno, line in enumerate(text.splitlines(), start=1):
        if BARE_ALLOW_RE.search(line):
            problems.append(
                f"{rel}:{lineno}: a naming-gate allowance needs a reason "
                f"(// naming-gate: allow <why>)"
            )
            continue
        if ALLOW_RE.search(line):
            continue
        for name, pattern, advice in RULES:
            if pattern.search(line):
                problems.append(f"{rel}:{lineno}: [{name}] {advice}\n      {line.strip()}")
    return problems


def run(root: Path) -> int:
    paths = files_to_scan(root)
    problems: list[str] = []
    for path in paths:
        problems.extend(check_file(path, root))

    if problems:
        print("Generated names must be built in MantlePlaceImportNaming, not at the call site.")
        print("See unreal/CLAUDE.md (Naming) and docs/adr/0003-naming-authority-and-mp-prefix.md.")
        print()
        for problem in problems:
            print(f"  {problem}")
        print()
        print(f"{len(problems)} problem(s) in {len(paths)} scanned file(s).")
        return 1

    unit = "file" if len(paths) == 1 else "files"
    print(f"OK: {len(paths)} {unit} build every generated name in the naming module.")
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--root",
        type=Path,
        default=REPO_ROOT,
        help="repository root to scan (defaults to this script's repository)",
    )
    args = parser.parse_args(argv)
    return run(args.root)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
