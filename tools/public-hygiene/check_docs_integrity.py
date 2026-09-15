#!/usr/bin/env python3
"""Refuse a documentation reference that does not resolve, and a docs cache that has gone stale.

This is the `references` job's second gate, and it shares that job's charter: every reference in a
public file resolves for a stranger. The sibling script refuses a reference that resolves only
inside a private repository. This one refuses a reference that resolves nowhere at all. From a
reader's seat they are the same defect -- a pointer that looks authoritative and is a dead end.

A documentation audit found six stale claims of exactly this class by hand, in one pass: a link to
a file that had moved, a count that was one short, an index that named documents that were gone.
Every one was mechanically checkable, and nothing checked it. A prose keeper is the thing this
repository generally refuses to rely on, so the three checks below are what replaces one.

What is refused:

  a relative link     A Markdown link whose target is a path must name something that exists, spelled
                      exactly as the link spells it. Both forms are read: the inline
                      "[text](path/file.md)" and the reference definition "[id]: path/file.md". The
                      fragment is split off and the FILE half is what is checked -- whether a
                      `#section` matches a heading in the target is deliberately not checked, because
                      that is materially more brittle and a gate with false positives is a gate
                      somebody switches off.

  a site-root link    A target beginning with "/". It looks like a repository path and is not one:
                      GitHub resolves it against the SITE root, so `/docs/x.md` sends a reader to
                      `github.com/docs/x.md`. It is a dead end even when a file of that name exists
                      here, which is why it is refused rather than quietly resolved.

  missing frontmatter A document under `docs/` or a host `CLAUDE.md` must open with a frontmatter
                      block that parses and that carries both `name` and `description`. The docs
                      graph is what an agent navigates by; a document with no `description` is one
                      an agent has to open to find out whether it wanted it.

  a stale ADR index   Root `CLAUDE.md` lists the ADRs by number. That list is a deliberate cache of
                      a directory listing, and a cache nothing checks drifts on the first ADR that
                      lands in a hurry. Both directions are refused: a file in `docs/adr/` the list
                      does not name, and a number the list names with no file behind it.

Deliberately NOT refused, each because refusing it would produce findings that are wrong:

  fenced code         Several documents show example commands and illustrative link syntax. A
  and code spans      fenced block and an inline code span are stripped before any link is read, so
                      an example is an example rather than a broken link.
  an external URL     Anything with a scheme, and a protocol-relative "//host/path". This job makes
                      no network call, ever, so liveness is not knowable here and is not claimed.
  a bare "#anchor"    A same-document fragment. There is no file half to check.
  a footnote          "[^1]: some prose" is a footnote definition, not a link definition. GitHub
                      renders footnotes; reading the first word of one as a path would fail CI with
                      a finding that is nonsense.
  a target that       `../../security/advisories/new`, used in `README.md` and `SECURITY.md`, is a
  escapes the root    GitHub repo-relative route rather than a path, and correctly does not resolve
                      on disk. The exemption is STRUCTURAL -- a target that normalizes to somewhere
                      outside the repository root is a host-provided route -- rather than a list of
                      the two filenames that use it today. A filename list would be a second cache
                      of exactly the kind this gate exists to retire.
  frontmatter on the  `README.md`, `CONTRIBUTING.md`, `SECURITY.md`, `ROADMAP.md`, `CONTEXT.md`,
  human reading       root `CLAUDE.md`, everything under `spec/`, and the rest of the human reading
  surface             surface deliberately carry none: GitHub renders a frontmatter block as a
                      visible table at the top of the page, which is a regression for a human
                      reader. The scope here is `docs/` plus the host `CLAUDE.md` files, and no
                      wider.

What the gate checks is presence, parse and resolution. It never judges whether a `description` is
a good one or whether a link points at the right file -- both are review's job, and a gate that
tried would be a gate arguing with an author.

Stdlib only, no network: the job's standing property is that a registry outage cannot break it.
Frontmatter here is a fenced block of flat string keys, not arbitrary YAML, so it is parsed here
rather than imported.

Run with no arguments to check the tree this script lives in; `--root PATH` checks another.
"""

from __future__ import annotations

import argparse
import posixpath
import re
import subprocess
import sys
from pathlib import Path
from urllib.parse import unquote

REPO_ROOT = Path(__file__).resolve().parents[2]

# The directory whose documents are agent-facing, and the ADR index's home.
DOCS_DIR = "docs/"
ADR_DIR = "docs/adr"
ROOT_GUIDE = "CLAUDE.md"

# The frontmatter keys the docs graph navigates by.
REQUIRED_KEYS = ("name", "description")

# A fence opens and closes with three or more backticks or tildes, optionally indented.
FENCE = re.compile(r"^[ \t]{0,3}(`{3,}|~{3,})")

# An inline code span. Stripped before links are read, for the same reason a fence is. The opening
# run of backticks is what closes it, so ``a ` b`` is one span rather than two mis-cut ones.
CODE_SPAN = re.compile(r"(`+)(?:(?!\1).)*?\1")

# The two link forms.
#
# The inline form is matched on its TARGET half, "](...", rather than on the whole link. The link
# text is not read, which is what makes an image inside a link ("[![alt](icon.png)](page.md)")
# yield both targets: a pattern anchored on the opening bracket consumes the outer link whole and
# never sees the inner one, because matches do not overlap.
#
# The target allows a balanced parenthesised pair, so a filename containing parentheses is read
# whole rather than cut at the first ")" and then reported as missing -- a false finding, and a
# false finding is how a gate gets switched off. An optional `"title"` after the target stops at
# the whitespace and is left behind.
#
# ⛔ A footnote definition is NOT a link definition. "[^1]: some prose" would otherwise hand the
# gate the first word of the footnote as a path; GitHub renders footnotes, so the first document
# here to use one would fail CI with a finding that is nonsense.
INLINE_LINK = re.compile(r"\]\(\s*((?:[^()\s]|\([^()]*\))*)")
REFERENCE_LINK = re.compile(r"(?m)^[ \t]{0,3}\[(?!\^)[^\]]+\]:[ \t]*(\S+)")

# A scheme ("https:", "mailto:") or a protocol-relative host. Either way it is an address, not a
# path, and this job never resolves an address.
ABSOLUTE_TARGET = re.compile(r"(?i)^(?:[a-z][a-z0-9+.-]*:|//)")

# A frontmatter line: a flat key and a value. Anything else in the block is a parse failure, which
# is the honest reading -- this parser is not YAML and must not pretend to have understood a
# construct it cannot.
FRONTMATTER_LINE = re.compile(r"^([A-Za-z][\w-]*):\s*(.*)$")

# The ADR index bullet in root CLAUDE.md, and the numbers it names. Bold is what makes an entry an
# entry: the same paragraph mentions "0002's bug in the other host" in passing, and a mention is
# not a listing.
ADR_INDEX_BULLET = re.compile(r"(?ms)^- [^\n]*\]\(docs/adr/\).*?(?=^- |\Z)")
ADR_INDEX_ENTRY = re.compile(r"\*\*(\d{4})\*\*")
ADR_FILENAME = re.compile(r"^(\d{4})-[\w-]+\.md$")


def tracked_markdown(root: Path) -> list[str]:
    """Every tracked Markdown file, repository-relative. An untracked file is not published."""
    listed = subprocess.run(
        ["git", "-C", str(root), "ls-files", "-z", "*.md"],
        check=True,
        capture_output=True,
        text=True,
    ).stdout
    return sorted(name for name in listed.split("\0") if name)


def without_code(text: str) -> str:
    """The text with fenced blocks and inline code spans blanked out.

    Blanked rather than deleted: line numbers stay true, so a finding names the line the author is
    looking at. A fence closes only on a marker of the same character and at least the same length,
    which is how a ```` ``` ```` inside a ```` ```` ```` block stays inside it.
    """
    lines = text.split("\n")
    kept: list[str] = []
    fence: str | None = None
    for line in lines:
        marker = FENCE.match(line)
        if fence is None:
            if marker:
                fence = marker.group(1)
                kept.append("")
                continue
            kept.append(CODE_SPAN.sub(lambda match: " " * len(match.group(0)), line))
            continue
        if marker and marker.group(1)[0] == fence[0] and len(marker.group(1)) >= len(fence):
            fence = None
        kept.append("")
    return "\n".join(kept)


def link_targets(text: str) -> list[tuple[int, str]]:
    """Every link target in the text, as (line number, target), code already stripped."""
    prose = without_code(text)
    found: list[tuple[int, str]] = []
    for pattern in (INLINE_LINK, REFERENCE_LINK):
        for match in pattern.finditer(prose):
            target = match.group(1).strip("<>")
            if target:
                found.append((prose.count("\n", 0, match.start()) + 1, target))
    return sorted(found)


def resolved(relative: str, target: str) -> str | None:
    """Where a target points, repository-relative -- or None when it is not a path to check.

    None covers the three forms that have no file half to resolve: an external address, a bare
    fragment, and a target that normalizes to somewhere outside the repository root, which is a
    route the host provides rather than a path this tree contains.

    A target beginning with "/" is NOT one of them. GitHub resolves it against the site root rather
    than this repository -- `/docs/x.md` lands on `github.com/docs/x.md` -- so it is a dead end for
    every reader even when a file of that name exists here. It is resolved from the repository root
    anyway, so that the finding can say what the author meant.
    """
    if ABSOLUTE_TARGET.match(target) or target.startswith("#"):
        return None

    file_half = unquote(target.split("#", 1)[0])
    if not file_half:
        return None

    if file_half.startswith("/"):
        joined = file_half.lstrip("/")
    else:
        joined = posixpath.join(posixpath.dirname(relative), file_half)

    normalized = posixpath.normpath(joined)
    if normalized == ".." or normalized.startswith("../"):
        return None
    return normalized


def exists_exactly(root: Path, destination: str) -> bool:
    """True when every segment of the path exists spelled exactly as the link spells it.

    `Path.exists()` is case-insensitive on Windows and on a case-insensitive macOS volume, so a
    link whose case is wrong passes on the author's machine and fails on the hosted runner. A gate
    that disagrees with CI about what is broken is a gate nobody trusts, so each segment is matched
    against the real directory entry.
    """
    current = root
    for segment in destination.split("/"):
        if segment in ("", "."):
            continue
        try:
            entries = {entry.name for entry in current.iterdir()}
        except OSError:
            return False
        if segment not in entries:
            return False
        current = current / segment
    return True


def link_findings(root: Path, documents: list[str]) -> list[str]:
    findings: list[str] = []
    for relative in documents:
        text = (root / relative).read_text(encoding="utf-8", errors="replace")
        for line, target in link_targets(text):
            destination = resolved(relative, target)
            if destination is None:
                continue
            if target.startswith("/"):
                findings.append(
                    f"{relative}:{line}: link target is site-root-relative, which GitHub resolves "
                    f"outside this repository: {target}"
                )
                continue
            if not exists_exactly(root, destination):
                findings.append(f"{relative}:{line}: link target does not exist: {target}")
    return findings


def frontmatter(text: str) -> tuple[dict[str, str], str | None]:
    """The block's keys, and why it does not parse when it does not.

    A document with no opening `---` is reported as having none rather than as unparseable: the two
    are different defects and a reader fixes them differently.

    An INDENTED line continues the key above it -- a wrapped value, or a list. That is as far
    towards YAML as this goes: the gate needs to know whether a key is present and non-empty, never
    what shape its value has, so a continuation is folded onto the value and nothing interprets it.
    Refusing one instead would fail a document for using a construct the format allows.
    """
    lines = text.split("\n")
    if not lines or lines[0].strip() != "---":
        return {}, "no frontmatter block"

    keys: dict[str, str] = {}
    last: str | None = None
    for line in lines[1:]:
        if line.strip() == "---":
            return keys, None
        if not line.strip():
            continue
        if line[:1] in (" ", "\t") and last is not None:
            keys[last] = f"{keys[last]} {line.strip()}".strip()
            continue
        match = FRONTMATTER_LINE.match(line)
        if not match:
            return {}, f"frontmatter does not parse at: {line.strip()[:60]}"
        last = match.group(1)
        keys[last] = match.group(2).strip()
    return {}, "frontmatter block is never closed"


def needs_frontmatter(relative: str) -> bool:
    """`docs/` and the host guides, and nothing else.

    The host guides are matched structurally -- a `CLAUDE.md` that is not the root one -- so a
    `max/` or `blender/` added later is in scope the day it appears, without an edit here.
    """
    if relative.startswith(DOCS_DIR):
        return True
    return relative.endswith("/" + ROOT_GUIDE)


def frontmatter_findings(root: Path, documents: list[str]) -> list[str]:
    findings: list[str] = []
    for relative in documents:
        if not needs_frontmatter(relative):
            continue
        keys, problem = frontmatter((root / relative).read_text(encoding="utf-8", errors="replace"))
        if problem:
            findings.append(f"{relative}: {problem}")
            continue
        for key in REQUIRED_KEYS:
            if not keys.get(key):
                findings.append(f"{relative}: frontmatter is missing `{key}`")
    return findings


def adr_index_findings(root: Path) -> list[str]:
    """Both directions between root CLAUDE.md's ADR list and the directory it caches."""
    guide = root / ROOT_GUIDE
    if not guide.is_file():
        return [f"{ROOT_GUIDE}: not found, so the ADR index cannot be checked"]

    bullet = ADR_INDEX_BULLET.search(guide.read_text(encoding="utf-8", errors="replace"))
    if not bullet:
        return [f"{ROOT_GUIDE}: no ADR index bullet found (a bullet linking to `{ADR_DIR}/`)"]

    listed = set(ADR_INDEX_ENTRY.findall(bullet.group(0)))
    directory = root / ADR_DIR
    present = {
        match.group(1)
        for path in sorted(directory.glob("*.md"))
        if (match := ADR_FILENAME.match(path.name))
    }

    findings = [
        f"{ADR_DIR}/{number}-*.md exists but root {ROOT_GUIDE}'s ADR index does not name it"
        for number in sorted(present - listed)
    ]
    findings.extend(
        f"root {ROOT_GUIDE}'s ADR index names {number}, but no {ADR_DIR}/{number}-*.md exists"
        for number in sorted(listed - present)
    )
    return findings


def run(root: Path) -> int:
    documents = tracked_markdown(root)
    findings = link_findings(root, documents)
    findings.extend(frontmatter_findings(root, documents))
    findings.extend(adr_index_findings(root))

    if findings:
        print(f"Refused {len(findings)} documentation defect(s) in {len(documents)} document(s):\n")
        for finding in findings:
            print(f"  {finding}")
        print(
            "\nA pointer that does not resolve is a dead end for a stranger, which is the same "
            "defect as a pointer only we can follow. Fix the target, add the frontmatter, or "
            "update the index — see tools/public-hygiene/check_docs_integrity.py for what each "
            "finding means."
        )
        return 1

    print(
        f"OK: {len(documents)} document(s) resolve every relative link, carry the frontmatter "
        "the docs graph needs, and agree with the ADR directory."
    )
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument(
        "--root",
        type=Path,
        default=REPO_ROOT,
        help="repository root to check (defaults to this script's repository)",
    )
    arguments = parser.parse_args(argv)
    return run(arguments.root)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
