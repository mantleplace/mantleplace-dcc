# Roadmap

Where these plugins are going, a quarter at a time. The near-term arc is the **Unreal plugin from
early access to 1.0 on Fab**, and the headline item on the way there is **World Partition large-AOI
import** — today an imported bundle becomes a single `ALandscape`, which is the honest ceiling on
how much ground one order can practically bring in.

Three threads run through every quarter, and each bullet below belongs to one of them:
**import capability** (the code), **documentation and onboarding** (the tutorial, the media, the
published format spec), and **release infrastructure** (the packaging, the CI that gates public
artifacts).

Dates are intentions, not promises. This is a small project with a
[published best-effort posture](CONTRIBUTING.md#the-maintenance-posture-stated-up-front); the
roadmap moves when reality does, by pull request, where you can see it.

## Q4 2026

- **World Partition large-AOI import begins** — streaming-proxy landscapes so a large area of
  interest imports as World Partition regions rather than one monolithic `ALandscape`, working
  toward the platform's largest orderable AOIs.
- **Packaged releases** — versioned GitHub Releases carrying a built plugin, with the packaging
  step automated so a release is repeatable rather than an event.
- **Unreal quickstart tutorial** — the missing walk-through from empty project to imported site,
  plus the README's screenshot and GIF slots filled with real captures.

## Q1 2027

- **World Partition import lands** — large-AOI orders import end-to-end, single-Landscape import
  remains the default for small sites.
- **Import-surface completion** — consume the manifest's `landscape_layers` rasters, finish the
  imagery-drape work, and add a per-layer import picker so a bundle can be brought in selectively.
- **The bundle format published as a spec** — the portable half of the host-plugin contract, with a
  changelog, a deprecation window and a compatibility policy, so a second implementer has a document
  and not just the [conformance corpus](tools/manifest-conformance/).

## Q2 2027

- **1.0 on Fab** — the Unreal plugin graduates early access and lists on Fab; in-repo installs keep
  working exactly as they do now.
- **Documentation completion** — the full import reference for both hosts, written against the
  shipped feature set rather than ahead of it.
- **Release-gating infrastructure** — the private engine-compile check wired to block a release
  (not a merge) automatically, narrowing the accepted public-CI gap the
  [README](README.md#ci-and-what-it-does-not-cover) documents.

## What is deliberately not on this roadmap

- **Other hosts** (Rhino, Blender, 3ds Max) — top-level folders appear when they have real content,
  never as placeholders, and none is scheduled. The Revit plugin continues at conformance parity —
  it tracks the manifest contract but takes no headline features in this window.
- **Anything that thickens the client.** Coordinate machinery, source selection, mosaic assembly and
  their relatives stay on the platform side by rule, so no quarter will ever contain them — see
  [What these plugins do](README.md#what-these-plugins-do).
