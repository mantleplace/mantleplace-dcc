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

Files are one surface of five. A commit message, a pull request title or body, and a branch name
are all world-readable the moment they are pushed — the branch name before any review exists — and
a commit message can never be edited at all. Those "metadata" surfaces carry a different pattern
set, because what renders there differs in kind:

  #NNN            ALLOWED here. In a commit message or PR body a bare number is this repository's
                  native way to cite its own issue, and GitHub appends one to every squash-merge
                  subject. Refusing it would flag every merge on main.
  repo`#`NNN      Refused, own repository excepted. Qualified or not, a reference into another
                  repository hands the reader a name they cannot open and a 404 — CLAUDE.md forbids
                  the citation in both shapes, by explicit decision rather than by accident.
  sibling names   Refused structurally: the pattern is "this project's naming stem, not this
                  repository", so no private repository is ever named in this file. The tracked
                  artifact stems that must keep working are the only literals it carries.
  nat `#`NNN      The shorthand shape that reached a real pull request body. Lowercase and followed
                  by a reference, so prose about NAT traversal is untouched.

A branch name additionally has a shape rule: a path segment that starts with digits is an issue
number wearing a slash, whatever tracker it came from — the convention is type/short-description.

Run with no arguments to check the whole tree; pass paths to check just those. The other surfaces:
`--stdin --label "PR body"` checks piped text, `--branch-name NAME` a ref name,
`--commit-msg-file PATH` a message being written, and `--commit-range ARGS...` every commit
`git log` selects — pass `HEAD --not origin/main`-style arguments so history that predates the gate
is never re-litigated.
"""

from __future__ import annotations

import argparse
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

# Metadata-surface patterns. QUALIFIED_REFERENCE needs a word character hard against the hash, so
# it never overlaps the bare form the metadata surface allows. PRIVATE_SIBLING is structural — the
# stem plus "not this repository" — with the negative lookahead also carrying the two tracked
# artifact-filename stems (`.mantleplace-import.log`, `mantleplace-terrain*.log`) that legitimate
# commit messages must be able to name; its trailing (?!#) hands the qualified form to
# QUALIFIED_REFERENCE so one token is one finding.
QUALIFIED_REFERENCE = re.compile(r"\b[\w.-]+(?:/[\w.-]+)?#\d+")
PRIVATE_SIBLING = re.compile(r"\bmantleplace-(?!dcc\b|import\b|terrain\b)[a-z][a-z0-9-]*\b(?!#)")
SHORTNAME_REFERENCE = re.compile(r"\bnat(?=\s+(?:PR\s+)?#\d+)")
OWN_REPOSITORY = frozenset({"mantleplace-dcc", "mantleplace/mantleplace-dcc"})

# The shape rule for branch names: a path segment that opens with digits. Anchored to the segment
# start so version digits inside a word (ue5-8, net10, mpb-1.0.0) stay legal.
NUMERIC_SEGMENT = re.compile(r"(?:^|/)\d+(?:[-_/]|$)")

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


def inside_url(line: str, start: int) -> bool:
    """True when the match sits inside a URL token — a public address whose fragment or path is
    allowed to contain slashes and hashes. Structural: the token the match belongs to, read back to
    the nearest whitespace, carries a scheme separator."""
    token_start = max(line.rfind(" ", 0, start), line.rfind("\t", 0, start)) + 1
    return "://" in line[token_start:start]


def violations_in_text(text: str, label: str, surface: str = "file") -> list[str]:
    """Every finding in one piece of text, labelled so the report points at what to edit.

    The surface picks the pattern set. "file" is tracked-file prose, where a bare number is the
    hazard. "metadata" is a commit message, PR title or body, or branch name, where a bare number
    is this repository's own voice and the hazards are the qualified and named shapes. The
    exemption machinery is shared: a code span documents the rule on every surface.
    """
    found: list[str] = []
    for number, line in enumerate(text.splitlines(), start=1):
        spans = code_spans(line) + link_spans(line)

        if surface == "file":
            for match in ISSUE_REFERENCE.finditer(line):
                if not exempt(line, match.start(), match.end(), spans):
                    found.append(
                        f"{label}:{number}: {match.group()} cites an issue in a private tracker; "
                        f"where GitHub renders Markdown it also auto-links to an unrelated issue "
                        f"in THIS public repository"
                    )
        else:
            for match in QUALIFIED_REFERENCE.finditer(line):
                qualifier = match.group().rsplit("#", 1)[0]
                if qualifier in OWN_REPOSITORY:
                    continue
                if inside_url(line, match.start()):
                    continue
                if not exempt(line, match.start(), match.end(), spans):
                    found.append(
                        f"{label}:{number}: {match.group()} cites an issue in another repository "
                        f"— a stranger meets a 404, and the rule forbids the citation either way"
                    )

            for match in PRIVATE_SIBLING.finditer(line):
                if not exempt(line, match.start(), match.end(), spans):
                    found.append(
                        f"{label}:{number}: {match.group()} names a private sibling repository; "
                        f"say which side owns the work without naming what a reader cannot open"
                    )

            for match in SHORTNAME_REFERENCE.finditer(line):
                if not exempt(line, match.start(), match.end(), spans):
                    found.append(
                        f"{label}:{number}: '{match.group()} #NN' is shorthand for a private "
                        f"repository's issue, and resolves for nobody reading this"
                    )

        for match in DECISION_REFERENCE.finditer(line):
            if not any(low <= match.start() < high for low, high in spans):
                found.append(
                    f"{label}:{number}: {match.group()} is a decision-log id from a private "
                    f"repository, and names a document no reader here can open"
                )

    return found


def violations(path: pathlib.Path, relative: str) -> list[str]:
    try:
        text = path.read_text(encoding="utf-8")
    except (UnicodeDecodeError, OSError):
        # Not text after all, so nothing renders. Silence here is correct, not a gap.
        return []

    return violations_in_text(text, relative, surface="file")


def branch_name_violations(name: str) -> list[str]:
    """A branch name is a metadata surface with one extra rule: its shape. A path segment opening
    with digits is an issue number whatever tracker it came from, and the public remote renders it
    in the pull request header forever — deleting the ref later does not unpublish it."""
    found = violations_in_text(name, f"branch {name}", surface="metadata")
    if NUMERIC_SEGMENT.search(name):
        found.append(
            f"branch {name}:1: a path segment starts with digits, which reads as an issue number; "
            f"branch names here are type/short-description, and the cross-reference belongs in the "
            f"private side's pin-bump pull request"
        )
    return found


def commit_messages(log_arguments: list[str]) -> list[tuple[str, str]]:
    """(label, full message) for every commit `git log` selects. The caller passes exclusion-style
    arguments ("HEAD --not origin/main") so main's own history — which contains messages that
    predate this gate and are accepted as record — is never re-litigated."""
    listed = subprocess.run(
        ["git", "log", "--format=%h%x01%B%x00", *log_arguments],
        check=True,
        capture_output=True,
        text=True,
    ).stdout
    selected: list[tuple[str, str]] = []
    for record in listed.split("\0"):
        record = record.strip("\n")
        if not record:
            continue
        short, message = record.split("\x01", 1)
        selected.append((f"commit {short}", message))
    return selected


def report(found: list[str], checked: int, unit: str) -> int:
    if found:
        print(f"Refused {len(found)} private reference(s) in {checked} {unit}:\n")
        for line in found:
            print(f"  {line}")
        print(
            "\nEverything in this repository is world-readable, permanently. State the reasoning "
            "in prose instead of citing something only we can open — the argument has to stand on "
            "its own for a reader who has no access to the tracker."
        )
        return 1

    print(f"OK: {checked} {unit} cite nothing that only resolves in a private repository.")
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("paths", nargs="*", help="tracked files to check; none means the tree")
    parser.add_argument("--stdin", action="store_true", help="check text piped on stdin")
    parser.add_argument("--label", default="stdin", help="what the piped text is, for the report")
    parser.add_argument("--branch-name", help="check one ref name, shape rule included")
    parser.add_argument("--commit-msg-file", help="check a message being written (commit-msg hook)")
    parser.add_argument(
        "--commit-range",
        nargs=argparse.REMAINDER,
        help="check every commit these git-log arguments select, e.g. HEAD --not origin/main",
    )
    arguments = parser.parse_args(argv)

    if arguments.stdin:
        found = violations_in_text(sys.stdin.read(), arguments.label, surface="metadata")
        return report(found, 1, "text(s)")

    if arguments.branch_name:
        found = branch_name_violations(arguments.branch_name)
        return report(found, 1, "branch name(s)")

    if arguments.commit_msg_file:
        text = pathlib.Path(arguments.commit_msg_file).read_text(encoding="utf-8")
        found = violations_in_text(text, "commit message", surface="metadata")
        return report(found, 1, "message(s)")

    if arguments.commit_range:
        found = []
        selected = commit_messages(arguments.commit_range)
        for label, message in selected:
            found.extend(violations_in_text(message, label, surface="metadata"))
        return report(found, len(selected), "commit message(s)")

    root = pathlib.Path(
        subprocess.run(
            ["git", "rev-parse", "--show-toplevel"], check=True, capture_output=True, text=True
        ).stdout.strip()
    )

    if arguments.paths:
        paths = [pathlib.Path(argument).resolve() for argument in arguments.paths]
    else:
        paths = tracked_files(root)

    found = []
    checked = 0
    for path in paths:
        if path.suffix.lower() not in TEXT_SUFFIXES or not path.is_file():
            continue
        relative = path.relative_to(root).as_posix()
        if relative in SELF_EXEMPT:
            continue

        checked += 1
        found.extend(violations(path, relative))

    return report(found, checked, "file(s)")


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
