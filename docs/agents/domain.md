# Domain docs

How the engineering skills consume this repo's domain documentation when exploring the codebase.
**Single-context**: one `CONTEXT.md` and one `docs/adr/` at the root, both cross-host.

## Before exploring, read these

- **[`CONTEXT.md`](../../CONTEXT.md)** at the repo root — the glossary, and only that. It settles
  which of two words to use and what each denotes. It holds no implementation detail, no rule and no
  decision.
- **[`docs/adr/`](../adr/)** — the ADRs that touch the area you are about to work in. They are
  numbered and cross-host: a decision recorded for one host is evidence for the next, and where a
  host departs from one the departure is itself recorded rather than assumed.

There is no `CONTEXT-MAP.md` here and there should not be one: the host folders are hosts, not
bounded contexts, and the vocabulary is deliberately shared across them.

## File structure

```
/
├── CONTEXT.md
├── docs/adr/
│   ├── 0001-per-host-release-tracks.md
│   ├── 0002-import-identity.md
│   └── ...
├── revit/          host
└── unreal/         host
```

Each host folder carries its own `CLAUDE.md` with toolchain specifics. Those are instructions, not
domain docs — read them for how to build, not for what a word means.

## Use the glossary's vocabulary

When your output names a domain concept — an issue title, a type name, a log line a curator will
read, a test name — use the term as `CONTEXT.md` defines it, and avoid the synonyms it lists under
`_Avoid_`. Those lists are not style preferences: each one is a word that has already meant two
things to two people.

A concept missing from the glossary is a signal. Either you are inventing language the project does
not use, or there is a real gap — and the bar for filling it is stated in root `CLAUDE.md`: a term
belongs in `CONTEXT.md` **once the same word has meant two things to two people**, not before.

## Flag ADR conflicts

If your output contradicts an existing ADR, surface it explicitly rather than silently overriding:

> _Contradicts ADR 0002 (import identity) — but worth reopening because…_

A host departing from a cross-host ADR is not a conflict to hide; it is the next ADR. ADR 0004 is the
worked example: it takes ADR 0002's diagnosis and rejects its remedy, for a reason particular to
Revit, and says so in as many words.

## Where the rest lives

The contract, the public format prose and the normative cross-host rules are **not** here. Root
[`CLAUDE.md`](../../CLAUDE.md) has the map under "Where knowledge lives"; read it before proposing
that a fact be written down, because most facts already have exactly one home.
