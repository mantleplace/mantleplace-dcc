---
name: adr-0011-revit-provenance-record-and-attribution-note-identity
description: Revit records which bundle a project came from in an ExtensibleStorage entity on Project Information whose schema GUID is permanent and whose write access is this add-in's vendor alone, and it recognises its own attribution note by the exact text that record says it wrote — not by a stamp, and not by rebuilding the text from the sources. Read before changing a provenance field, the note's line format, or how a re-import finds the note.
status: accepted
---

# 11. The Revit provenance record is permanent, and the attribution note is known by its recorded text

Date: 2026-09-17

## Status

Accepted.

## Context

An import now writes the manifest's `attribution.sources[]` into a drafting view named
**Mantle Place Attribution**, as one text note, and records on Project Information which order,
build and manifest version the project came from. A re-import has to find the note it wrote and
rewrite it when the build changes, without touching anything else in that view.

Three facts constrain this. Every other element this plugin creates is recognised by a stamp in its
Comments parameter ([ADR 0004](0004-revit-terrain-identity.md)), but a drafting view has no Comments
slot and a text note has no identity slot a curator cannot also type into. An ExtensibleStorage
schema is immutable once any saved project holds it: changing a field under the same GUID makes
Revit refuse the new definition in every project that already has the old one. And a project can
hold two orders' ground, so the view can hold two orders' credits.

## Decision

- **The schema's GUID is a Core constant and never changes.** A change to the fields is a new GUID,
  and a reader that wants older projects reads both. The sources are one JSON string rather than
  sub-entities, so what is stored about a source can grow without a new schema.
- **Read access is public, write access is this vendor's.** Any tool may ask which bundle a project
  came from; only this add-in may answer. The vendor id is asserted equal to the `.addin` file's,
  because a mismatch compiles and fails only inside Revit.
- **The note is recognised by the exact text the record says was written.** The record stores that
  text in a field of its own. The note carrying it, for the *same order*, is this plugin's, and it is
  rewritten in place when the build changes. A note already carrying this build's text is kept. Any
  other note — a curator's, a note of ours they edited, another order's — is left alone, and a new
  note is added beneath it.
- **View and record are written in one transaction**, because each is how the next import reads the
  other.

## Considered options

- **Rebuild the previous text from the stored sources.** Rejected: it ties the note's identity to
  today's line format, so a later plugin that lays a line out differently would stop finding every
  note written before it and add a second. The written text costs one string field, and it has to be
  decided now, while the schema can still change for nothing.
- **Stamp the note with an ExtensibleStorage entity of its own.** Rejected: a second schema and more
  unexecuted API surface, to identify an element whose content already identifies it.
- **Own the view outright and rewrite everything in it.** Rejected for ADR 0004's reason: touch what
  this import owns and nothing else. A curator may annotate the view they put on a sheet.
- **Rewrite whatever note the record points at, whichever order it belongs to.** Rejected: importing
  a second order would erase the first order's credits while its ground stays in the project.
- **Refuse on a stale build, as the terrain does.** Rejected: ADR 0004 refuses because deleting
  ground takes a curator's hosted work with it. Rewriting a text note takes nothing, and the issue
  that specified this asks for the rewrite.

## Consequences

- **Update-in-place crosses over from Unreal here, and only here.** The note is the one element whose
  replacement destroys nothing a curator built on it.
- **A project holding two orders keeps one record — the last import's.** Re-importing the earlier
  order does not find its own note by record; it keeps it when the text is unchanged and adds a fresh
  one beside it when not. Multi-bundle assembly is deferred work, and this is its to fix.
- **A curator who edits the plugin's note has made it theirs.** The next import leaves it and adds
  another, which is visible and easy to delete; overwriting their words would not be.
- **The field list is spent.** Order id, job id, manifest version, sources and note text are what
  every project imported from now on will carry under this GUID.
