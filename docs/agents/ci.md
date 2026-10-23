---
name: ci
description: The pull-request gates this repository runs, the jobs behind them, which of those jobs are required checks on main and which are not, and the workflow rules that keep a required check from hanging — no paths filter on pull_request, no self-hosted runner — and the commands that run every gate locally. Read before pushing, adding or renaming a workflow or job, before reading a red or pending check, or when a green pull request might still break the Unreal build.
---

# CI

Five workflows run on every pull request, all on free hosted runners: `ci-manifest-conformance`,
`ci-revit-tests`, `ci-public-hygiene`, `ci-unreal-naming` and `ci-brand-assets`. `stale.yml` and
`ci-tracker-hygiene.yml` are tracker hygiene, not gates — the second runs on issue and comment
events, a pull request's own comments included, and it detects rather than blocks
([`public-surface.md`](public-surface.md)).

`release-mpb.yml` is not a gate: it runs on a `mpb-<version>` tag, not on a pull request, and
publishes the `mantleplace-mpb` package to PyPI through trusted publishing (no stored token; the `pypi`
environment is where a maintainer approves). `release_check.py` holds the tag to the package version and
the package version to the conformance corpus; the mpb tests hold the same line on every pull request.

`ci-brand-assets` is **half a gate by construction**: the mark renderer's input is a private file,
so no runner can regenerate the committed PNGs, and the job tests the rules that decide what a
render looks like rather than the renders themselves
([ADR 0009](../adr/0009-host-assets-render-the-monogram.md)).

## A workflow name is not a check name

Branch protection matches *jobs*, and the mapping is not one-to-one — `ci-revit-tests` contributes
two. The four required checks on `main` are `conformance`, `pure-core`, `pure-core-windows` and
`references`; read them from the repository rather than from this list:

```bash
gh api repos/mantleplace/mantleplace-dcc/branches/main/protection
```

Note what is **absent**: `generated-names`, the `ci-unreal-naming` job, reports on every pull
request but is not a required check, so the one automated guard in front of an Unreal naming
regression cannot currently block a merge.

## Rules for every workflow

- ⛔ **No `paths:` filter on `pull_request`.** A required check that is path-filtered never reports
  on a pull request outside its paths, so the check sits pending forever and nothing can merge. A
  `paths:` filter on `push` is fine; `ci-revit-tests` has one.
- ⛔ **Never attach a self-hosted runner to this repository.** A fork's pull request would execute
  on the build machine. The Unreal compile stays on private infrastructure for exactly that reason,
  so a green pull request here can still break the engine build — an accepted, published lag
  ([README](../../README.md#ci-and-what-it-does-not-cover)).

## Running the gates locally

The commands the workflows run over the tree, from the repository root, Python 3.12, offline
except where a checker says otherwise. Run them before you push, not after review finds what they
would have:

```bash
python tools/manifest-conformance/check_manifest_conformance.py
python -m unittest discover -s tools/manifest-conformance
python -m unittest discover -s tools/mantleplace-mpb/tests
PYTHONPATH=tools/mantleplace-mpb/src python -m mantleplace_mpb corpus tools/manifest-conformance/corpus
python tools/public-hygiene/check_public_references.py
python tools/public-hygiene/check_docs_integrity.py
python tools/unreal-naming/check_generated_names.py
python tools/unreal-naming/check_third_party_layout.py
pwsh -NoProfile -File tools/public-hygiene/Test-PowerShellParses.ps1
(cd tools/public-hygiene && python -m unittest discover -p "test_*.py")
(cd tools/unreal-naming && python -m unittest discover -p "test_*.py")
(cd tools/brand-assets && python -m unittest discover -p "test_*.py")
python tools/public-hygiene/check_public_references.py --stdin --label "PR body" < pr-body.md
```

The last line checks a pull request body before it is published; the same `--stdin` takes a
title or a commit message. The branch-name and commit-range runs are the opt-in pre-push hook's
(`git config core.hooksPath .githooks`). The Revit suite's build and run lines live in
[`revit/README.md` ▸ Build and test](../../revit/README.md#build-and-test). On Windows, a one-off
`json.load` over the corpus needs `encoding="utf-8"` (or `PYTHONUTF8=1`); the checkers already
pass it.

## C++ formatting

[`unreal/.clang-format`](../../unreal/.clang-format) is for new code only. Nothing in CI checks it,
and a reformat sweep is refused.
