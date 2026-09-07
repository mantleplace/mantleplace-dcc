---
status: accepted
---

# The Unreal asset-naming standard lives here, and `MP_` is allowed on actor labels

The naming standard the Unreal importer follows had exactly one statement of authority: a code
comment citing a section of an internal style guide that does not exist in this repository and that
no reader of this repository can open. The prefix table itself was in code, four entries wide, while
three more prefixes were invented ad hoc at call sites. The standard is now written out in
[`unreal/CLAUDE.md`](../../unreal/CLAUDE.md) as this repository's own, and the dangling citation is
replaced by a pointer to it.

Alongside it, an exception is recorded rather than left to be rediscovered. Root `CLAUDE.md` says
names are spelled out in full — `mantleplace`, never `mp` — and every actor the importer spawns is
labelled `MP_*`. That reads as a contradiction, and a reader who resolves it in the obvious direction
removes the only marker that tells a user which actors came from Mantle Place. The rule is therefore
scoped explicitly: it governs the repository and the code, not strings the plugin writes into a
user's project, where `MP_` is permitted on outliner-visible actor labels and nowhere else.

## Considered options

- **Mark provenance by folder alone, dropping the prefix.** This is the consistent reading — the
  Content Browser marks provenance only by folder, and asset names carry a type prefix with no vendor
  mark. Rejected on two grounds. An actor label reappears unqualified in the Details panel, in log
  lines, in Blueprint references and in outliner search, none of which show the folder. And the
  content root is a project setting, so a studio that repoints it deletes the folder-borne marker
  entirely — the folder cannot be the only marker precisely for the studios that setting exists for.
- **Spell it `MantlePlace_` on labels.** Rejected: the label sits in a narrow outliner column beneath
  a folder already named `MantlePlace`, so it is mostly redundancy, and it makes the useful half of
  the name — which actor this is — the part that gets truncated.
- **Add a vendor mark to asset names too, for symmetry.** Rejected: it would break the type-prefix
  table for a marker the folder already supplies in the common case, and the type prefix is the part
  a user actually navigates by.
- **Keep citing the internal style guide.** Refused. Root `CLAUDE.md` is explicit that internal
  documents are not citable here and that the reasoning belongs in prose instead of a reference a
  stranger cannot follow. A standard nobody in the repository can read is not a standard.

## Consequences

- The prefix table now has two homes — this repository and the internal guide it came from — and they
  can drift. This repository's copy governs what this repository ships; a divergence is a thing to
  resolve deliberately, not a bug in the copy here.
- Generated *assets* keep the weaker marking: `SM_Terrain` says nothing about Mantle Place outside
  its folder. That is accepted, not overlooked.
- The scoping sentence needs to survive. A future reader meeting `MP_*` will reach for the root rule
  again, which is why the carve-out is written in `unreal/CLAUDE.md` next to the table rather than
  left in this record alone.
