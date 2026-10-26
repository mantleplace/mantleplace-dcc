# Roadmap

Where these plugins are going, a quarter at a time. The near-term arc is the **Unreal plugin from
pre-1.0 to 1.0**, and the headline item on the way there is **large-AOI World Partition import**:
real-world sites brought in as streaming World Partition worlds, lifting the ceiling on how much
ground one order can practically bring in. What has shipped is on the
[Releases page](https://github.com/mantleplace/mantleplace-dcc/releases), not here.

This page says **when**. What each host does today, and why a host leaves something out, is the
[host support matrix](docs/host-support.md); an item leaves this page once it ships there.

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

- **Large-AOI import beyond the first World Partition release** — the first World Partition release
  has shipped (see Releases); what remains is working toward the platform's largest orderable AOIs.
  **Mesh Terrain** (Experimental in UE 5.8) is parked until UE 5.9: its spike is done and the
  `ALandscape` stays the only terrain target ([ADR 0016](docs/adr/0016-unreal-mesh-terrain-is-parked-until-5-9.md)).
- **Unreal packaging** — Unreal's release packaging is still assembled by hand; automating it so a
  release is repeatable rather than an event is what remains of the packaged-releases item. The scheme
  is settled: one release track per host, tagged `<host>-<version>`
  ([ADR 0001](docs/adr/0001-per-host-release-tracks.md)). It cannot run in public CI, because it needs a
  licensed install this repository must not put on a runner.
- **Unreal quickstart tutorial** — the missing walk-through from empty project to imported site,
  plus the README's screenshot and GIF slots filled with real captures.

## Q1 2027

- **Large-AOI World Partition import, completed** — large-AOI orders import end-to-end as streaming
  World Partition worlds. Single-Landscape import remains the default for small sites, and Mesh Terrain
  stays parked until UE 5.9 ([ADR 0016](docs/adr/0016-unreal-mesh-terrain-is-parked-until-5-9.md)).
- **Import-surface completion** — a per-layer import picker so a bundle can be brought in
  selectively, and a shipped landscape material that renders the painted weight layers and samples the
  coverage rasters.
- **The spec earns its second implementer** — [`spec/`](spec/) shipped early, so what this quarter
  owes it is use rather than prose: a worked third-party read of a real bundle against the spec and
  the [conformance corpus](tools/manifest-conformance/) alone, and whatever that exercise proves is
  missing.

## Q2 2027

- **Unreal 1.0** — the Unreal plugin reaches 1.0. Each Unreal release is submitted to Fab the same
  day it is published, from the same asset ([releases.md](docs/releases.md#the-cut)); in-repo installs
  keep working exactly as they do now.
- **Documentation completion** — the full import reference for both hosts, written against the
  shipped feature set rather than ahead of it.
- **Release-gating infrastructure** — the private engine-compile check wired to block a release
  (not a merge) automatically, narrowing the accepted public-CI gap the
  [README](README.md#ci-and-what-it-does-not-cover) documents.

## What is deliberately not on this roadmap

- **Other hosts** (Rhino, Blender, 3ds Max) — top-level folders appear when they have real content,
  never as placeholders, and none is scheduled.
- **Anything that thickens the client.** Coordinate machinery, source selection, mosaic assembly and
  their relatives stay on the platform side by rule, so no quarter will ever contain them — see
  [What these plugins do](README.md#what-these-plugins-do).
