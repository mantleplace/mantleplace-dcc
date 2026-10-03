# mantleplace-dcc — repo identity and layout

**This repo is `mantleplace-dcc`. Version control is git. It is open source, Apache 2.0, and
everything in it is world-readable.**

Read that line before running any write command. Both halves matter — see "The three rules" below.

**Workflow** — worktrees, branches, issues, landing, finishing a session — is the Mantle Place agent
standard, loaded by the maintainers' tooling at session start. This file holds only what is specific
to this repository; nothing a contributor needs lives only in that standard.

## Identity

| | |
| --- | --- |
| Repo | `mantleplace/mantleplace-dcc` on GitHub |
| VCS | **git**. No Git LFS — see "Binaries" below |
| Licence | [Apache 2.0](LICENSE); the name is not licensed ([TRADEMARK.md](TRADEMARK.md)) |
| Contribution terms | DCO sign-off, no CLA ([CONTRIBUTING.md](CONTRIBUTING.md)) |

**dcc means *digital content creation*:** the host applications designers work in. This repo holds
the Mantle Place plugin for each such host, and nothing else. A consumer mounts it as a git
submodule inside the Unreal project's `Plugins/` directory, where plugin discovery is a recursive
scan.

## Layout

```
unreal/MantlePlace/            the UE 5.8 plugin, folder + .uplugin — PascalCase within
revit/                         the Revit plugin: pure Core, Client, Addin shim, headless tests
spec/                          the public MPB format spec — prose only; the schema stays remote
tools/manifest-conformance/    the contract gate + the shared conformance corpus
tools/public-hygiene/          the private-reference and docs-integrity gates + their cases
tools/unreal-naming/           the generated-name drift gate + its cases
tools/brand-assets/            renders the mark for both hosts; its input is private
tools/local-install/           the session-start check that a host's local install is the tree
docs/adr/                      architecture decision records, numbered and cross-host
docs/agents/                   how agents work this repo — tracker, labels, domain, CI, public text
docs/platform-auth-contract.md sign-in and tokens — the one contract both hosts implement
docs/host-support.md           which host does what with each feature, and why a gap exists
docs/import-inventory.md       what each host imports, when, and what it was measured to cost
.githooks/                     opt-in pre-publication hooks (core.hooksPath) running that gate
.github/workflows/             the five public CI gates, plus the stale and tracker-hygiene jobs
LICENSE  TRADEMARK.md  SECURITY.md  CONTRIBUTING.md  CODE_OF_CONDUCT.md  ROADMAP.md  README.md
CONTEXT.md  CLAUDE.md
```

**Every future top-level addition is a DCC host or a cross-host concern, nothing else.** Names are
spelled out in full — `mantleplace`, never `mp`. `max/`, `blender/`, `rhino/` are created only with
real content: the triad every layer is built as (an impure host shim, a pure logic core, a headless
test of that core — [CONTRIBUTING.md](CONTRIBUTING.md#style-and-shape)), the host's local-install
tool and check script (`HPS-50`), and its own `<host>/CLAUDE.md`. **That rule governs this
repository's code** — paths, folders, modules, symbols — **not the strings a plugin writes into a
user's project.** Unreal abbreviates on actor labels, and only there
([ADR 0003](docs/adr/0003-naming-authority-and-mp-prefix.md)). Do not "fix" one by citing the other.

## The three rules

### 1. Everything here is public

Anything you write lands in a world-readable repository, permanently, across six surfaces: tracked
files, commit messages, pull request titles, pull request bodies, branch names and the issue
tracker's own text. Internal trackers, documents and repositories are not citable here — not by URL,
path or issue number; a bare `#42` in a Markdown file auto-links to *this* repo's issue 42. No
credentials, including in `.uasset` files, which serialize property values. `ci-public-hygiene`
checks the first five (its `references` job is required on `main`); `ci-tracker-hygiene` detects the
sixth after the fact. **Before writing on any of the six, read
[`docs/agents/public-surface.md`](docs/agents/public-surface.md)** — what is refused, the one split
that makes a bare `#42` fine in a commit message, the exemptions, and the pre-publication hook.

### 2. Confirm the repo before any write command

`git remote -v` must show `mantleplace-dcc`. Sibling checkouts of other Mantle Place repositories
sit beside this one, all git, so a confused session can run a *successful* commit in the wrong
place — silently. Never assume the working directory from conversation history; check it.

### 3. If this tree is a submodule, create a branch before your first edit

⛔ **Run `git status` first. `HEAD detached at <sha>` means you are inside a consumer's submodule
checkout, and a commit made now belongs to no branch.** That is the default state in every consumer
— `git submodule update` checks out a commit, not a branch — and the next update leaves the commit
unreachable. Nothing warns you. Before you edit anything:

```bash
git fetch origin
git switch -c <type>/<short-description> origin/main
```

Commit with `-s` ([CONTRIBUTING.md](CONTRIBUTING.md)), **push the branch before touching the
consuming project**, and open the PR here. The consumer's pin moves only after that PR merges, and
only to the merged commit on `main` — a pin to your branch tip is unfetchable for everyone else. The
Mantle Place project tree mounts this repo at `Plugins/MantlePlaceDcc/` and documents its side.

## Worktrees

Beside the checkout, never inside the repository: a worktree under the repo root puts a second
`MantlePlace.uplugin` inside the consumer's recursive plugin scan. Harness-made worktrees are
relocated beside `main`; everything else about worktrees is the agent standard's.

## Releases

**One release track per host**, tagged `<host>-<version>` with no `v` (`revit-0.1.0`,
`unreal-0.4.0`), so tag-matches-artifact is a string equality; `v0.1.0`–`v0.3.0` are Unreal's
pre-history, never renamed. The release body is the changelog and the provenance record; there is no
changelog file. **No release is built or gated in public CI, ever** — both hosts need a licensed
install (an engine; `RevitAPI.dll` from Revit 2025), so packaging runs privately. Revit's gate is the
ribbon loading and one real import completing in 2025, 2026 and 2027.
[ADR 0001](docs/adr/0001-per-host-release-tracks.md) and
[`revit/README.md` ▸ Packaging a release](revit/README.md#packaging-a-release) hold the rest.

## Binaries

**No Git LFS, on purpose** — a stranger's first clone must not be a multi-hundred-megabyte pull. The
binaries here (one `.uasset`, three fonts, twenty-seven PNG icons, one Revit family, about 1.6 MB)
are plain git blobs. **Ask before you `git add` any binary**, a new file of a type already here
included: the axis is bytes, not novelty, and there is deliberately no per-file size threshold, which
is why the answer is to ask. A binary committed here is in history forever. **No engine binaries, no
compiled plugins, no test bundles, no sample assets, no sample bundles — ever**; real geospatial data
carries licence obligations, so the docs show generation instead.

## The boundary that keeps this client thin

**Any logic whose capture by a fork would hurt Mantle Place does not belong in this repository.**
The plugins apply pre-derived values and never derive them: no CRS or datum machinery, no
coverage-aware source selection, no mosaic assembly, no material-weight derivation, no licence
compliance gating. The manifest publishes a survey point, a landscape transform, a drape extent — the
plugin reads them and applies them verbatim. Enforced at review, permanently: a patch that computes a
placement value locally is refused even when the arithmetic is correct.

That bounds *logic*. Which repository a piece of *work* belongs to is
[`docs/agents/work-routing.md`](docs/agents/work-routing.md); the two are independent.

## Local installs

**The plugin a host loads on this machine is a copy of the tree: a single slot that tracks `main`**
(`HPS-50`; [`tools/local-install/`](tools/local-install/README.md) holds the model and the verdicts).

- **At session start, run `./tools/local-install/Check-LocalInstall.ps1`.** `current` and `preview`
  need nothing; `stale` or `unverified` means a bug seen in the host is not yet evidence about the
  code. Unreal says `not configured` without `MANTLEPLACE_CONSUMING_PROJECT_ROOT`, which is fine.
- **A session that merges a PR touching `revit/` or `unreal/` deploys from `main` before it
  finishes**: pull `main`, run `revit/tools/Deploy-MantlePlaceRevit.ps1` and
  `unreal/tools/Refresh-UnrealInstall.ps1`, and report each stamp. Revit refuses while Revit is open;
  report that as a leftover, never skip it silently. A new Revit build's first launch is a human's
  ([`revit/README.md`](revit/README.md#loading-it-into-revit)).
- **A preview from a branch happens only when the founder asks in that session.** It is stamped as a
  preview, and the next post-merge deploy returns the slot to `main`.

## CI

Five workflows gate every pull request on free hosted runners: `ci-manifest-conformance`,
`ci-revit-tests`, `ci-public-hygiene`, `ci-unreal-naming`, `ci-brand-assets`. **No workflow may
carry a `paths:` filter on `pull_request`**, and **never attach a self-hosted runner** — a fork's
pull request would execute on the build machine, which is why the Unreal compile is private and a
green PR here can still break it. The required checks, the jobs behind them, what is not required,
and C++ formatting are in [`docs/agents/ci.md`](docs/agents/ci.md).

## Where knowledge lives

Most facts already have exactly one home. Find it before writing a fact down anywhere else.

- **Which repository a piece of work belongs to** → [`docs/agents/work-routing.md`](docs/agents/work-routing.md).
  Work belongs to the repository whose tracked files its merge commit touches, and the project you
  open to do the work is never the tracker for it.
- **Which host does what with each feature, and why a gap exists** →
  [`docs/host-support.md`](docs/host-support.md), the one place a per-host status is stated.
- **What each host imports, by default or not, and what it costs** →
  [`docs/import-inventory.md`](docs/import-inventory.md); Revit's measured figures stay in
  `SlowStepNotice.Measured` and are cited there.
- **Writing anything public** → [`docs/agents/public-surface.md`](docs/agents/public-surface.md).
- **CI jobs, required checks, workflow rules** → [`docs/agents/ci.md`](docs/agents/ci.md).
- **What a word means** → [`CONTEXT.md`](CONTEXT.md) — the glossary only; no rule, decision or
  implementation detail. A term belongs there once the same word has meant two things to two people.
- **Why a decision was taken** → [`docs/adr/`](docs/adr/), numbered and append-only:
  **0001** per-host release tracks, no `v` · **0002** Unreal: a **re-import replaces** ·
  **0003** naming authority and the `MP_` prefix · **0004** Revit terrain: a **re-import refuses** ·
  **0005** release installs are copy-first · **0006** Unreal **declines**
  `elevation.dem.bounds_target_crs`, Revit consumes it · **0007** a publicly cited `HPS-NN` must
  resolve in a public document · **0008** the Revit drape is anchored to the **smooth-shading
  origin** · **0009** host assets render the **monogram** · **0010** Revit tooltip **vignettes are
  drawn**, capped at 355 px · **0011** the Revit provenance **schema GUID is permanent** ·
  **0012** Revit context buildings are **the site model's own extrusions, copied** · **0013** Revit
  **published contours** are DirectShapes · **0014** Revit draws hazards **on a hazard plan**.
  Write one only for a decision hard to reverse, surprising without the context, and the result of a
  real trade-off; an ADR is not a design document. Adding one is two edits
  ([`docs/agents/domain.md`](docs/agents/domain.md)).
- **The manifest contract** → the published JSON Schema series, cited by public URL. It is the
  authority; never restate a value it owns or hardcode a version in prose — each host's verified
  version lives in
  [`tools/manifest-conformance/verified-against.json`](tools/manifest-conformance/verified-against.json),
  where CI checks it.
- **The bundle format, in public prose** → [`spec/`](spec/), descriptive — it never restates a field,
  enum, constraint or version. [`format.md`](spec/format.md): the zip layout, the pointer doctrine
  (find every file by a manifest pointer, never by folder name), the `hosts.<hostId>` boundary,
  sha256 present / absent / required-and-missing, apply-placement-verbatim.
  [`compatibility.md`](spec/compatibility.md): MAJOR/MINOR/PATCH, and an unknown field, enum value or
  higher major. [`conformance.md`](spec/conformance.md): what claiming a corpus group obliges; the
  corpus is maintainer-owned and a case is proposed by pull request, never forked
  ([`corpus/README.md`](tools/manifest-conformance/corpus/README.md)).
  [`changelog.md`](spec/changelog.md): the one place versions appear.
- **Cross-host normative rules** → [`docs/host-plugin-standard.md`](docs/host-plugin-standard.md),
  cited by `HPS-NN` id. A public file may cite only a rule whose text is published there
  ([ADR 0007](docs/adr/0007-publicly-cited-standard-rules-are-published-here.md)), and
  `ci-public-hygiene` enforces it; if the rule is not published yet, state it rather than drop the
  citation. The standard says what a host must *do* with a field; `spec/` says what the field is.
- **Signing in, tokens, refresh, sign-out** →
  [`docs/platform-auth-contract.md`](docs/platform-auth-contract.md) — cross-host; both hosts share
  one stored credential.
- **Building, testing, or what a host writes into a *user's* project** → that host's own `CLAUDE.md`
  ([`unreal/`](unreal/CLAUDE.md), [`revit/`](revit/CLAUDE.md)), read before touching that host.
- **What the plugins do, and how to build them** → [`README.md`](README.md).
- **Governance, and what this repo refuses** → [`CONTRIBUTING.md`](CONTRIBUTING.md) (merge bar, DCO,
  patches declined unread), [`SECURITY.md`](SECURITY.md) — **the auth flow (PKCE, the loopback
  listener, the token grant, the auth state machine) and the secret stores are closed to outside
  patches: a defect there is a private report, not a pull request** — and
  [`TRADEMARK.md`](TRADEMARK.md).

## Agent skills

How the engineering skills work *this* repo:

- **Issue tracker** → [`docs/agents/issue-tracker.md`](docs/agents/issue-tracker.md). GitHub issues
  on `mantleplace/mantleplace-dcc`, via `gh`; external PRs are not a triage surface.
- **Triage labels** → [`docs/agents/triage-labels.md`](docs/agents/triage-labels.md): five states
  and two categories, each label string equal to its own name; the `spec` parent label; and the
  orthogonal stale-exemption, `wayfinder:` and repeatable `host:` labels.
- **Domain docs** → [`docs/agents/domain.md`](docs/agents/domain.md). Single-context: one
  [`CONTEXT.md`](CONTEXT.md) and one [`docs/adr/`](docs/adr/), both cross-host.
