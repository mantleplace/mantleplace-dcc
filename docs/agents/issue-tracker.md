---
name: issue-tracker
description: This repository's tracker is GitHub issues driven by the `gh` CLI — the exact commands for creating, reading, listing, labelling, commenting on and closing one, plus the flag saying external pull requests are not a triage surface. Read before filing, triaging or fetching a ticket, and for what is safe to type into a world-readable issue body.
---

# Issue tracker: GitHub

Issues for this repo live as GitHub issues on `mantleplace/mantleplace-dcc`. Use the `gh` CLI for all
operations; it infers the repo from `git remote -v` when run inside a clone.

## Conventions

- **Create an issue**: `gh issue create --title "..." --body-file <path>`. Use a file rather than an
  inline heredoc for multi-line bodies.
- **Read an issue**: `gh issue view <number> --comments`, or
  `gh issue view <number> --json number,title,state,labels,body,comments`.
- **List issues**:
  `gh issue list --state open --json number,title,body,labels,comments --jq '[.[] | {number, title, body, labels: [.labels[].name], comments: [.comments[].body]}]'`,
  with `--label` and `--state` filters as needed.
- **Comment**: `gh issue comment <number> --body-file <path>`
- **Apply / remove labels**: `gh issue edit <number> --add-label "..."` / `--remove-label "..."`
- **Close**: `gh issue close <number> --comment "..."`, or let a PR body's `Closes #<n>` do it on merge.

## Pull requests as a triage surface

**PRs as a request surface: no.** _(Set to `yes` if this repo starts treating external PRs as feature
requests; `/triage` reads this flag.)_

When set to `yes`, PRs run through the same labels and states as issues, using the `gh pr`
equivalents — `gh pr view <number> --comments`, `gh pr diff <number>`, `gh pr comment`,
`gh pr edit --add-label`. Discovery keeps only an `authorAssociation` of `CONTRIBUTOR`,
`FIRST_TIME_CONTRIBUTOR` or `NONE`; a collaborator's in-flight PR is not triage work.

GitHub shares one number space across issues and PRs, so a bare `#42` may be either — resolve with
`gh pr view 42` and fall back to `gh issue view 42`.

## What this repo's own rules add

This tracker is **public**, and so is everything written to it: a title, a body and every comment are
world-readable the moment they are posted, and there is no draft state before that. Nothing that
resolves only inside a private repository goes into one — not by URL, not by path, not by issue
number, not as `repo#42`. What is refused, on which surface and with which exemptions is
[`public-surface.md`](public-surface.md); root [`CLAUDE.md`](../../CLAUDE.md) governs.

The one split worth knowing before you type: **a bare `#42` in an issue body, a comment, a commit
message or a PR body is this repo's own self-reference and is fine.** In a *file* it is not, and
`ci-public-hygiene` refuses it there.

Commits are signed off (`git commit -s`, DCO, no CLA — see [`CONTRIBUTING.md`](../../CONTRIBUTING.md)).

## When a skill says "publish to the issue tracker"

Create a GitHub issue.

## When a skill says "fetch the relevant ticket"

Run `gh issue view <number> --comments`.
