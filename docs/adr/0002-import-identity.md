---
status: accepted
---

# Imported content is keyed on the order, not the build that produced it

The Unreal importer groups everything it generates under one folder per import, and names its actors
with the same key. That key has been the pipeline job id, which changes on every rebuild of the same
order. Re-materialising an order therefore produced a *new* folder and a *new* set of actors, while
the idempotent wipe that exists to replace prior content looked at a path nobody was using any more —
so a user who refreshed an area got a second landscape sitting on top of the first, silently, with no
way to tell which was current. The key becomes the **order** identity, falling back to the bundle's
own content hash when a bundle carries no order (a locally produced or admin bundle).

This is not a naming preference. The job id is an identity for a *build*; the folder needs an
identity for a *thing the user owns*, and those are different domain concepts —
[`CONTEXT.md`](../../CONTEXT.md) now names them separately for exactly this reason. Using one where
the other belongs is what made re-import non-idempotent, and it presented as a cosmetic untidiness
rather than as the data problem it was.

## Considered options

- **Keep the job id and accept a folder per rebuild.** Rejected: it makes every refresh accumulate a
  full copy of the imported content, and leaves the user to work out by hand which landscape is live.
  It also makes the wipe permanently dead code, which is worse than either behaviour on purpose.
- **Order id when present, job id otherwise.** Rejected: the fallback is the local-development path,
  which is the one exercised most often during plugin work, so the bug would have stayed alive
  exactly where it is most likely to be hit and least likely to be believed.
- **A user-visible name from the area label.** Rejected: it is not an identity — two orders can carry
  the same label — and sanitising arbitrary text into a package path is its own hazard.

## Consequences

- The fallback puts a content hash fragment in a package path, which looks arbitrary to a reader who
  does not know why. That is the main reason this record exists.
- **What gets hashed is the manifest, not the whole zip.** The manifest declares every artifact's
  own sha256, so hashing it is a content hash of the bundle by proxy — and the bytes are already in
  memory when the importer needs them, where hashing a multi-hundred-megabyte archive would add a
  full read and a full allocation to every local import.
- **The identity is validated, not sanitised.** It becomes a path segment of a directory the
  importer force-deletes, and an order id arrives from bundle JSON. An empty one would collapse that
  path onto the content root; a relative segment would walk it elsewhere. A bundle whose identity is
  unusable is refused, because rewriting one into something that looks fine is how a delete ends up
  pointed somewhere nobody chose.
- A rebuild of the same order now *replaces* prior content rather than adding to it. That is the
  intent, and it means a rebuild is destructive by design — so the identity a folder was created
  under is recorded, and a mismatch refuses rather than deletes. Truncated identities can collide,
  and a collision that silently force-deleted a different order's content would be the worst outcome
  available.
- **That record lives outside the content tree**, beside the project's `Saved` data rather than as
  an asset in the folder it describes. It has to be readable at the moment the importer is deciding
  whether to delete that folder, and loading a package about to be force-deleted is the exact hazard
  already documented at the delete site. The cost is that deleting the content folder by hand leaves
  a stale record; that case is treated as no prior import, which is what the person deleting it
  meant.
- **Re-import matches an actor tag, not an actor label.** The label was the key, and a label is a
  thing a user edits — renaming an actor in the outliner broke that user's own next re-import and
  then stacked a second landscape on the first. Labels are now presentation only.
- Content imported by 0.3.0 and earlier is keyed the old way and matches nothing. It is not migrated:
  the importer detects a folder with no recorded identity and says so, leaving the deletion to the
  user. Automatically deleting a user's content on a plugin upgrade is not a thing this plugin will
  do.
