#!/usr/bin/env python3
"""Refuse references that only resolve inside a private repository.

Everything in this repository is world-readable, permanently, whether or not it is later deleted.
The repository's own CLAUDE.md states the rule: internal trackers, internal documents and internal
repositories are not citable here -- not by URL, not by path, not by issue number. It also states
exactly why a bare issue number is the worst of them:

    A bare `#42` in a Markdown file auto-links to *this* repo's issue 42, which is worse than
    dangling: it is wrong and it looks deliberate.

That rule was prose only, and prose is not an enforcer. It was broken -- a private tracker's issue
number reached a committed test comment, where it renders as a link to an unrelated public issue.
This script is what turns the rule into a gate, for the same reason the manifest gate exists beside
the sentence describing the contract: a rule nothing checks is a rule that decays silently and is
noticed by a stranger rather than by us.

What is refused, and why each is a real hazard rather than a style preference:

  #NNN            A private tracker's issue number. In Markdown, a pull request body or a commit
                  message, GitHub renders it as a link to an issue in THIS repository, so a reader
                  following it lands somewhere real and unrelated — worse than dangling. In a source
                  comment it does not link, and is still a citation of something no reader here can
                  open. Both are refused; only the first is also misleading.

  `D-` + two      A decision-log id from the private project repository. It names a document a
  capitals        reader cannot open, and unlike a rule id it is not a stable public identifier.

Deliberately NOT refused, because they are stable public identifiers the repository is expected to
use as prose: HPS-NN, DOC-NN and their kin. The distinction is the one CLAUDE.md already draws --
rule ids are fine, links to them are not.

Exemptions are structural rather than a list of blessed strings, so that a legitimate construct
keeps working as the tree grows and an illegitimate one cannot be waved through by adding a line to
a file nobody rereads:

  ](#anchor)      a Markdown link target, same-document or cross-file ("](format.md#3-heading)").
                  Both resolve for every reader, inside this repository.
  `#42`           a fenced or inline code span. This is how the rule documents ITSELF, and a gate
                  that fails its own rulebook is a gate that gets deleted.
  host #2         an ordinal in prose. "Host #2" is a count of DCC hosts, not a tracker.
  :#111           a CSS colour. Three or six hex digits after a colon is a hex triplet.
  &#39;           an HTML numeric character reference. This tree serves a sign-in page, so
                  they appear in the string literals that build its markup.

One path is exempt, and only one: this gate's own test corpus. A gate for forbidden strings must
contain forbidden strings, the same way a spam filter's fixtures contain spam. It is named
explicitly rather than matched by a pattern, so the exemption cannot quietly widen — and the gate's
own script is deliberately NOT on the list, which is why the placeholders in this docstring are
written inside code spans like every other quotation of the rule.

Run with no arguments to check the whole tree; pass paths to check just those.
"""

from __future__ import annotations

import pathlib
import re
import subprocess
import sys

# The suffixes worth reading. A reference can only mislead where a human or GitHub renders it.
TEXT_SUFFIXES = frozenset(
    {".cs", ".md", ".py", ".yml", ".yaml", ".json", ".ps1", ".txt", ".props", ".csproj", ".slnx"}
)

# ⛔ Exactly one path, spelled out. A gate for forbidden strings has to contain forbidden strings.
# Widening this to the directory would exempt the gate's own script, whose prose is ordinary prose
# and should be held to the rule like anything else.
SELF_EXEMPT = frozenset({"tools/public-hygiene/test_check_public_references.py"})

ISSUE_REFERENCE = re.compile(r"#\d+")
DECISION_REFERENCE = re.compile(r"\bD-[A-Z]{2}\b")

# Structural exemptions, each anchored to the character sequence immediately before the match so
# that "looks like an exemption somewhere on the line" cannot launder a real violation.
HOST_ORDINAL = re.compile(r"(?i)\bhost $")
HTML_ENTITY = re.compile(r"&$")
CSS_COLOUR = re.compile(r":$")
CSS_DIGITS = re.compile(r"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})\b")


def tracked_files(root: pathlib.Path) -> list[pathlib.Path]:
    """Every tracked file, because an untracked one is not published."""
    listed = subprocess.run(
        ["git", "-C", str(root), "ls-files", "-z"],
        check=True,
        capture_output=True,
        text=True,
    ).stdout
    return [root / name for name in listed.split("\0") if name]


def code_spans(line: str) -> list[tuple[int, int]]:
    """Half-open ranges covered by backtick-delimited code spans.

    An unmatched trailing backtick covers nothing: a lone backtick in prose must not silence the
    rest of the line.
    """
    spans: list[tuple[int, int]] = []
    ticks = [match.start() for match in re.finditer(r"`", line)]
    for index in range(0, len(ticks) - 1, 2):
        spans.append((ticks[index], ticks[index + 1] + 1))
    return spans


def link_spans(line: str) -> list[tuple[int, int]]:
    """Half-open ranges covered by a Markdown link target — the "](...)" half.

    Both forms are targets and both resolve for any reader: "](#heading)" points inside this file,
    "](format.md#heading)" points at a section of another file in this repository. Matching the
    whole target rather than the two characters before the hash is what covers the second one.
    """
    spans: list[tuple[int, int]] = []
    start = line.find("](")
    while start != -1:
        end = line.find(")", start + 2)
        if end == -1:
            break
        spans.append((start, end + 1))
        start = line.find("](", end)
    return spans


def exempt(line: str, start: int, end: int, spans: list[tuple[int, int]]) -> bool:
    if any(low <= start < high for low, high in spans):
        return True

    before = line[:start]
    if HOST_ORDINAL.search(before) or HTML_ENTITY.search(before):
        return True

    return bool(CSS_COLOUR.search(before) and CSS_DIGITS.match(line[start:end]))


def violations(path: pathlib.Path, relative: str) -> list[str]:
    try:
        text = path.read_text(encoding="utf-8")
    except (UnicodeDecodeError, OSError):
        # Not text after all, so nothing renders. Silence here is correct, not a gap.
        return []

    found: list[str] = []
    for number, line in enumerate(text.splitlines(), start=1):
        spans = code_spans(line) + link_spans(line)

        for match in ISSUE_REFERENCE.finditer(line):
            if not exempt(line, match.start(), match.end(), spans):
                found.append(
                    f"{relative}:{number}: {match.group()} cites an issue in a private tracker; "
                    f"where GitHub renders Markdown it also auto-links to an unrelated issue in "
                    f"THIS public repository"
                )

        for match in DECISION_REFERENCE.finditer(line):
            if not any(low <= match.start() < high for low, high in spans):
                found.append(
                    f"{relative}:{number}: {match.group()} is a decision-log id from a private "
                    f"repository, and names a document no reader here can open"
                )

    return found


def main(argv: list[str]) -> int:
    root = pathlib.Path(
        subprocess.run(
            ["git", "rev-parse", "--show-toplevel"], check=True, capture_output=True, text=True
        ).stdout.strip()
    )

    if argv:
        paths = [pathlib.Path(argument).resolve() for argument in argv]
    else:
        paths = tracked_files(root)

    found: list[str] = []
    checked = 0
    for path in paths:
        if path.suffix.lower() not in TEXT_SUFFIXES or not path.is_file():
            continue
        relative = path.relative_to(root).as_posix()
        if relative in SELF_EXEMPT:
            continue

        checked += 1
        found.extend(violations(path, relative))

    if found:
        print(f"Refused {len(found)} private reference(s) in {checked} file(s):\n")
        for line in found:
            print(f"  {line}")
        print(
            "\nEverything in this repository is world-readable, permanently. State the reasoning "
            "in prose instead of citing something only we can open — the argument has to stand on "
            "its own for a reader who has no access to the tracker."
        )
        return 1

    print(f"OK: {checked} file(s) cite nothing that only resolves in a private repository.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
