# mantleplace-dcc — repo identity and layout

**This repo is `mantleplace-dcc`. Version control is git. It is open source, Apache 2.0, and
everything in it is world-readable.**

Read that line before running any write command. Both halves matter — see "The three rules" below.

## Identity

| | |
| --- | --- |
| Repo | `mantleplace/mantleplace-dcc` on GitHub |
| VCS | **git**. No Git LFS — see "Binaries" below |
| Licence | [Apache 2.0](LICENSE); the name is not licensed ([TRADEMARK.md](TRADEMARK.md)) |
| Contribution terms | DCO sign-off, no CLA ([CONTRIBUTING.md](CONTRIBUTING.md)) |

**dcc means *digital content creation*:** the host applications designers work in. This repo holds
the Mantle Place plugin for each such host, and nothing else. It is consumed by the Mantle Place
project tree as a git submodule mounted inside the Unreal project's `Plugins/` directory, where
plugin discovery is a recursive scan.

## Layout

```
unreal/MantlePlace/            the UE 5.8 plugin, folder + .uplugin — PascalCase within
revit/                         the Revit plugin: pure Core, Client, Addin shim, headless tests
tools/manifest-conformance/    the contract gate + the shared conformance corpus
.github/workflows/             the two public CI gates, plus the stale-tracker job
LICENSE  TRADEMARK.md  SECURITY.md  CONTRIBUTING.md  README.md  CLAUDE.md
```

**The rule for every future top-level addition:** *a top-level folder is a DCC host or a cross-host
concern, nothing else.* Names are spelled out in full — `mantleplace`, never `mp`. `max/`,
`blender/`, `rhino/` are created when they have real content, never as empty placeholders.

Each host folder carries its own `CLAUDE.md` with the toolchain specifics. For Revit, read
[`revit/CLAUDE.md`](revit/CLAUDE.md).

## The three rules

### 1. Everything here is public

Anything you write lands in a world-readable repository, permanently, whether or not it is later
deleted. So:

- **Cite only public URLs.** The bundle-manifest schema series at
  `https://mantle.place/.well-known/schemas/bundle-manifest/v{N}.json` is public and is the authority
  on the contract. Internal trackers, internal documents and internal repositories are not citable
  here — not by URL, not by path, not by issue number. A bare `#42` in a Markdown file auto-links to
  *this* repo's issue 42, which is worse than dangling: it is wrong and it looks deliberate.
- **Rule ids are fine, links to them are not.** `HPS-40`, `DOC-06` and the like are stable
  identifiers and stay as prose. Do not turn them into paths.
- **No credentials, ever, including in binary assets.** `.uasset` files serialize property values, so
  a URL typed into a Blueprint's class defaults is *in the file*. Capture-sensitive values are
  hydrated at packaging time from the build's secret store and must never be set in a committed
  asset.

### 2. Confirm the repo before any write command

`git remote -v` must show `mantleplace-dcc`. This tree sits beside sibling checkouts of other Mantle
Place repositories, all of them git, so a session confused about which root it is in can run a
*successful* commit in the wrong place. A commit that lands in the wrong repo is a real incident, and
it is silent. Never assume the working directory from conversation history — check it.

### 3. If this tree is a submodule, create a branch before your first edit

⛔ **Run `git status` here first. If it says `HEAD detached at <sha>`, you are inside a consuming
project's submodule checkout, and a commit made now belongs to no branch.**

```
$ git status
HEAD detached at a0f1c37
```

`git submodule update` checks out a *commit*, not a branch — that is what a pin is — so this is the
**default state** in every consumer, not a mistake someone made. Commit in it, let any later
`git submodule update` run, and the commit is unreachable and for practical purposes gone. Nothing
warns you. The work is simply lost.

The cure is one command, and it has to come **before** you edit anything:

```bash
git fetch origin
git switch -c <type>/<short-description> origin/main
```

Then commit with `-s` (see [CONTRIBUTING.md](CONTRIBUTING.md)), **push the branch before you touch
anything in the consuming project**, and open the PR here. The consuming project's pin moves only
after that PR merges, and only to the merged commit on `main` — pinning to your branch tip is green
on your machine and unfetchable for everyone else.

This applies to any consumer of this repo. The Mantle Place project tree mounts it at
`unreal/Plugins/MantlePlaceDcc/` and documents the full loop on its side; the rule above is what
matters wherever you are.

## Binaries

**There are no Git LFS patterns in this repository, on purpose.** A stranger's first clone must not
be a multi-hundred-megabyte pull. The binaries that are here — two `.uasset` files, three fonts,
three PNG icons — total about 1.2 MB and are plain git blobs.

**Do not add a new binary type without asking.** Git decides text-vs-binary at `git add` time, and a
binary committed here is in the history forever; there is no later fix that is not a force-push. If a
real need for LFS arises, that is a decision to take deliberately, once, rather than by accident.

**No engine binaries, no compiled plugins, no test bundles, no sample assets, and no sample bundles —
ever.** The last one is a rule with teeth: real geospatial data carries licence obligations, and
shipping a bundle is redistributing it. The docs show generation instead.

## The boundary that keeps this client thin

**Any logic whose capture by a fork would hurt Mantle Place does not belong in this repository.**

Concretely: the plugins apply pre-derived values and never derive them. No CRS or datum machinery, no
coverage-aware source selection, no mosaic assembly, no material-weight derivation, no licence
compliance gating. The manifest publishes a survey point, a landscape transform, a drape extent — the
plugin reads them and applies them verbatim.

This is enforced at review, permanently. A patch that computes a placement value locally is refused
even when the arithmetic is correct.

## Where knowledge lives

- **The contract** → the published JSON Schema series, cited by public URL. It is the authority; never
  restate a value the schema owns. The version each host is verified against lives in
  [`tools/manifest-conformance/verified-against.json`](tools/manifest-conformance/verified-against.json),
  where CI checks it — never hardcode a version in prose.
- **Cross-host normative rules** → the Host Plugin Standard, cited by `HPS-NN` id.
- **What the plugins do, and how to build them** → [`README.md`](README.md).
- **Governance** → [`CONTRIBUTING.md`](CONTRIBUTING.md), [`SECURITY.md`](SECURITY.md),
  [`TRADEMARK.md`](TRADEMARK.md).

## CI

Two workflows, both on free hosted runners, both required checks on `main`, together the merge bar:
`ci-manifest-conformance` and `ci-revit-tests`. **Neither may carry a `paths:` filter on
`pull_request`** — a required check that is path-filtered never reports on a pull request outside its
paths, so the check sits pending forever and nothing can merge. (`stale.yml` is tracker hygiene, not
a gate.)

**C++ formatting** is [`unreal/.clang-format`](unreal/.clang-format), for new code only: the existing
files predate it and are not clean against it. Nothing in CI checks formatting, and a reformat sweep
is refused — see [CONTRIBUTING.md](CONTRIBUTING.md).

**Never attach a self-hosted runner to this repository.** A fork's pull request would execute on the
build machine. The Unreal compile stays on private infrastructure for exactly this reason, which
means a green pull request here can still break the engine build — an accepted, published lag
([README](README.md#ci-and-what-it-does-not-cover)).
