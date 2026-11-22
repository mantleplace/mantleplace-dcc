---
name: adr-0022-shared-dotnet-core-library-decisions
description: Decisions recorded from the code comments of the shared .NET core library (cache, download, export, drape, datums, import, materialize and manifest handling). Read before changing how the core validates a cache, reads a manifest, polls a job, layers a drape or decides what to import.
status: accepted
---

# 22. The shared .NET core library: decisions recorded from code

Date: 2026-11-21

## Status

Accepted. Recorded from the code comments that held these decisions, when the comments were removed; each decision was already in force in the code and none is new.

## Context

These decisions lived in comments in the shared .NET core library (`shared/src/MantlePlace.Core`), the host-neutral home described in [ADR 0019](0019-plugins-share-a-common-core-a-dotnet-home-and-corpus-cases.md). Comments are being removed from the code, so the decisions and their reasons are recorded here.

## Decisions

### Cache validity

1. **A null expected hash means unknown, not absent: the integrity check is skipped and the entry is valid but unverified.** Treating it as a mismatch would make unknown and corrupt look identical.
   Source: shared/src/MantlePlace.Core/CacheValidity.cs
2. **A cached host download view is checked against the size and digest recorded when it arrived.** The vault listing's size and digest describe the whole archive and would flag every view as wrong-sized and re-download it.
   Source: shared/src/MantlePlace.Core/CacheValidity.cs
3. **The cache sidecar records host and level.** Revit and AutoCAD plugins share one cache folder and a host's download view holds only that host's files, so a zip fetched for one is never a hit for the other.
   Source: shared/src/MantlePlace.Core/CacheValidity.cs
4. **The cache sidecar reads manifestVersion as either a string or a legacy JSON number, and stringifies the number.** Old pre-re-baseline entries then sort below the floor and are invalidated and re-downloaded once, instead of looking importable.
   Source: shared/src/MantlePlace.Core/CacheValidity.cs

### Host view download

5. **The host download view is used only when it holds every delivered file the host reads; otherwise the whole bundle is fetched.** The view holds only the hosts.revit block's files, but Revit also reads the tree points outside that block, and a view without them would import with trees missing.
   Source: shared/src/MantlePlace.Core/CompleteOrders.cs
6. **Every host-block entry is requested at level MAX.** The platform takes one level per entry and cut levels are subsets of the MAX file, so MAX holds all of them; a level with its own file would break this.
   Source: shared/src/MantlePlace.Core/CompleteOrders.cs
7. **With the delivery state unknown, only a view block proves an order complete.** A whole-archive answer after a 503 delivery_state_unknown may be an on-demand order's unbuilt bundle that Prepare must build.
   Source: shared/src/MantlePlace.Core/CompleteOrders.cs

### Connected host presence

8. **The presence payload's manifest range is never written separately.** reads.min and reads.maxMajor come from the same constants that gate sent bundles, so the web greys out exactly what the host would refuse.
   Source: shared/src/MantlePlace.Core/ConnectedHost.cs
9. **The connection id is random, made once per plugin launch and never persisted.** An export addressed to a closed host then cannot find a new one. It is the presence key and is treated as visible, never secret.
   Source: shared/src/MantlePlace.Core/ConnectedHost.cs

### Identity matching

10. **Contour stamp matching is ordinal over the whole prefix including the separator.** Cache-key stems are truncated hashes, and one stem being a prefix of another would cause a false collision.
    Source: shared/src/MantlePlace.Core/ContourIdentity.cs
11. **Every .NET host plugin loads the same assembly, so the host id is set once by each shell's module initializer, and reading it earlier throws rather than guessing.** Signing in as the wrong host would register a second copy of that host.
    Source: shared/src/MantlePlace.Core/HostIdentity.cs

### Curator link

12. **Site-location size choices run from 500 to 4000 m.** The web curator's area floor is about 316 m a side and its ceiling 8 km, so every offered square must sit inside both limits.
    Source: shared/src/MantlePlace.Core/CuratorExtent.cs
13. **Extent bounds are scaled to four decimals.** The curator keeps four decimals (about 11 m), so a link carrying more precision than the curator stores would be pointless and unstable.
    Source: shared/src/MantlePlace.Core/CuratorExtent.cs

### Delivery header

14. **The delivery CRS prints verbatim from the bundle's label, or its EPSG code for older bundles.** Deriving words from tier and EPSG would need a CRS table here, a second authority on something the format owns.
    Source: shared/src/MantlePlace.Core/DeliveryHeader.cs

### Download levels

15. **In 3.0 the level belongs to the download and is chosen before fetching; the request names this host's promised files by path and one level.** The platform has no per-host file list and never returns a host view for 3.0.
    Source: shared/src/MantlePlace.Core/DownloadLevels.cs
16. **Names in a file_not_in_bundle refusal are compared exactly, case included.** Catalog file names are case-sensitive, and a case-only difference is a different file whose refusal must be reported as it stands.
    Source: shared/src/MantlePlace.Core/DownloadLevels.cs

### Drape

17. **The plugin wants smooth shading and gives each element its own anchored material.** Revit measures real-world texture offsets from the project origin under flat shading but from the element's bounding-box minimum corner under smooth shading (measured on a real site). See [ADR 0008](0008-revit-drape-is-anchored-to-the-smooth-shading-origin.md).
    Source: shared/src/MantlePlace.Core/DrapeAnchor.cs
18. **The imagery layer takes exactly the host's minimum layer thickness and the lower layer keeps the remainder.** Total thickness is preserved and the terrain cannot drift off its survey elevation however Revit extends the structure.
    Source: shared/src/MantlePlace.Core/DrapeLayering.cs
19. **The drape material and toposolid type name has one owner, derived from the bundle stem.** Terrain and drape steps must resolve the same type; two spellings would make the drape duplicate a type and retype the terrain.
    Source: shared/src/MantlePlace.Core/DrapeLayering.cs
20. **A layer split is refused when the remainder would be thinner than twice the minimum thickness.** A lower layer squeezed to the order of the sliver is a degenerate structure, and the caller's rollback is the honest answer.
    Source: shared/src/MantlePlace.Core/DrapeLayering.cs
21. **Whether to add the imagery layer is decided by asking whether layer 0 already wears the plugin's drape material, not by which import ran.** Single-layer types left by older builds or edits are then repaired rather than reused with imagery on vertical faces.
    Source: shared/src/MantlePlace.Core/DrapeLayering.cs
22. **The imagery-on-top-only check is reported and never gated.** SetCompoundStructure's behaviour has not been observed inside Revit, and a new refusal path from an unobserved read would decline a working drape undiagnosably.
    Source: shared/src/MantlePlace.Core/DrapeLayering.cs

### Export desk, lifecycle and readiness

23. **The export desk acknowledges every export as received on sight, works them one at a time in arrival order with one report in flight and coalesced progress, and treats routes' proof of completeness as ready.** Protocol 1 cannot read order state on connect.
    Source: shared/src/MantlePlace.Core/ExportDesk.cs
24. **The first importing transition sends no progress.** The platform starts the import at progress 0 and ignores a progress value sent with the first call.
    Source: shared/src/MantlePlace.Core/ExportLifecycle.cs
25. **Export transitions are validated locally before sending, and the web-only reasons not_received and host_disconnected are refused here.** The server does not enforce that rule, so this is the only enforcer, and it keeps plugin bugs from looking like web races.
    Source: shared/src/MantlePlace.Core/ExportLifecycle.cs
26. **Import progress reports are paced to about one every two seconds, starting at 0.** The export route is rate limited per source address, and per-step reporting would spend the allowance on one import.
    Source: shared/src/MantlePlace.Core/ExportLifecycle.cs
27. **A bundle is ready only per the order row (fulfilled, or paid with processing phase ready), never by a download route's answer.** A host view of an unfinished bundle returns 200 as a partial and would import half a bundle.
    Source: shared/src/MantlePlace.Core/ExportReadiness.cs
28. **Export refusals are handled by HTTP status class (retry on 5xx and 429, not on other 4xx), with the code only naming the refusal.** A code the platform adds later is then handled correctly the day it appears.
    Source: shared/src/MantlePlace.Core/ExportRefusals.cs
29. **Exports are read from both the route's camelCase nested form and the Realtime row change's flat snake_case form by one reader; unknown fields are ignored, an unknown status is not.** A new status needs a new protocol number.
    Source: shared/src/MantlePlace.Core/HostExport.cs

### Validation patterns

30. **The reason-code pattern and the feedback version pattern end with \z rather than $.** .NET's $ also matches before a trailing newline, which the contract's JavaScript pattern does not, and a version such as 1.2 followed by a newline is not valid.
    Source: shared/src/MantlePlace.Core/ExportLifecycle.cs, shared/src/MantlePlace.Core/FeedbackLink.cs

### Feedback link

31. **The feedback URL carries only five parameters, and any version, order id or rating failing its pattern is left out rather than sent raw.** No address, name, free text or token can then ride along in the query.
    Source: shared/src/MantlePlace.Core/FeedbackLink.cs

### Fidelity levels

32. **Subdivision stamps are named over the whole layer before the level cut is applied.** A feature's stamp is then identical at every level, so a later higher-level import recognises earlier cuts and cuts only the rest; cuts are never deleted.
    Source: shared/src/MantlePlace.Core/FidelityLevels.cs
33. **Unknown values in a levels block fail closed.** An unknown same_as target makes the whole entry MAX only; unknown cut forms, chains or malformed levels are offered as unavailable, never as the whole file; unknown cost units lose cost estimates.
    Source: shared/src/MantlePlace.Core/FidelityLevelsReader.cs

### Foliage and geometry reading

34. **Foliage type is read only from the platform's closed vocabulary and never inferred from height or crown radius; unknown values read as Tree.** Shrubs can be taller than young trees, and vocabulary growth must not discard the column.
    Source: shared/src/MantlePlace.Core/FoliageType.cs
35. **The GeoPackage decoder accepts ISO WKB and EWKB flags, and a blob it cannot follow yields no part.** GDAL and some writers differ, and one malformed feature must not cost the curator the rest of the layer.
    Source: shared/src/MantlePlace.Core/GeoPackageGeometry.cs

### Geo projection

36. **Forward WGS84 lon/lat to UTM projection is the single permitted exception to applying placement verbatim; it stays narrow (UTM only, no datum shift) and refuses unsupported EPSG codes rather than approximating.** GeoJSON layers are RFC 7946 lon/lat.
    Source: shared/src/MantlePlace.Core/GeoProjection.cs
37. **EPSG codes are parsed from both the 'EPSG:NNNN' string and the bare number, and unparseable input is unknown so it never compares equal to a known CRS.** The manifest spells a CRS both ways.
    Source: shared/src/MantlePlace.Core/GeoProjection.cs

### Ground cuts and materials

38. **Only water bodies and road surfaces are cut as subdivisions, each polygon whole with its holes left out.** Toposolid.CreateSubDivision was measured in Revit 2025 to 2027 to accept holes, whereas cutting inner rings separately would pave blocks and flood islands. See [ADR 0015](0015-revit-subdivisions-are-for-built-surfaces.md).
    Source: shared/src/MantlePlace.Core/GroundCuts.cs
39. **Under smooth shading every subdivision gets its own drape material, because each anchors to its own corner; flat shading shares one material per keyword.** Names derive from stamp and layer so re-imports resolve existing materials.
    Source: shared/src/MantlePlace.Core/GroundMaterialNames.cs
40. **Land plan region stamps include the word Plan and never use the pre-ADR-0015 land cover subdivision stamp.** The drape still recognises that older stamp and would mistake plan regions for subdivisions.
    Source: shared/src/MantlePlace.Core/LandPlan.cs

### Hazard plan

41. **A hazard plan is never redrawn: layers already on it are left alone, and a later build gets its own plan, though a re-import may add missing layers.** A curator may have annotated the plan or put it on a sheet. See [ADR 0014](0014-revit-hazards-are-drawn-on-a-hazard-plan.md).
    Source: shared/src/MantlePlace.Core/HazardPlan.cs
42. **The plan crop is widened east to hold the zone key's swatches.** A crop region hides detail elements outside it, and a crop of the drape alone would hide the key explaining the plan.
    Source: shared/src/MantlePlace.Core/HazardPlan.cs

### Height datums

43. **The ground records its vertical datum and each height-placing step compares against it, skipping on mismatch with no conversion.** US bundles from MPB 1.8.0 use NAVD88 where earlier ones used EGM2008, and a silent mismatch lands content up to a metre off.
    Source: shared/src/MantlePlace.Core/HeightDatums.cs
44. **A block's vertical_datum is read as a statement only from MPB 1.8.0; older grounds record it as unstated, which compares as EGM2008.** Earlier versions had an undocumented free string there.
    Source: shared/src/MantlePlace.Core/HeightDatums.cs
45. **A 3.x frame's vertical CRS and geoid is renamed to the 2.x datum words for known pairs, never converted.** Re-imports across majors then compare as one datum; NAVD88 on other geoids or WKT2-only CRS fails closed as unknown.
    Source: shared/src/MantlePlace.Core/HeightDatums.cs

### Import chunking

46. **Trees and context buildings are chunked into transactions, but subdivisions and terrain are not.** Chunking lets Revit repaint and honours Cancel, but Revit 2025 probes showed it did not lower the ground-relation rebuild cost at commit.
    Source: shared/src/MantlePlace.Core/ImportChunking.cs
47. **ElementsPerTransaction is 200, chosen for Cancel responsiveness, not speed.** Revit charges per tree at commit regardless of chunk size and batch creation showed no gain; larger chunks only lengthen waits.
    Source: shared/src/MantlePlace.Core/ImportChunking.cs

### Import failure policy

48. **Revit failure severity decides the action first and the failure id only chooses wording; nothing is absorbed silently.** The id is a composite (slab shape edit wraps too-thin), so id-keyed rules would miss the outer error.
    Source: shared/src/MantlePlace.Core/ImportFailurePolicy.cs
49. **Errors roll back by default; only allowlisted errors resolve or delete named elements, and unknown warnings are swallowed and reported.** Revit's own resolution for toposolid failures deletes the toposolid, and unattended runs must never block on a modal.
    Source: shared/src/MantlePlace.Core/ImportFailurePolicy.cs

### Import layers

50. **Attribution and provenance is not a choosable import layer.** Crediting the data is the licence's condition for importing any of it; shared coordinates builds nothing, and the terrain is one layer across three step kinds.
    Source: shared/src/MantlePlace.Core/ImportLayers.cs
51. **Every layer starts ticked except the site model link, and the unattended path imports everything at MAX.** The link would show each building twice beside the copied context buildings; an import nobody chooses for brings in everything.
    Source: shared/src/MantlePlace.Core/ImportLayers.cs
52. **A prerequisite layer the bundle does not carry is not reported missing; the dependent step falls back to the project's own ground.** Disabling the dependent would leave a row nobody could tick.
    Source: shared/src/MantlePlace.Core/ImportLayers.cs

### Extraction lifetime

53. **Files Revit links by path (CAD or IFC links) are retained in a stable per-order directory and never auto-cleaned.** Deleting them leaves the user's links permanently unresolved with no warning at link time.
    Source: shared/src/MantlePlace.Core/ImportStepKinds.cs
54. **Extraction lifetime is derived from the step kind, not stored on the step, and unclassified kinds default to Retained.** A wrongly Transient file breaks links on a customer's machine later, while Retained only leaks visible disk.
    Source: shared/src/MantlePlace.Core/ImportStepKinds.cs
55. **The imagery drape is stated Retained although it builds no geometry.** A Revit appearance asset stores the bitmap's path and re-reads it, so a Transient file would come back unresolved when the project reopens.
    Source: shared/src/MantlePlace.Core/ImportStepKinds.cs

### Import window

56. **Stacking rules float the import window only while Revit is in front and never over a Revit modal.** The window is not owned by Revit's window and is centred where Revit centres dialogs, so it would hide the dialog Revit is blocked on.
    Source: shared/src/MantlePlace.Core/ImportWindowStacking.cs
57. **A minimised window's restored position is computed from workspace coordinates without adding the work area's corner.** On monitors above the first, the restored rectangle already includes the offset, and adding it again lands off-screen.
    Source: shared/src/MantlePlace.Core/ImportWindowStacking.cs
58. **Closing the import window while a run goes is treated as a cancel, and the window stays until the run stops at its next boundary.** The run lives on the host's thread with no other place to see or stop it.
    Source: shared/src/MantlePlace.Core/ImportWindowState.cs
59. **The window phase becomes Begun before the host is called, not after.** A second Import click queued behind a slow re-plan would otherwise begin the same import twice.
    Source: shared/src/MantlePlace.Core/ImportWindowState.cs
60. **The curator's level choice is stored in per-order state instead of the control.** The vault window re-seats rows on refresh and rebuilds the control each selection, which would otherwise lose the choice and revert to the recommendation.
    Source: shared/src/MantlePlace.Core/LevelChoices.cs

### Interrupted prepares

61. **Prepare entries are written at start and removed at end, never at shutdown; a live owner keeps its entry.** A crash never reaches shutdown, and Revit 2025 and 2027 run side by side sharing one record and would poll twice.
    Source: shared/src/MantlePlace.Core/InterruptedPrepares.cs

### JSON reading

62. **Manifest accessors never throw, and optional numbers and booleans read as null rather than zero or false.** A refusal message is actionable where a stack trace is not, and a null on the wire must not become a real-looking 0.
    Source: shared/src/MantlePlace.Core/JsonReading.cs
63. **Numeric fields are parsed from strings with the invariant culture.** The ETL occasionally serialises numbers as strings, and silently reading zero would corrupt values.
    Source: shared/src/MantlePlace.Core/JsonReading.cs
64. **The manifest version is read by truncation, clamped to int.** The Unreal reference truncates, and both hosts must agree on which side of the floor a non-integral value falls.
    Source: shared/src/MantlePlace.Core/JsonReading.cs

### Linear units

65. **US survey foot and international foot are both written out as exact constants and never conflated or rounded.** They differ by about 2 ppm, harmless for placement but wrong in a survey deliverable.
    Source: shared/src/MantlePlace.Core/LinearUnits.cs

### Unattended bundle source

66. **The bundle path can come from an environment variable, and the rule lives in Core.** The file dialog is created by the add-in and invisible to Revit's journal, so journal playback could not drive imports; the host shim is never built in CI.
    Source: shared/src/MantlePlace.Core/LocalBundleSource.cs
67. **Unattended runs write their log beside the input zip and never raise a TaskDialog.** Nothing is there to dismiss it, and journal playback blocks on it forever.
    Source: shared/src/MantlePlace.Core/LocalBundleSource.cs

### Manifest versions

68. **The manifest version floor lives in one constant that the version gate reads by regexing the file via the revit.floorSource entry.** Renaming the constant or moving the file requires editing that entry in the same commit.
    Source: shared/src/MantlePlace.Core/ManifestVersions.cs
69. **NewestSupportedMajor and AutoCadNewestImportedMajor keep their names and stay integer literals.** Tooling outside this repository reads those lines by name. NewestSupportedMajor is the 2.x reader's ceiling, separate from the HPS-31 floor.
    Source: shared/src/MantlePlace.Core/ManifestVersions.cs
70. **Version components are digits only with no leading zeros and parsed invariantly.** int.TryParse would accept signs, whitespace and group separators, and a component this host accepts but the platform gate rejects would admit non-contract versions.
    Source: shared/src/MantlePlace.Core/ManifestVersions.cs
71. **Any non-semver version, such as integer-era values or malformed text, sorts below any semver floor.** Callers need not know which era a stored version came from, which matters for cache sidecars written before the re-baseline.
    Source: shared/src/MantlePlace.Core/ManifestVersions.cs

### Project constraints

72. **Core references nothing, no host API or NuGet, and targets net8.0 rather than net10.0.** Conformance then runs on a hosted runner without a host installed; nothing needs .NET 10, and net8.0 loads in net10.0 hosts.
    Source: shared/src/MantlePlace.Core/MantlePlace.Core.csproj

### Materialize

73. **Materialize polls no faster than every three seconds, the reference floor.** Going below it invites platform rate limiting.
    Source: shared/src/MantlePlace.Core/MaterializeJob.cs
74. **Poll failures are counted consecutively, not cumulatively.** A job surviving scattered transient errors over ten minutes is healthy, and abandoning it would discard a completed ETL run the curator paid for.
    Source: shared/src/MantlePlace.Core/MaterializeJob.cs
75. **The host sends an explicit token list, never the server-side host keyword or all; the list grows only when a step reading that layer lands.** The platform's own expansion silently dropped layers, and the cost is the drape's size.
    Source: shared/src/MantlePlace.Core/MaterializeJob.cs
76. **Materialize-start outcomes are recognised by body shape, each keyed on its own marker, never on a missing jobId; a join with a null job id is still a join.** Polling is keyed on the order.
    Source: shared/src/MantlePlace.Core/MaterializeJob.cs
77. **The permanently absent reasons mirror the platform's non-retryable set exactly.** Treating one as outstanding makes a complete bundle poll its whole budget and time out.
    Source: shared/src/MantlePlace.Core/MaterializeJob.cs
78. **Polling derives completion from the delivery-state document rather than job status words.** A materialize run reports completed even when every token's emit failed; delivery is proof of production, and a failed status is still a valid body.
    Source: shared/src/MantlePlace.Core/MaterializeJob.cs
79. **The delivery-state document is detected by the delivered array, not any optional field.** activeJob is legitimately null on an idle order, and keying on it would misread an idle bundle as a foreign shape.
    Source: shared/src/MantlePlace.Core/MaterializeJob.cs
80. **Delivery derivation checks unknown delivery state first, lets nothing-outstanding beat a running job, treats terminal attempts leaving tokens outstanding as failures, and never reads activeJob.steps.** A curator is never handed an unconfirmed bundle.
    Source: shared/src/MantlePlace.Core/MaterializeJob.cs
81. **An empty requested token set yields Unknown, not Complete.** Nothing outstanding is vacuously true, and Unknown is non-terminal so polling continues rather than downloading on a tautology.
    Source: shared/src/MantlePlace.Core/MaterializeJob.cs
82. **An absent or unparseable presigned URL expiry reads as not expired.** The URL is minted per import and seconds old, and refusing it over a timestamp format change would break downloads.
    Source: shared/src/MantlePlace.Core/MaterializeJob.cs
83. **The presign request names the format bundle, not the glb alias.** glb returns a single mesh when the order has a glb artifact, which fails the archive sha256 check on a download that succeeded.
    Source: shared/src/MantlePlace.Core/MaterializeJob.cs

### Other reading and presentation rules

84. **ODbL content is identified by the manifest role, failing closed to database when missing or unknown, and filed in a Revit selection filter.** Marking element Comments stamps would break re-import matching by exact stamp.
    Source: shared/src/MantlePlace.Core/OdblContent.cs
85. **The Logs button selects the most recent log in the bundle cache rather than a fixed folder or remembered path.** Logs are written beside whichever zip was imported, so a fixed folder is wrong for half of runs.
    Source: shared/src/MantlePlace.Core/OpenLogsTarget.cs
86. **A non-2xx response is always a refusal even when its body explains nothing, and shows both message and detail.** Passing a 502 proxy page through as success produced misleading no-job-to-poll errors.
    Source: shared/src/MantlePlace.Core/PlatformError.cs
87. **The imagery drape's PNG size is read from the first 24 header bytes only, never by decoding the roughly 50 MB image.** The planner runs before extraction, and decoding would turn a free refusal into a minute-long one.
    Source: shared/src/MantlePlace.Core/PngHeader.cs
