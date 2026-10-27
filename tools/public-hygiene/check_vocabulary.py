#!/usr/bin/env python3
"""Refuse a banned term in a doc, a README or a user-visible host string.

This is the `references` job's third gate, and it enforces the one list that was prose until now:
the glossary's "Avoid list" (`CONTEXT.md`). The glossary says which words the public surface does
not use and what to say instead; without a gate the list stays true only until the first pull
request that forgets it. The sibling scripts refuse a reference nobody outside can follow; this one
refuses a word the project has decided not to say.

THE LIST IS READ, NOT COPIED. The terms come from the table under the glossary's `## Avoid list`
heading, parsed on every run. Adding a row there changes what is refused with no edit here, and a
second copy of the list in this file is exactly the cache this repository refuses to keep. A row
reads:

    | "term", "another term", said about bundles | the wording to use instead |

  the quoted strings   the banned terms, matched as whole words, case-insensitively, with any run
                       of whitespace (a line wrap included) between the words of a phrase.
  "said about X"       optional. The term is refused only in a unit of text that also mentions X
                       (X is read by its stem, so "bundles" also finds "bundle"). "own" is an
                       ordinary word, and the glossary bans it only about bundles; "about" means X
                       appears in the same sentence or clause (a colon or semicolon ends one) as the term. A wider window finds "own" beside
                       an unrelated "bundle" in a long list item, and a gate that cries wolf is
                       switched off; the cost is that "own" one sentence from its bundle is missed. A row with no qualifier is refused everywhere.
  the second column    the replacement, quoted back in every finding.

WHAT IS CHECKED, and the exemptions that keep it honest:

  docs                 Every tracked Markdown file. A fenced block and an inline code span are
                       blanked first (a code identifier is exempt, the glossary's own rule), and so
                       is a link target, which is an address and not prose.
  host strings         A string literal in production host code: C#, C++ headers and sources, and
                       Python under `unreal/`, `revit/` and `blender/`, `.uplugin` included. Comments are not
                       strings and are not read. A path with a `test` component is not read, since a
                       test's name for a case is not shown to a curator. A literal with no space and
                       no hyphen is an identifier, a key or a path, never a sentence, and is exempt.
  the glossary         `CONTEXT.md` states the list, so the Avoid-list section is exempt, and a
                       term inside quotation marks anywhere in it is a mention, not a use.
  "its own"            The bare word "own" directly after a possessive ("its own", "a host's own")
                       is the adjective, which says nothing about who holds a bundle, and is never
                       refused. The same word as a verb or in "owned", "owning", "ownership" is.

What the gate does not judge: whether a sentence that avoids the list reads well. It matches words.

Stdlib only, no network, like the rest of the job.

Run with no arguments to check the tree this script lives in; `--root PATH` checks another.
"""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

from check_docs_integrity import tracked_markdown, without_code

REPO_ROOT = Path(__file__).resolve().parents[2]

# The glossary, and the heading its banned-term table sits under.
GLOSSARY = "CONTEXT.md"
AVOID_HEADING = re.compile(r"(?m)^##[ \t]+Avoid list[ \t]*$")
ANY_H2 = re.compile(r"(?m)^##[ \t]+\S")

# Where host strings live. Production code only; the scan skips any path with a test component.
HOST_ROOTS = ("unreal/", "revit/", "blender/")
HOST_SUFFIXES = (".cs", ".cpp", ".h", ".py", ".uplugin")
TEST_PATH = re.compile(r"(?i)(^|/)[^/]*tests?[^/]*(/|\.[a-z]+$)|(^|/)test_[^/]*$")

# A quoted term in the table's first column, and the optional "said about X" after the last one.
QUOTED = re.compile(r'"([^"]+)"|“([^”]+)”')
SAID_ABOUT = re.compile(r"(?i)\bsaid about\s+(?:the\s+)?([a-z][a-z-]*)")

# The word "own" after one of these is the adjective ("its own", "a host's own").
POSSESSIVE_BEFORE = re.compile(r"(?i)(?:\b(?:my|our|your|his|her|its|their|whose)|['’]s?)\s+[*_]*$")

# A string with none of these is an identifier, a key or a path rather than prose.
PROSE_MARK = re.compile(r"[\s-]")

# A mention in quotation marks inside the glossary.
QUOTED_SPAN = re.compile(r'"[^"\n]*"|“[^”\n]*”')

LINK_TARGET = re.compile(r"\]\(\s*(?:[^()\s]|\([^()]*\))*|^[ \t]{0,3}\[[^\]]+\]:[ \t]*\S+|https?://\S+", re.M)
LIST_OR_ROW = re.compile(r"^\s*(?:[-*+]\s|\d+[.)]\s|\||#)")


@dataclass(frozen=True)
class Rule:
    """One row of the Avoid list."""

    terms: tuple[str, ...]
    scope: str | None  # the stem a unit must mention for the terms to count, or None
    replacement: str
    pattern: re.Pattern[str]
    scope_pattern: re.Pattern[str] | None


@dataclass(frozen=True)
class Finding:
    path: str
    line: int
    term: str
    scope: str | None
    replacement: str

    def render(self) -> str:
        about = f" (said about {self.scope})" if self.scope else ""
        return f'{self.path}:{self.line}: "{self.term}"{about} -- use instead: {self.replacement}'


def term_pattern(terms: tuple[str, ...]) -> re.Pattern[str]:
    """Whole-word, case-insensitive, any whitespace between the words of a phrase."""
    alternatives = [r"\s+".join(re.escape(word) for word in term.split()) for term in terms]
    alternatives.sort(key=len, reverse=True)
    return re.compile(r"(?<![\w-])(?:" + "|".join(alternatives) + r")(?![\w-])", re.IGNORECASE)


def avoid_section(glossary: str) -> str:
    """The text between the `## Avoid list` heading and the next `##` heading, or ''."""
    heading = AVOID_HEADING.search(glossary)
    if not heading:
        return ""
    rest = glossary[heading.end() :]
    following = ANY_H2.search(rest)
    return rest[: following.start()] if following else rest


def section_span(glossary: str) -> tuple[int, int]:
    """Character offsets of the Avoid-list section in the glossary, (0, 0) when it has none."""
    heading = AVOID_HEADING.search(glossary)
    if not heading:
        return (0, 0)
    following = ANY_H2.search(glossary, heading.end())
    return (heading.start(), following.start() if following else len(glossary))


def parse_rules(glossary: str) -> list[Rule]:
    """The Avoid-list table as rules. Cells without a quoted term, and the header row, are skipped."""
    rules: list[Rule] = []
    for row in avoid_section(glossary).splitlines():
        cells = [cell.strip() for cell in row.strip().strip("|").split("|")]
        if not row.lstrip().startswith("|") or len(cells) < 2:
            continue
        banned, replacement = cells[0], cells[1]
        terms = tuple(a or b for a, b in QUOTED.findall(banned))
        if not terms:
            continue
        # Only the text after the last quoted term is the qualifier, so a quoted term can never
        # be mistaken for one.
        last = list(QUOTED.finditer(banned))[-1]
        said = SAID_ABOUT.search(banned[last.end() :])
        scope = said.group(1).lower() if said else None
        stem = re.sub(r"(es|s)$", "", scope) if scope else None
        rules.append(
            Rule(
                terms=terms,
                scope=scope,
                replacement=replacement,
                pattern=term_pattern(terms),
                scope_pattern=re.compile(r"\b" + re.escape(stem), re.IGNORECASE) if stem else None,
            )
        )
    return rules


def units(text: str) -> list[tuple[int, str]]:
    """The text in units, as (offset of the unit's first character, unit text).

    A unit ends at a blank line, and a list item, a table row or a heading starts a new one.
    """
    result: list[tuple[int, str]] = []
    start: int | None = None
    offset = 0
    pieces: list[str] = []
    for line in text.split("\n"):
        blank = not line.strip()
        if blank or LIST_OR_ROW.match(line):
            if start is not None:
                result.append((start, "".join(pieces)))
                start, pieces = None, []
        if not blank:
            if start is None:
                start = offset
            pieces.append(line + "\n")
        offset += len(line) + 1
    if start is not None:
        result.append((start, "".join(pieces)))
    return result


SENTENCE_END = re.compile(r"(?<=[.!?:;])\s+")


def in_scope(rule: Rule, unit: str, position: int) -> bool:
    """Whether the rule's qualifier is met at a position: the sentence holding it says X."""
    if not rule.scope_pattern:
        return True
    starts = [0] + [m.end() for m in SENTENCE_END.finditer(unit)]
    index = max(i for i, start in enumerate(starts) if start <= position)
    last = starts[index + 1] if index + 1 < len(starts) else len(unit)
    return bool(rule.scope_pattern.search(unit[starts[index] : last]))


def refused(rule: Rule, unit: str, masked: str) -> list[tuple[int, str]]:
    """Matches of the rule in one unit, as (offset in the unit, matched text)."""
    found: list[tuple[int, str]] = []
    for match in rule.pattern.finditer(masked):
        if match.group(0).lower() == "own" and POSSESSIVE_BEFORE.search(masked[: match.start()]):
            continue
        if in_scope(rule, unit, match.start()):
            found.append((match.start(), match.group(0)))
    return found


def blank(text: str, pattern: re.Pattern[str]) -> str:
    """The text with every match replaced by spaces, newlines kept so line numbers stay true."""
    return pattern.sub(lambda match: re.sub(r"[^\n]", " ", match.group(0)), text)


def document_findings(path: str, text: str, rules: list[Rule]) -> list[Finding]:
    prose = blank(without_code(text), LINK_TARGET)
    exempt_from, exempt_to = (0, 0)
    if path == GLOSSARY:
        exempt_from, exempt_to = section_span(prose)
        prose = blank(prose, QUOTED_SPAN)
    findings: list[Finding] = []
    for offset, unit in units(prose):
        for rule in rules:
            for at, term in refused(rule, unit, unit):
                absolute = offset + at
                if exempt_from <= absolute < exempt_to:
                    continue
                findings.append(
                    Finding(path, prose.count("\n", 0, absolute) + 1, term, rule.scope, rule.replacement)
                )
    return findings


def string_literals(path: str, source: str) -> list[tuple[int, str]]:
    """Every string literal in the source, as (offset of its first content character, content).

    A scanner rather than a regex, because a comment can hold a quote and a string can hold `//`.
    It reads `//` and `/* */` comments for the C family and `#` for Python, ordinary and verbatim
    strings, C# raw strings and C++ raw strings. Python triple-quoted strings are docstrings and
    are skipped. Anything it cannot parse is left, never guessed at.
    """
    python = path.endswith(".py")
    json_like = path.endswith(".uplugin")
    found: list[tuple[int, str]] = []
    i, n = 0, len(source)
    while i < n:
        char = source[i]
        if json_like:
            pass
        elif python and char == "#":
            i = source.find("\n", i)
            i = n if i < 0 else i
            continue
        elif not python and source.startswith("//", i):
            i = source.find("\n", i)
            i = n if i < 0 else i
            continue
        elif not python and source.startswith("/*", i):
            end = source.find("*/", i + 2)
            i = n if end < 0 else end + 2
            continue
        if python and source.startswith(('"""', "'''"), i):
            end = source.find(source[i : i + 3], i + 3)
            i = n if end < 0 else end + 3
            continue
        if not python and not json_like and source.startswith('R"', i):
            opening = source.find("(", i)
            delimiter = source[i + 2 : opening] if opening > 0 else None
            if delimiter is not None and len(delimiter) <= 16 and '"' not in delimiter:
                close = ")" + delimiter + '"'
                end = source.find(close, opening)
                if end >= 0:
                    found.append((opening + 1, source[opening + 1 : end]))
                    i = end + len(close)
                    continue
        if not python and not json_like and source.startswith('"""', i):
            end = source.find('"""', i + 3)
            if end >= 0:
                found.append((i + 3, source[i + 3 : end]))
                i = end + 3
                continue
        if char == '"' or (python and char == "'"):
            verbatim = path.endswith(".cs") and i > 0 and source[i - 1] == "@"
            j = i + 1
            while j < n:
                if verbatim:
                    if source[j] == '"':
                        if source.startswith('""', j):
                            j += 2
                            continue
                        break
                else:
                    if source[j] == "\\":
                        j += 2
                        continue
                    if source[j] == char or source[j] == "\n":
                        break
                j += 1
            if j < n and source[j] == char:
                found.append((i + 1, source[i + 1 : j]))
            i = j + 1
            continue
        if char == "'" and not python and not (i > 0 and source[i - 1].isalnum()):
            # A character literal, which is never prose; step over it so '"' is not a string.
            j = i + 1
            while j < n and source[j] != "'" and source[j] != "\n":
                j += 2 if source[j] == "\\" else 1
            i = j + 1
            continue
        i += 1
    return found


def host_string_findings(path: str, source: str, rules: list[Rule]) -> list[Finding]:
    findings: list[Finding] = []
    for offset, content in string_literals(path, source):
        if not PROSE_MARK.search(content):
            continue
        for rule in rules:
            for at, term in refused(rule, content, content):
                line = source.count("\n", 0, offset + at) + 1
                findings.append(Finding(path, line, term, rule.scope, rule.replacement))
    return findings


def tracked_host_sources(root: Path) -> list[str]:
    listed = subprocess.run(
        ["git", "-C", str(root), "ls-files", "-z"],
        check=True,
        capture_output=True,
        text=True,
    ).stdout
    return sorted(
        name
        for name in listed.split("\0")
        if name.startswith(HOST_ROOTS) and name.endswith(HOST_SUFFIXES) and not TEST_PATH.search(name)
    )


def run(root: Path) -> int:
    glossary_path = root / GLOSSARY
    if not glossary_path.is_file():
        print(f"Refused: {GLOSSARY} is missing, so there is no Avoid list to enforce.")
        return 1
    rules = parse_rules(glossary_path.read_text(encoding="utf-8"))
    if not rules:
        # A list that parses to nothing would pass everything: the failure to refuse here is the
        # one a reformatted table would otherwise cause in silence.
        print(
            f"Refused: no banned term could be read from the '## Avoid list' table in {GLOSSARY}. "
            'A row is | "term", "another term" | the wording to use instead |.'
        )
        return 1

    documents = tracked_markdown(root)
    sources = tracked_host_sources(root)
    findings: list[Finding] = []
    for name in documents:
        findings.extend(document_findings(name, (root / name).read_text(encoding="utf-8"), rules))
    for name in sources:
        findings.extend(host_string_findings(name, (root / name).read_text(encoding="utf-8"), rules))

    if findings:
        print(f"Refused {len(findings)} banned term(s):\n")
        for finding in sorted(findings, key=lambda f: (f.path, f.line, f.term)):
            print(f"  {finding.render()}")
        print(
            f"\nThe list is the '## Avoid list' table in {GLOSSARY}; it covers docs, READMEs and "
            "user-visible strings, and code identifiers are exempt. Use the wording in the last "
            "column, or, if the term should be allowed, change the glossary rather than this "
            "script. See tools/public-hygiene/check_vocabulary.py for what each rule matches."
        )
        return 1

    print(
        f"OK: {len(rules)} Avoid-list row(s) read from {GLOSSARY}; {len(documents)} document(s) and "
        f"{len(sources)} host source file(s) use none of them."
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
    return run(parser.parse_args(argv).root)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
