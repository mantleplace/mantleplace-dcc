---
name: adr-0017-the-mpb-reader-is-a-ci-published-python-track
description: The mantleplace-mpb Python reader is a third release track, tagged `mpb-<version>` with no `v`, built and published to PyPI by a public workflow, and the one exception to "no release is built in public CI". Read before changing the mpb release workflow, its tag, or claiming that releases are never built in public CI.
status: accepted
---

# 17. The mpb reader is a CI-published Python track

Date: 2026-10-22

## Status

Accepted. Amends the "no release is built or gated in public CI" clause of
[ADR 0001](0001-per-host-release-tracks.md) for this one track; everything else in 0001 stands.

## Context

ADR 0001 kept releases out of public CI because both DCC hosts need a licensed install to package.
`tools/mantleplace-mpb` (`mantleplace-mpb` on PyPI) has no such need: it is pure Python, tested on
free runners, and PyPI trusted publishing removes any stored token.

## Decision

- `mpb` is a third release track. Its tag is `mpb-<version>`, no `v`, so tag-matches-artifact is a
  string equality; `release_check.py --tag` enforces it against `__version__`.
- `.github/workflows/release-mpb.yml` tests, builds, checks and publishes it on that tag, through
  trusted publishing and an approval-gated `pypi` environment.
- The rule is now: no **host** release is built or gated in public CI. The mpb track is the exception.
- As in ADR 0001 there is no changelog file; the release body is the changelog.
