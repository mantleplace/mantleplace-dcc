---
name: work-routing
description: Which repository a piece of work belongs to, and why the project you open to do the work is never the tracker for it. Read before filing an issue, opening a pull request, or deciding that something you found while working here is this repository's problem. The companion rule about what may be *cited* across the boundary is public-surface.md.
---

# Where work goes

This repository is one of several. Most of the others are private, and this file exists because the
boundary between them is invisible from here: a reader standing in this tree can see what is in it,
and cannot see what is next to it. That asymmetry is deliberate — a stranger must be able to read,
build and contribute to this repository without knowing that anything else exists — and it has one
cost, which is that an agent working here has no way to discover that some work belongs elsewhere.

This file is that discovery.

## The test

**Work belongs to the repository whose tracked files its merge commit touches.**

That is the whole test, and it is deliberately mechanical. It does not ask who benefits, who asked,
what the work is about, or whether it is sensitive. Those questions all have defensible answers that
disagree with each other, which is exactly why none of them is the test.

Applied here: if the deliverable is a file under [`unreal/`](../../unreal/),
[`revit/`](../../revit/), [`spec/`](../../spec/), [`tools/`](../../tools/) or [`docs/`](../), it is
this repository's work and it gets an issue here. If the deliverable is a level, a rendered image, a
packaged application, a private test fixture, a build gate that needs a licensed install, or a
document that governs more than one repository, it is not, whatever it is about.

## The corollary: the workbench is never the tracker

**Where you sit to do the work has no bearing on where the work is tracked or committed.**

This is the half that gets forgotten, because it is counter-intuitive in exactly the cases that
matter most. Some work on this repository's Unreal plugin cannot be done without an Unreal project
to open, and this repository does not contain one — it contains a plugin, which a *consuming
project* mounts. So the doing happens in a consuming project and the landing happens here.

That is normal. It is not a sign that the work has moved, that the boundary is fuzzy, or that the
consuming project has a claim on the result. A consuming project is a workbench. A workbench holds
the work while you do it and owns none of it.

The failure mode this prevents is small and expensive: an item gets filed against the repository the
person happened to be standing in, the two trackers drift apart, and the same piece of work ends up
described twice, in two places, in two vocabularies, with neither copy knowing about the other.

Two consequences worth stating plainly:

- **A consuming project is never this repository's issue tracker**, and this repository is never a
  consuming project's.
- **A branch in a submodule checkout of this repository is a branch of this repository.** It is
  pushed here and reviewed here, whatever tree it was typed in. The hazard in that workflow is the
  detached `HEAD` a submodule update leaves behind, not the directory — root
  [`CLAUDE.md`](../../CLAUDE.md) has the cure, and it has to come before the first edit.

## Work that spans repositories

A plan, a standard or a contract that governs more than one repository belongs to none of them. It
lives above all of them, in the place that holds cross-repository knowledge, and each repository
carries only the part it executes.

This repository therefore does not host a sequencing plan for work spread across several
repositories, even when most of the phases land here. What it hosts is the issues for its own
phases.

## When you find work that belongs somewhere else

Do not file it here and do not fix it here. Both are worse than leaving it, because a misfiled issue
looks settled and a stray fix arrives with no reviewer who owns it.

- **Say so in the thread you are already in.** The founder routes it. That is a one-line cost.
- **Do not describe the other repository, name it, or link to it** while doing so, on any of the
  five publication surfaces. What is refused and where is
  [`public-surface.md`](public-surface.md), and it is a required check on `main`.
- **Never move an issue across the boundary with a transfer.** A transfer carries the title, the
  body and every comment in one action, and the comments are the part nobody re-reads. Re-file
  instead, writing the new body clean, and close the old one pointing at the new. A pointer from a
  private tracker to a public one is legal; the reverse is not.

## Citing across the boundary

One direction only. A private document may cite a public one freely. **A public file, commit
message, pull request or branch name may never cite a private one** — not by URL, not by path, not
by issue number. [`public-surface.md`](public-surface.md) is the authority on that, including the
one split where a bare issue reference is this repository's own self-citation and is fine.

The tracker is a publication surface too. An issue title, an issue body and every comment on one are
world-readable the moment they are posted and there is no draft state before that. The gate does not
read them, so this one is on you.
