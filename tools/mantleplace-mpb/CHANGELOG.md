<!-- SPDX-License-Identifier: Apache-2.0 -->
<!-- SPDX-FileCopyrightText: Mantle Place LLC -->

# Changelog

The package version is the MPB manifest version the conformance corpus is written against;
`.postN` marks a release that changes the package and not the suite.

## 1.14.0

- `read_bundle`: every file of a bundle with its frame and ODbL role; per file the axis transform
  and, for an offset-mode file, the site origin as its offset; `transform` between two files of
  one frame. It never reprojects: files in different frames raise `FrameMismatch`.
- Versioning tied to the conformance suite (`release_check.py`); package metadata for PyPI; a
  release workflow publishing through trusted publishing.

## 0.1.0

- `mpb validate` and `mpb corpus`: schema, pointers, sha256, frames and ODbL roles.
