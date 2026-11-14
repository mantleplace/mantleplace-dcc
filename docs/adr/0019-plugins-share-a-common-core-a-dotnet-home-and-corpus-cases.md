---
name: adr-0019-plugins-share-a-common-core-a-dotnet-home-and-corpus-cases
description: Every plugin supports the same core actions in the same order with the same words; host-neutral .NET source lives in one home named for no host; a reader in any language proves itself against the conformance corpus cases; each host keeps its own version track. Read before adding a plugin action, copying reader code into a host tree, adding a reader in another language, or tying one host's release to another's.
status: accepted
---

# 19. Plugins share a common core, a .NET home and corpus cases; each host versions alone

Date: 2026-11-13

## Status

Accepted. Builds on [ADR 0001](0001-per-host-release-tracks.md), which gave each host its own
release track; this record states why the tracks stay independent as the hosts share more.

## Context

Each plugin carries its own copy of the bundle reader and its own idea of which actions a user gets
and what they are called. The AutoCAD tree links source from the Revit tree to avoid a second copy.
The 3.0 readers are built on the four decisions below, and a later reader needs to know they were
deliberate.

## Decision

1. **A common core.** Every plugin supports the same actions, in the same order, with the same
   words: sign in, list the vault, choose a level, download, import, import a zip from disk with no
   account, refuse a version out of range, show credits and notices, import again. A feature only
   one host can offer sits beside the core and is named as host-only, never folded into it.
2. **A shared .NET home.** Host-neutral .NET source and windows live in one home named for no host,
   and every .NET host builds from it. A host tree holds only what touches its own host.
3. **Corpus reader cases.** A reader in any language proves itself against the same cases in the
   conformance corpus. This is how the C++ and Python readers share with .NET: they share the
   cases, not the code.
4. **A version track per host.** Hosts do not stay in step. A rule that they must would force empty
   releases, or hold one host's fix back for another.

## Alternatives set aside

- **One copy of the reader per host (today).** The copies drift, and a fix lands in one host and
  not the others.
- **Linked source from the Revit tree (today, for AutoCAD).** It names one host as the owner of
  code every host needs, and a Revit-only change can break a build that is not Revit's.
- **A single cross-language reader library.** It would put a native or interop boundary under every
  host and tie each host's toolchain to one build; shared cases give the same agreement without it.

## Consequences

- A new action is added to the core for every host, or recorded as host-only; the order and wording
  are the shared part.
- A new reader, in any language, is accepted when it passes the corpus cases.
- Versions are compared within a host, never across hosts.
