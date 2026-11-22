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
use as prose: HPS-NN and their kin. The distinction is the one CLAUDE.md already draws --
rule ids are fine, links to them are not.

Exemptions are structural rather than a list of blessed strings, so that a legitimate construct
keeps working as the tree grows and an illegitimate one cannot be waved through by adding a line to
a file nobody rereads:

  ](#anchor)      a Markdown link target, same-document or cross-file ("](format.md#3-heading)").
                  Both resolve for every reader, inside this repository.
  `#42`           a fenced or inline code span. This is how the rule documents ITSELF, and a gate
                  that fails its own rulebook is a gate that gets deleted.
  host #2         an ordinal in prose. "Host #2" is a count of DCC hosts, not a tracker.
  :#111           a CSS colour. Three or six hex characters after a colon is a hex triplet --
                  characters, not digits, because `#0B0C10` is a colour and `#0` is not a citation.
  &#39;           an HTML numeric character reference. This tree serves a sign-in page, so
                  they appear in the string literals that build its markup.

One path is exempt, and only one: this gate's own test corpus. A gate for forbidden strings must
contain forbidden strings, the same way a spam filter's fixtures contain spam. It is named
explicitly rather than matched by a pattern, so the exemption cannot quietly widen — and the gate's
own script is deliberately NOT on the list, which is why the placeholders in this docstring are
written inside code spans like every other quotation of the rule.

Files are one surface of six. A commit message, a pull request title or body, a branch name, and
the issue tracker's own text are all world-readable the moment they are pushed or posted — the
branch name before any review exists, a tracker post with no draft state at all — and a commit
message can never be edited. Those "metadata" surfaces carry a different pattern set, because what
renders there differs in kind:

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

Two further families apply here. The sibling pattern now applies to tracked files too, with the same
exemptions (a code span, a link target) and the same two artifact filenames, plus the two things a
tracked tree legitimately carries that a commit message does not: the product's PascalCase strings
(a closed set of exact-case brand strings, see BRAND_TAILS, written into layer and archive names) and a short
list of tracked identifiers (see FILE_ARTIFACT_NAMES). And agent tooling that is not published here
is refused on EVERY surface, files included, by structure rather than by name:

  plugin@catalogue  A plugin installed from, or enabled by a setting naming, a catalogue called after
                    the project's stem. One pattern covers an install command and an enabling
                    setting, because both write the same `name@catalogue` token.
  /stem-x:command   A command namespaced by such a plugin, with or without the leading slash.

A code span is deliberately NOT an exemption for these two: an install line is nearly always written
inside one, so exempting spans would gut the check. No private name is written in this file; the
only literal either pattern carries is the stem itself.

A branch name additionally has a shape rule: a path segment that starts with digits is an issue
number wearing a slash, whatever tracker it came from — the convention is type/short-description.

The tracker is the sixth surface and needs no new rules — an issue title, an issue body and a
comment are metadata in exactly this sense, so they go through `--stdin` with the metadata pattern
set. What is different is the reporting: CI cannot block a post the way a required check blocks a
merge, because the text is already public by the time the workflow starts. So the tracker gate
detects rather than prevents, and it reports by posting a comment on the offending issue — which is
why `--redact` exists. See REDACTIONS.

Run with no arguments to check the whole tree; pass paths to check just those. The other surfaces:
`--stdin --label "PR body"` checks piped text, `--branch-name NAME` a ref name,
`--commit-msg-file PATH` a message being written, and `--commit-range ARGS...` every commit
`git log` selects — pass `HEAD --not origin/main`-style arguments so history that predates the gate
is never re-litigated. Add `--redact` to any of them when the report itself will be published.
"""

from __future__ import annotations

import argparse
import pathlib
import re
import subprocess
import sys

TEXT_SUFFIXES = frozenset(
    {".cs", ".md", ".py", ".yml", ".yaml", ".json", ".ps1", ".txt", ".props", ".csproj", ".slnx"}
)

SELF_EXEMPT = frozenset({"tools/public-hygiene/test_check_public_references.py"})

ISSUE_REFERENCE = re.compile(r"#\d+")
DECISION_REFERENCE = re.compile(r"\bD-[A-Z]{2}\b")

QUALIFIED_REFERENCE = re.compile(r"\b[\w.-]+(?:/[\w.-]+)?#\d+")
PRIVATE_SIBLING = re.compile(
    r"(?i)\bmantleplace-(?!dcc\b|import\.log|terrain[\w-]*\.log)[a-z][a-z0-9-]*\b(?!#)"
)

PLUGIN_AT_CATALOGUE = re.compile(r"(?i)\b[\w.-]+@mantleplace(?:-[a-z0-9]+)*(?![\w-]|\.[a-z0-9])")
NAMESPACED_COMMAND = re.compile(
    r"(?i)(?:(?<![\w/.:~])/mantleplace(?:-[a-z0-9]+)*"
    r"|(?<![\w/.:~-])mantleplace-(?!dcc(?![a-z0-9-]))[a-z0-9]+(?:-[a-z0-9]+)*)"
    r":[a-z][\w-]*"
)

BRAND_TAILS = frozenset(
    {
        "Terms",
        "Surface",
        "Revit",
        "UE5",
        "AutoCad",
        "Linework",
        "Contours",
        "Aerial",
        "Records",
        "Sample",
    }
)
BRAND_CASED = re.compile(r"MantlePlace-(?:" + "|".join(sorted(BRAND_TAILS)) + r")(?![A-Za-z0-9])")

FILE_ARTIFACT_NAMES = frozenset(
    {
        "mantleplace-mpb",
        "mantleplace-mpb-dist",
        "mantleplace-autocad-bundle",
        "mantleplace-revit-bundle",
        "mantleplace-adjacent",
        "mantleplace-carrier",
        "mantleplace-levels",
        "mantleplace-local-import-tests",
        "mantleplace-platform-api",
        "mantleplace-stamp",
        "mantleplace-timing-declined",
        "mantleplace-trusted",
    }
)
SHORTNAME_REFERENCE = re.compile(r"\b[Nn]at(?=\s+(?:(?i:PR|issue)\s+)?#\d+)")
OWN_REPOSITORY = frozenset({"mantleplace-dcc", "mantleplace/mantleplace-dcc"})

PUBLISHED_STANDARD = "docs/host-plugin-standard.md"
RULE_REFERENCE = re.compile(r"\bHPS-\d+[a-z]?\b")

RULE_DEFINITION = re.compile(r"(?m)^\s*(?:[-*]\s+)?(?:⛔\s*)?(?:\*\*)?`(HPS-\d+[a-z]?)`\s+—")

RULE_SELF_EXEMPT = frozenset({PUBLISHED_STANDARD})

NUMERIC_SEGMENT = re.compile(r"(?i)(?:^|/)(?:#|(?:issue|gh)-?)?\d+(?:[-_/]|$)")

HOST_ORDINAL = re.compile(r"(?i)\bhost $")
HTML_ENTITY = re.compile(r"&$")
CSS_COLOUR = re.compile(r":$")
CSS_DIGITS = re.compile(r"^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6})\b")


REDACTIONS = {
    "issue": "a refused token",
    "qualified": "a refused token",
    "sibling": "a refused token",
    "shorthand": "a refused token",
    "plugin": "a refused token",
    "command": "a refused token",
    "decision": "a refused token",
    "branch": "this branch name",
}


def shown(token: str, kind: str, redact: bool) -> str:
    """The token itself, or the category it belongs to when the finding is going somewhere public."""
    return REDACTIONS[kind] if redact else token


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


def exempt(line: str, start: int, spans: list[tuple[int, int]]) -> bool:
    if any(low <= start < high for low, high in spans):
        return True

    before = line[:start]
    if HOST_ORDINAL.search(before) or HTML_ENTITY.search(before):
        return True

    return bool(CSS_COLOUR.search(before) and CSS_DIGITS.match(line[start:]))


def inside_url(line: str, start: int) -> bool:
    """True when the match sits inside a URL token — a public address whose fragment or path is
    allowed to contain slashes and hashes. Structural: the token the match belongs to, read back to
    the nearest whitespace, carries a scheme separator."""
    token_start = max(line.rfind(" ", 0, start), line.rfind("\t", 0, start)) + 1
    return "://" in line[token_start:start]


def violations_in_text(
    text: str, label: str, surface: str = "file", redact: bool = False
) -> list[str]:
    """Every finding in one piece of text, labelled so the report points at what to edit.

    The surface picks the pattern set. "file" is tracked-file prose, where a bare number is the
    hazard. "metadata" is a commit message, PR title or body, a branch name, or tracker text — an
    issue title, an issue body, a comment — where a bare number is this repository's own voice and
    the hazards are the qualified and named shapes. The exemption machinery is shared: a code span
    documents the rule on every surface.

    `redact` replaces each refused token with the category it belongs to, for a report that is
    itself going to be published. See REDACTIONS.
    """
    found: list[str] = []
    for number, line in enumerate(text.splitlines(), start=1):
        spans = code_spans(line) + link_spans(line)

        tooling: list[tuple[int, int]] = []
        for pattern, kind, what in (
            (
                PLUGIN_AT_CATALOGUE,
                "plugin",
                "names a plugin from a catalogue that is not public (an install reference or a "
                "setting that enables one)",
            ),
            (
                NAMESPACED_COMMAND,
                "command",
                "is a slash command namespaced by agent tooling that is not published here",
            ),
        ):
            for match in pattern.finditer(line):
                tooling.append(match.span())
                found.append(
                    f"{label}:{number}: {shown(match.group(), kind, redact)} {what}; "
                    f"describe what the step does, not the tooling that runs it"
                )

        for match in PRIVATE_SIBLING.finditer(line):
            if exempt(line, match.start(), spans):
                continue
            if any(low <= match.start() < high for low, high in tooling):
                continue
            if surface == "file" and (
                BRAND_CASED.match(match.group()) or match.group() in FILE_ARTIFACT_NAMES
            ):
                continue
            found.append(
                f"{label}:{number}: {shown(match.group(), 'sibling', redact)} names a private "
                f"sibling repository; "
                f"say which side owns the work without naming what a reader cannot open"
            )

        if surface == "file":
            for match in ISSUE_REFERENCE.finditer(line):
                if not exempt(line, match.start(), spans):
                    found.append(
                        f"{label}:{number}: {shown(match.group(), 'issue', redact)} cites an issue "
                        f"in a private tracker; "
                        f"where GitHub renders Markdown it also auto-links to an unrelated issue "
                        f"in THIS public repository"
                    )
        else:
            for match in QUALIFIED_REFERENCE.finditer(line):
                qualifier = match.group().rsplit("#", 1)[0]
                if qualifier in OWN_REPOSITORY:
                    continue
                if len(qualifier) == 1:
                    continue
                if inside_url(line, match.start()):
                    continue
                if not exempt(line, match.start(), spans):
                    found.append(
                        f"{label}:{number}: {shown(match.group(), 'qualified', redact)} cites an issue "
                        f"in another repository "
                        f"— a stranger meets a 404, and the rule forbids the citation either way"
                    )

            for match in SHORTNAME_REFERENCE.finditer(line):
                if not exempt(line, match.start(), spans):
                    found.append(
                        f"{label}:{number}: {shown(match.group() + ' #NN', 'shorthand', redact)} is "
                        f"shorthand for a private "
                        f"repository's issue, and resolves for nobody reading this"
                    )

        for match in DECISION_REFERENCE.finditer(line):
            if not any(low <= match.start() < high for low, high in spans):
                found.append(
                    f"{label}:{number}: {shown(match.group(), 'decision', redact)} is a decision-log "
                    f"id from a private "
                    f"repository, and names a document no reader here can open"
                )

    return found


def violations(path: pathlib.Path, relative: str, redact: bool = False) -> list[str]:
    try:
        text = path.read_text(encoding="utf-8")
    except (UnicodeDecodeError, OSError):
        return []

    return violations_in_text(text, relative, surface="file", redact=redact)


def defined_rule_ids(published: str) -> set[str]:
    """Every rule the published standard actually states."""
    return set(RULE_DEFINITION.findall(published))


def unresolved_rule_ids(text: str, label: str, defined: set[str]) -> list[str]:
    """Every `HPS-NN` in this text that the published standard does not state.

    Reported per occurrence rather than per id: a file citing two unknown rules has two things to
    fix, and collapsing them costs the author a second round trip to learn the second one.
    """
    found: list[str] = []
    for number, line in enumerate(text.splitlines(), start=1):
        for match in RULE_REFERENCE.finditer(line):
            if match.group() in defined:
                continue
            found.append(
                f"{label}:{number}: {match.group()} resolves nowhere a reader here can reach — "
                f"state the rule in {PUBLISHED_STANDARD}, or replace the citation with prose"
            )
    return found


def branch_name_violations(name: str, redact: bool = False) -> list[str]:
    """A branch name is a metadata surface with one extra rule: its shape. A path segment opening
    with digits is an issue number whatever tracker it came from, and the public remote renders it
    in the pull request header forever — deleting the ref later does not unpublish it.

    The label carries the name itself, so it is redacted too when the report is being published."""
    where = "branch" if redact else f"branch {name}"
    found = violations_in_text(name, where, surface="metadata", redact=redact)
    if NUMERIC_SEGMENT.search(name):
        found.append(
            f"{where}:1: a path segment of {shown(name, 'branch', redact)} starts with digits, "
            f"which reads as an issue number; branch names here are type/short-description, and "
            f"the cross-reference belongs in the private side's pin-bump pull request"
        )
    return found


def commit_message_body(text: str) -> str:
    """What git will keep of COMMIT_EDITMSG. The commit-msg hook runs BEFORE cleanup: comment
    lines and everything below the scissors line (`git commit -v` puts the whole diff there) are
    still in the file, and judging them refuses commits over text that will never be part of the
    message — which teaches the author --no-verify. Judge the survivor instead. Assumes the
    default comment character; a custom core.commentChar makes this conservative, never unsafe."""
    kept: list[str] = []
    for line in text.splitlines():
        if line.startswith("#") and ">8" in line:
            break
        if line.startswith("#"):
            continue
        kept.append(line)
    return "\n".join(kept)


def commit_messages(log_arguments: list[str]) -> list[tuple[str, str]]:
    """(label, full message) for every commit `git log` selects. The caller passes exclusion-style
    arguments ("HEAD --not origin/main") so main's own history — which contains messages that
    predate this gate and are accepted as record — is never re-litigated."""
    try:
        listed = subprocess.run(
            ["git", "log", "--format=%h%x01%B%x00", *log_arguments],
            check=True,
            capture_output=True,
            text=True,
        ).stdout
    except subprocess.CalledProcessError as error:
        sys.stderr.write(error.stderr)
        sys.stderr.write(
            "The commit-range arguments did not resolve, so nothing was checked and this refuses "
            "rather than passes. Fix the range (git's message above names the revision) and rerun."
            "\n"
        )
        raise SystemExit(1)
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
        "--redact",
        action="store_true",
        help="name each finding's category instead of quoting the refused token, for a report "
        "that is itself going to be published",
    )
    parser.add_argument(
        "--commit-range",
        nargs=argparse.REMAINDER,
        help="check every commit these git-log arguments select, e.g. HEAD --not origin/main",
    )
    arguments = parser.parse_args(argv)

    if arguments.stdin:
        found = violations_in_text(
            sys.stdin.read(), arguments.label, surface="metadata", redact=arguments.redact
        )
        return report(found, 1, "text(s)")

    if arguments.branch_name is not None:
        if not arguments.branch_name:
            parser.error("--branch-name was given an empty value; nothing was checked")
        found = branch_name_violations(arguments.branch_name, redact=arguments.redact)
        return report(found, 1, "branch name(s)")

    if arguments.commit_msg_file is not None:
        if not arguments.commit_msg_file:
            parser.error("--commit-msg-file was given an empty value; nothing was checked")
        raw = pathlib.Path(arguments.commit_msg_file).read_text(
            encoding="utf-8", errors="replace"
        )
        found = violations_in_text(
            commit_message_body(raw), "commit message", surface="metadata", redact=arguments.redact
        )
        return report(found, 1, "message(s)")

    if arguments.commit_range is not None:
        if not arguments.commit_range:
            parser.error("--commit-range needs git-log arguments, e.g. HEAD --not origin/main")
        found = []
        selected = commit_messages(arguments.commit_range)
        for label, message in selected:
            found.extend(
                violations_in_text(message, label, surface="metadata", redact=arguments.redact)
            )
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

    standard = root / PUBLISHED_STANDARD
    defined = defined_rule_ids(standard.read_text(encoding="utf-8")) if standard.is_file() else set()

    found = []
    checked = 0
    for path in paths:
        if path.suffix.lower() not in TEXT_SUFFIXES or not path.is_file():
            continue
        relative = path.relative_to(root).as_posix()
        if relative in SELF_EXEMPT:
            continue

        checked += 1
        text = path.read_text(encoding="utf-8", errors="replace")
        found.extend(violations_in_text(text, relative, surface="file", redact=arguments.redact))
        if relative not in RULE_SELF_EXEMPT:
            found.extend(unresolved_rule_ids(text, relative, defined))

    return report(found, checked, "file(s)")


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
