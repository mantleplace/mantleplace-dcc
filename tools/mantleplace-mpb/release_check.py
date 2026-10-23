# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""Hold a release of mantleplace-mpb to the conformance suite it was tested against.

The package version is `MAJOR.MINOR.PATCH` of the newest MPB manifest version the conformance
corpus declares a case against, and `MAJOR.MINOR.PATCH.postN` for a release that changes the
package and not the suite. A release is cut from the tag `mpb-v<version>`.

    python tools/mantleplace-mpb/release_check.py                    # the package against the corpus
    python tools/mantleplace-mpb/release_check.py --tag mpb-v1.14.0  # and the tag against the package

Exit 0: consistent. Exit 1: each problem is printed. Standard library only, like the package.
"""

from __future__ import annotations

import argparse
import ast
import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
INIT = HERE / "src" / "mantleplace_mpb" / "__init__.py"
CORPUS_INDEX = HERE.parents[1] / "tools" / "manifest-conformance" / "corpus" / "index.json"
TAG_PREFIX = "mpb-v"

_SEMVER = re.compile(r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")
_RELEASE = re.compile(r"^((?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))(?:\.post[0-9]+)?$")


def package_version(init: Path = INIT) -> str:
    """`__version__` as the package source states it, read without importing the package."""
    for node in ast.parse(init.read_text(encoding="utf-8")).body:
        if isinstance(node, ast.Assign) and any(
                isinstance(t, ast.Name) and t.id == "__version__" for t in node.targets):
            return ast.literal_eval(node.value)
    raise ValueError(f"{init} states no __version__")


def base_version(version: str) -> str:
    """The MPB version a package version stands for: `.postN` removed. Refuses any other form."""
    found = _RELEASE.match(version)
    if not found:
        raise ValueError(f"{version!r} is not MAJOR.MINOR.PATCH or MAJOR.MINOR.PATCH.postN")
    return found.group(1)


def suite_version(index: Path = CORPUS_INDEX) -> str:
    """The newest semver manifest version any case of the corpus declares."""
    cases = json.loads(index.read_text(encoding="utf-8")).get("cases", [])
    versions = [c["manifestVersion"] for c in cases
                if isinstance(c.get("manifestVersion"), str) and _SEMVER.match(c["manifestVersion"])]
    if not versions:
        raise ValueError(f"{index} declares no semver manifestVersion on any case")
    return max(versions, key=lambda v: tuple(int(n) for n in v.split(".")))


def problems(package: str, suite: str, tag: str | None = None) -> list[str]:
    found: list[str] = []
    try:
        base = base_version(package)
    except ValueError as exc:
        return [str(exc)]
    if base != suite:
        found.append(f"the package is {package}, but the conformance corpus is written against "
                     f"MPB {suite}; raise __version__ to {suite} (or {suite}.postN)")
    if tag is not None and tag != f"{TAG_PREFIX}{package}":
        found.append(f"the tag {tag!r} does not name the package version; expected "
                     f"{TAG_PREFIX}{package}")
    return found


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--tag", help="the release tag, e.g. mpb-v1.14.0")
    parser.add_argument("--index", type=Path, default=CORPUS_INDEX, help="the corpus index.json")
    args = parser.parse_args(argv)
    package = package_version()
    found = problems(package, suite_version(args.index), args.tag)
    for line in found:
        print(f"ERROR: {line}", file=sys.stderr)
    if not found:
        print(f"OK: mantleplace-mpb {package} matches the conformance suite"
              + (f" and the tag {args.tag}" if args.tag else ""))
    return 1 if found else 0


if __name__ == "__main__":
    raise SystemExit(main())
