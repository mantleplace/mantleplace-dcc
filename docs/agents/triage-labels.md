---
name: triage-labels
description: The labels this repository's tracker uses: the issue types, the one state label, the `blocked:` family, the Brief and comment format, and the orthogonal labels. Read before applying, removing or reasoning about a label.
---

# Triage labels and the tracker convention

This repo's issue tracker follows the Mantle Place tracker convention, copied here by `/bootstrap` so
the repo needs nothing else. Where a skill names a triage role, use the label string in the state table
below. This text overrides the tracker text of an upstream skill such as `/triage`: its labels, its
comment disclaimer, its titles.

## Vocabulary

| Term | Meaning |
| --- | --- |
| Issue | any GitHub issue |
| Spec | a parent issue |
| Ticket | a sub-issue of a Spec that one PR delivers |
| Decision ticket | a child of a Map |
| Map | a `wayfinder:map` issue |

## Issue types

Kind is the org issue type, not a label. The `mantleplace` org has seven, shown as a badge in the
list and filterable with `type:`:

| Type | For |
| --- | --- |
| Bug | something is broken |
| Feature | new capability or improvement |
| Task | a unit of work that is neither of those |
| Chore | upkeep with no user-visible change |
| Spec | a parent issue |
| Map | a `wayfinder:map` issue |
| Decision | a child of a Map |

Every issue has exactly one type. A Map's children are typed Decision. The labels
`wayfinder:research`, `wayfinder:prototype`, `wayfinder:grilling` and `wayfinder:task` stay as
sub-kind tags, and `wayfinder:map` stays, because upstream `/map` reads them. The retired labels
`bug`, `enhancement` and `spec` became types.

## State

**Every issue carries exactly one state label.** `ready-for-agent` is the only state that means
"start a session". Everything that cannot be started wears a `blocked:` label.

| Role | Label | Colour | Meaning |
| --- | --- | --- | --- |
| `needs-triage` | `needs-triage` | `fbca04` | maintainer needs to evaluate this issue |
| `needs-info` | `needs-info` | `fbca04` | waiting on the reporter for more information |
| `ready-for-agent` | `ready-for-agent` | `0e8a16` | fully specified, and nothing blocks it |
| `blocked:issue` | `blocked:issue` | `d93f0b` | another issue lands first |
| `blocked:date` | `blocked:date` | `d93f0b` | nothing to do until a date |
| `blocked:founder` | `blocked:founder` | `d93f0b` | a manual step only the founder can do |
| `blocked:decision` | `blocked:decision` | `d93f0b` | a founder judgment call |
| `blocked:external` | `blocked:external` | `d93f0b` | a vendor, an app review or a customer |

Colours: `ready-for-agent` green `0e8a16`, `needs-*` yellow `fbca04`, all `blocked:*` red-orange
`d93f0b`. One visual family per meaning.

- A founder-only task takes `blocked:founder`.
- `ready-for-agent` with an open native blocker is wrong: the issue is `blocked:issue`.
- `wontfix` is gone: close the issue as "not planned".
- `ready-for-human`, `waiting`, `waiting:date` and `deferred` are retired, each became the matching
  `blocked:` state.
- A parent Spec carries its type and a state like any issue; it is never grabbed by an agent, and it
  closes when its last ticket does.
- Blocked states clear themselves: `land` moves the dependants of a closed ticket from
  `blocked:issue` to `ready-for-agent`, and a daily workflow wakes `blocked:date` issues on their
  date and reconciles any missed `blocked:issue`. The daily job is `.github/workflows/tracker-daily.yml`,
  which runs the kit's `unblock` script (`--dry-run` lists the moves) with the org token in the repo secret
  `TRACKER_TOKEN`; a date is due on the day itself, and a `Then:` that names a `blocked:` state is left
  for a person.

## Orthogonal labels

`host:*`, `user-request`, `documentation`, `public-hygiene`, `wayfinder:*` and a repo's CI labels
are not states and not kinds. They stay, and they are the only per-repo additions. `documentation`
is an area tag: a docs issue is a Task or Chore plus `documentation`.

## Titles

- Form: `<Area>: <imperative summary>` in sentence case, about 70 characters at most. The area is
  optional.
- No kind prefix. `Spec:` and `Grill:` are retired, because the type badge shows in the list.
- State appears in a title in one place only: a `blocked:date` issue ends ` — until YYYY-MM-DD`
  (spaced em dash). A label cannot carry a date without label sprawl, so the suffix is the canonical
  copy of the date. Setting `blocked:date` adds it; setting any other state strips it.

## The Brief

Every issue opens with a Brief for the founder, under `## Brief`, followed by a `---` divider and
then the robot sections. The markup is exact:

```
## Brief

**What:** <one sentence: the outcome for a user or the founder>
**Why:** <one sentence: why it matters now>
**Waiting on:** <what clears it, and who or when>. Then: `<next state>`

---
```

- `Waiting on` is present only while the state is `blocked:*`. Each state change rewrites that line
  or removes it.
- `Then:` names the state the issue moves to once cleared, and the daily workflow reads it. With no
  `Then:`, the next state is `ready-for-agent`.
- No paths in the Brief. Write it so a newcomer could follow it.

## Body order

- **Spec:** Brief, then the `/spec` template sections.
- **Ticket:** Brief, then `## What to build` (with the `**Agent tier:** light|standard|heavy` line)
  and `## Acceptance criteria`.
- **Map:** Brief, then the `/map` sections.

Native sub-issue and blocked-by links are the only copy of parent and blockers; they work across
repos within the org. A body carries no `## Parent` or `## Blocked by` section. A ticket without an
`**Agent tier:**` line is refused.

## Comments

An agent comment has this shape:

```
<tag> <one plain sentence: the outcome>

<details><summary>Detail</summary>

<the detail>

<!-- mp:comment tag=<tag-name> -->
_Generated by AI._
</details>
```

- **First line.** One plain sentence starting with one of the tags `✅ Landed`, `🔄 Progress`,
  `⛔ Blocked`, `❓ Needs you`, `📋 Breakdown`, `🧭 Handoff` or `🔍 Triage`.
- **Body.** The detail goes inside `<details><summary>Detail</summary> … </details>`.
- **Footer.** Inside the details, the AI disclaimer `_Generated by AI._` and the hidden marker
  `<!-- mp:comment tag=<tag-name> -->` (the tag lower-cased, one word: `landed`, `progress`,
  `blocked`, `needs-you`, `breakdown`, `handoff`, `triage`), so scripts find agent comments by the
  marker, not by sniffing for disclaimer text.

PR comments use the same format, through the kit's `pr-comment`.

## Pull requests

A PR body gains a one-line Brief above `Closes #N`, and nothing else in it changes:

```
**Brief:** <what this changes and why, in one line>
```

## Writing through `issue`

The kit's `issue` script (`create`, `state`, `brief`, `comment`) is the choke point: skills and
sessions create, relabel and comment through it rather than each embedding the format. It refuses a
title that breaks the rules, a missing type, a missing tier on a ticket, a `blocked:date` without a
date, and a state outside this vocabulary. A refusal is status `fail` with every reason in
`data.refused` and nothing written.

- `issue create --title … --type … --state … --what … --why …` writes the title, type, one state label and
  the Brief, then the native sub-issue (`--parent`) and blocked-by (`--blocked-by`) links.
- `issue state --issue n --state …` moves the one state label, adds or strips the ` — until` suffix, and
  rewrites or removes the Brief's Waiting on line.
- `issue brief --issue n …` rewrites the Brief; `issue comment --issue n --tag … --body-uri …` posts the
  house comment, the first line of the text being its outcome.
- A ticket is a `--parent` issue whose type is not Decision; it needs `--tier` or a tier line in its body.

## The startable query

The bookmarked org query lists only what a session can start, each result showing its type badge:

```
org:mantleplace is:open label:ready-for-agent -is:blocked
```

`next` counts a ticket as startable only when it is `ready-for-agent` with no open native blocker,
and flags each open issue that does not conform: no type, not exactly one state, no Brief, a
`blocked:date` without a title suffix, `ready-for-agent` with an open blocker, or a body copy of
`## Parent` or `## Blocked by`.

## Repo-local labels

A repo documents its local labels in its own label page,
which holds only a pointer here and those labels. Nothing in a repo's local labels may be a state.
