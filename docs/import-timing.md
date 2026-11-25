---
name: import-timing
description: How long each import step took at Max, Med and Min in every host, as measured by the MPB 3.0 release gates — Unreal 0.9.0 on Unreal 5.8, Revit 1.2.0 on Revit 2025, 2026 and 2027, AutoCAD 1.2.0 on AutoCAD 2025, 2026 and 2027 and Civil 3D 2027, and AutoCAD 1.3.0's import of the reviewer's sample step by step — with the site, the counts and the machine, and the rule the recommended level follows. Read before choosing or changing a recommended level, before quoting how long an import takes, or when a host's import gets slower.
---

# Import timing

How long an import took in each host at each level, step by step, as the MPB 3.0 release gates
measured it on the packaged plugins. Every figure here is a gate's own, copied from the gate's public
record (linked under each table) and dated by it. Nothing here is computed by a plugin.

**What lives where.** This page is the release gates' record. What each step imports, by default or
not, and what its cost grows with is the [import inventory](import-inventory.md)'s. The per-step
figures the Revit import window reads at run time are `SlowStepNotice.Measured(ImportStepKind)` in
[`SlowStepNotice.cs`](../shared/src/MantlePlace.Core/SlowStepNotice.cs); they are an earlier
measurement on other orders, and this page neither restates nor replaces them. Which host supports
which feature is the [host support matrix](host-support.md)'s.

## The recommendation rule

**The recommended level for an order is the highest level expected to import in about five minutes
on the machine below.** The platform sets the recommendation from figures like these; a plugin reads
it from the platform and never derives it.

On the site measured here, every host imports Max in well under five minutes, so Max is the
recommended level in every host:

| Host | Import at Max, seconds | Recommended |
| --- | ---: | --- |
| Unreal 5.8 | 8.18 | Max |
| Revit 2025 | 113.67 | Max |
| Revit 2026 | 179.02 | Max |
| Revit 2027 | 161.42 | Max |
| AutoCAD 2027 | 2.47 | Max |
| Civil 3D 2027 | 15.23 | Max |

The steps before the import (opening the vault window, choosing the order and the level, the
download) add under 25 seconds in every host. The rule is applied per order: a larger or denser site
costs more, and its recommendation can be a lower level. What drives each step's cost is the
[import inventory](import-inventory.md)'s.

## What was measured

- **The site.** One MPB 3.0.0 order built by the development platform: 1.44 km², with level downloads
  of 108.4 MB at Max, 76.4 MB at Med and 61.7 MB at Min, as the vault shows them. The Revit, AutoCAD
  and Civil 3D gates ran on it. The Unreal gate ran a day earlier on the development vault's 3.0.0
  order and did not record the order's size.
- **The counts.** Revit 2027 added 30,092 elements to the project at Max, 24,260 at Med and 21,344 at
  Min; Revit 2025 added 30,065 and Revit 2026 30,074, both at Max. Civil 3D built its TIN surface from
  75,328 points at every level. The Unreal and AutoCAD gates count assertions only, not what was
  placed.
- **The machine.** One Windows 11 Pro workstation: AMD Ryzen 9 5900X (12 cores, 24 threads), 128 GB
  of RAM, NVIDIA GeForce RTX 3080 Ti, NVMe SSD. Every gate on this page ran on it.
- **The runs.** One run per host, version and level, on the packaged release zip installed clean,
  signed in on the development platform and driven by the host harness, one host at a time. Each 3.0
  run downloaded its level fresh unless its table says *cached*. The download rows time a download
  from the development platform, not a curator's internet connection. One run is not a host's time:
  where a gate saw a repeat differ, its table says so.

## Unreal

Unreal 5.8, plugin 0.9.0 (the Win64 zip built from `95140cc`), 2026-11-17. Seconds.

The two panel phases are the import panel's own; the rest are the plugin's import timeline. A row of
greater depth is inside the next row above it of one depth less, which ends after it, and that row's
time includes it.

| Step | Depth | Max | Med | Min |
| --- | ---: | ---: | ---: | ---: |
| Downloading (panel phase) | | 11.49 | 2.86 | 2.14 |
| Importing (panel phase) | | 8.18 | 2.39 | 7.94 |
| zip open + manifest parse | 0 | 0.00 | 0.00 | 0.00 |
| integrity entry read | 1 | 0.07 | 0.04 | 0.02 |
| integrity sha256 digest | 1 | 0.10 | 0.04 | 0.03 |
| integrity pre-check | 0 | 0.17 | 0.08 | 0.05 |
| artifact | 0 | 0.35 | 0.31 | 0.28 |
| wipe (prior import) | 1 | 0.00 | 0.00 | 0.00 |
| weight resample | 1 | 0.04 | 0.01 | 0.03 |
| landscape heightmap extract | 2 | 0.00 | 0.00 | 0.00 |
| landscape heightmap decode | 2 | 0.02 | 0.01 | 0.01 |
| landscape heightmap orient | 2 | 0.00 | 0.00 | 0.00 |
| landscape actor spawn | 2 | 0.00 | 0.00 | 0.00 |
| landscape layer info assets | 2 | 0.01 | 0.01 | 0.01 |
| landscape engine import | 2 | 0.73 | 0.24 | 0.74 |
| landscape layer info map | 2 | 0.00 | 0.00 | 0.00 |
| landscape world partition split | 2 | 6.60 | 1.62 | 6.64 |
| artifact | 1 | 7.38 | 1.89 | 7.42 |
| inside transaction | 0 | 7.55 | 1.97 | 7.50 |
| Total import wall time | | 8.18 | 2.39 | 7.94 |

Source: the gate on [issue 616](https://github.com/mantleplace/mantleplace-dcc/issues/616), its last
table. The 0.9.1 gate ([pull request 724](https://github.com/mantleplace/mantleplace-dcc/pull/724))
imported every level again on the 0.9.1 zip and passed, and posted no step times. 0.9.1's one change
adds the public-record layers to a level download, so a 0.9.1 import also draws that linework, and
these 0.9.0 figures do not include it.

## Revit

Plugin 1.2.0 (the zip built from `156146c`), 2026-11-18. Med and Min were run on Revit 2027 only;
Revit 2025 and 2026 were gated at Max, the recommended level. Every layer the import window offers was
ticked. Seconds.

The first five rows are timed through the plugin's windows, from press to result; the rest are the
import log's own step times.

| Step | 2027 Max | 2027 Med | 2027 Min | 2025 Max | 2026 Max |
| --- | ---: | ---: | ---: | ---: | ---: |
| Open the vault window | 4.81 | 5.36 | 5.39 | 4.73 | 3.66 |
| Choose the order | 0.08 | 0.08 | 0.16 | 0.06 | 0.06 |
| Set the level | 0.50 | 0.23 | 0.22 | 0.05 | 0.08 |
| Download | 7.47 | 18.69 | 7.23 | 6.92 | 2.62 |
| Import, Import pressed to log closed | 161.42 | 142.97 | 136.88 | 113.67 | 179.02 |
| `ToposurfaceFromSurfaceTin` | 66.00 | 65.30 | 67.10 | 25.90 | 64.90 |
| `PublishedContours` | 1.10 | 1.10 | 1.20 | 0.90 | 1.50 |
| `ContextBuildings` | 16.70 | 16.80 | 17.20 | 23.80 | 23.10 |
| `LinkSiteIfc` | 3.90 | 3.80 | 3.80 | 3.20 | 3.90 |
| `SetSharedCoordinates` | 0.00 | 0.00 | 0.00 | 0.00 | 0.10 |
| `SetSiteLocation` | 0.30 | 0.30 | 0.20 | 0.20 | 0.20 |
| `FloodZones` | 0.30 | 0.30 | 0.30 | 0.20 | 0.30 |
| `SteepGround` | 20.30 | 20.50 | 20.50 | 16.20 | 21.70 |
| `SiteContextView` | 0.80 | 0.80 | 0.80 | 0.40 | 1.20 |
| `Vegetation` | 48.60 | 30.40 | 20.80 | 41.00 | 57.90 |
| `AttributionAndProvenance` | 0.10 | 0.10 | 0.10 | 0.10 | 0.10 |
| `ImageryDrape` | 2.50 | 2.40 | 2.30 | 1.20 | 2.30 |

**Not run in this order:** road centrelines, road surfaces, water, land use and land cover. The order
holds none of their files, so the import showed each step as skipped and no gate timed it.

**A repeat that differed:** in a first Revit 2026 run, `ContextBuildings` took 126.3 s, 114.3 s of it
converting the site model; the run in the table, on the same zip, order and level, took 23.1 s. The
gate did not look for the cause.

Source: Revit 2027 on [issue 617](https://github.com/mantleplace/mantleplace-dcc/issues/617); Revit
2025 and 2026 on [pull request 722](https://github.com/mantleplace/mantleplace-dcc/pull/722) and
[issue 618](https://github.com/mantleplace/mantleplace-dcc/issues/618).

### Revit 1.3.0: the reviewer's import, step by step

Plugin 1.3.0 (the zip built from `f5185af`), 2026-11-24. The import the storefront's reviewer is asked to
make: the Austin Downtown sample from mantle.place/samples, downloaded at Min (89.9 MB), imported from disk
with Import Bundle into a new project, with no account. The import ran unattended, which imports every
layer, Site Model and Road Surfaces included. One run per version. Seconds, from the import log.

| Step | Revit 2025 | Revit 2026 | Revit 2027 |
| --- | ---: | ---: | ---: |
| `ToposurfaceFromSurfaceTin` | 42.0 | 95.9 | 52.4 |
| `PublishedContours` | 4.5 | 6.1 | 3.4 |
| `ContextBuildings` | 16.0 | 129.8 | 9.5 |
| `LinkSiteIfc` | 4.0 | 4.3 | 2.9 |
| `SetSharedCoordinates` | 0.0 | 0.0 | 0.0 |
| `SetSiteLocation` | 0.3 | 0.3 | 0.2 |
| `LandUse` | 28.4 | 34.0 | 19.7 |
| `LandCover` | 1.8 | 2.3 | 1.5 |
| `FloodZones` | 93.1 | 113.5 | 73.9 |
| `SteepGround` | 66.0 | 78.1 | 45.6 |
| `SiteContextView` | 0.9 | 1.4 | 0.8 |
| `RoadCentrelines` | 4.8 | 5.9 | 3.6 |
| `Water` | 12.5 | 33.3 | 34.6 |
| `RoadPolygons` | 1,082.8 | 1,346.0 | 484.8 |
| `Vegetation` | 47.2 | 37.6 | 27.0 |
| `AttributionAndProvenance` | 0.4 | 0.2 | 0.2 |
| `ImageryDrape` | 23.5 | 47.6 | 36.1 |
| Import, all steps | 1,428.2 | 1,936.3 | 796.2 |

The *Import, all steps* row is the sum of the steps, and equals the import log's first to last stamp. The
import added about 188,000 elements in each version: 7,302 trees, 1,812 road centrelines, 1,022 road features
cut as 66 subdivisions (Revit declined one), 55 water features cut as 3, and 54 flood zones on the hazard
plan. `RoadPolygons`, the road surfaces, is 76 % of the import in Revit 2025, 70 % in Revit 2026 and 61 % in
Revit 2027.

**What the window told the reviewer:** in the import window, opened on the same zip, the note under Road
Subdivisions read "13 subdivisions, about 8 to 22 seconds" in Revit 2025 and about 24 to 32 seconds in Revit 2026
and 2027, so the box was not unticked as over budget. The step cut 66 subdivisions and took 485 to 1,346 s.
The count was the catalog's 13 road-surface features, priced at the time per subdivision. The note now prices each
road-surface feature at this gate's own time per feature, so the same zip reads "13 road surfaces, about 18
minutes" in Revit 2025, about 22 minutes in Revit 2026 and about 8 minutes in Revit 2027, and the box starts
unticked. The Planting and Context Buildings notes scale from this gate's runs and the earlier Max-level runs
of the same order; no other box has an estimate.

**A repeat that differed, again:** `ContextBuildings` took 129.8 s in Revit 2026, against 16.0 s and 9.5 s in
the other two, as the 1.2.0 gate's first Revit 2026 run did; the gate did not look for the cause.

Source: [issue 768](https://github.com/mantleplace/mantleplace-dcc/issues/768).

## AutoCAD and Civil 3D

Plugin 1.2.0 (the zip built from `86cc2f0`), 2026-11-20. AutoCAD 2027 is Civil 3D 2027 started as
plain AutoCAD, so it builds no surface. Seconds.

**The import steps are given as one total.** Each step of an AutoCAD import ends faster than the host
harness reads the import window, every 0.5 to 2 s, so no step could be timed on its own. The steps
are Verify Files, Aerial Image, Contours, Public-Record Layers and Site Linework, and in Civil 3D the
Civil 3D Surface as well; their time at each level is the *Import, all steps* row. In Civil 3D the
surface is most of the difference between the two hosts.

### AutoCAD 2027

| Step | Max | Med | Min |
| --- | ---: | ---: | ---: |
| Open the vault window | 4.97 | 5.39 | 4.98 |
| Choose the order | 0.06 | 0.08 | 0.08 |
| Set the level | 0.16 | 0.23 | 0.19 |
| Download | cached; 11.07 in a rehearsal of the same order and level | 3.20 | 2.95 |
| Import, all steps | 2.47 | 1.41 | 1.55 |

Source: [issue 619](https://github.com/mantleplace/mantleplace-dcc/issues/619) and
[pull request 734](https://github.com/mantleplace/mantleplace-dcc/pull/734).

### Civil 3D 2027

| Step | Max | Med | Min |
| --- | ---: | ---: | ---: |
| Open the vault window | 5.09 | 2.62 | 4.80 |
| Choose the order | 0.09 | 0.05 | 0.06 |
| Set the level | 0.20 | 0.16 | 0.14 |
| Download | 2.92 | cached; 2.72 in the first attempt | 11.19 |
| Import, all steps, surface included | 15.23 | 10.41 | 9.62 |

Source: [issue 620](https://github.com/mantleplace/mantleplace-dcc/issues/620) and
[pull request 735](https://github.com/mantleplace/mantleplace-dcc/pull/735).

### AutoCAD 2025 and AutoCAD 2026

Gated at the recommended level alone, on the same order. That order's vault showed 69.2 MB at Max,
55.3 MB at Med and 43.5 MB at Min on 2026-11-20, so these rows are not comparable with the 2027 rows
above, which were measured when it showed 108.4 MB at Max.

| Step | AutoCAD 2025, Max | AutoCAD 2026, Max |
| --- | ---: | ---: |
| Open the vault window | 5.61 | 5.58 |
| Choose the order | 0.06 | 0.05 |
| Set the level | 0.06 | 0.03 |
| Download | 12.26 | cached from the AutoCAD 2025 run |
| Import, all steps | 0.67 | 2.27, opening the cached copy included |

Source: [issue 621](https://github.com/mantleplace/mantleplace-dcc/issues/621).

### AutoCAD 1.3.0: the reviewer's import, step by step

Plugin 1.3.0 (the zip built from `de2e981`), 2026-11-24. The import the storefront's reviewer is asked
to make: the Austin Downtown sample from mantle.place/samples, downloaded at Min (89.9 MB), imported
from disk with Import Bundle into a new drawing, with no account. From 1.3.0 the import window's
closing report lists each step's time, so each step is timed on its own; the times below are copied
from that report. Seconds.

| Step | AutoCAD 2025 | AutoCAD 2026 | AutoCAD 2027 |
| --- | ---: | ---: | ---: |
| Verify Files | 0.19 | 0.19 | 0.23 |
| Aerial Image | 0.03 | 0.31 | 0.38 |
| Contours | 0.23 | 0.22 | 0.26 |
| Public-Record Layers | 0.04 | 0.05 | 0.05 |
| Site Linework | 0.18 | 0.18 | 0.20 |
| Import, all steps | 0.67 | 0.95 | 1.12 |

The import placed 851 contour polylines, the aerial at 1424 by 1417 metres, 470 public-record
polylines on 6 layers and 3458 site linework polylines on 7 layers, in each year. The *Import, all
steps* row is the sum of the steps. AutoCAD 2027 is Civil 3D 2027 started as plain AutoCAD, so it
builds no surface.

Source: [issue 769](https://github.com/mantleplace/mantleplace-dcc/issues/769).
