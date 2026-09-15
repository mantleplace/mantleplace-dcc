---
name: triage-labels
description: Maps the skills' five canonical triage state roles and two categories to the label strings that actually exist on this tracker, and names the labels that are not triage roles at all, and the separate wayfinder family used for planning work. Read before applying, removing or reasoning about a label.
---

# Triage labels

The engineering skills speak in terms of five canonical triage roles plus two categories. This file
maps those roles to the label strings that actually exist on this repo's tracker.

## State roles

| Canonical role    | Label in our tracker | Meaning                                  |
| ----------------- | -------------------- | ---------------------------------------- |
| `needs-triage`    | `needs-triage`       | Maintainer needs to evaluate this issue  |
| `needs-info`      | `needs-info`         | Waiting on reporter for more information |
| `ready-for-agent` | `ready-for-agent`    | Fully specified, ready for an AFK agent  |
| `ready-for-human` | `ready-for-human`    | Requires human implementation            |
| `wontfix`         | `wontfix`            | Will not be actioned                     |

## Categories

| Canonical role | Label in our tracker | Meaning                    |
| -------------- | -------------------- | -------------------------- |
| `bug`          | `bug`                | Something is broken        |
| `enhancement`  | `enhancement`        | New feature or improvement |

Every triaged issue carries exactly one category and one state.

## Labels that are not triage roles

The tracker also carries `tracking`, `confirmed`, `security` and `pinned`, which exempt an issue
from the stale job (`stale.yml` — tracker hygiene, not a merge gate). They are orthogonal to the
table above: an issue can be `confirmed` and `needs-info` at once, and none of them counts as the
one state role a triaged issue carries. Leave them alone unless the stale job is what you are
reasoning about. All four exist on the tracker; `stale.yml` named `security` and `pinned` in its
exempt lists for some time before either label was created, which made those two exemptions inert
without erroring.

`public-hygiene` is applied and removed by `ci-tracker-hygiene`, never by hand. It means this
issue's own text cites something only a private repository can resolve
([`public-surface.md`](public-surface.md)). It is also orthogonal to the table, and it clears itself
once the text is edited — removing it manually hides a finding rather than fixing one.

## Wayfinder labels

Planning work uses a second family: `wayfinder:map` for the map issue that indexes an effort's
decisions, and `wayfinder:research` / `wayfinder:prototype` / `wayfinder:grilling` /
`wayfinder:task` for its child tickets. The skill that creates them is now `/map`, but the label
strings kept the older `wayfinder:` prefix on purpose, because they were already live on trackers
and renaming them would have been a behaviour change rather than a rename.

**A wayfinder ticket is exempt from the one-category-one-state invariant.** It is a planning
artifact created by whoever is running the effort, not an inbound report, and triage is only ever
for issues you did not create. It carries its wayfinder label and needs nothing else.

## Host labels

Which plugin an issue is about. **Orthogonal to category and state, and more than one may apply** —
a change to the shared spec or the conformance corpus lands on several hosts at once.

| Label | Host |
| --- | --- |
| `host:unreal` | Mantle Place for Unreal Engine |
| `host:revit` | Mantle Place for Revit |
| `host:rhino` | Mantle Place for Rhino |
| `host:blender` | Mantle Place for Blender |
| `host:max` | Mantle Place for 3ds Max |
| `host:all` | Cross-host: every plugin host, or the shared spec, tools and corpus |

`host:all` is not "I don't know which" — that is an unlabelled host, and it is a `needs-triage`
signal. Use `host:all` when the answer is genuinely every host: the spec, the conformance corpus,
`tools/`, the workflows, the repo's own documentation.

The roster is fixed and ordered — Unreal and Revit first, then Rhino, Blender, Max — and
`ROADMAP.md` already names the three that have no code yet. Labels exist for all five so an issue
filed against a future host has somewhere to go; a label is not a folder, and the rule that a
top-level folder appears only with real content is untouched by this.

The bug-report template asks for the host as a required dropdown, but an issue form cannot turn that
answer into a label. Applying it is a triage step.

## Deleting a label

Don't, if anything has ever carried it. Removing a label strips it from every issue that used it,
including closed ones, and the tracker is the only record that it was ever applied.

`bug`, `enhancement` and `wontfix` are GitHub's defaults. Edit the right-hand column if the
vocabulary ever diverges — the left-hand column is what the skills say and does not change.
