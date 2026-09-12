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
spec/                          the public MPB format spec — prose only; the schema stays remote
tools/manifest-conformance/    the contract gate + the shared conformance corpus
tools/public-hygiene/          the private-reference gate + its cases
tools/unreal-naming/           the generated-name drift gate + its cases
docs/adr/                      architecture decision records, numbered and cross-host
docs/agents/                   how the engineering skills read this repo — tracker, labels, domain
docs/platform-auth-contract.md sign-in and tokens — the one contract both hosts implement
.githooks/                     opt-in pre-publication hooks (core.hooksPath) running that gate
.github/workflows/             the four public CI gates, plus the stale-tracker job
LICENSE  TRADEMARK.md  SECURITY.md  CONTRIBUTING.md  CODE_OF_CONDUCT.md  ROADMAP.md  README.md
CONTEXT.md  CLAUDE.md
```

**The rule for every future top-level addition:** *a top-level folder is a DCC host or a cross-host
concern, nothing else.* Names are spelled out in full — `mantleplace`, never `mp`. `max/`,
`blender/`, `rhino/` are created when they have real content, never as empty placeholders — and
"real content" means the triad every layer here is built as (an impure host shim, a pure logic core,
and a headless test of that core — [CONTRIBUTING.md](CONTRIBUTING.md#style-and-shape)) plus the new
folder's own `<host>/CLAUDE.md`. **That
rule governs this repository and its code** — paths, folders, modules, symbols — **not the strings a
plugin writes into a user's project.** A host may abbreviate where a user reads the name in a cramped
host UI; Unreal does, on actor labels, and only there
([ADR 0003](docs/adr/0003-naming-authority-and-mp-prefix.md)). Do not "fix" one by citing the other.

## The three rules

### 1. Everything here is public

Anything you write lands in a world-readable repository, permanently, whether or not it is later
deleted — across five surfaces: tracked files, commit messages, pull request titles, pull request
bodies and branch names. Internal trackers, internal documents and internal repositories are not
citable here, not by URL, not by path, not by issue number; a bare `#42` in a Markdown file
auto-links to *this* repo's issue 42, which is worse than dangling. Credentials never, including in
`.uasset` files, which serialize property values. **This one is checked**, by `ci-public-hygiene`,
whose `references` job is a required check on `main`. **Before writing on any of those five
surfaces, read [`docs/agents/public-surface.md`](docs/agents/public-surface.md)** — what is refused,
the one split that makes a bare `#42` fine in a commit message, the structural exemptions, and the
pre-publication hook that catches a violation before a push publishes it.

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

## Worktrees and branches

Worktrees are created by hand with `git worktree add ../<dir> -b <type>/<short-description>`, as
**siblings of `main`** — never inside the repository. The directory name is the branch name with `/`
replaced by `-`, so the folder always names the branch it holds; a folder whose name does not resolve
to its branch is a defect, not a style choice. **Do not use `claude --worktree` or the
`EnterWorktree` tool here:** both are hard-coded to `<repo-root>/.claude/worktrees/<name>` and to a
branch named `worktree-<name>`, neither is configurable, and the location rule has teeth here — this
tree is mounted inside a consuming Unreal project's `Plugins/` directory, where plugin discovery is a
recursive scan, so a worktree under the repo root would put a second `MantlePlace.uplugin` inside
that scan. Retire a worktree when its pull request merges — `git worktree remove <dir>` then
`git branch -d <branch>`. Nothing does this for you: Claude Code's periodic sweep removes only the
worktrees it created itself and never touches one made with `git worktree add`.

## Releases

**One release track per host.** Tags are `<host>-<version>` — `revit-0.1.0`, `unreal-0.4.0` — no
`v`, so tag-matches-artifact is a string equality; `v0.1.0`–`v0.3.0` are Unreal's pre-history and are
never renamed. Each release body is that track's changelog *and* its provenance record; there is no
changelog file. **No release can be built or gated in public CI, and none ever will be** — both hosts
need a licensed install on the build machine (Unreal an engine, Revit `RevitAPI.dll` from Revit
2025), and a self-hosted runner is forbidden here, so packaging runs privately. Packaging is not the
gate: for Revit the gate is the ribbon loading and one real import completing in **2025, 2026 and
2027**, which no machine without all three can claim. See
[`docs/adr/0001-per-host-release-tracks.md`](docs/adr/0001-per-host-release-tracks.md), and
`revit/tools/Package-MantlePlaceRevit.ps1` for the repeatable half.

## Binaries

**There are no Git LFS patterns in this repository, on purpose** — a stranger's first clone must not
be a multi-hundred-megabyte pull; the binaries that are here — one `.uasset`, three fonts, three PNG
icons — total well under 1.2 MB and are plain git blobs. **Ask before you `git add` any binary, a
new file of a type already here included:** the axis is bytes, not novelty, and the budget being
protected is a stranger's first clone rather than a list of blessed extensions. Git decides
text-vs-binary at `git add` time, and a binary committed here is in the history forever with no later
fix that is not a force-push. There is deliberately **no stated per-file size threshold** — nobody
has set one — which is exactly why the answer is to ask rather than to judge. **No engine binaries,
no compiled plugins, no test bundles, no sample assets, and no sample bundles — ever.** The last one has teeth: real geospatial data carries licence
obligations, and shipping a bundle is redistributing it. The docs show generation instead.

## The boundary that keeps this client thin

**Any logic whose capture by a fork would hurt Mantle Place does not belong in this repository.**

Concretely: the plugins apply pre-derived values and never derive them. No CRS or datum machinery, no
coverage-aware source selection, no mosaic assembly, no material-weight derivation, no licence
compliance gating. The manifest publishes a survey point, a landscape transform, a drape extent — the
plugin reads them and applies them verbatim.

This is enforced at review, permanently. A patch that computes a placement value locally is refused
even when the arithmetic is correct.

That is a boundary on *logic*. The boundary on *work* — which repository an item belongs to, and why
the project you open to do the work is never the tracker for it — is
[`docs/agents/work-routing.md`](docs/agents/work-routing.md). The two are independent: work can be
perfectly thin and still belong somewhere else.

## Finishing a session

A session finishes what it starts. An item may outlive the session only if it (1) needs a decision
only the founder can make, (2) needs access the agent does not have, (3) touches a file this repo's
law forbids editing, or (4) sits outside the session's working tree, where fixing it would put
unrelated changes in the diff. Nothing else qualifies — not size, not risk, not "the founder might
not want it." Where checks exist, closed out means the checks pass; if they cannot be made to pass,
that is (2), and it is raised when it is hit, not at the end. There is no standing "next steps" or
"outstanding" section: one appears only when an item passes one of the four tests or the founder
asks, and each item names the test it claims. Work resolved on the agent's own judgment is disclosed
in writing (commit body, ledger, or manifest), never saved up for the closing message.

## CI

Four workflows run on every pull request, all on free hosted runners: `ci-manifest-conformance`,
`ci-revit-tests`, `ci-public-hygiene` and `ci-unreal-naming`. (`stale.yml` is tracker hygiene, not a
gate.)

**A workflow name is not a check name.** Branch protection matches *jobs*, and the mapping is not
one-to-one — `ci-revit-tests` contributes two. The four required checks on `main` are
`conformance`, `pure-core`, `pure-core-windows` and `references`; read them from the repository
rather than from this list, with
`gh api repos/mantleplace/mantleplace-dcc/branches/main/protection`. Note what is **absent**:
`generated-names`, the `ci-unreal-naming` job, reports on every pull request but is not a required
check, so the one automated guard in front of an Unreal naming regression cannot currently block a
merge.

**No workflow may carry a `paths:` filter on `pull_request`** — a required check that is
path-filtered never reports on a pull request outside its paths, so the check sits pending forever
and nothing can merge. (A `paths:` filter on `push` is fine; `ci-revit-tests` has one.) **Never attach a self-hosted runner to this repository:** a fork's pull request would
execute on the build machine. The Unreal compile stays on private infrastructure for exactly that
reason, so a green pull request here can still break the engine build — an accepted, published lag
([README](README.md#ci-and-what-it-does-not-cover)). **C++ formatting** is
[`unreal/.clang-format`](unreal/.clang-format), for new code only; nothing in CI checks it and a
reformat sweep is refused.

## Where knowledge lives

Most facts already have exactly one home. Find it before writing a fact down anywhere else.

- **Which repository a piece of work belongs to** → [`docs/agents/work-routing.md`](docs/agents/work-routing.md).
  The test is mechanical: **work belongs to the repository whose tracked files its merge commit
  touches**, and **the project you open to do the work is never the tracker for it**. Read it before
  filing an issue or deciding that something you hit while working here is this repository's problem.
- **Writing anything public** — a file, a commit message, a PR title or body, a branch name →
  [`docs/agents/public-surface.md`](docs/agents/public-surface.md).
- **What a word means** → [`CONTEXT.md`](CONTEXT.md) — the glossary, and only that; no rule, no
  decision, no implementation detail. A term belongs there once the same word has meant two things
  to two people.
- **Why a decision was taken** → [`docs/adr/`](docs/adr/), numbered and append-only. Today:
  **0001** per-host release tracks and the missing `v` · **0002** Unreal import identity, where a
  **re-import replaces** · **0003** naming authority and the `MP_` prefix · **0004** Revit terrain
  identity, where a **re-import refuses** — 0002's bug in the other host with the opposite remedy ·
  **0005** release installs are copy-first · **0006** Unreal **declines**
  `elevation.dem.bounds_target_crs` while Revit consumes it, and why the asymmetry is the contract
  rather than a disagreement · **0007** every `HPS-NN` cited in a public file must resolve in a
  public document, so the **publicly-cited half of the standard is published here** and a private
  rule becomes uncitable in public. Write one only for a decision hard to reverse,
  surprising without the context, and the result of a real trade-off; an ADR is not a design
  document.
- **The manifest contract** → the published JSON Schema series, cited by public URL. It is the
  authority; never restate a value it owns, and never hardcode a version in prose — the version each
  host is verified against lives in
  [`tools/manifest-conformance/verified-against.json`](tools/manifest-conformance/verified-against.json),
  where CI checks it.
- **The bundle format, in public prose** → [`spec/`](spec/), **descriptive** — it never restates a
  field, an enum, a constraint or a version:
  - [`format.md`](spec/format.md) — the zip layout, **the pointer doctrine** (find every file by a
    manifest pointer value, never by folder name), the `hosts.<hostId>` block boundary a consumer
    may not cross, **sha256 present / absent / required-and-missing**, and apply-placement-verbatim.
  - [`compatibility.md`](spec/compatibility.md) — what MAJOR/MINOR/PATCH mean, and what to do with
    **an unknown field, an unknown enum value, or an unknown higher major**.
  - [`conformance.md`](spec/conformance.md) — what claiming a corpus group obliges you to.
    **Adding a corpus case** starts here and continues in
    [`corpus/README.md`](tools/manifest-conformance/corpus/README.md); the corpus is normative and
    maintainer-owned, so a case is proposed by pull request, never forked.
  - [`changelog.md`](spec/changelog.md) — the one place versions appear, as dated history.
- **Cross-host normative rules** → the Host Plugin Standard, cited by `HPS-NN` id. Its master is
  private, and [`docs/adr/0007-publicly-cited-standard-rules-are-published-here.md`](docs/adr/0007-publicly-cited-standard-rules-are-published-here.md)
  decides that **a public file may cite only a rule whose text is public** — the publicly-cited half
  is published here, and a rule that stays private becomes uncitable in public. Until that document
  exists, **no public file may add an `HPS-NN` citation it does not already carry.** This is not
  [`spec/`](spec/), whose charter is the bundle format and nothing else.
- **Signing in, tokens, refresh, sign-out** →
  [`docs/platform-auth-contract.md`](docs/platform-auth-contract.md) — what `mantle.place` must
  serve for either host to sign in and stay signed in, and which rejections are definitive. Both
  hosts implement it against one shared credential, so it is cross-host, not Unreal's.
- **Building, testing, or what a host writes into a *user's* project** → that host's own `CLAUDE.md`
  ([`unreal/`](unreal/CLAUDE.md), [`revit/`](revit/CLAUDE.md)), read before touching that host. Not
  `spec/`, which describes the format, and not here.
- **Issues, labels, triage** → [`docs/agents/`](docs/agents/) — see "Agent skills" below.
- **What the plugins do, and how to build them** → [`README.md`](README.md).
- **Governance, and what this repo refuses** → [`CONTRIBUTING.md`](CONTRIBUTING.md) (the merge bar,
  DCO sign-off, and the patches declined unread), [`SECURITY.md`](SECURITY.md) — **the auth flow
  (PKCE, the loopback redirect listener, the token grant, the auth state machine) and the secret
  stores are closed to outside patches: a defect there is a private report, not a pull request**,
  [`TRADEMARK.md`](TRADEMARK.md).

## Agent skills

Configuration the engineering skills read before they act — how *this* repo is worked, not what it
contains.

- **Issue tracker** → [`docs/agents/issue-tracker.md`](docs/agents/issue-tracker.md). GitHub issues
  on `mantleplace/mantleplace-dcc`, via `gh`; external PRs are **not** a triage surface.
- **Triage labels** → [`docs/agents/triage-labels.md`](docs/agents/triage-labels.md). Five state
  roles and two categories, each label string equal to its own name.
- **Domain docs** → [`docs/agents/domain.md`](docs/agents/domain.md). Single-context: one
  [`CONTEXT.md`](CONTEXT.md) and one [`docs/adr/`](docs/adr/), both cross-host.
