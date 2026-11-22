---
name: adr-0032-code-carries-no-comments
description: Source code in this repository carries no comments; a comment that repeats the code or has gone stale is deleted, one that excuses a shortcut becomes an issue, one that records a non-obvious decision moves into an ADR, and a ratchet check fails any change that adds comment lines. Read before adding a comment, before writing a rationale next to code, or when the comment-ratchet check fails.
status: accepted
---

# 32. Code carries no comments

Date: 2026-11-21

## Status

Accepted. The decisions the removed comments held are recorded in ADRs 0020 to 0031.

## Context

Comments drift. A comment that repeats the code adds a second place to keep true, and a stale one
misleads a reader more than no comment. Rationale written next to a line of code is found only by
someone already reading that line, and it is lost when the file moves. A decision belongs in a
record a reader can search and cite.

## Decision

Code carries no comments. Each comment that existed was handled one of three ways:

1. **It repeats the code, or has gone stale:** deleted.
2. **It excuses a shortcut:** the shortcut becomes an issue, and the comment is deleted.
3. **It records a non-obvious decision:** the decision moves into an ADR, and the comment is deleted.

Names, types and tests say what code does. An ADR says why.

**Not counted as comments:** a shebang; a licence header at the top of a file; a directive a tool
reads (linter, type-checker, formatter or compiler pragmas); a marker listed as a regular expression
under `directives` in the baseline; a Python docstring a framework reads at runtime; and a C++
header comment Unreal shows as an editor tooltip, above `UPROPERTY`, `UFUNCTION`, `UCLASS`,
`USTRUCT` and `UENUM` and above `UENUM` values.

**The ratchet.** `node tools/comment-ratchet/comment-ratchet.mjs check --base origin/main` runs on
every pull request (`ci-comments`). It fails when a file holds more comment lines than
`tools/comment-ratchet/comment-baseline.json` allows, when a file holds fewer and the baseline is
not lowered, or when the baseline total rises against the base ref. The script and the baseline
live in `tools/comment-ratchet/`. Other commands are `write` (rewrite the baseline from the tree),
`report` and `strip`.

**The baseline only falls.** Generated code, `shared/src/MantlePlace.PlatformApi/Generated/**`, is
excluded because a regenerate rewrites it. To lower the baseline, delete the comment, run `write`
and commit the baseline with the change.

## Alternatives set aside

- **Keep comments that explain why.** They go stale unseen and cannot be searched or cited; an ADR
  can.
- **Ask reviewers to catch new comments.** A rule held only by review erodes; a check does not.

## Consequences

- A reader who needs the reason for a rule finds it in an ADR, not beside the code. ADRs 0020 to
  0031 hold what the removed comments recorded.
- A new decision that once would have been a comment is written as an ADR in the same pull request.
- The baseline total is zero and cannot rise.
