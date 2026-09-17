# Local installs track `main`

The plugin a host application loads on a maintainer's machine is not the tree being edited. It is
a copy: for Revit, the assemblies in the per-user add-ins folder; for Unreal, the consuming
project's submodule checkout of this repository. Nothing keeps a copy in step with the tree, and
the failure is silent — a fix present in git and absent in the host, reported as a bug against code
that already contains the fix, with a file timestamp as the only tell. This folder is what makes
that visible, and the rules in root [`CLAUDE.md`](../../CLAUDE.md) are what make it rare.

## The model

**Each host's local install is a single slot that tracks `main`.** Revit loads one Mantle Place per
process — the add-in manifest carries one `ClientId`, and Revit refuses a duplicate — and a
consuming Unreal project has one submodule checkout, so an install per worktree is not available.
With several worktrees and one slot, the slot has to say what it holds:

- **Revit** — `Deploy-MantlePlaceRevit.ps1` writes `MantlePlace.install.json` beside the manifest:
  the commit, the branch, the worktree, whether that tree had uncommitted changes, and when. The
  assembly's own version carries the commit and a `-dirty` marker; About shows both, checked
  against each other.
- **Unreal** — the checkout *is* the stamp: `git` in the submodule says the commit, the branch and
  the dirty state.

A **preview** from a branch is allowed and stamped as such. It happens only when asked for in that
session, and the slot returns to `main` when a session deploys after a merge. This is the
"consistent and predictable" half: the default is `main`, and anything else says so on screen.

## The contract every host implements

Each host owns `<host>/tools/Check-<Host>Install.ps1`. It prints **one line** and exits by it:

| Verdict          | Exit | Meaning                                                                                      |
| ---------------- | ---- | -------------------------------------------------------------------------------------------- |
| `current`        | 0    | The slot holds `main` and nothing touching that host's folder has landed since.              |
| `preview`        | 0    | The slot holds a branch, deliberately, and is current with that branch's tip. Says so.       |
| `stale`          | 1    | `origin/main` (or the previewed branch) has newer commits under the host's folder. Names the count and the script to run. |
| `unverified`     | 1    | The slot cannot be compared: no stamp, a dirty tree, or a commit this clone has never seen.  |
| `not installed`  | 1    | The slot is empty.                                                                           |
| `not configured` | 0    | There is nothing to check on this machine. Quiet on purpose, so a stranger's clone sees nothing red. |

Counts are **scoped to the host's folder** (`revit/`, `unreal/`), so a docs-only merge never asks
for a redeploy. The check fetches first, because the tree it runs in is moved by other sessions;
`-NoFetch` compares against what the clone already has.

[`Check-LocalInstall.ps1`](Check-LocalInstall.ps1) runs every host's script and is the
session-start check root `CLAUDE.md` asks for. The shared verdict logic is
[`LocalInstall.psm1`](LocalInstall.psm1), and [`LocalInstall.Tests.ps1`](LocalInstall.Tests.ps1)
pins its sentences:

```powershell
pwsh -NoProfile -Command "Invoke-Pester tools/local-install/LocalInstall.Tests.ps1"
```

The module and every host script are **Windows PowerShell 5.1 compatible**, for the reason the
deploy script's header gives: that is what a curator machine has, and one pwsh-only construct
breaks the installer for the people it exists for. The Pester file is pwsh-only; it is the test,
not the code.

## Per host

**Revit** — `revit/tools/Deploy-MantlePlaceRevit.ps1` builds, installs into 2025, 2026 and 2027,
writes the stamp, and with `-Launch` starts Revit 2027 ready for Hot Reload.
`revit/tools/Check-RevitInstall.ps1` reads the stamp back. The loop is in
[`revit/README.md`](../../revit/README.md#loading-it-into-revit).

**Unreal** — the consuming project is private and lives wherever it lives, so the scripts are told
where with `-ConsumingProjectRoot` or `MANTLEPLACE_CONSUMING_PROJECT_ROOT`. `unreal/tools/Refresh-UnrealInstall.ps1` fetches
and checks out a ref in the submodule, detached, `origin/main` by default;
`unreal/tools/Check-UnrealInstall.ps1` compares the checkout. Neither touches the consuming
project's own tree: its pin is a commit in *its* history and moves only by its own pull request,
after the change has merged here.

**A new host** adds `<host>/tools/Check-<Host>Install.ps1` under the contract above, an install
tool that writes a stamp, and one line to the list in `Check-LocalInstall.ps1`. The obligation is
`HPS-50` in the [host plugin standard](../../docs/host-plugin-standard.md).

## What this does not do

- **Hot reload of a ribbon.** .NET Hot Reload, from a debugger attached to a Revit that the deploy
  script launched, applies method-body edits live. It cannot re-run `OnStartup`, so a ribbon change
  is a deploy and a restart. Unreal's Live Coding has the same shape: `.cpp` bodies live, headers
  and assets on restart.
- **The first launch.** A newly built, unsigned add-in raises Revit's *Security — Unsigned Add-In*
  dialog once per version, and the answer is kept only if that Revit then exits normally. No script
  answers it.
- **Refuse a deploy.** The deploy script records a dirty or off-`main` tree and never refuses it:
  it ships verbatim in the release zip, and a refusal path a curator can never reach is untested
  code. The check script is where "unverified" is said.
- **Run in CI.** No hosted runner has Revit, an engine, or the consuming project.
