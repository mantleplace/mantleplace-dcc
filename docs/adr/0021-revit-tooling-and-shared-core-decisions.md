---
name: adr-0021-revit-tooling-and-shared-core-decisions
description: Decisions that lived in comments in the Revit build, packaging and tooling scripts and in the shared MantlePlace.Core library (sign-in, fault handling, bundle manifest reading, import planning, cache keys), recorded when the comments were removed. Read before changing how the Revit plugin is built, packaged, timed or deployed, or how the shared core reads a bundle and plans an import.
status: accepted
---

# 21. Revit tooling and the shared core: decisions recorded from code

Date: 2026-11-21

## Status

Accepted. Recorded from the code comments that held these decisions, when the comments were removed; each decision was already in force in the code and none is new.

## Context

These decisions lived in comments in two code areas: the Revit tree (`revit/`, its build properties, packaging, deploy, uninstall, rendering, carrier-spike and timing tools) and the shared .NET core (`shared/src/MantlePlace.Core`). Comments are being removed from the code, so the decisions and their reasons are recorded here.

## Decisions

### Revit build and install

**`RevitApiDir` defaults to the oldest supported Revit (2025).** A plugin compiled against a newer API will not load in older Revit, so pointing at a newer install silently narrows what ships.
Source: revit/Directory.Build.props

**A `-dirty` suffix is appended to the informational version, after the SDK sets the commit sha, when `revit/` or `shared/` has uncommitted changes.** Two builds naming one commit would otherwise look identical. `BuildIdentity` reads this shape.
Source: revit/Directory.Build.props

**A cmd wrapper runs the PowerShell installer with ExecutionPolicy Bypass.** A downloaded `.ps1` is blocked by Restricted policy and Mark-of-the-Web. Group Policy overrides are deliberately unhandled, and manual copy is the fallback.
Source: revit/packaging/Install.cmd

### Revit deploy, packaging and uninstall scripts

**One Deploy script serves maintainers and curators, builds against the oldest supported Revit API so one assembly loads in Revit 2025 to 2027, and stays Windows PowerShell 5.1 compatible.** Newer syntax would break curator machines.
Source: revit/tools/Deploy-MantlePlaceRevit.ps1

**`Invoke-Git` scopes `ErrorActionPreference` to Continue, and is duplicated from `LocalInstall.psm1`.** Windows PowerShell 5.1 turns redirected native stderr into a terminating error under Stop. The release zip ships this file verbatim and it can import nothing, hence the copy.
Source: revit/tools/Deploy-MantlePlaceRevit.ps1

**`Invoke-Git` is a simple function, not an advanced one.** With `[Parameter()]`, git's own `-e`, `-c` and `-C` flags would bind to PowerShell's common parameters instead of reaching git.
Source: revit/tools/Deploy-MantlePlaceRevit.ps1

**Release packaging does not run in CI.** Building the shim needs a licensed `RevitAPI.dll` that cannot be vendored, and the repository must never carry a self-hosted runner because a fork's pull request would execute on it.
Source: revit/tools/Package-MantlePlaceRevit.ps1

**The release zip is written entry by entry with forward slashes, not with `Compress-Archive`.** Windows PowerShell 5.1 stores backslash names that some extractors keep literally, so a bundle would load nothing.
Source: revit/tools/Package-MantlePlaceRevit.ps1

**Uninstall deletes local data from an explicit name list, not everything except auth.** Unlisted folders, including any future credential-adjacent file, are then never deleted by default.
Source: revit/tools/Uninstall-MantlePlaceRevit.ps1

### Revit asset rendering

**Theme colours in the ribbon icon renderer are written as PowerShell hex literals, not hash-prefixed strings.** `ci-public-hygiene` refuses a six-digit hash triplet in a tracked file as a tracker citation and exempts only the CSS spelling.
Source: revit/tools/Render-RibbonIcons.ps1

**Tooltip vignettes use a 355 by 266 canvas rendered at 96 DPI.** Revit silently clips or drops tooltip images over 355 pixels on the longest side, and it lays a tooltip image out at its device-independent size, so a 2x render would be 710 pixels on its long side. The headless suite checks the cap against the PNG headers.
Source: revit/tools/Render-TooltipVignettes.ps1

**The Import Bundle vignette draws only the toposolid.** It is the one output every bundle import produces. Other outputs are conditional on bundle content, and drawing them would promise things an import may not deliver.
Source: revit/tools/Render-TooltipVignettes.ps1

**Path data numbers are formatted in the invariant culture.** PowerShell's `-f` uses the current culture while `Geometry.Parse` reads only invariant, so a comma-decimal locale would break the picture on another machine.
Source: revit/tools/Render-TooltipVignettes.ps1

### Carrier spike

**The footprints baseline is the order-CRS GeoJSON written by `make_carriers.py`.** No Revit GeoJSON carries footprints, and the one permitted lon/lat projection cannot reach a State Plane order's frame.
Source: revit/tools/carrier-spike/Readers/CarrierComparison.cs

**The DXF carrier encodes attributes, polygon number and ring role as MANTLEPLACE extended data, and uses a 3-D POLYLINE for per-vertex Z.** Plain DXF has no attribute table, no polygons and no per-vertex elevation on LWPOLYLINE.
Source: revit/tools/carrier-spike/Readers/DxfLayerReader.cs

**SQLite is reached by P/Invoke into the Windows-shipped `winsqlite3.dll`, with no NuGet packages or native binaries.** Nothing then collides with another add-in's SQLite inside Revit's process.
Source: revit/tools/carrier-spike/Readers/WinSqlite.cs

### Timing harness

**The timing export uses Windows' own System32 `tar.exe`.** A GNU tar earlier on PATH, such as Git's, reads a drive letter like `D:` in a path as a remote host.
Source: revit/tools/timing/Export-TimingSource.ps1

**The harness does nothing unless the process was launched for exactly its compiled tag, and only one Revit may run at a time.** Revit watches the Addins folder, and a same-year Revit can load another run's temporary manifest and DLL. Concurrent Revits also distort timings. The tag gate is only the second line of defence.
Source: revit/tools/timing/Harness/TimingHarness.cs, revit/tools/timing/Invoke-TimingRun.ps1

**Stop-before stepping never cancels through `ActiveImport.Advance`.** That path runs Finish and its committed smoothing transaction, which a full import only does inside the drape step, so a checkpoint would not measure the drape.
Source: revit/tools/timing/Harness/TimingHarness.cs

**Ground height is read from the toposolid's own triangulated upward faces, not a `ReferenceIntersector` ray cast.** The ray cast returned only the flat bottom face in every attempt.
Source: revit/tools/timing/Harness/Verify.cs

**Cold IFC runs move the converted `Site.year.rvt` aside.** The plugin extracts into a per-order shared folder and reuses an existing converted file, so a second run of a year would otherwise be warm.
Source: revit/tools/timing/Invoke-TimingRun.ps1

**The import window is read on a separate thread.** A UI Automation call to a window inside a long commit blocks until the commit ends, and the five-second sample lines must not wait for it.
Source: revit/tools/timing/Invoke-TimingRun.ps1

### Host UI and fault handling

**The ribbon identity row is hidden when the address is empty or contains a percent sign; the tooltip still carries the address.** `RibbonItem.ItemText` throws on those inside a dispatcher state-change handler, which would end the Revit process.
Source: shared/src/MantlePlace.Core/AccountRibbon.cs

**The dispatcher fault handler claims a fault only when a plugin frame is on the stack, by ownership and not severity.** Swallowing host faults leaves the application running on state it judged untrustworthy.
Source: shared/src/MantlePlace.Core/AddinFault.cs

**The own-namespace prefix keeps its trailing dot.** Without it the check would match unrelated `MantlePlaceSomething` namespaces and claim a fault the plugin did not cause.
Source: shared/src/MantlePlace.Core/AddinFault.cs

**Any stack frame, not only the innermost, decides ownership of a fault.** Plugin bugs usually mis-drive framework types such as `CancellationTokenSource.Cancel`. The accepted cost is claiming some host faults, preferring a kept session over terminating Revit.
Source: shared/src/MantlePlace.Core/AddinFault.cs

**The host UI spends one brand accent on one control per window and keeps the host's default chrome elsewhere.** A Revit add-in painting its own colours stops looking like part of Revit.
Source: shared/src/MantlePlace.Core/BrandPalette.cs

**`MixToWhite` lightens every channel by the same fraction toward white, and clamps out-of-range or NaN amounts.** Hover and press then read as the same button lit, and a throw on Revit's dispatcher ends the process.
Source: shared/src/MantlePlace.Core/BrandPalette.cs

### Sign-in and loopback callback

**The redirect URI uses the literal `127.0.0.1`, not `localhost`, per RFC 8252 section 8.3.** It forces the loopback interface and avoids DNS or hosts-file surprises, including corporate resolvers answering `localhost` differently.
Source: shared/src/MantlePlace.Core/AuthUrls.cs

**Authorize URL query parameters are emitted in a pinned order set by a corpus vector, although OAuth does not require an order.** Both hosts then produce byte-identical URLs, and a captured URL reproduces issues across hosts.
Source: shared/src/MantlePlace.Core/AuthUrls.cs

**`Base64Url` is its own type, with padding stripped and the url-safe alphabet.** PKCE verifier and challenge depend on exact encoding, and a wrong encoder fails invisibly until the authorization server rejects the exchange step.
Source: shared/src/MantlePlace.Core/Base64Url.cs

**Callback state validation is case-sensitive, and an empty expected state is never valid.** The loopback port is reachable by anything on the machine, and treating empty equals empty would accept an attacker's callback carrying their authorization code.
Source: shared/src/MantlePlace.Core/AuthCallback.cs

**Callback classification checks an explicit error first, then state mismatch, then missing code.** Checking state first would report possible CSRF for an ordinary user decline, and checking code first would report a useless "no code".
Source: shared/src/MantlePlace.Core/AuthCallback.cs

**The success page probes the done URL with a no-cors fetch and navigates only if something answered.** A meta refresh would navigate offline into a browser error page. The script is kept off the failure page that echoes server text.
Source: shared/src/MantlePlace.Core/AuthCallback.cs

**Browser pages use a dark ground matching the platform's hand-off page and system font faces only.** A white flash read as failure, and the brand typeface would need a network request the page must not depend on.
Source: shared/src/MantlePlace.Core/AuthCallback.cs

**An event with no matching transition rule leaves the auth state unchanged.** Late callbacks such as a redirect after cancel or a refresh after sign-out must not corrupt the session or turn into a signed-out curator.
Source: shared/src/MantlePlace.Core/AuthStateMachine.cs

### Project anchor and adjacent bundles

**A later bundle is built at the difference of published survey points against the first bundle's anchor, only within one EPSG and angle, with no reprojection, and height is excluded.** Z is absolute orthometric, so including height would double-count it.
Source: shared/src/MantlePlace.Core/AdjacentBundle.cs

**The maximum offset from the anchor is 16 km.** That is half of Autodesk's roughly 32 km guidance for distance from the internal origin, leaving the other half for the bundle's own extent, because past it Revit graphics and tolerances degrade.
Source: shared/src/MantlePlace.Core/AdjacentBundle.cs

**Two angles are equal within 1e-7 radians, not exactly.** The angle round-trips through Revit as a double and a degrees-to-radians conversion, and at the 16 km limit that tolerance moves a point under 2 mm.
Source: shared/src/MantlePlace.Core/AdjacentBundle.cs

**The project's EPSG code is stored in its own ExtensibleStorage schema, and the anchor position is read live.** Revit holds no CRS and the provenance schema cannot grow. The schema GUID is permanent, and reading the position live means moving the survey point moves the anchor.
Source: shared/src/MantlePlace.Core/AdjacentBundle.cs

### Attribution view and building identity

**The attribution drafting view is identified by name, and its note by matching the recorded text.** Neither has an identity slot. An edited note is treated as the curator's and a fresh one is added beside it (ADR 0011).
Source: shared/src/MantlePlace.Core/AttributionView.cs

**Note paragraphs are separated by a carriage return.** `TextNote.Text` hands paragraphs back split by CR whatever they were written with, so comparisons must normalise to it.
Source: shared/src/MantlePlace.Core/AttributionView.cs

**Only the recorded note for this bundle is treated as ours, identified by order or, for bundles before MPB 1.7.0 that name no order, by the build's JobId.** Two such bundles then never touch each other's notes.
Source: shared/src/MantlePlace.Core/AttributionView.cs

**Building stamps carry the build token as well as the GlobalId; the stale arm names what to delete but never deletes.** Emitters keep GlobalIds stable across builds, so a GlobalId match alone cannot refuse an older build's buildings. Curators may have edited them.
Source: shared/src/MantlePlace.Core/BuildingIdentity.cs

**Stamp prefixes are matched ordinally, including the trailing separator.** Cache-key stems are truncated hashes, where one stem being a prefix of another is a real collision risk.
Source: shared/src/MantlePlace.Core/BuildingIdentity.cs

### Autodesk bundle manifest

**The bundle manifest's `UpgradeCode` stays constant across versions.** The Autodesk storefront recognises an upgrade of the same product by it, and changing it would make each release install as a new product.
Source: shared/src/MantlePlace.Core/AutodeskBundleManifest.cs

**Each supported year gets its own `RuntimeRequirements` Components group.** Both hosts read only one requirement per group. One shared group made AutoCAD load nothing and Revit load only the first year (seen in live runs).
Source: shared/src/MantlePlace.Core/AutodeskBundleManifest.cs

**The AutoCAD component is a .NET assembly loaded at startup, and the Civil 3D part is deliberately not named.** Plugin commands then exist before the first is typed, and the shell loads the Civil 3D part only when Civil 3D is in the process.
Source: shared/src/MantlePlace.Core/AutodeskBundleManifest.cs

**The product code is derived from the version, not drawn at random.** The same version always builds the same bytes, and a rebuilt release never presents itself to the storefront as a new product.
Source: shared/src/MantlePlace.Core/AutodeskBundleManifest.cs

### Bundle entry resolution

**Manifest pointers resolve exactly, else beneath a single archive-wide root folder (the re-zipped bundle case), and never by any entry ending in the pointer.** The loose match would silently import a stale Backup copy, which is worse than not finding the file.
Source: shared/src/MantlePlace.Core/BundleEntryIndex.cs

### Import plan: terrain

**Terrain has three exclusive tiers: the surface DXF's TIN vertices are preferred over the regular-lattice points file, the points file stays as the no-origin path, and the linked DXF is the fallback with a different extraction lifetime.** The points lattice has cocircular cells that make Delaunay triangulation degenerate and the terrain look faceted.
Source: shared/src/MantlePlace.Core/BundleImportPlan.cs, shared/src/MantlePlace.Core/BundleImportPlanner.cs

**An unreadable points-file unit stops the terrain instead of falling back to the surface DXF.** That file carries the same terrain from the same emitter at the same suspect scale.
Source: shared/src/MantlePlace.Core/BundleImportPlanner.cs

**An unreadable unit on the TIN tier falls through to the points file.** The points file is a different deliverable with its own unit declaration, unlike the rule forbidding fallback onto the same terrain from the same emitter.
Source: shared/src/MantlePlace.Core/BundleImportPlanner.cs

**The terrain is created on the imagery toposolid type exactly when a drape step is planned.** Retyping a toposolid rebuilds element relations and cost 409 seconds on an 80,372-point terrain, and a drape-less plan would show a blank layer.
Source: shared/src/MantlePlace.Core/BundleImportPlan.cs

### Import plan: site IFC and layers

**The site model is planned as a building copy, checked by default, and an unchecked Link IFC row; one missing site model yields a single skip.** The context-buildings step already places the same buildings as elements, and both together show every building twice.
Source: shared/src/MantlePlace.Core/BundleImportPlan.cs, shared/src/MantlePlace.Core/BundleImportPlanner.cs

**When both the context-buildings copy and the IFC link run, the copy saves its IFC conversion as the link's companion.** The link finds it and converts nothing a second time, because the conversion is slow.
Source: shared/src/MantlePlace.Core/BundleImportPlan.cs, shared/src/MantlePlace.Core/BundleImportPlanner.cs

**Water imports polygons only.** Turning stream centrelines into areas would mean inventing a width, which is derivation and belongs upstream in the emitter.
Source: shared/src/MantlePlace.Core/BundleImportPlan.cs

**Flood zones are drawn as filled regions on the hazard plan, never as terrain subdivisions.** They overlap land cover and roads, and coincident subdivisions do not draw in cut order. Enum members are declared after older ones to keep numbers.
Source: shared/src/MantlePlace.Core/BundleImportPlan.cs

**Road centrelines, water, road surfaces and trees share a frame gate, and are planned independently of the terrain.** Their coordinates are absolute. Road centrelines carry their own draped Z, and coupling them to the terrain would lose roads when the points file is superseded.
Source: shared/src/MantlePlace.Core/BundleImportPlanner.cs

**Layers the curator did not choose are planned in full and removed afterwards; a layer the bundle lacks keeps its "missing" skip rather than "not chosen".** Every tier and extent decision is then taken as for an import of everything.
Source: shared/src/MantlePlace.Core/BundleImportPlanner.cs

**Land and hazard build tokens come from the job id, falling back to the order id.** A 3.0 manifest states no job, so every plan would otherwise be named unidentified and two orders in one project would share a plan.
Source: shared/src/MantlePlace.Core/BundleImportPlanner.cs

### Import plan: step order

**Whether a drape will run is decided first, but its steps are appended last.** The terrain's toposolid type depends on the drape existing, while list order must stay unchanged.
Source: shared/src/MantlePlace.Core/BundleImportPlanner.cs

**Land and hazard plans are drawn after the terrain and before road and water subdivisions, with flood before steep ground.** In Revit 2027 a plan over a terrain costs far more with more subdivisions, and plans crop to the published rectangle.
Source: shared/src/MantlePlace.Core/BundleImportPlanner.cs

**The imagery drape step runs last.** It needs the terrain material and subdivisions to exist, its Revit API surface has never executed, and `Execute` runs a transaction per step without catching, so order decides what survives.
Source: shared/src/MantlePlace.Core/BundleImportPlanner.cs

**A plan counts as importable only if it has a content-building step, excluding settings-only steps such as survey point, site location and the credits view.** A bundle of only those would otherwise report imported over an empty model.
Source: shared/src/MantlePlace.Core/BundleImportPlanner.cs

**The image-size probe argument to the planner is required, with no overload.** The previous optional integrity input was parsed and consumed by nothing, and a default probe would silently plan a drape whose extent nothing corroborated.
Source: shared/src/MantlePlace.Core/BundleImportPlanner.cs

### Import plan: skip reasons

**Skips carry a closed-vocabulary reason code beside the prose reason.** The prose is written for curators and will be reworded, so tests, UI branches and support triage must branch on the code.
Source: shared/src/MantlePlace.Core/BundleImportPlan.cs

**`DeclaredAbsent` is a separate skip code from `ArtifactNotInManifest`; new codes are appended to keep numbering.** The import window lists the latter with a vault remedy and stays silent on the former, which no re-download can fix.
Source: shared/src/MantlePlace.Core/BundleImportPlan.cs

**The `NotHeld` skip is never split by the manifest's stated reason.** The reasons grow, and one the plugin does not know must still be shown as stated.
Source: shared/src/MantlePlace.Core/BundleImportPlan.cs

**The skip reason surfaces the manifest's readiness reason translated through a clause table, not verbatim.** The vocabulary is open and includes internal stage identifiers such as `emit_threw` that curators must not see.
Source: shared/src/MantlePlace.Core/BundleImportPlan.cs

**An uncorroborated ground extent has its own skip code.** A manifest that said something plausible the bytes do not back up yields a drape of somewhere else, a failure curators cannot notice.
Source: shared/src/MantlePlace.Core/BundleImportPlan.cs

**When the base road layer exists but `road_polygons` does not, the skip says surfaces were not derived rather than suggesting a re-download; neither present means no roads.** `road_polygons` is best-effort and the vault has nothing to give.
Source: shared/src/MantlePlace.Core/BundleImportPlanner.cs

**An absent hazard layer is explained by its readiness verdict and never read as a statement about the site.** Out-of-coverage is logged but not listed as unavailable, while pre-1.4.0 bundles with the GeoPackage are listed for re-download.
Source: shared/src/MantlePlace.Core/BundleImportPlanner.cs

**The not-drawn contours message names both causes of a `not_produced` verdict: a bundle cut before MPB 1.4.0, or one with no site origin.** The platform's word cannot tell them apart.
Source: shared/src/MantlePlace.Core/BundleImportPlanner.cs

### Import plan: placement and site location

**The survey point elevation is always zero.** The manifest states every artifact's Z is absolute orthometric height, so a non-zero survey-point elevation would double-count it.
Source: shared/src/MantlePlace.Core/BundleImportPlan.cs

**An unstated grid rotation is treated as zero, and a published non-zero value is still applied verbatim.** Only the local-origin tier omits it, and the own block states zero by construction because emitters reproject per vertex.
Source: shared/src/MantlePlace.Core/BundleImportPlanner.cs

**The survey point takes the own georeference origin first and `delivery.local_origin` as fallback, and is never computed.** Existing `local_ft` bundles keep their origin and other tiers gain one without a coordinated pipeline release.
Source: shared/src/MantlePlace.Core/BundleManifest.cs

**Site location uses published latitude and longitude with west-negative sign, and writes the published time zone after the coordinates.** Revit derives a zone from longitude whenever coordinates are set, and that derived zone is wrong at political boundaries.
Source: shared/src/MantlePlace.Core/BundleImportPlan.cs

**Published UTC offsets beyond Revit's plus or minus 12 hour range are wrapped by 24 hours rather than clamped.** The date moving one day shifts the sun under half a degree, while clamping puts every sun-study hour out by one.
Source: shared/src/MantlePlace.Core/BundleImportPlan.cs

**Site location reads only the host's own georeference block, not `delivery.local_origin`, and off-globe pairs are refused.** An unaudited field would misplace the sun in every renderer, and Revit silently wraps longitude past the antimeridian.
Source: shared/src/MantlePlace.Core/BundleImportPlanner.cs

**Placed artifacts have their stated CRS checked against the origin's rather than trusted.** Subtracting one CRS's easting from another's gives a plausible number about 2000 km wrong, for example lon/lat layers on a State Plane foot tier.
Source: shared/src/MantlePlace.Core/BundleImportPlanner.cs

### Import plan: drape extent

**The drape extent comes from the most specific source: the host's own block first, then host-neutral `imagery.drape`, and only then the DEM's undeclared bounds, used only when the image's pixel grid agrees.** The host's own drape is never replaced by the shared one when it exists, because on a State Plane delivery the shared one is on the UTM grid and is a different image. Extents are placed through `SiteFrame` with no new arithmetic.
Source: shared/src/MantlePlace.Core/BundleImportPlanner.cs

**Only an inferred drape extent is checked, as pixels times GSD against metres, with a tolerance of two pixels per axis.** A declared extent is a contract. The check proves the extent's size, not its position, and is the strongest host-neutral check, not proof. Two pixels is above routine rounding yet far below the hundreds of metres a drape cut to imagery footprint instead of the DEM would differ by, and a tolerance tripping on rounding would be switched off.
Source: shared/src/MantlePlace.Core/BundleImportPlanner.cs

### Bundle manifest reading

**Contours' vertical unit is read from the manifest pointer alone.** A DXF has one `$INSUNITS` for the whole drawing and cannot state a separate elevation unit about itself.
Source: shared/src/MantlePlace.Core/BundleManifest.cs

**A GeoPackage that holds one layer is read from its one table, whatever it is named.** The producer names it in CamelCase while the layer name is snake_case, and the spec states neither.
Source: shared/src/MantlePlace.Core/BundleManifest.cs

**The file frame's unit is compared against each file's own units and never substituted for the origin's.** On a local grid the origin is in metres while the files are in feet.
Source: shared/src/MantlePlace.Core/BundleManifest.cs

**`GeoOrigin` carries its linear unit with its coordinates and is not assumed metric.** The v19 projected origin publishes State Plane foot values, and treating them as metres placed such sites 3.28 times out.
Source: shared/src/MantlePlace.Core/BundleManifest.cs

**The host reads its own `revit.georeference` block and never a sibling host's as fallback.** The shared corpus states the two in conflict on purpose, and merging would report the wrong CRS.
Source: shared/src/MantlePlace.Core/BundleManifest.cs

**An inverted or zero-area ground extent is refused rather than normalised.** A drape stretched over a silently flipped rectangle lands mirrored and reads as a plausible aerial photo of somewhere else.
Source: shared/src/MantlePlace.Core/BundleManifest.cs

**Attribution sources are copied into the project verbatim, with no licensing decision by this host.** Terms and obligations are the platform's statement. `license_url` may be null for some sources despite the schema typing it string.
Source: shared/src/MantlePlace.Core/BundleManifest.cs

**A parsed manifest is always returned, even on refusal; `IsValid` means a usable materialized bundle, not Revit content.** The vault join key and streaming path must survive, and the shared corpus requires every host to accept such manifests.
Source: shared/src/MantlePlace.Core/BundleManifest.cs

**The filing key stays the order id or, before MPB 1.7.0, `attribution.order_id` (a packaging job id).** Re-imports then find elements already stamped under it rather than placing them a second time.
Source: shared/src/MantlePlace.Core/BundleManifest.cs

**The manifest version is kept as an unparsed string.** A refusal can then quote exactly what the bundle said, including integer-era values this reader does not speak, which would be unrecoverable once coerced to a number.
Source: shared/src/MantlePlace.Core/BundleManifest.cs

**Vector layers come from the host's own `hosts.revit.vectors` copy alone when present, in the host's frame, and from the shared lon/lat set otherwise.** The copy replaces the shared layers all or nothing, and a layer it lacks is null. When `readiness.vectors` is false, only hazard layers of other sources are read from the block and the shared set remains the fallback. The three flat shared-set members stay as the corpus pins them.
Source: shared/src/MantlePlace.Core/BundleManifest.cs, shared/src/MantlePlace.Core/BundleManifestReader.cs

**The steep-ground threshold is kept as the manifest's own text, such as `35.0`.** The zone key quotes policy rather than reformatting it, and its presence distinguishes pre-1.4.0 bundles from ones with no steep ground.
Source: shared/src/MantlePlace.Core/BundleManifest.cs

**Imagery marked absent only by an explicit `present: false` is distinguished from an absent block.** Telling a curator whose ETL produced no imagery to re-download sends them round a loop that cannot end differently.
Source: shared/src/MantlePlace.Core/BundleManifest.cs

**The vault join key is top-level `order_id`, falling back to `attribution.order_id` only when `attribution.job_id` sits beside it, and never the ETL job id.** The ETL job id changes every rebuild, so joining on it addresses the wrong row.
Source: shared/src/MantlePlace.Core/BundleManifestReader.cs

**Shared vector layers are selected by name, format and geometry family.** A layer split by family has one file per family, and taking the first geojson row for water would take the streams and drop every lake.
Source: shared/src/MantlePlace.Core/BundleManifestReader.cs

**`delivery.unit_system` is recorded but not refused when unknown.** Every scale decision comes from `linear_unit` and per-artifact units, and refusing would make bundles unimportable if the platform adds a third token.
Source: shared/src/MantlePlace.Core/BundleManifestReader.cs

**Flood and steep-ground layers are read from the host's own block only.** The shared GeoPackages are unreadable here, and flood's lon/lat would need a projection permitted only onto a UTM origin. Malformed flood facts cost only provenance lines.
Source: shared/src/MantlePlace.Core/BundleManifestReader.cs

**MPB 2.0 projected twins are planned as the old carrier was, absolute against the origin with the CRS checked on read.** A layer without a twin keeps the shared lon/lat GeoJSON, and twins have no manifest hash and ride the bundle's own verification.
Source: shared/src/MantlePlace.Core/BundleManifestReader.cs

**Artifact paths come from the host's own block first, then layout, then the detail block, and a block describing a different file has its metadata discarded.** Carrying `ftUS` units onto another file imports a site 3.28 times wrong.
Source: shared/src/MantlePlace.Core/BundleManifestReader.cs

### Cache keys

**The reserved-name check (empty, `.`, `..`) runs on the mapped stem, not the raw id.** Mapping leaves `..` unchanged, and it would still be a path traversal.
Source: shared/src/MantlePlace.Core/CacheKeySanitiser.cs

**Order ids are mapped per Unicode rune, with alphanumerics bounded to the Basic Multilingual Plane.** Every host then derives the same directory. A UTF-16 walk or a per-host astral table would drift and re-download bundles every session.
Source: shared/src/MantlePlace.Core/CacheKeySanitiser.cs

**The collision suffix hashes the raw order id, never the mapped stem.** Colliding ids already share the same mapped stem and would otherwise get identical suffixes.
Source: shared/src/MantlePlace.Core/CacheKeySanitiser.cs

**Hex encoding uses `Convert.ToHexString` plus `ToLowerInvariant`.** `ToHexStringLower` needs .NET 9, while the library targets net8.0 so Revit 2025 hosts can load it.
Source: shared/src/MantlePlace.Core/CacheKeySanitiser.cs
