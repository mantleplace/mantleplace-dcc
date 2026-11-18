---
name: contributor-procedure
description: The whole working procedure for a contributor, human or agent, in this repository — where a worktree goes, how a branch is named, which gate to run before pushing, how a pull request closes its issue, and how it lands. Read before creating a worktree or branch, before opening a pull request, or when a session ends.
---

# Contributor procedure

Everything a contributor needs to do the work here is on this page and the pages it links. Nothing
depends on a document outside this repository, and nothing here names a tool a reader cannot install:
the procedure is plain `git` and `python`, and an agent follows it the same way a person does.

## Worktrees

Make a worktree for every piece of work, inside your checkout, never beside it:

```bash
git fetch origin
git worktree add .claude/worktrees/<type>-<slug> -b <type>/<slug> origin/main
```

- The folder is the branch with the `/` turned into `-`, so `fix/revit-tin-holes` lives at
  `.claude/worktrees/fix-revit-tin-holes`. `.claude/worktrees/` is git-ignored.
- Do not create one beside the checkout or with `../<dir>`; a tool that walks the checkout will not
  find it, and a stray sibling folder is easy to commit into by mistake.
- Make it from a standalone clone of this repository, never from a consumer's submodule mount under
  `Plugins/`: a worktree there puts a second plugin descriptor inside the consumer's recursive plugin
  scan. If `git status` says `HEAD detached at <sha>`, you are in such a mount; read
  [`CLAUDE.md`](../../CLAUDE.md) rule 3 first.
- Confirm the repository before the first write: `git remote -v` must show `mantleplace-dcc`.
- Work from the worktree by absolute path. When the work is merged, remove the worktree and delete
  its branch.

## Branches

A branch is `<type>/<slug>`: a conventional-commit type (`feat`, `fix`, `docs`, `chore`, `refactor`,
`test`, `ci`), a slash, and two to four kebab-case words that say what the change is. A branch name is
public the moment it is pushed, so it never carries an issue number or the name of anything a reader
cannot open ([`public-surface.md`](public-surface.md)).

## Commits

Conventional-commit subject (`fix(revit): build from the TIN`), signed off with `git commit -s`
([`CONTRIBUTING.md`](../../CONTRIBUTING.md)). A commit message cannot be edited once pushed, so read
[`public-surface.md`](public-surface.md) before writing one. Catch a bad message early with
`git config core.hooksPath .githooks`.

## The gate

Before you push, run the gates for the files you touched. The commands, and which workflow runs each,
are listed once in [`ci.md`](ci.md#running-the-gates-locally); the public-hygiene gate is the one every
change needs:

```bash
python tools/public-hygiene/check_public_references.py
python tools/public-hygiene/check_docs_integrity.py
python tools/public-hygiene/check_vocabulary.py
(cd tools/public-hygiene && python -m unittest discover -p "test_*.py")
```

Every workflow that runs on a pull request must be green; [`ci.md`](ci.md) names the required ones.
Write the failing test before the change it covers. A behaviour the conformance suite pins changes
only together with a case in that suite.

## Closing the issue

Put `Closes #<number>` on its own line in the pull request body, naming an issue in *this* repository.
The squash-merge then closes it. A body states what changed and why in prose, lists the decisions the
change took with a reason for each, and cites no document a reader here cannot open. Use `Part of
#<number>` instead of `Closes` when the issue needs a further pull request.

Work belongs to the repository whose tracked files its merge commit touches
([`work-routing.md`](work-routing.md)); open the issue here when this repository's files change.

## Landing

Open the pull request from your branch against `main`; squash is the default merge. Do not push to
`main`, and do not force-push a shared branch. When the branch is behind, merge `origin/main` into it
rather than rebasing. Merge only when every required check is green, then confirm the issue closed,
remove the worktree and delete the branch.
