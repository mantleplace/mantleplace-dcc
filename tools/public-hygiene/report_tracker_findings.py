#!/usr/bin/env python3
"""Report tracker-hygiene findings onto the issue they were found on.

The issue tracker is the sixth publication surface, and it is the one where a gate cannot be a
gate. A required check blocks a merge; nothing blocks a post. By the time a workflow starts, the
title, the body or the comment is already world-readable and already indexed. So this is
**detection, not prevention**, and it says so in every message it writes — a tool that implied
otherwise would be worse than no tool, because someone would rely on it.

Detection still has to reach a human, so it lands three ways, each covering a different reader:

  a comment on the issue   the author, who is looking at the issue and nowhere else
  a label                  a maintainer scanning the list, and `gh issue list --label` afterwards
  a failed workflow run    anyone watching Actions, without opening the issue at all

⛔ The comment never quotes the refused token. It is itself public and permanent, so quoting would
make this tool's own comment a second copy of the thing it refused — and then editing the issue
would no longer clean it up, which is the entire point of noticing. The findings arrive here
already redacted (`check_public_references.py --redact`); the Actions log keeps the exact token,
because the reader there is us.

One comment per issue, updated in place rather than appended. A new comment on every edit would
bury the issue in bot noise and train everyone to scroll past the one that matters. The marker is
an HTML comment: invisible to a reader, exact to match on.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys

MARKER = "<!-- ci-tracker-hygiene -->"
LABEL = "public-hygiene"

FOOTER = (
    "_This is **detection, not prevention**: the text was public the moment it was posted, and no "
    "workflow can unpublish it. `docs/agents/public-surface.md` is the rule, and "
    "`docs/agents/issue-tracker.md` states it for this surface._"
)

CLEAN_BODY = f"""{MARKER}
✅ **Tracker hygiene: clean.** The finding reported earlier on this issue is no longer present.

{FOOTER}"""

FOUND_BODY = """{marker}
⚠️ **Tracker hygiene:** this issue's text cites something that only resolves inside a private
repository.

```
{findings}
```

The refused tokens are deliberately **not** quoted above. This comment is public and permanent too,
so quoting them would make it a second copy of the thing being refused — and editing the issue
would then no longer clean it up. The [workflow run]({run_url}) names them.

Everything in this repository is world-readable, permanently, whether or not it is later deleted.
State the reasoning in prose instead of citing what a reader here cannot open.

{footer}"""


def gh(*arguments: str, check: bool = True) -> str:
    """Run `gh`, returning stdout. Failures are reported rather than swallowed: a reporter that
    fails silently leaves a finding nobody sees, which is the same as no gate."""
    finished = subprocess.run(
        ["gh", *arguments], capture_output=True, text=True, encoding="utf-8"
    )
    if check and finished.returncode != 0:
        sys.stderr.write(finished.stderr)
        raise SystemExit(f"gh {' '.join(arguments)} failed ({finished.returncode})")
    return finished.stdout


def existing_comment_id(issue: str) -> str | None:
    """The id of this tool's own comment on the issue, if it has one. `last` rather than `first`:
    if a duplicate was ever posted, the newest is the one a reader sees at the bottom."""
    listed = gh("issue", "view", issue, "--json", "comments")
    comments = json.loads(listed or '{"comments": []}').get("comments", [])
    matching = [comment for comment in comments if MARKER in (comment.get("body") or "")]
    if not matching:
        return None
    return (matching[-1].get("url") or "").rsplit("issuecomment-", 1)[-1] or None


def upsert(issue: str, body: str) -> None:
    comment_id = existing_comment_id(issue)
    if comment_id:
        gh(
            "api",
            "--method",
            "PATCH",
            f"repos/{{owner}}/{{repo}}/issues/comments/{comment_id}",
            "-f",
            f"body={body}",
        )
        return
    # `--body-file -` rather than `--body`: the text is multi-line and carries backticks, and a
    # command line is the wrong place for either.
    finished = subprocess.run(
        ["gh", "issue", "comment", issue, "--body-file", "-"],
        input=body,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    if finished.returncode != 0:
        sys.stderr.write(finished.stderr)
        raise SystemExit(f"gh issue comment failed ({finished.returncode})")


def label(issue: str, add: bool) -> None:
    """Best-effort: a missing label must not lose the comment that carries the actual finding."""
    flag = "--add-label" if add else "--remove-label"
    subprocess.run(["gh", "issue", "edit", issue, flag, LABEL], capture_output=True, text=True)


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--issue", required=True, help="the issue number to report on")
    parser.add_argument("--findings-file", required=True, help="redacted findings, or empty")
    parser.add_argument("--run-url", default="", help="the workflow run, for the full detail")
    arguments = parser.parse_args(argv)

    with open(arguments.findings_file, encoding="utf-8") as handle:
        findings = handle.read().strip()

    if not findings:
        # Silence on a clean issue is the correct amount of noise — unless we said something
        # before, in which case saying it is resolved is what closes the loop.
        label(arguments.issue, add=False)
        if existing_comment_id(arguments.issue):
            upsert(arguments.issue, CLEAN_BODY)
        return 0

    upsert(
        arguments.issue,
        FOUND_BODY.format(
            marker=MARKER, findings=findings, run_url=arguments.run_url, footer=FOOTER
        ),
    )
    label(arguments.issue, add=True)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
