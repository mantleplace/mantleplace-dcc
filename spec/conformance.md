# Conformance

**A conforming reader passes the corpus.** That sentence is the whole of this document; the rest
says what it means precisely enough to argue about.

## 1. A spec release is three artifacts, versioned together

| Artifact | Where | What it settles |
| --- | --- | --- |
| The **schema** | `https://mantle.place/.well-known/schemas/bundle-manifest/` | What a manifest may contain |
| The **prose** | this directory | What the blocks are for, and what a reader owes them |
| The **corpus** | [`tools/manifest-conformance/corpus/`](../tools/manifest-conformance/corpus/) | What correct behaviour looks like, executably |

The schema and the corpus are **normative**. The prose describes.

They ship as a set. The corpus declares the manifest version its fixtures are written against, and
that declaration is machine-checked against the fixtures themselves — a corpus whose stated version
and actual fixtures disagree fails the gate before any host suite sees it. That is what makes
"conforming to MPB *x.y.z*" a claim with a referent instead of a feeling.

## 2. What the corpus is

Language-neutral test vectors, as JSON: accept shapes, reject shapes, derived placement expectations
with explicit tolerances, and known-answer tables. Every Mantle Place host plugin runs them against
its own parser, and so can yours.

The mechanics — the index format, the case fields, the `accept` / `reject` / `vector` verdicts, the
tolerances, how a case is scoped to one host — are documented once, in the
[corpus README](../tools/manifest-conformance/corpus/README.md), and are not restated here.

These vectors began as inline C++ literals inside one plugin's test suite. That is a perfectly good
test suite and a completely unusable specification: a second implementer could read them only by
reading C++, and would re-derive them slightly differently. The bugs that follow — a null digest
read as "corrupt" rather than "unknown", a false northing dropped in the southern hemisphere — are
all silent, and they produce a plugin that works on the developer's bundle and misplaces a
customer's site by kilometres. Lifting the vectors into one language-neutral artifact is what makes
"the second host agrees with the first" something a test asserts rather than something a reviewer
hopes.

## 3. What conformance requires

A reader claims a set of **groups** and is bound by all of them.

⛔ **Every case in a claimed group must be driven.** Partial coverage inside a group is not
conformance. A group whose cases each exercise a different code path invites a suite that
dispatches by id and quietly skips the case added last month — so a conforming suite tracks which
ids it drove and fails on any it did not. A reviewer cannot see a missing branch in a four-hundred-
line test.

**A reader need not claim every group.** A manifest-first implementation claims the manifest group
and adds others as those layers land. Not claiming a group is honest; claiming it and skipping cases
is not.

**A case scoped to one host is skipped by every other host.** Placement math and block-specific
requirements are per-host by construction. A case that carries no such scope binds everyone — the
version gate, the unmaterialised-bundle partial parse, and the top-level pointers among them — and
may not declare an expectation only one host could compute.

⛔ **The corpus reader is itself under test.** Alongside the corpus proper is a self-test set of
deliberately broken fixtures — a malformed case file, an orphan, a duplicate id, an expectation key
declared with the wrong JSON type, an expectation no reader may consume. A reader that *accepts*
any of them has failed. The corpus proper proves your parser; the self-test proves the thing running
it, and a reader that silently reads past an expectation it does not understand will pass a corpus
full of assertions it never made.

## 4. Which groups a third-party reader needs

The corpus covers more than this spec does, because it also holds our own plugins to the vault
service contract.

- **`manifest`, `digest`, `projection`, `cache`** — the portable half. These are the groups this
  spec governs, and the ones a third-party bundle reader claims.
- **`vault`, `auth`** — the Mantle Place vault's REST and OAuth behaviour. First-party plugin
  obligations, not interchange. A reader that only opens bundles from disk does not claim them, and
  is fully conforming without them.

## 5. Proving it, and the two pins that record it

The offline gate lives at [`tools/manifest-conformance/`](../tools/manifest-conformance/) and needs
only Python and its standard library:

```bash
python -m unittest discover -s tools/manifest-conformance   # offline: corpus integrity + self-test
python tools/manifest-conformance/check_manifest_conformance.py
```

The second form also reaches the network, to compare what each host is verified against with what
the platform has actually published. It runs on every pull request here as a required check.

Two numbers record where a reader stands, and they are deliberately separate:

- **The corpus pin** — the manifest version the fixtures are written against, declared once by the
  corpus itself.
- **The per-host pin** — [`verified-against.json`](../tools/manifest-conformance/verified-against.json),
  one entry per host, recording the version that host's parser was actually **exercised** against.
  Registering is a claim of evidence, not of tolerance; raising a number means the tests were
  updated. Each entry also names where that host's version floor lives, so the gate can read the
  floor out of a C++ header, a C# constant or a Python module without knowing which is which.

The two are split because hosts move at different speeds. A pin moves per host; a floor moves in
lockstep, because one shared corpus cannot state a rule in absolute versions and be right for two
different floors at once. A practical consequence: a retired version joins the corpus' reject set
only once **every** registered host has cleared it. The reject set waits for the slowest host, and
that is a correctness property rather than a courtesy.

## 6. Changing the corpus

The corpus is **maintainer-owned**, and that is load-bearing: a forked private copy is exactly the
drift it exists to prevent. A reader that needs a new case proposes it here, by pull request.

Every case states its own reason. A fixture nobody can explain is deleted the first time it is
inconvenient, usually by the person it would have saved.

If you build a consumer against this spec and find a behaviour the corpus does not pin, that is the
most useful bug report this project can receive. It means two implementations could disagree while
both stay green, and it is worth an issue whether or not you send the case with it.
