#!/usr/bin/env python3
"""Refuse a licence text anywhere in the Unreal plugin but under Source/ThirdParty/.

Fab's technical review requires third-party content to sit in a `ThirdParty` folder directly under
the plugin's `Source/`, and a plugin that ships any must file Fab's Third Party Software form. The
plugin ships no third-party content today (its editor panel uses the engine's own typefaces).
Nothing in review catches a third-party file landing beside the icons, and the review rejection comes
weeks later, after a release was cut. `unreal/CLAUDE.md` states the rule; this gate enforces it.

A licence text is the marker of third-party content, so that is what is looked for:

  * a file named `OFL*`, `LICENSE*`, `COPYING*` or `NOTICE*`, whatever it contains;
  * a `Config/FilterPlugin.ini` entry naming a `LICENSE*`, `COPYING*`, `NOTICE*`, `EULA*`,
    `TRADEMARK*` or `CODE_OF_CONDUCT*` file: the Source zip carries no licence or policy file, as the
    storefront refuses them beside its own licence;
  * a plain-text file (`.txt`, `.md`, or no extension) whose text names the SIL Open Font License,
    the MIT License, BSD, the GPL or the GNU General Public License.

Source files (`.cpp`, `.h`, `.cs`) are not read: they may mention a licence word in a comment, and
they carry our own Apache header. A full licence scanner is more than a plugin with no third-party content needs; this
list is the one the review cares about.

It is text-only and offline, like the generated-name gate beside it.
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]

# The tree this gate governs, and the only folder inside it that may hold a licence text.
PLUGIN_ROOT = Path("unreal/MantlePlace")
THIRD_PARTY = Path("Source/ThirdParty")

NAME_RE = re.compile(r"^(?:OFL|LICENSE|COPYING|NOTICE)", re.IGNORECASE)

# Suffixes whose text is read for a licence phrase. Anything else (source, images, fonts) is not.
TEXT_SUFFIXES = ("", ".txt", ".md", ".license")
MAX_READ_BYTES = 1_000_000

FILTER_PLUGIN = Path("Config/FilterPlugin.ini")
FILTER_ENTRY_RE = re.compile(
    r"^(?:LICENSE|COPYING|NOTICE|EULA|TRADEMARK|CODE_OF_CONDUCT)", re.IGNORECASE
)

PHRASE_RE = re.compile(
    r"SIL\s+Open\s+Font\s+Licen[sc]e"
    r"|\bMIT\s+Licen[sc]e\b"
    r"|\bBSD\b"
    r"|\bGPL\b"
    r"|GNU\s+General\s+Public\s+Licen[sc]e",
    re.IGNORECASE,
)


def reason(path: Path) -> str | None:
    """Why this file counts as a licence text, or None when it does not."""
    if NAME_RE.match(path.name):
        return f"named {path.name}"
    if path.suffix.lower() in TEXT_SUFFIXES and path.stat().st_size <= MAX_READ_BYTES:
        match = PHRASE_RE.search(path.read_text(encoding="utf-8-sig", errors="replace"))
        if match:
            return f"its text names {match.group(0)!r}"
    return None


def filter_offenders(plugin: Path) -> list[tuple[str, str]]:
    """FilterPlugin.ini lines that pull a licence or policy file into a package."""
    ini = plugin / FILTER_PLUGIN
    if not ini.is_file():
        return []
    found: list[tuple[str, str]] = []
    for line in ini.read_text(encoding="utf-8-sig", errors="replace").splitlines():
        entry = line.strip()
        if not entry or entry.startswith((";", "[")):
            continue
        if FILTER_ENTRY_RE.match(entry.lstrip("/\\").split("/")[-1].split("\\")[-1]):
            found.append((FILTER_PLUGIN.as_posix(), f"lists {entry!r}, a licence or policy file"))
    return found


def offenders(root: Path) -> list[tuple[str, str]]:
    plugin = root / PLUGIN_ROOT
    if not plugin.is_dir():
        return []
    allowed = plugin / THIRD_PARTY
    found: list[tuple[str, str]] = filter_offenders(plugin)
    for path in sorted(plugin.rglob("*")):
        if not path.is_file() or allowed in path.parents:
            continue
        why = reason(path)
        if why:
            found.append((path.relative_to(plugin).as_posix(), why))
    return found


def run(root: Path) -> int:
    problems = offenders(root)
    if problems:
        print("A licence text marks third-party content, which belongs only under")
        print(f"{PLUGIN_ROOT.as_posix()}/{THIRD_PARTY.as_posix()}/ (Fab's review requires it).")
        print("See unreal/CLAUDE.md (Third-party content).")
        print()
        for rel, why in problems:
            print(f"  {rel}: {why}")
        print()
        print(f"{len(problems)} licence text(s) outside {THIRD_PARTY.as_posix()}/.")
        return 1

    print(f"OK: no licence text in {PLUGIN_ROOT.as_posix()} outside {THIRD_PARTY.as_posix()}/.")
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
