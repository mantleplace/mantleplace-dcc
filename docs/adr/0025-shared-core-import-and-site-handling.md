---
name: adr-0025-shared-core-import-and-site-handling
description: Decisions recorded from the comments in the shared .NET core, its conformance tests and the platform API sync tool: import failure policy, bundle identity, materialize polling, site and terrain geometry, Revit safety rules and test-harness choices. Read before changing any of these areas.
status: accepted
---

# 25. Shared core, import and site handling: decisions recorded from code

Date: 2026-11-21

## Status

Accepted. Recorded from the code comments that held these decisions, when the comments were removed; each decision was already in force in the code and none is new.

## Context

These decisions lived in comments in the shared .NET code area: the shared project settings, the core conformance tests under `shared/tests/MantlePlace.Core.Tests` and the platform API sync tool under `shared/tools/platform-api`. Comments are being removed from the code, so the decisions and their reasons are recorded here.

## Decisions

### Shared .NET projects

**Shared .NET projects take no NuGet packages, reference no host API or host name, and carry no version.** The pure core then restores offline, and each host's plugin owns its own version.

Source: shared/Directory.Build.props

### Revit safety rules

**A signed-in address containing a percent sign is hidden from the ribbon.** Revit's `RibbonItem.ItemText` throws `ArgumentException` on it, and a throw during state update reaches Revit's dispatcher and kills the process.

Source: shared/tests/MantlePlace.Core.Tests/AccountRibbonTests.cs

**`BrandPalette.MixToWhite` clamps out-of-range fractions to the nearest end instead of throwing.** It runs on a Revit dispatcher, where an exception takes Revit down.

Source: shared/tests/MantlePlace.Core.Tests/WindowLabelsTests.cs

**Every plugin window sets the Mantle Place title-bar mark, and a `TaskDialog` default command link is set after `AddCommandLink`.** Revit refuses a default button that has not been added yet.

Source: shared/tests/MantlePlace.Core.Tests/ShimWindowTests.cs

**Tooltip vignette render size is checked against the committed files.** Revit silently clips or drops an over-large tooltip image on a hover-delayed surface, so only a build-time check can detect it.

Source: shared/tests/MantlePlace.Core.Tests/RibbonImageryTests.cs

### Import windows and checklist

**A disabled Revit main window is not treated as an absent host.** The main window reads as disabled whenever any modal is up, such as the import-already-open dialog, a fault dialog or a save prompt.

Source: shared/tests/MantlePlace.Core.Tests/ImportWindowStackingTests.cs

**A window that should not float is put behind the foreground window before it appears.** A new window starts on top of ordinary windows, so otherwise it flashes over another application.

Source: shared/tests/MantlePlace.Core.Tests/ImportWindowStackingTests.cs

**The stacking math treats a minimised window's restored place as screen coordinates less its monitor's work-area inset.** Windows stores it that way, so only a taskbar on the left or top shifts it.

Source: shared/tests/MantlePlace.Core.Tests/ImportWindowStackingTests.cs

**The import checklist tolerates spurious Checked and Unchecked events.** The window's own repaint of a disabled box raises them, and a slip there must not cost the curator their terrain choice.

Source: shared/tests/MantlePlace.Core.Tests/ImportLayerTests.cs

### Import transactions and failure policy

**The elements-per-transaction size balances Cancel responsiveness against overhead.** A commit is what Cancel waits on, and tiny chunks would create tens of thousands of transactions, raises and log lines.

Source: shared/tests/MantlePlace.Core.Tests/ImportChunkingTests.cs

**The failure policy leads with severity, and the failure id can only soften it.** Composite Revit failures such as `SlabShapeEditFailedError` embed an inner failure's text, so a policy keyed on the inner id never fires.

Source: shared/tests/MantlePlace.Core.Tests/ImportFailurePolicyTests.cs

**Only allowlisted failures, such as "Can't keep elements joined", are auto-resolved, not any error that has a resolution.** Revit's resolution for toposolid failures deletes the toposolid and would yield a silently empty import.

Source: shared/tests/MantlePlace.Core.Tests/ImportFailurePolicyTests.cs

**A subdivision refusal that arrives at commit and names one of the import's own new elements is resolved by deleting that element.** Deleting it loses one feature, while rolling back would lose the whole layer.

Source: shared/tests/MantlePlace.Core.Tests/ImportFailurePolicyTests.cs

**The terrain, a curator's element or an earlier import's subdivision is never deleted to make a commit succeed, and nothing is deleted when the error names no element.**

Source: shared/tests/MantlePlace.Core.Tests/ImportFailurePolicyTests.cs

### Bundle identity, lifetime and verification

**A bundle is filed, and its placed elements are stamped, under `attribution.order_id`, even when that field holds a packaging job id (bundles before MPB 1.7.0).** Filing under another key would make a re-import a stranger to its own elements and place them twice. Re-import identity is the stem stamped on placed elements, which is the filing key, so a 3.0 bundle filed by zip path would not be recognised when the same order arrives from the vault or under another file name.

Source: shared/tests/MantlePlace.Core.Tests/LocalBundleArchiveTests.cs, shared/tests/MantlePlace.Core.Tests/ManifestReaderTests.cs, shared/tests/MantlePlace.Core.Tests/ThreeArchiveTests.cs

**A vault row is joined to an order only on a declared order id, so an old bundle yields no order.** Before MPB 1.7.0 `attribution.order_id` held the packaging job id, which changes on every rebuild.

Source: shared/tests/MantlePlace.Core.Tests/ManifestReaderTests.cs

**Retained extraction directories are never deleted on dispose.** Revit links created by the import point into them and would silently break the next time the project is opened.

Source: shared/tests/MantlePlace.Core.Tests/LocalBundleArchiveTests.cs

**An unclassified step kind defaults to Retained, not Transient.** A Transient default leaves links pointing into a deleted scratch directory that breaks on the next project open, while Retained merely leaks disk.

Source: shared/tests/MantlePlace.Core.Tests/ImportStepLifetimeTests.cs

**The imagery drape step is Retained, not Transient, although it builds no geometry.** A Transient drape textures correctly once and comes back unresolved.

Source: shared/tests/MantlePlace.Core.Tests/ImportStepLifetimeTests.cs

**The retained imagery rewrite tolerates AutoCAD holding the earlier image open.** Importing one order into a second Civil 3D drawing otherwise threw `IOException` while the first drawing still showed its aerial.

Source: shared/tests/MantlePlace.Core.Tests/LocalBundleArchiveTests.cs

**An artifact with no advertised hash, as in bundles below v19, is valid but unverified.** Calling it corrupt makes those bundles unimportable, and calling it verified would be false.

Source: shared/tests/MantlePlace.Core.Tests/LocalBundleArchiveTests.cs

**All artifact hashes are swept before any step runs.** Per-entry checking would abort only after earlier steps had already created a toposolid in the user's model.

Source: shared/tests/MantlePlace.Core.Tests/LocalBundleArchiveTests.cs

**The drape image size is probed from a header prefix while planning, without extracting the roughly 50 MB file.** A refusal then costs far less than a successful import.

Source: shared/tests/MantlePlace.Core.Tests/LocalBundleArchiveTests.cs

**The corpus manifest pin may exceed a host's own floor but must never fall below it.** Hosts repin independently, and a pin below the floor turns every accept case into a document the parser refuses.

Source: shared/tests/MantlePlace.Core.Tests/ManifestConformanceTests.cs

### Planner

**The planner surfaces the manifest's reason in translated form rather than echoing the producer's token.** `dcc_readiness` reasons are an open vocabulary that includes `emit_threw` stage identifiers, which must not reach dialogs.

Source: shared/tests/MantlePlace.Core.Tests/ImportPlannerTests.cs

**The satellite drape's extent is inferred and corroborated against the image's pixel grid.** The declared extent lives in a sibling host's block, and the host-neutral field carrying it is undeclared by the schema.

Source: shared/tests/MantlePlace.Core.Tests/ImportPlannerTests.cs

**Site latitude and longitude outside the valid range are refused, not clamped or wrapped.** Revit throws past a pole and silently wraps past the antimeridian, which would put the sun over another site unannounced.

Source: shared/tests/MantlePlace.Core.Tests/ImportPlannerTests.cs

**Time zone offsets beyond Revit's -12 to +12 range are wrapped, not clamped.** Wrapping keeps the clock and moves the date a day, while clamping would put every hour of a sun study one out.

Source: shared/tests/MantlePlace.Core.Tests/ImportPlannerTests.cs

### Materialize job protocol

**An in-flight materialize report with no job id is still followed.** Polling is keyed on the order rather than the job, and refusing would tell a curator to retry a build already running.

Source: shared/tests/MantlePlace.Core.Tests/MaterializeJobTests.cs

**An unknown delivery state is not terminal and never counts as Complete.** An empty delivered list with an empty requested set would otherwise pass the nothing-outstanding test vacuously.

Source: shared/tests/MantlePlace.Core.Tests/MaterializeJobTests.cs

**Materialize completion is judged on delivered tokens, not the status word.** The worker's soft-fail envelope swallows per-token emit errors and still reports completed.

Source: shared/tests/MantlePlace.Core.Tests/MaterializeJobTests.cs

**Deliverables the platform will never produce for an area are not treated as outstanding.** Otherwise a finished bundle polls its whole budget and times out.

Source: shared/tests/MantlePlace.Core.Tests/MaterializeJobTests.cs

**Right after a start, an empty delivered list with no active job is not Complete.** The job row is not yet visible, and otherwise the caller would download a bundle missing the requested layers.

Source: shared/tests/MantlePlace.Core.Tests/MaterializeJobTests.cs

**Materialize job start success is judged by the outcome field, not by the presence of a job id.** The platform's no-op is a success that names no job, and inferring failure from a missing `jobId` stopped the host importing bundles with nothing left to build.

Source: shared/tests/MantlePlace.Core.Tests/VaultConformanceTests.cs

### Site geometry and terrain surface

**Polygon rings are thinned by skipping vertices within Revit's short-curve tolerance of the last kept one, closing edge included.** Dropping a tiny edge left a gap Revit refused as not contiguous.

Source: shared/tests/MantlePlace.Core.Tests/SiteRingTests.cs

**The duplicated closing position that GeoJSON requires is dropped from polygon rings.** Revit's `CurveLoop` closes itself and rejects a zero-length final segment outright.

Source: shared/tests/MantlePlace.Core.Tests/SiteVectorTests.cs

**Surface Z is an absolute orthometric height on every artifact and is never reduced against the origin.** Subtracting it would put the terrain underground.

Source: shared/tests/MantlePlace.Core.Tests/SurfaceTinTests.cs

**The surface sanitiser crops to the bounding box of the projected AOI corners, not their inner extremes.** Grid convergence tilts the quadrilateral, and the inner rectangle cut 1,980 good points to remove 400 bad ones.

Source: shared/tests/MantlePlace.Core.Tests/SurfaceSanitiserTests.cs

**The crop tolerance stays tight on purpose.** Widening it to keep a row only 0.4 m outside the AOI would reach 3.3 m and re-admit a column carrying 116 of the 400 filled points.

Source: shared/tests/MantlePlace.Core.Tests/SurfaceSanitiserTests.cs

**A DXF triangle is read as a 3DFACE with a repeated fourth corner, not as a quad.** DXF has no triangle primitive, so TIN emitters write it this way, and reading it as a quad adds a degenerate face and a phantom vertex.

Source: shared/tests/MantlePlace.Core.Tests/SurfaceTinTests.cs

**DXF geometry inside BLOCKS is treated as a definition placed by an INSERT with its own transform.** Reading its corners as world coordinates scatters the site.

Source: shared/tests/MantlePlace.Core.Tests/SurfaceTinTests.cs

**The nodata-fill discriminator uses a median of neighbour drops, not the largest drop.** Genuine flat ground at a site edge usually has a headland; measured, fill and water separate by two orders of magnitude.

Source: shared/tests/MantlePlace.Core.Tests/SurfaceTinTests.cs

**The nodata-fill guard drops nothing and says so above `SurfaceGrid.MaxDroppedFraction`.** A guard that can eat the terrain is worse than the artefact it removes.

Source: shared/tests/MantlePlace.Core.Tests/SurfaceTinTests.cs

**The contour DXF reader skips VIEWPORT entities and sheet frames in group 67 = 1.** The platform's site linework DXF carries plan-sheet viewports, and Civil 3D 2027 refused the whole drawing on them.

Source: shared/tests/MantlePlace.Core.Tests/PublishedContourTests.cs

### Site companion file

**The converted Site `.rvt` is named per Revit version.** A shared `Site.rvt` was upgraded in place by the second Revit to import an order, which cannot be undone and broke the older project's link.

Source: shared/tests/MantlePlace.Core.Tests/SiteCompanionPathTests.cs

**The companion stays inside `extracted/`.** The recursive per-order sweep then removes it without a second place to sweep.

Source: shared/tests/MantlePlace.Core.Tests/SiteCompanionPathTests.cs

**Version tokens containing a dot are mapped to a collision-free name.** Stripping or pre-mapping the dot lands on a string another version already maps to, and two versions would share one companion.

Source: shared/tests/MantlePlace.Core.Tests/SiteCompanionPathTests.cs

### Terrain base, toposolid types and drape

**Terrain base clearance reuses the drape's splitting minimum (total at least three times the host minimum).** A base that clears the terrain then also leaves a toposolid type the drape can still split.

Source: shared/tests/MantlePlace.Core.Tests/TerrainBaseTests.cs

**The terrain base is not placed on the lowest level.** That would bury the terrain in a solid nobody asked for, which every section through the project would then cut.

Source: shared/tests/MantlePlace.Core.Tests/TerrainBaseTests.cs

**Toposolid type choice judges splittability on layer 0's own width, not the total width or the name.** The drape splits layer 0, and for multi-layer types the two differ; a type that looks splittable by its 2 m total is refused when layer 0 has nothing to give, which rolls back the whole drape. A re-import must not build terrain on the type the previous import derived, because its top layer is the imagery sliver at the host minimum, which cannot be split again. Layer functions, not names, separate ground from paving.

Source: shared/tests/MantlePlace.Core.Tests/TerrainBaseTests.cs

**Toposolid type preferences are tie-breaks, not filters.** Refusing to build ground just because the photograph would not fit is the wrong trade.

Source: shared/tests/MantlePlace.Core.Tests/TerrainBaseTests.cs

**The drape imagery type and material names are pinned, not derived.** Earlier projects already hold types spelled this way, and a respelling would orphan them and duplicate types on re-import.

Source: shared/tests/MantlePlace.Core.Tests/DrapeLayeringTests.cs

**A subdivision with no keyword gets its own type name, distinct from the terrain's imagery type.** Reusing that type would put it on the terrain's own layer stack, and editing it would edit the terrain.

Source: shared/tests/MantlePlace.Core.Tests/SubDivisionMaterialTests.cs

**A layered imagery type is not split again; its top layer's material is re-pointed.** Splitting would stack a second thin layer or be refused by Revit as too thin.

Source: shared/tests/MantlePlace.Core.Tests/SubDivisionMaterialTests.cs

**Terrain smoothing is reported as document-wide.** `SetSmoothedSurface` is static and takes only a `Document`, so per-toposolid wording would describe a nonexistent API and invite a loop that must not be written.

Source: shared/tests/MantlePlace.Core.Tests/TerrainSmoothingTests.cs

**The drape notice says what turning smoothing off will look like.** The photograph is placed for the smoothed renderer, whose offset origin is the element's corner, and the curator owns the switch.

Source: shared/tests/MantlePlace.Core.Tests/TerrainSmoothingTests.cs

**The drape notice does not predict four quarters.** That is the opposite mismatch; an offset written for the element's corner then read from the origin shows as a shattered mosaic because flat shading maps per face.

Source: shared/tests/MantlePlace.Core.Tests/TerrainSmoothingTests.cs

**A tree family fits only if its crown is large enough.** The apex is a fixed fraction of the crown, and a one-centimetre crown asks Revit for a half-millimetre circle, below its shortest curve.

Source: shared/tests/MantlePlace.Core.Tests/TreeFamilyTests.cs

### Log search

**The log search never descends into `extracted/`.** It holds every tile and mesh of every imported bundle and is never evicted, and a recursive walk would stat tens of thousands of files on Revit's UI thread.

Source: shared/tests/MantlePlace.Core.Tests/OpenLogsTests.cs

### Sign-in and notices

**The client name sent on the authorize URL is never treated as a client id.** The platform treats it as display-only, reads it from an untrusted query and never validates it against a registry.

Source: shared/tests/MantlePlace.Core.Tests/AuthClientTests.cs

**The browser success page hands off with script, not a meta refresh.** A meta refresh navigates even when the done page does not answer, leaving an offline curator on the browser's own error page.

Source: shared/tests/MantlePlace.Core.Tests/AuthConformanceTests.cs

**Only the success page carries the hand-off target; the failure page does not.** The failure page interpolates an `error_description` written by the authorization server and should execute nothing beyond the escape pass.

Source: shared/tests/MantlePlace.Core.Tests/AuthConformanceTests.cs

**The per-machine announced-order record lets the first Revit to claim an order announce it, and an order is still announced when the record cannot be written.** A duplicate notice is a smaller failure than a lost one.

Source: shared/tests/MantlePlace.Core.Tests/AnnouncedOrderStoreTests.cs

### Test harness and fixtures

**The conformance suite is a framework-free `dotnet run` harness targeting both net8.0 and net10.0.** Hosts load Core on either runtime, no test package is wanted, and promote-by-rename needs the client covered.

Source: shared/tests/MantlePlace.Core.Tests/MantlePlace.Core.Tests.csproj

**The Realtime test server is a raw `TcpListener` with a hand-done RFC 6455 upgrade.** It avoids http.sys URL reservation on Windows, needs no package and runs the same on the Linux runner.

Source: shared/tests/MantlePlace.Core.Tests/LoopbackRealtimeServer.cs

**Platform conformance replay sends order bodies as the case's raw JSON, not serialised from the generated body type.** The generated payment enum writes its C# member name instead of the spec's value.

Source: shared/tests/MantlePlace.Core.Tests/PlatformConformanceTests.cs

**PNG test inputs are built byte by byte instead of committed.** `.gitattributes` tracks `*.png` in Git LFS, which bills every version pushed, and corrupt binary fixtures are indistinguishable from a corrupt directory.

Source: shared/tests/MantlePlace.Core.Tests/PngHeaderTests.cs

**Site model fixtures write a tilde where STEP writes a hash and swap it back at run time.** A hash followed by a number is exactly the shape the public-hygiene gate refuses as a tracker citation.

Source: shared/tests/MantlePlace.Core.Tests/SiteModelReaderTests.cs

**An explicit JSON null counts as read by the vector tracker.** Null is a value in the corpus (the sha256 null cache row), and treating it as nothing would let a suite skip that row for free.

Source: shared/tests/MantlePlace.Core.Tests/VectorDocument.cs

### Platform API sync and generated client

**The platform API sync names itself in the User-Agent.** The edge in front of the platform refuses Python's default user agent.

Source: shared/tools/platform-api/sync_platform_api.py

**The generated client excludes the client header from every signature, and optional properties are nullable.** `PlatformTransport` applies the client header once per request, and a nullable property lets an absent member, like a public Quote's Credit balance, read as absent, not zero.

Source: shared/tools/platform-api/sync_platform_api.py

**Regeneration swaps NSwag's `JsonStringEnumConverter` for `EnumMemberJsonConverter`.** The stock converter ignores `EnumMember` and writes C# names where the spec serves lowercase, and the framework fix needs .NET 9 while the client targets net8.0.

Source: shared/tools/platform-api/sync_platform_api.py
