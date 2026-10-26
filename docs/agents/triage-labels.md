---
name: triage-labels
description: The labels this repository's tracker uses itself: the states and categories an issue carries, the `waiting` state, the `spec` label a parent spec carries in place of a state, the `host:*` labels and the `public-hygiene` label. Read before applying, removing or reasoning about a label.
---

# Triage labels

The engineering skills speak in terms of canonical triage roles. This file lists the label strings
this tracker uses for them, and the labels that are this repository's own. Any other label on the
tracker is the skills' general vocabulary, not restated here.

## States

| Label             | Meaning                                                                   |
| ----------------- | ------------------------------------------------------------------------- |
| `needs-triage`    | Maintainer needs to evaluate this issue                                   |
| `needs-info`      | Waiting on the reporter for more information                              |
| `ready-for-agent` | Fully specified, ready for an AFK agent                                   |
| `ready-for-human` | Requires human implementation                                             |
| `waiting`         | Parked until the trigger its own body names                               |
| `wontfix`         | Will not be actioned                                                      |

`waiting` marks an issue parked until a trigger its own body names: an observation, a platform change,
a real bundle to design against. Un-park it by removing the label and saying in a comment whether the
trigger fired or the maintainer chose to bring the item forward. It is not exempt from the stale job;
a waiting issue whose trigger is long-lived wants `pinned` as well.

## Categories

`bug` (something is broken) and `enhancement` (new feature or improvement).

Every triaged issue carries exactly one category and one state, except a parent spec.

## Spec

`spec` marks a parent spec: an effort that spans several pull requests. It carries its category and
`spec`, and **no state** — `spec` takes the state's place. Its tickets are native sub-issues, each a
normal triaged issue with its own category and state. The parent closes when its last sub-issue
closes. A spec one pull request can deliver is its own ticket and gets no `spec` label.

## `public-hygiene`

Applied and removed by `ci-tracker-hygiene`, never by hand. It means this issue's own text cites
something only a private repository can resolve ([`public-surface.md`](public-surface.md)). It is
orthogonal to the states, and it clears itself once the text is edited — removing it manually hides a
finding rather than fixing one.

## Host labels

Which plugin an issue is about. **Orthogonal to category and state, and more than one may apply** —
a change to the shared spec or the conformance corpus lands on several hosts at once.

| Label | Host |
| --- | --- |
| `host:unreal` | Mantle Place for Unreal |
| `host:revit` | Mantle Place for Revit |
| `host:rhino` | Mantle Place for Rhino |
| `host:blender` | Mantle Place for Blender |
| `host:max` | Mantle Place for 3ds Max |
| `host:all` | Cross-host: every plugin host, or the shared spec, tools and corpus |

`host:all` is not "I don't know which" — that is an unlabelled host, and it is a `needs-triage`
signal. Use `host:all` when the answer is genuinely every host: the spec, the conformance corpus,
`tools/`, the workflows, the repo's own documentation.

Labels exist for all five hosts so an issue filed against a future host has somewhere to go; a label
is not a folder, and the rule that a top-level folder appears only with real content is untouched by
this. The bug-report template asks for the host as a required dropdown, but an issue form cannot turn
that answer into a label. Applying it is a triage step.

## Deleting a label

Don't, if anything has ever carried it. Removing a label strips it from every issue that used it,
including closed ones, and the tracker is the only record that it was ever applied.
