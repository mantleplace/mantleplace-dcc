# Roadmap

Where these plugins are going, a quarter at a time. The near-term arc is the **Unreal plugin from
early access to 1.0 on Fab**, and the headline item on the way there is **World Partition import**:
real-world sites brought in as streaming World Partition worlds. Today an imported bundle becomes a
single `ALandscape`, which is the honest ceiling on how much ground one order can practically bring
in.

Three threads run through every quarter, and each bullet below belongs to one of them:
**import capability** (the code), **documentation and onboarding** (the tutorial, the media, the
published format spec), and **release infrastructure** (the packaging, the CI that gates public
artifacts).

Dates are intentions, not promises. This is a small project with a
[published best-effort posture](CONTRIBUTING.md#the-maintenance-posture-stated-up-front); the
roadmap moves when reality does, by pull request, where you can see it. It moves early sometimes:
**the bundle format is published as a spec now**, in [`spec/`](spec/), ahead of the quarter that
planned it.

## Q4 2026

- **World Partition import begins** — imported sites become streaming World Partition worlds
  rather than one monolithic `ALandscape`, working toward the platform's largest orderable AOIs.
  The quarter also opens a spike on **Mesh Terrain** (Experimental in UE 5.8) as the forward
  terrain path alongside the `ALandscape` default.
- **Packaged releases** — versioned GitHub Releases carrying a built plugin, with the packaging
  step automated so a release is repeatable rather than an event.
- **Unreal quickstart tutorial** — the missing walk-through from empty project to imported site,
  plus the README's screenshot and GIF slots filled with real captures.

## Q1 2027

- **World Partition import lands** — large-AOI orders import end-to-end as streaming World
  Partition worlds: bundle layers arrive as **Data Layers** with generated **HLODs**, and the
  bundle's canopy and landcover data drives **PCG foliage** that generates and streams with World
  Partition. Single-Landscape import remains the default for small sites, and Mesh Terrain
  continues to be tracked as the Experimental feature matures.
- **Import-surface completion** — a per-layer import picker so a bundle can be brought in
  selectively, a shipped landscape material that samples the coverage rasters the importer already
  lands, and the remaining imagery-drape work.
- **The spec earns its second implementer** — [`spec/`](spec/) shipped early, so what this quarter
  owes it is use rather than prose: a worked third-party read of a real bundle against the spec and
  the [conformance corpus](tools/manifest-conformance/) alone, and whatever that exercise proves is
  missing.

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
