# Contributing

Thanks for looking. This document is short and specific, and it tells you the things that will get a
pull request closed before it is read — so you can spend your time on the ones that will not.

## The maintenance posture, stated up front

**Best-effort. A weekly triage batch. No SLA.**

This is a small project with a small maintainer team. Issues and pull requests are triaged in a batch
roughly once a week. A patch that is small, tested and inside the merge bar below is likely to land
quickly; a large architectural change is likely to get a conversation before it gets a review.

Issues are marked stale after 90 days without activity and closed 14 days later; pull requests get 45
days and the same 14. That is not a judgement on the item — it is the alternative to a tracker with
hundreds of open entries nobody has read in five years, which tells a newcomer nothing about what is
actually live. Any comment clears the mark, a confirmed defect is labelled and exempted, and a closed
issue reopens on request. The job that does this is
[`.github/workflows/stale.yml`](.github/workflows/stale.yml), so the policy and its enforcement are
the same artifact.

If sustained load makes this posture dishonest, we will change **the published policy** rather than
quietly stop answering. Any such change is announced here.

## Sign your commits off (DCO)

This project uses the [Developer Certificate of Origin](https://developercertificate.org/). There is
**no CLA**. You keep the copyright in your contribution; you licence it to everyone under
[Apache 2.0](LICENSE), the same terms as the rest of the repository.

Add a sign-off line to each commit:

```
Signed-off-by: Your Name <you@example.com>
```

`git commit -s` writes it for you. It asserts that you wrote the patch, or have the right to submit
it under Apache 2.0.

> ⛔ **Working from a submodule checkout?** Run `git status` before you edit. If it reports
> `HEAD detached at <sha>`, create a branch first — `git switch -c <type>/<name> origin/main` — or the
> commit you are about to sign off belongs to no branch and is lost the next time the consuming
> project runs `git submodule update`. Nothing warns you. See `CLAUDE.md` rule 3.

We are not reserving the right to relicense this code. There is no CLA precisely because the only
thing a CLA would buy here is an option we do not intend to exercise.

## The merge bar is the conformance suite

**The objective bar is: the conformance suite passes, and behaviour it pins is not changed without
changing a case.**

Two workflows run on every pull request, on free hosted runners:

- **`ci-manifest-conformance`** — fetches the published bundle-manifest schema, checks every
  registered host is verified against the newest version, and checks the shared corpus under
  `tools/manifest-conformance/corpus/` for integrity.
- **`ci-revit-tests`** — builds the Revit pure core and client and drives the shared corpus through
  them on **.NET 8 and .NET 10**.

Both must be green. If your change makes a corpus case fail, the interesting question is whether the
case or the code is wrong — say which you think it is in the pull request, and why.

**Adding a corpus case.** The corpus is maintainer-owned, and a case binds *every* host, not just the
one you are working on. Propose a case here by pull request rather than forking a private copy; a
forked corpus is exactly the drift the corpus exists to prevent. Every case needs a `reason` — a
fixture nobody can explain gets deleted the first time it is inconvenient, usually by the person it
would have saved.

## The Unreal-compile gap, stated honestly

**A pull request can pass its merge bar here and still break the Unreal Engine compile.**

Compiling the Unreal plugin needs a licensed engine install on a Windows machine, which is not
something a public hosted runner has, and attaching a self-hosted runner to a public repository would
let a fork's pull request execute on the build machine. So the Unreal compile runs privately, and it
runs at integration time rather than at merge time.

The consequence is a real lag, and it is accepted rather than hidden: if your change breaks the
engine build, we will find out after it merges and you may be asked to follow up. Changes under
`unreal/` therefore get a slower, more careful review than changes under `revit/` or `tools/`. If you
have an engine install, build it locally and say so in the pull request — that shortens the loop more
than anything else you can do.

## What does not go in this repository

- **No binaries beyond what is already here.** No engine binaries, no compiled plugins, no test
  bundles, no sample models, no sample assets. This repository must stay something a stranger can
  clone in seconds.
- **No sample bundles, ever.** Not a small one, not a trimmed one. The docs show you how to *generate*
  one instead — see the [README](README.md). This is deliberate and is not negotiable per-PR.
- **No new binary file types** without asking first. There are no Git LFS patterns here on purpose,
  and a binary committed without one is in the history permanently.
- **No auth or secret-store patches.** See [SECURITY.md](SECURITY.md) — report, don't patch.
- **No derived numbers.** The plugins apply placement values the platform publishes; they do not
  re-derive them. A patch that computes a survey point, an EPSG zone or a landscape scale locally
  will be refused on principle even when the arithmetic is right — that boundary is what keeps this
  client thin, and it is enforced at review, permanently.

## Style and shape

Each layer is a **triad**: an impure host shim, a pure logic core, and a headless test of the core.
Protocol behaviour lives in the core, where it is testable with the DCC application not installed. If
you cannot assert your change without launching Revit or the Unreal editor, it is probably in the
wrong file.

Per-host specifics live beside the code:

- Unreal — [`unreal/MantlePlace/`](unreal/MantlePlace/)
- Revit — [`revit/README.md`](revit/README.md) and [`revit/CLAUDE.md`](revit/CLAUDE.md)

Match the surrounding code. The comment density here is higher than most codebases and that is
deliberate: comments explain *why*, especially where a rule guards a failure that looks like success.

**C++ formatting:** [`unreal/.clang-format`](unreal/.clang-format) is the authority for new Unreal
code. It is deliberately conservative — no rewrapping, no include reordering, no comment churn — but
**the existing files predate it and are not clean against it**, so do not run it over a file you are
not otherwise changing. Reformat-only pull requests are declined: they bury the change nobody can
review in a diff nobody can read, and this repository's Unreal compile runs privately and after the
merge, so a mechanical sweep is the worst possible thing to have to bisect.

## Reporting a bug

Open an issue with the host and version, what you expected, what happened, and the smallest bundle or
manifest shape that reproduces it. **Do not attach a bundle** — describe the manifest fields that
matter, or paste the relevant block with any identifiers removed.

Security issues go through [SECURITY.md](SECURITY.md), not the issue tracker.
