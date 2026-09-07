---
status: accepted
---

# The Revit terrain carries a stamp, and a stale one is refused rather than replaced

Every repeatable step of the Revit import asked "is this already here" except the one that mattered
most. The IFC link reuses the link it finds at that path, the terrain base level is found by name,
the drape's duplicated toposolid type is found by name, and the site-boundary subdivisions are found
by a stamp written into their Comments. The terrain step called `Toposolid.Create` unconditionally.
So a second import of one bundle left **two ground toposolids**, one exactly on top of the other,
and the subdivision guard could not save it: that guard reads the stamps on *the terrain it is
working on*, and the terrain step handed it a brand-new toposolid every run, so it found nothing and
rebuilt the whole set underneath it. Neither the duplication nor the rebuild was reported — the log
read like a clean import.

The terrain now carries an identity of its own, in the same instance Comments parameter the
subdivisions use: `Mantle Place Terrain {stem}/{build}`.

**The two halves answer different questions, and which one matched decides what happens.** The stem
is the cache key — the sanitised order id, or the zip's own path for a bundle that declares no order
— and says *whose ground this is*. The build token is the first twelve hex characters of the surface
artifact's declared sha256 and says *which build of it*. Then:

| what matched | what happens |
| --- | --- |
| stem and build | **Reuse.** The same ground from the same surface; nothing is rebuilt, and the later steps drape onto it. |
| stem only | **Refuse.** This order's terrain from an *earlier* build. No second ground is created, and the first is not deleted — the log names the toposolid and says to delete it and import again. |
| neither | **Create**, and stamp it. |

A bundle that declares no sha256 stamps its build half `unknown`, which matches only another
`unknown`: ⛔`HPS-28` says a null digest is *unknown*, never "verified", and the identity keeps that
true rather than claiming a sameness nothing demonstrates.

## Why this is not what the other host does

[ADR 0002](0002-import-identity.md) is this same bug in Unreal — *"a user who refreshed an area got
a second landscape sitting on top of the first, silently"* — and it resolved to **replace**: keyed on
the order, the importer force-deletes the prior content and re-imports. That is right there and wrong
here, and the difference is the host, not a preference.

In Unreal the imported content is generated and disposable; nobody hand-edits the actors in that
folder. In Revit the ground is the thing everything else is built on. A curator hosts buildings on
it, draws their own subdivisions on it, sets up views and sections against it — and deleting a
toposolid takes its subdivisions with it, including the ones this plugin's own drape step repairs on
a re-import. A destructive refresh in Revit destroys work the plugin did not make and cannot restore.

So the stale arm stops and explains rather than deleting. This is the same line
`SiteBoundaryIdentity` already draws: touch what this import owns, and nothing else.

## Considered options

- **Replace, as ADR 0002 does.** Rejected above: it deletes a curator's hosted work, and Revit's
  cascade makes the blast radius larger than the element named.
- **Reuse on the stem alone, ignoring which build the ground came from.** Rejected. A rebuilt order
  is the case the whole feature exists for, and reusing there keeps stale ground in the project
  while reporting success — the same silent-wrong-content failure as ADR 0002, arrived at from the
  opposite direction. The two-part stamp costs nothing: the sha is already on the import step,
  carried there so the integrity check cannot be skipped.
- **Refuse whenever any of this bundle's ground is present, without comparing builds.** Rejected: a
  re-import of the *same* build is how a curator repairs a partial import — it is what lets the drape
  find subdivisions it failed to paint the first time — and refusing outright would break the repair
  path that the drape's stamp lookup was written for.
- **Compare geometry instead of a stamp.** Rejected for `SiteBoundaryIdentity`'s reason: it means new
  API surface plus a tolerance nobody can defend, where a string in a parameter is decidable
  headlessly.
- **Remember the element id from the import session.** Rejected: it is an identity for a run, not for
  a document. It is exactly what the terrain step already did, and it is why the bug existed.
- **Claim an unstamped ground as ours.** Rejected. An unstamped toposolid is a curator's own, or
  another order's, or one from an import made before this record — and none of those are this
  bundle's to reuse or refuse. Guessing here is how a plugin ends up rewriting someone else's site
  model.

## Consequences

- **A project imported twice before this change still holds two grounds, and this does not clean them
  up.** The next import stamps neither: they are unstamped, so it builds a third alongside them and
  says so. Automatically deleting a user's model content on a plugin upgrade is not a thing this
  plugin will do — the same call ADR 0002 made for unrecognised content. The terrain probe now prints
  each ground's stamp, or `UNSTAMPED`, so which one to delete is readable rather than guessed.
- **On the stale arm the rest of the import still runs, onto the older ground.** The boundaries and
  the drape are independent steps, and the doctrine here is that a refusal in one step keeps the
  work of the others rather than abandoning them. The log says plainly which ground they landed on.
- **A curator who edits the terrain's Comments breaks their own next re-import**, which will build a
  second ground. That is already true of the subdivisions and is the accepted cost of using a
  user-visible parameter as an identity; a subdivision has no name slot, and neither does anything
  else about a toposolid instance that Revit does not also let a user type into.
- **For a bundle that declares no order id, the stem is the zip's full path**, so the same such
  bundle opened from two folders reads as two bundles and stacks. That is the pre-existing
  cache-key rule, not a new one, and it affects only order-less bundles — the local-development and
  admin path.
- **The guard runs before the surface artifact is extracted or parsed**, so a re-import no longer
  pays to unzip and triangulate a surface it is not going to use.
- Whether Revit accepts a `Comments` write on a `Toposolid` instance is not something the compiler
  can answer. A terrain it could not stamp is kept — the ground is real — and the log says it will
  not be recognised by a re-import, exactly as the subdivisions already do.
