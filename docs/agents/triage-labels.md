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

The tracker also carries `tracking` and `confirmed`, which exempt an issue from the stale job
(`stale.yml` — tracker hygiene, not a merge gate). They are orthogonal to the table above: an issue
can be `confirmed` and `needs-info` at once, and neither counts as the one state role a triaged
issue carries. Leave them alone unless the stale job is what you are reasoning about.

`bug`, `enhancement` and `wontfix` are GitHub's defaults. Edit the right-hand column if the
vocabulary ever diverges — the left-hand column is what the skills say and does not change.
