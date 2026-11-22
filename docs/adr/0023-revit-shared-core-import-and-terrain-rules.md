---
name: adr-0023-revit-shared-core-import-and-terrain-rules
description: Decisions recorded from the comments in the shared .NET core (MantlePlace.Core) that the Revit plugin builds on: sign-in and realtime handling, provenance storage, DXF and TIN readers, terrain and subdivision identity, terrain base plane, surface checks, ribbon assets, family naming. Read before changing any of these rules.
status: accepted
---

# 23. The shared .NET core behind the Revit import: decisions recorded from code

Date: 2026-11-21

## Status

Accepted. Recorded from the code comments that held these decisions, when the comments were removed; each decision was already in force in the code and none is new.

## Context

These decisions lived in comments in `shared/src/MantlePlace.Core/`, the host-neutral .NET core that the Revit plugin builds from (see [ADR 0019](0019-plugins-share-a-common-core-a-dotnet-home-and-corpus-cases.md)). Comments are being removed from the code, so the decisions and their reasons are recorded here.

## Decisions

### Sign-in, session and realtime

**Whether a Prepare ended signed out is read when the Prepare ends, not from the auth event stream.** A token rotated by the other host looks like a brief sign-out, and a watch that reacted to it would drop itself wrongly.
Source: shared/src/MantlePlace.Core/PrepareNotices.cs

**Sessions renew proactively ahead of token expiry.** An idle connected host makes no route calls that would trigger 401 renewal, so its realtime channel would rejoin with a dead token and drop out of the vault every hour.
Source: shared/src/MantlePlace.Core/SessionRenewal.cs

**A refresh rejection is definitive only for a client-error status that carries a grant error code, never for prose; any unrecognised failure is transient.** Wrongly discarding a working credential is silent, while a needless retry is not.
Source: shared/src/MantlePlace.Core/TokenGrant.cs

**The vault's sign-in edge tracks the last state that was not Refreshing.** Refreshing is both startup restore, which is a sign-in, and the hourly token renewal, which is not, and treating them alike would trigger an extra vault listing every hour.
Source: shared/src/MantlePlace.Core/VaultNews.cs

**The realtime reconnect backoff is 1, 2, 5, then 10 seconds.** These match Supabase's own clients, so a platform tuned for those clients sees no new reconnect behaviour from this one.
Source: shared/src/MantlePlace.Core/RealtimeFrames.cs

**The Phoenix Realtime protocol is spoken directly, not through a Supabase SDK.** An SDK would load its own JSON and HTTP assemblies into Revit's process, where another add-in's copy decides which version loads.
Source: shared/src/MantlePlace.Core/RealtimeFrames.cs

**A refused join's reason is read from `reason`, `response` or `message` in the payload.** Realtime has been observed putting the explanation under any of those keys.
Source: shared/src/MantlePlace.Core/RealtimeFrames.cs

### Vault listing, selection and notices

**The manifest version in a vault listing is a string that spans both version families, and it is surfaced and never gated on.** The vault lists bundles at rest, where bundles from before the re-baseline report the integer 19 and later ones report 1.0.0.
Source: shared/src/MantlePlace.Core/VaultListing.cs

**Vault selection is carried by order id and returned as an index into the rows shown, not into the full listing.** Under a filter the two indexes disagree, and Prepare, Import or Remove would act on the wrong bundle.
Source: shared/src/MantlePlace.Core/VaultRows.cs

**Manifest readiness reasons are mapped to readable clauses and not printed verbatim.** The host's readiness vocabulary is open-ended (for example `emit_threw:stage`) and would expose internal stage names; an unknown token is quoted only when it is short and token-shaped.
Source: shared/src/MantlePlace.Core/ReadinessReasons.cs

**Two empty or whitespace SHA-256 digests compare as not equal.** An absent digest is unknown, and unknown equalling unknown would report a verified match for a bundle that nothing actually checked.
Source: shared/src/MantlePlace.Core/Sha256Digest.cs

### Project provenance in Revit ExtensibleStorage

**The provenance ExtensibleStorage schema is immutable once any document holds it, so any field change must use a new GUID.** Changing fields under the same GUID makes Revit refuse the definition in every project that already carries the old one.
Source: shared/src/MantlePlace.Core/ProjectProvenance.cs

**The schema's VendorId must equal the add-in manifest's own VendorId.** A mismatch compiles cleanly and fails only inside Revit, so the headless suite checks that they match.
Source: shared/src/MantlePlace.Core/ProjectProvenance.cs

**The attribution note text is stored verbatim and not rebuilt from the sources.** Rebuilding ties the note's identity to today's line format, and a later plugin that lays lines out differently would add a duplicate note.
Source: shared/src/MantlePlace.Core/ProjectProvenance.cs

### Published contours and vector layers

**The contours DXF reader ignores `$INSUNITS` and takes units from the manifest pointer.** One delivery tier has X/Y and Z in different units. The reader refuses unplaceable entities by name but skips TEXT and MTEXT labels, which otherwise made it refuse every file.
Source: shared/src/MantlePlace.Core/PublishedContourReader.cs

**Only the DXF ENTITIES section is read for contours.** A polyline inside BLOCKS is a block definition placed by an INSERT with its own transform, not a contour in world coordinates.
Source: shared/src/MantlePlace.Core/PublishedContourReader.cs

**Contour Z is the published elevation with nothing added, and no offset is computed to lift lines off the toposolid.** Segments shorter than Revit's short-curve tolerance are handled by skipping vertices near the last kept one, which keeps the line continuous.
Source: shared/src/MantlePlace.Core/PublishedContours.cs

**Polygon rings drop vertices within the short-curve tolerance of the last kept vertex, instead of dropping short edges.** Dropping an edge leaves its neighbours disjoint, and Revit refuses the loop as not contiguous.
Source: shared/src/MantlePlace.Core/SiteRings.cs

**One malformed GeoJSON feature is dropped while the layer survives, but malformed JSON or a missing features array fails the read.** A bad row then costs nothing, and a wrong pointer is not hidden as zero roads imported.
Source: shared/src/MantlePlace.Core/SiteVectorReader.cs

**Lon/lat layers are placed only into a UTM origin; on State Plane the layer is refused.** UTM is the only forward projection available, and projecting into the wrong coordinate reference system yields plausible but wrong coordinates, off by hundreds of kilometres.
Source: shared/src/MantlePlace.Core/SiteFrame.cs

### Road, tree and subdivision identity

**Roads are identified by a stamp that uses the one-based row position, not the name, and nothing deletes existing roads.** Road names repeat or are missing, and a curator may have moved or scheduled the roads (ADR 0004).
Source: shared/src/MantlePlace.Core/RoadIdentity.cs

**Tree stamps are per row, and nothing is deleted.** The tree step is chunked and a cancel leaves committed chunks, so a re-import finds them and creates the rest; curators may have moved or scheduled trees (ADR 0004).
Source: shared/src/MantlePlace.Core/TreeIdentity.cs

**The ground layer is part of a subdivision's stamp identity.** Land-use feature 1 must never read as land-cover feature 1 already cut. The `LandUse` and `LandCover` enum members remain so that subdivisions cut by earlier builds are still recognised and draped.
Source: shared/src/MantlePlace.Core/SiteBoundaryIdentity.cs

**Every ground layer is named explicitly in the stamp, and an unnamed one throws.** A default arm would give it the land-use stamp spelling, and re-imports would misread its subdivisions. Land use keeps its legacy spellings because they are identity.
Source: shared/src/MantlePlace.Core/SiteBoundaryIdentity.cs

**Subdivision identity is a string stamp in the Comments parameter, not a geometric comparison of curve loops.** Revit projects profiles onto the relief, so the loops differ from the flat ones drawn and a comparison would need an indefensible tolerance.
Source: shared/src/MantlePlace.Core/SiteBoundaryIdentity.cs

**Subdivisions are found for draping by their Comments stamp, not by a remembered element id.** Re-imports then repair un-draped patches. The bundle-stem half of the stamp keeps the plugin from touching a curator's subdivisions or another order's.
Source: shared/src/MantlePlace.Core/SiteBoundaryIdentity.cs

**Stamp ownership matching compares the whole prefix, including the trailing slash separator.** Cache-key stems are truncated hashes, and without the separator one stem that is a prefix of another would claim its subdivisions.
Source: shared/src/MantlePlace.Core/SiteBoundaryIdentity.cs, shared/src/MantlePlace.Core/TerrainIdentity.cs

**A subdivision whose Comments refused the stamp is named by the stamp it was cut with, not by its element id.** The material and type pair made for it is then found and reused by the next import that cuts that feature.
Source: shared/src/MantlePlace.Core/SiteBoundaryIdentity.cs

**The site context filter matches every element whose Comments begin with the stamp prefix, so every stamp this host writes must begin with it.** The headless suite enforces this. The view and filter are reused by name and never edited on re-import.
Source: shared/src/MantlePlace.Core/SiteContext.cs

**Ground material names use the surface's plain name first and a renderer keyword second.** One renderer reads the word water, while asphalt is documented to no renderer, and no renderer effect is claimed as verified without a licensed human check.
Source: shared/src/MantlePlace.Core/SiteBoundaryIdentity.cs

**Terrain is stamped with stem, build token and datum.** A re-import reuses the same ground, refuses an earlier build, never deletes, and never claims unstamped ground, because deleting a toposolid takes the curator's subdivisions with it (ADR 0004).
Source: shared/src/MantlePlace.Core/TerrainIdentity.cs

### Site companion file and site model

**The Revit-converted site `.rvt` companion is named per Revit version, and it stays beside the IFC under `extracted/`.** A newer Revit upgrades an `.rvt` in place with no downgrade, so a shared file would silently break an older project's link. Beside the IFC, Remove Download deletes it.
Source: shared/src/MantlePlace.Core/SiteCompanionPath.cs

**Companion recognition is shape-based: a `.rvt` beside the IFC with at most one extra component that has no dot.** Another Revit version may have created the link, and two IFCs that differ by one dotted component could otherwise mis-claim each other's file.
Source: shared/src/MantlePlace.Core/SiteCompanionPath.cs

**Dots are removed from the Revit version token after sanitising, and the token is re-suffixed with a digest of the raw string.** A dotted token would not be recognised as a companion, and sanitising alone would let different versions collide into one companion.
Source: shared/src/MantlePlace.Core/SiteCompanionPath.cs

**The companion path is built by hand and not with `Path.ChangeExtension`.** That method replaces only the last extension, so it would turn `Site.2025.rvt` into `Site.2025.2026.rvt` or mangle dotted IFC stems.
Source: shared/src/MantlePlace.Core/SiteCompanionPath.cs

**Context buildings are identified from the IFC entity type the platform published, not from the Revit category the IFC import files them under, and the terrain element is never copied.** A copied terrain would lay a second unselectable ground over the toposolid.
Source: shared/src/MantlePlace.Core/SiteModelReader.cs

### Import staging and slow-step notices

**The import runs as short slices, one per ExternalEvent raise, instead of one call.** Revit's document is reachable only on its own thread, and a long handler blocks repainting and Cancel. A single commit cannot yield, so it is one slice.
Source: shared/src/MantlePlace.Core/StagedImport.cs

**Slow import steps are announced, not made faster.** Simplifying the terrain worsens a worse defect, sampling is unmeasured, and skipping boundaries saves half at best. A single commit cannot report progress, so the line announced before the step is the only one.
Source: shared/src/MantlePlace.Core/SlowStepNotice.cs

**Slow-step notices state the measured time and this terrain's point count side by side, without scaling one into the other.** Cost against vertex count was measured at one point, and an interpolated duration would be a guess.
Source: shared/src/MantlePlace.Core/SlowStepNotice.cs

**Polygon-layer notices quote the layer's own measured row and do not explain how cost grows.** Explanations built from one run were wrong twice: first promising later layers the boundaries' speed, then misattributing cost to existing subdivisions (see [ADR 0015](0015-revit-subdivisions-are-for-built-surfaces.md)).
Source: shared/src/MantlePlace.Core/SlowStepNotice.cs

### Terrain smoothing and subdivision drape

**The import turns toposolid smooth shading on, places the photograph for it, and never turns the setting off for the curator; the log uses two sentences.** The setting is document-wide: `Toposolid.SetSmoothedSurface` and `IsSmoothedSurfaceEnabled` are static methods that take only the document. It changes where Revit measures a real-world texture offset from: the project origin per face under flat shading, the element's bounding-box corner under smooth shading. This was measured, not reasoned. The switch may be deliberate for the curator's other toposolids, so the notice tells them what it does to the photograph. The two mismatches show differently (an origin-anchored offset rendered smooth rolls the photograph into four quarters, and a corner-anchored offset rendered flat shatters into a mosaic), so the sentences must not swap their symptoms.
Source: shared/src/MantlePlace.Core/TerrainSmoothing.cs

**The subdivision drape route is chosen from the element's observed shape, never from the Revit version number.** In Revit 2025 subdivisions are typeless with an instance material, while in 2026 and later they are typed Toposolids that retype.
Source: shared/src/MantlePlace.Core/SubDivisionMaterial.cs

**A subdivision type name is never the material's raw name.** Under flat shading that name is the terrain's imagery type, and finding it would put the subdivision on the terrain's layer stack.
Source: shared/src/MantlePlace.Core/SubDivisionMaterial.cs

**A type whose top layer is already this plugin's thin photograph layer is re-pointed and never split again.** Splitting would stack a second thin layer or be refused, and 2026 subdivisions may start on any default type.
Source: shared/src/MantlePlace.Core/SubDivisionMaterial.cs

**The terrain type is chosen by the same function the drape splits (layer 0), must have a Structure layer, and the thinnest wins.** Total-width logic drifted from the drape, and thickness alone picked paths and the plugin's own type.
Source: shared/src/MantlePlace.Core/ToposolidTypeChoice.cs

### Toposolid base plane

**The toposolid base plane is chosen from the relief, not from the first level.** `Toposolid.Create` points build the top face, so points below zero need a lower base plane or Revit refuses with too-thin.
Source: shared/src/MantlePlace.Core/TerrainBase.cs

**Base-plane clearance is three times the minimum layer thickness.** It restates `DrapeLayering.Split`'s own floor, so a plane that clears the terrain always leaves a type the imagery drape can still split.
Source: shared/src/MantlePlace.Core/TerrainBase.cs

**Base-plane preference is the highest existing level that is low enough, then the lowest level with a negative offset, then a created level.** A created level appears in every view forever, and a very deep plane buries the terrain.
Source: shared/src/MantlePlace.Core/TerrainBase.cs

**Escalation to a dedicated level exists, and both arms stay reachable.** The API cannot show whether Revit checks too-thin against the later height offset or against the raw level, so the log records which one was accepted.
Source: shared/src/MantlePlace.Core/TerrainBase.cs

### Surface points, TIN and crop

**The three-point minimum for `Toposolid.Create` lives in the pure points reader.** It is a property of the file, and when it was checked in the shim it fired after the planner had already reported CanImport, where no headless test could reach it.
Source: shared/src/MantlePlace.Core/SurfacePointsReader.cs

**TIN Z is only unit-converted and never re-referenced.** Every artifact's Z is an absolute orthometric height, and subtracting anything from it would put the terrain underground.
Source: shared/src/MantlePlace.Core/SurfaceTinFrame.cs

**TIN faces are carried although `Toposolid.Create` takes only points and Revit re-triangulates.** The vertex placement is what the TIN buys, and the sanitiser needs face adjacency to tell a fill from flat ground.
Source: shared/src/MantlePlace.Core/SurfaceTinReader.cs

**Only 3DFACE entities in the ENTITIES section are read.** A 3DFACE inside BLOCKS is a definition placed by an INSERT with its own transform, and reading its corners as world coordinates scatters the terrain.
Source: shared/src/MantlePlace.Core/SurfaceTinReader.cs

**TIN vertices are interned by exact equality, not by tolerance.** Shared corners round-trip identically, a tolerance would weld distinct vertices on dense slopes, and the sanitiser reasons about bit-identity afterwards.
Source: shared/src/MantlePlace.Core/SurfaceTinReader.cs

**The crop window is never built from the DEM bounds.** Those are the over-hanging raster extent that the crop exists to discard, so cropping to them keeps every bad point and looks like a fix.
Source: shared/src/MantlePlace.Core/SurfaceCrop.cs

**The crop window uses the outer bounding box of the projected area-of-interest quadrilateral, not the inscribed rectangle.** Grid convergence tilts the quadrilateral, and the inner extremes measurably dropped far more good terrain.
Source: shared/src/MantlePlace.Core/SurfaceCrop.cs

**A missing crop on State Plane bundles is correct and must not be fixed in the host.** Projecting a WGS84 area of interest into State Plane is coordinate-system machinery outside the thin-client boundary; the remedy is an upstream manifest in the delivery coordinate system.
Source: shared/src/MantlePlace.Core/SurfaceCrop.cs

### Fill detection

**The grid guard detects a producer's bit-identical fill constant and does not judge terrain.** A neighbour-step rule would erase genuine relief, while 400 identical values beside 200 m of relief is diagnostic.
Source: shared/src/MantlePlace.Core/SurfaceGrid.cs

**The fill guard caps removal at a fraction of the surface and reports instead above it.** A guard able to eat the terrain is worse than the artefact it removes.
Source: shared/src/MantlePlace.Core/SurfaceGrid.cs

**Filled edge lines are evaluated as a whole run against the first line outside the run.** Scanning one line at a time compares two adjacent identical fill columns with each other and keeps both.
Source: shared/src/MantlePlace.Core/SurfaceGrid.cs

**Fill offset is measured by median, not maximum, and is shared with the TIN sanitiser.** Genuinely flat edge ground, such as a bay, often has a cliff, and a maximum would condemn it along with the fill.
Source: shared/src/MantlePlace.Core/SurfaceGrid.cs

**A fill group must touch the surface's outer boundary to be removed.** The defect is a raster sliver overhanging the area of interest, and a bit-identical plateau with no edge contact is a reservoir, car park or roof, which is terrain.
Source: shared/src/MantlePlace.Core/SurfaceTinSanitiser.cs

### Surface agreement check

**Mesh chord error is measured as a noise floor on the probed document, not assumed.** Independently tessellated identical surfaces disagree by chord height, and a reading means nothing without that number.
Source: shared/src/MantlePlace.Core/SurfaceAgreement.cs

**The one-millimetre agreement tolerance does not absorb tessellation chord error.** Independently tessellated surfaces can exceed it without differing, so readings between the tolerance and the chord height are inconclusive, not defects.
Source: shared/src/MantlePlace.Core/SurfaceAgreement.cs

**The barycentric inside-triangle test uses a small nonzero tolerance.** Samples come from the reference point set and land exactly on shared edges and vertices, and a strict test would report ground as absent.
Source: shared/src/MantlePlace.Core/SurfaceAgreement.cs

**Reference triangles are indexed in plan before measuring.** A linear scan scales as samples times triangles, and the caller deliberately tessellates finely to make the triangle count large.
Source: shared/src/MantlePlace.Core/SurfaceAgreement.cs

**Agreement and the noise floor share one traversal.** The floor is the yardstick for the deviation, so both must agree exactly on which samples count as measured; separate footprint tests could drift and compare different point sets.
Source: shared/src/MantlePlace.Core/SurfaceAgreement.cs

**Vertex sampling is strided across the surface, including endpoints, and is not a prefix.** Tessellation order is not random, and a prefix samples only the boundary, where artifacts could mask a mid-surface disagreement.
Source: shared/src/MantlePlace.Core/SurfaceAgreement.cs

**The noise-floor control samples at a coarser tessellation of the same face and never only the fine mesh's own vertices.** Those vertices interpolate exactly and would report a false zero floor.
Source: shared/src/MantlePlace.Core/SurfaceAgreement.cs

**Coincident samples are counted only over measured samples.** The plan match is a micron wide and the footprint test is not, so counting offered samples could make the floor unclassifiable and get it discarded.
Source: shared/src/MantlePlace.Core/SurfaceAgreement.cs

**The agreement verdict has three readings, with agreement tested first.** A large noise floor then never takes an agreement away, and a missing floor yields unclassified, not an unmeasured differs claim.
Source: shared/src/MantlePlace.Core/SurfaceAgreement.cs

### Planting families and tree rows

**Tree proportions live in the pure core.** The family's formulas and the DirectShape fallback must build the same shape, with only the published height and crown radius varying per tree.
Source: shared/src/MantlePlace.Core/TreeFamily.cs

**The tree height parameter is named Tree Height and the shrub's is named Shrub Height, never Height.** Planting families already have a built-in type parameter named Height, and Revit refuses a second parameter with the same name or making the built-in one per instance.
Source: shared/src/MantlePlace.Core/TreeFamily.cs, shared/src/MantlePlace.Core/ShrubFamily.cs

**The minimum tree dimension is two millimetres.** Revit refuses curves under its short-curve tolerance, and inside a family that refusal is a flex failure that can roll back the whole chunk's transaction.
Source: shared/src/MantlePlace.Core/TreeFamily.cs

**Each foliage family decides its own DirectShape fallback, and a type parameter where an instance one belongs is treated as missing.** Writing to a type would give every tree the last row's height while reporting success.
Source: shared/src/MantlePlace.Core/TreeFamily.cs

**The shrub is its own Planting family built from two stacked blends, not a revolve.** The family authoring command sets sizes through a radial dimension on each sketch arc, which a revolve profile lacks.
Source: shared/src/MantlePlace.Core/ShrubFamily.cs

**The shrub dome's apex radius fraction is never zero.** A Revit blend to a point is not a valid curve loop, so the shrub ends in a small flat top.
Source: shared/src/MantlePlace.Core/ShrubFamily.cs

**Tree rows that fail to parse or lack `ground_z` are dropped and counted, unlike the toposurface file, which fails closed.** One missing tree is visible and harmless, a terrain hole is invisible, and a zero ground value would be kilometres off.
Source: shared/src/MantlePlace.Core/TreePointsReader.cs

### Ribbon icons, tooltips and window labels

**Ribbon slots are measured in logical pixels, and the smallest committed render at least as large as the device need is chosen.** Scaling down is cheap and scaling up blurs, so nearest-size rounding would blur on scaled displays.
Source: shared/src/MantlePlace.Core/RenderSizes.cs

**An unreportable display scale is read as 100% and not rejected.** The pick runs while a window is built off a visual that may not be in a tree, and a throw there ends the Revit process.
Source: shared/src/MantlePlace.Core/RenderSizes.cs

**A 1e-9 epsilon is subtracted from the wanted size.** Exact cases such as 16 x 1.5 must not jump a render size because of binary floating-point rounding.
Source: shared/src/MantlePlace.Core/RenderSizes.cs

**The Account button has no glyph.** Its face is the mark, which comes from a private source that the public `Render-RibbonIcons.ps1` script cannot and must not render.
Source: shared/src/MantlePlace.Core/RibbonGlyphs.cs

**Separate light and dark icon sets are committed, rendered from the same SVGs, and glyphs ship in only two sizes.** One glyph colour is invisible on the opposite Revit theme, and two sizes limit committed binaries.
Source: shared/src/MantlePlace.Core/RibbonGlyphs.cs

**Icon file-name spelling must match the manually run render script, and the headless suite checks the directory against it.** Otherwise a rename on one side would surface only visually inside Revit.
Source: shared/src/MantlePlace.Core/RibbonGlyphs.cs

**Glyph file-name stems are written out explicitly and not taken from `Enum.ToString`.** Refactoring an enum member then cannot silently rename committed binary icon files that nobody regenerated.
Source: shared/src/MantlePlace.Core/RibbonGlyphs.cs

**Only two commands get tooltip vignettes, and the Account button is ineligible.** Revit's API says a SplitButton cannot display the extended tooltip image, and the Account face is a PushButton inside a SplitButton.
Source: shared/src/MantlePlace.Core/Vignettes.cs

**Vignettes ship one file per theme, capped at 355 pixels on the longest side, and a headless test checks PNG headers.** Revit silently clips or drops tooltip images over that size, and a header check is the only possible detector.
Source: shared/src/MantlePlace.Core/Vignettes.cs

**Revit window labels use Title Case to match Autodesk tabs, and the class is not named `WindowChrome`.** WPF's `System.Windows.Shell.WindowChrome` would cause CS0104 collisions that CI, which never builds the shim, cannot catch.
Source: shared/src/MantlePlace.Core/WindowLabels.cs
