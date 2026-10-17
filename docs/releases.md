---
name: releases
description: How a host release is cut and written up — the cut checklist both hosts share, the release title, and the release-body template every Unreal and Revit release follows. Read before tagging a release, publishing one, or editing a published release's title or body.
---

# Cutting a release

Each host has its own track, tagged `<host>-<version>` with no `v`
([ADR 0001](adr/0001-per-host-release-tracks.md)). How a host's zip is built and gated is that host's
own: [`unreal/README.md` ▸ Packaging a release](../unreal/README.md#packaging-a-release) and
[`revit/README.md` ▸ Packaging a release](../revit/README.md#packaging-a-release). This file holds what
both share: the cut order, the title and the body.

## The cut

1. **Version bump merged** on `main`, in the one place the host declares it.
2. **Gate run at that merged commit**, per the host's README, against the zip a user will download.
3. **Tag `<host>-<version>` at that commit** and publish with the zip as the release's only asset.
   Leave GitHub's *Latest* to the newest publish, whichever host it is (ADR 0001).
4. **Download the published asset and check it**: its sha256 matches the body, and it installs and
   loads in a project or host that never built the plugin. Record the result in the body.
5. **Submit to the storefront** the same day where the host has one (Fab for Unreal, the Autodesk App
   Store for Revit), from the same asset.

## Title

`<tag> — Mantle Place for <Host>`, the host's short name: `unreal-0.6.0 — Mantle Place for Unreal`,
`revit-0.1.0 — Mantle Place for Revit`.

## Body

Write for the user installing it. One line per change; the pull request holds the detail. Every
section but the first and **Verification** is left out when it would be empty.

```markdown
**<Host> <versions>, Windows 64-bit.** <Install in one sentence.> [Install guide](<host README link>)
Upgrade by <how>; <"nothing else to do" or "see below">.

## Upgrading from <previous version>

<Only what an existing user must do or will notice: content to delete, a layer now skipped, a
setting renamed. Bold the action.>

## What's new

- **<Change, as the user sees it>.** <One sentence of why it matters, if needed.> (#N)

## Known issues

- **<Symptom>.** <What happens and what to do instead.> (#N)

## Verification

- Built from `<short sha>` on `main`. <Gate result at that commit.>
- Published asset: <check from step 4 and its date>.
- `<asset name>` sha256 `<hash>`

Previous: [<previous tag>](https://github.com/mantleplace/mantleplace-dcc/releases/tag/<previous tag>)
```

- **The first release on a track** replaces *What's new* with *What it does*, and has no *Upgrading* or
  *Previous* line.
- **A check that was not run is named as not run**, never left out: a release published with a gate
  path that failed says so under *Known issues* or *Verification*.
- **Issues by bare `#N`.** They resolve to this repository; nothing private is cited
  ([public-surface.md](agents/public-surface.md)).
- **No internals a user cannot act on**: test names, file counts, build flags and timings stay in the
  pull request unless they change what the user should do.
