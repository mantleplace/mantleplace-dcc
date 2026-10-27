---
name: adr-0018-the-revit-track-goes-to-1-0-0-for-its-storefront
description: The Revit track's next release is `revit-1.0.0`, skipping the rest of 0.x, because the Autodesk storefront refuses a version that starts with 0; Unreal's numbering is unaffected. Read before choosing a Revit version, moving Revit back to 0.x, or reading the Revit and Unreal numbers as a statement of relative maturity.
status: accepted
---

# 18. The Revit track goes to 1.0.0 for its storefront

Date: 2026-10-27

## Status

Accepted. Adds to [ADR 0001](0001-per-host-release-tracks.md), whose independent per-host tracks are
what allow this; nothing in 0001 changes.

## Context

The Revit track reached `revit-0.2.0`. The Autodesk storefront (the Design and Make Marketplace,
formerly the Autodesk App Store) reviewed a 0.x submission and asked for a version that does not start
with `0.`, because its installer may fail to overwrite an installed bundle when it updates one. The
review names `1.0.0` as the example.

A version number cannot go back down. Once `1.0.0` is published and installed from the storefront,
every later Revit release is 1.x or above.

## Decision

- The next Revit release is `revit-1.0.0`. The version still has one home, the Revit build's declared
  version, and the tag still equals it as a string.
- From then on the Revit track follows ordinary semver from 1.0.0. It never returns to 0.x.
- The Unreal track keeps its own numbering. The two numbers are independent, as ADR 0001 already
  says, so a Revit 1.x beside an Unreal 0.x says nothing about which host is further along.

## Consequences

- A reader comparing the two tracks must not read the major version as maturity. The [host support matrix](../host-support.md), not the version, says what each host does.
- Revit users upgrading from 0.2.0 see a jump to 1.0.0 with no break in behaviour beyond what that
  release's body lists.
