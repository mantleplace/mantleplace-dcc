---
name: adr-0020-revit-add-in-decisions-recorded-from-code
description: Decisions about the Revit add-in (ribbon, threading, staged import, failure handling, site and terrain import, planting families, probe) that lived in code comments and are recorded here when the comments were removed. Read before changing how the add-in builds its ribbon, handles Revit threads and transactions, or imports terrain, imagery, vectors and vegetation.
status: accepted
---

# 20. The Revit add-in: decisions recorded from code

Date: 2026-11-21

## Status

Accepted. Recorded from the code comments that held these decisions, when the comments were removed; each decision was already in force in the code and none is new.

## Context

These decisions lived in comments in the Revit add-in (`revit/src/MantlePlace.Revit.Addin`). Comments are being removed from the code, so the decisions and their reasons are recorded here.

## Decisions

### Ribbon and Account face

**The Account face is one command, not two.** A PushButton binds to one class for the session while the face acts differently by state. The state decision lives in the pure core's `AccountRibbon`.
Source: revit/src/MantlePlace.Revit.Addin/AccountCommands.cs

**A disabled command class backs the address row, and it reports success rather than throwing.** Only a PushButton can hold text inside a split-button dropdown, and an accidental click must do nothing.
Source: revit/src/MantlePlace.Revit.Addin/AccountCommands.cs

**References to the Account split button and its items are kept in static fields.** Dropped references made it impossible to update the ribbon's text or enabled state when the session changed.
Source: revit/src/MantlePlace.Revit.Addin/MantlePlaceApplication.cs

**`IsSynchronizedWithCurrentItem` is false on the Account split button, and the face tooltip goes on the push button.** This keeps the first item as the face instead of the last-used dropdown item. SplitButton tooltips are never shown.
Source: revit/src/MantlePlace.Revit.Addin/MantlePlaceApplication.cs

**The Account face keeps the command id `MantlePlaceSignIn` although it also opens About when signed in.** Recorded journals key on command ids, and renaming one would break playback.
Source: revit/src/MantlePlace.Revit.Addin/MantlePlaceApplication.cs

**The ribbon repaints on `ThemeChanged` only for the UI theme type.** The event also fires for the canvas theme, which is set independently, and repainting then would swap in an icon set nothing is drawn for.
Source: revit/src/MantlePlace.Revit.Addin/MantlePlaceApplication.cs

**A duplicate ribbon tab is treated as already present.** `CreateRibbonTab` throws when the .addin manifest is installed both machine-wide and per-user, and an unhandled throw would disable the whole add-in.
Source: revit/src/MantlePlace.Revit.Addin/MantlePlaceApplication.cs

**Tooltip vignettes have no size ladder, unlike glyphs and the mark.** Revit caps `ToolTipImage` at 355 pixels, leaving no headroom for a second size to choose between.
Source: revit/src/MantlePlace.Revit.Addin/RibbonImagery.cs

**Ribbon buttons given images are kept in a list.** A ribbon item is only as changeable as the reference kept, and a theme swap must repaint every item at once.
Source: revit/src/MantlePlace.Revit.Addin/RibbonImagery.cs

**The tooltip image is set through `RibbonItem.ToolTipImage`, not `PushButtonData`.** Theme changes then reach it. The Account split-button face cannot have one, because Revit's API disallows tooltips on SplitButton.
Source: revit/src/MantlePlace.Revit.Addin/RibbonImagery.cs

**`GetDpiForSystem` uses `DllImport`, not the `LibraryImport` source generator.** `LibraryImport` requires `AllowUnsafeBlocks` across the whole assembly, which is too broad a change for one trivial call.
Source: revit/src/MantlePlace.Revit.Addin/RibbonImagery.cs

### Resources and build

**Ribbon images use build action Resource, not EmbeddedResource.** Pack URIs resolve against the WPF resource table, which EmbeddedResource does not populate. This also removes a packaging copy step and blank-ribbon releases. The glob excludes `Resources/src`, which is documentation.
Source: revit/src/MantlePlace.Revit.Addin/MantlePlace.Revit.Addin.csproj, revit/src/MantlePlace.Revit.Addin/ResourceImages.cs

**Images are decoded OnLoad and frozen, and a decode miss is cached.** The stream closes and any thread can use the image. A missing render is a build defect, not something to retry.
Source: revit/src/MantlePlace.Revit.Addin/ResourceImages.cs

**Touching `Application.ResourceAssembly` registers the pack URI scheme.** `PackUriHelper` alone does not, and without it pack URIs fail with "The URI prefix is not recognized" when WPF is not yet initialised.
Source: revit/src/MantlePlace.Revit.Addin/ResourceImages.cs

**The Tree and Shrub Planting families are embedded in the assembly.** The deploy and package scripts carry only dll, pdb and json files. The planting step writes each family out under its family name before loading.
Source: revit/src/MantlePlace.Revit.Addin/MantlePlace.Revit.Addin.csproj

**The add-in compiles against the oldest supported Revit API and follows its runtimeconfig target framework.** A net8 project cannot reference a .NET 10 API (CS1705). Revit assemblies are referenced `Private=false` and never redistributed.
Source: revit/src/MantlePlace.Revit.Addin/MantlePlace.Revit.Addin.csproj

**The reported plugin version keeps the commit sha as build metadata.** A bare version cannot tell two dev builds between releases apart, and diagnostic logs are the only artifact that reaches maintainers.
Source: revit/src/MantlePlace.Revit.Addin/PluginVersion.cs

### Revit threads, dialogs and lifecycle

**Auth state changes, watcher events and host calls hop onto Revit's UI thread, or are posted to Revit's dispatcher, before touching ribbon buttons.** The session raises auth changes on pool threads, watcher events arrive on thread-pool threads, and a main-thread violation terminates Revit. The two host calls arrive off Revit's thread and never wait on it: the queue is set under the handler's lock, and raise and close are posted to the dispatcher. The ribbon follows the shared credential only at the next renewal.
Source: revit/src/MantlePlace.Revit.Addin/MantlePlaceApplication.cs, revit/src/MantlePlace.Revit.Addin/PrepareNotifier.cs, revit/src/MantlePlace.Revit.Addin/RevitExportImportHost.cs

**The auth session and related services are static accessors, one per process.** Revit constructs commands itself and allows no injection. Two sessions would hold separate tokens, so signing out of one would leave the other authenticated.
Source: revit/src/MantlePlace.Revit.Addin/MantlePlaceApplication.cs

**Session restore at startup is fire-and-forget on a pool thread.** Blocking `OnStartup` on the network would delay Revit's splash, and throwing would lose the ribbon. The worst case is a ribbon that starts signed out.
Source: revit/src/MantlePlace.Revit.Addin/MantlePlaceApplication.cs

**The dispatcher unhandled-exception net marks a fault handled only when a frame on the stack belongs to the add-in, judged from all frames, not the throw site.** Swallowing Revit's own exceptions would leave it running on state it had given up on. A plugin usually mis-drives a framework type, so the innermost frame is typically a System type.
Source: revit/src/MantlePlace.Revit.Addin/MantlePlaceApplication.cs

**Import windows are closed at shutdown, not at `ApplicationClosing`.** A save prompt's Cancel can undo closing and would leave Revit open with its import window gone. An unowned window would otherwise outlive Revit's main window.
Source: revit/src/MantlePlace.Revit.Addin/BundleImportEventHandler.cs

**The client-update notice and the About window's Logs button use a WPF message box, not a TaskDialog.** The notice can arrive from any call outside a Revit API context, and the About window runs outside one, where a TaskDialog is not allowed. The ribbon Logs command and the About window's Logs button share one function so both open the same folder.
Source: revit/src/MantlePlace.Revit.Addin/ClientUpdateDialog.cs, revit/src/MantlePlace.Revit.Addin/OpenLogsCommand.cs

**The vault listing wait in Open in Curator is capped at six seconds.** The command runs on Revit's thread, so a slower listing is treated as unavailable and Revit is never left frozen on a slow network.
Source: revit/src/MantlePlace.Revit.Addin/OpenInCuratorCommand.cs

**The vault listing is started with `Task.Run`.** Its continuation then never needs Revit's thread, which is blocked waiting on it and would otherwise deadlock.
Source: revit/src/MantlePlace.Revit.Addin/OpenInCuratorCommand.cs

**The size choices in the task dialog are capped at four.** A Revit task dialog holds at most four command links.
Source: revit/src/MantlePlace.Revit.Addin/OpenInCuratorCommand.cs

**`DefaultButton` is set only after the command links are added.** Revit rejects a default naming a link not yet present with "Corresponding button not found", and the dialog never shows.
Source: revit/src/MantlePlace.Revit.Addin/OpenInCuratorCommand.cs

**Provenance is read with `Schema.Lookup`, not by building the schema.** A project never imported into has none, and a read must not create one in the user's model.
Source: revit/src/MantlePlace.Revit.Addin/OpenInCuratorCommand.cs

**The sign-in command starts the flow and returns, handing it to a modeless window.** `Execute` runs on Revit's synchronous UI thread, and waiting for the browser round-trip froze the application for up to five minutes.
Source: revit/src/MantlePlace.Revit.Addin/SignInCommand.cs

**A sign-in click during a Refreshing session is answered with a message instead of starting a flow.** The state machine would refuse it and return Abandoned, which reads as Cancelled and looks like a dead button.
Source: revit/src/MantlePlace.Revit.Addin/SignInCommand.cs

### Staged import and its record

**An import is planned twice: once for everything, to offer the checklist, and again with the curator's choice.** Choices then reach early decisions. `ActiveImport` also owns the archive, because disposing it deletes the scratch directory that staged steps extract into.
Source: revit/src/MantlePlace.Revit.Addin/ActiveImport.cs

**Abandon ends an import on any unanticipated exception.** A staged import outlives one call. Without abandon the handler would believe an import is running and the window would refuse to close until Revit restarts.
Source: revit/src/MantlePlace.Revit.Addin/ActiveImport.cs

**Imports run one slice per ExternalEvent raise, with the next raise posted at Background dispatcher priority.** Revit repaints, and a Cancel posted at normal priority is handled before the next slice instead of depending on Revit's servicing order.
Source: revit/src/MantlePlace.Revit.Addin/BundleImportEventHandler.cs

**An undispatched step kind throws rather than logging and continuing.** Otherwise the import would be silently incomplete while the plan and summary read as success. `StagedImport` lets this exception stop the import.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.cs

**Planting is one transaction per chunk, with a yield after each commit.** Revit regains its message loop, Cancel works between chunks, and committed chunks persist. Each family chooses its own fallback.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.Vegetation.cs

**The import record is streamed to disk as the import happens, and importer trace lines are streamed the moment they are produced.** The slowest step spends many minutes in one commit, the summary is written only after the last step, and a hung or crashed session must still leave the last in-flight line. The record is best-effort and swallows only file-sink exceptions.
Source: revit/src/MantlePlace.Revit.Addin/ImportLog.cs, revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.cs

**The log file is truncated by the first line written, not when the import opens.** A window opened and closed without importing then does not erase the previous real run's record.
Source: revit/src/MantlePlace.Revit.Addin/ImportLog.cs

**Each streamed log line carries its own time stamp.** Steps commit for minutes while Revit reports not responding, and a curator needs to know whether a wait began four minutes or forty ago.
Source: revit/src/MantlePlace.Revit.Addin/ImportLog.cs

**The Import Bundle picker is skippable through an environment variable.** The command can then run unattended from a journal or test script without raising dialogs. This is the only way Toposolid, IFC link and project-position calls execute under test.
Source: revit/src/MantlePlace.Revit.Addin/ImportLocalBundleCommand.cs

**The unattended import is synchronous and log-only, with every layer.** A modeless window in a journal playback never closes, and nobody is there to tick boxes or cancel.
Source: revit/src/MantlePlace.Revit.Addin/ImportLocalBundleCommand.cs

**Refusals show a dialog only when someone is driving.** A TaskDialog raised during journal playback is never dismissed and would hang the run that exists to prove the import works.
Source: revit/src/MantlePlace.Revit.Addin/ImportLocalBundleCommand.cs

### Failure handling and transactions

**Every failure accessor is read fully before anything is deleted or resolved.** Deleting a warning invalidates its accessor, and an exception inside a failures preprocessor makes Revit abort the transaction with no visible reason.
Source: revit/src/MantlePlace.Revit.Addin/ImportFailureSwallower.cs

**`DeleteWarning` is called only for warnings.** Revit throws if handed an error, so the severity test in `Decide` is load-bearing, not defensive.
Source: revit/src/MantlePlace.Revit.Addin/ImportFailureSwallower.cs

**After resolving errors the handler returns `ProceedWithCommit`.** Returning Continue with an unresolved error hands it back to Revit's default handler, a dead-end modal dialog.
Source: revit/src/MantlePlace.Revit.Addin/ImportFailureSwallower.cs

**Rollbacks set the clear-after-rollback failure option, and every importer transaction opens through one helper that suppresses dialogs and sets it.** `ProceedWithRollBack` alone still lets failures reach the user, and transactions this plugin did not open, such as the IFC importer's, carry no such flag. Any step can post warnings with nobody to dismiss them, and stale failures would poison retries.
Source: revit/src/MantlePlace.Revit.Addin/ImportFailureSwallower.cs, revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.Plumbing.cs

**A session-wide `FailuresProcessing` handler exists during import, attached only per slice and unsubscribed afterwards.** Revit's IFC importer opens its own transaction inside `CreateFromIFC`, which a per-transaction preprocessor cannot reach. A hook left on between slices would intercept the curator's edits.
Source: revit/src/MantlePlace.Revit.Addin/ImportFailureSwallower.cs, revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.cs

**The sub-divide failure is identified by its raw GUID.** Revit 2025's API has no `BuiltInFailures` member for it. The id was observed in Revit 2027 only, and other versions are unverified.
Source: revit/src/MantlePlace.Revit.Addin/ImportFailureSwallower.cs

### Anchor, attribution, handover and ODbL content

**The project's anchor is re-confirmed before the first step creates anything.** Two import windows can be open at once, and the second would otherwise be placed against a position the first has since set.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.Plumbing.cs

**A project already holding a Mantle Place import keeps its survey position.** The bundle was built at an offset from it, and a second `SetProjectPosition` would move the survey point under the existing bundle.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.SharedCoordinates.cs

**The anchor position is read live from the project, and only the EPSG code is stored.** A moved survey point then moves the anchor. Revit has nowhere to keep the EPSG code.
Source: revit/src/MantlePlace.Revit.Addin/ProjectAnchorStore.cs

**The time zone is written after latitude and longitude, in the same transaction.** Revit recalculates the zone when either coordinate setter runs. With no published zone, the project's own is read first and restored.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.SiteLocation.cs

**ODbL content is held in a selection filter per order and group, and element Comments stamps stay unchanged.** A selection filter is the one Revit element that holds any kind of element under a readable name. Unchanged stamps let later imports still find earlier elements.
Source: revit/src/MantlePlace.Revit.Addin/OdblContainers.cs

**ODbL filing treats stamped elements present after a step and absent before as the step's output, and files them in a `finally`.** Reused elements stay put, and a half-failed step still files its leftovers.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.OdblContainer.cs

**The stamp filter covers every model category that can carry Comments, not only those this run stamped.** Later imports and plugin builds that stamp new element kinds are still caught by the one Comments rule.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.SiteContextView.cs

**The existing attribution view lookup includes view templates.** Revit refuses a view name that any view already has, template or not.
Source: revit/src/MantlePlace.Revit.Addin/AttributionNotes.cs

**The attribution view and provenance record are written in one transaction.** Each is how the next import finds the other, and a view rewritten under a failed record would lead to a duplicate note.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.Attribution.cs

**A container's live ids are checked against the document during handover preparation.** An element deleted earlier this session may still be listed in a selection filter.
Source: revit/src/MantlePlace.Revit.Addin/PrepareForHandover.cs

**Handover only ensures the manifest's credit is present and does not work out licence requirements.** Import-made views, materials and plans stay because they hold no ODbL data once its elements are gone.
Source: revit/src/MantlePlace.Revit.Addin/PrepareForHandover.cs

### Site context, IFC and CAD link

**Context buildings are copied with no transform, and the converted site model is closed before the first chunk.** Buildings then land where the link did, and no invisible document stays open if the import is abandoned.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.ContextBuildings.cs

**Building solids are cloned out of the converted site model and keyed by IFC GlobalId.** Nothing may reference the model after it closes, and an iterator cannot catch Revit exceptions around a yield.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.ContextBuildings.cs

**The companion .rvt is named per Revit version.** The order's cache is shared across versions, and one shared companion was upgraded in place by the second version, breaking the older project's link unrepairably.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.SiteIfc.cs

**The IFC conversion runs outside any transaction and before the already-linked check.** `CreateFromIFC` does not convert and throws if the companion is missing, and converting first rebuilds a companion deleted while the link stayed.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.SiteIfc.cs

**The site model opens with reference intent, not `OpenIFCDocument` defaults.** Parametric intent plus auto-join posted unresolvable "Can't keep elements joined" errors on a 6,148-building model and refused conversion.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.SiteIfc.cs

**An existing site link is matched by any companion of the IFC, not only this Revit's.** A link made by another version or older build still counts, and missing it would make `CreateFromIFC` throw on a linked path.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.SiteIfc.cs

**The CAD link sets its import unit even though the ETL writes `$INSUNITS`.** Without a unit when the header is absent, Revit imports at original scale and reads metres as feet.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.Terrain.cs

### Terrain, subdivisions, plans and vectors

**The terrain re-import guard runs before the artifact is extracted or parsed.** A second import otherwise laid a second ground and rebuilt the boundary set against it, and a re-import should not pay to unzip.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.Terrain.cs

**The document is regenerated after setting the new base plane.** A too-thin refusal then fires inside the transaction, where the swallower turns it into a rollback instead of a dialog at commit.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.Terrain.cs

**Smooth shading is always turned on for terrain, before the drape, and never turned off.** It is a document-wide display setting that decides whether ground or mosaic shows, and it sets the origin the photograph offsets are measured from. Every drape material is therefore anchored to its own element's bounding-box corner.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.Terrain.cs, revit/src/MantlePlace.Revit.Addin/TerrainProbeCommand.Surface.cs

**Polygon layers become toposolid subdivisions, not model lines.** The GeoJSON polygons have no elevation, and a subdivision projects onto and follows the terrain. The layers overlap and none takes precedence.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.Subdivisions.cs

**The outer loop is passed first and holes after it.** Revit reads loops in that order and otherwise returns one subdivision with the holes left out, in Revit 2025 to 2027, regardless of winding.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.Subdivisions.cs

**Loop building never throws and returns null for an unclosable ring.** Callers build loops outside per-polygon failure handling, and a throw would cost the whole layer. Subdivision loops are flat at z zero because they project.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.Subdivisions.cs

**Hazard layers are filled regions on a dedicated plan view, not terrain subdivisions.** Flood zones overlap land cover and roads, coincident subdivisions do not draw in cut order, and regions persist in the model.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.HazardPlan.cs

**A published polygon is drawn whole or refused whole.** A hole that cannot close would leave the region covering ground the published polygon does not, which would be a repair and not the published shape.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.HazardPlan.cs

**Road centrelines are DirectShapes, not Model Lines.** Draped roads are non-planar, and ModelCurves need a SketchPlane each, which would mean tens of thousands of planes or flattened roads.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.Roads.cs

**Published contours are one DirectShape each, in a single transaction.** The layer's stamp has no row to resume from. The toposolid's own contour display is never touched, because it is the curator's setting.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.Contours.cs

**Contour vertices are re-thinned against the last drawn point after the unit round-trip.** Revit throws on a Line shorter than its tolerance instead of failing quietly.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.Contours.cs

**An existing Published Contours subcategory is used unchanged.** Its colour and weight are the curator's Object Styles settings, and a re-import must not reset them.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.Contours.cs

### Imagery drape

**The drape duplicates the toposolid type and never edits it.** Texturing the project's own type would repaint every toposolid. The terrain step now puts the ground on the imagery type, so the expensive retype is rare.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.Drape.cs

**Smooth shading is decided and read back before the drape material is written.** Under smooth shading Revit measures real-world texture offsets from the bounding-box corner, and under flat shading from the project origin.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.Drape.cs

**Under smooth shading each subdivision gets its own material anchored to its own corner.** One material carries one offset. Under flat shading the origin is shared and so is the material, except per renderer keyword.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.Drape.cs

**Drapeable subdivisions are found by the stamp in Comments, unioned with remembered ids.** On a re-import the remembered list is empty, and a curator's or another order's subdivision must never be touched.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.Drape.cs

**The imagery layer split is guarded by reading layer 0's material, not by which import ran.** Otherwise a reused single-layer type from an older build keeps the photograph on every vertical face permanently.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.Drape.cs

**The bitmap goes in the generic schema's diffuse slot, and four real-world properties pin it to ground.** The diffuse slot is what Revit renders and shades realistic views from. The pinning lets the image survive terrain edits.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.DrapeMaterial.cs

**A document from a projectless template falls back to the library "Generic" appearance asset, asked last.** Such a document has no appearance assets. The library lookup costs seconds, which is far less than a whole-terrain retype costing minutes.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.DrapeMaterial.cs

**The drape-bound toposolid type is built at terrain creation, inside a rolled-back sub-transaction.** A post-hoc retype costs minutes on large terrains, and a failure must leave no half-made type behind.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.DrapeMaterial.cs

**Refusal is asymmetric on purpose.** A parent type failure means no drape and rolls the transaction back, while one failing subdivision is only an undraped patch that is counted, not fatal.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.DrapeMaterial.cs

**The terrain is retyped only when it does not already wear the drape type.** A retype rebuilds every element relation and took 409 seconds on a large toposolid, and same-type behaviour was never measured.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.DrapeMaterial.cs

**Subdivision material routes are chosen by asking the element, not the Revit version.** In Revit 2025 a site-boundary subdivision is typeless, so `ChangeTypeId` refuses it, and it takes an instance material parameter. In 2026 and 2027 it is a Toposolid with no material parameter, and a retype onto a duplicate type holds, so the drape retypes there.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.DrapeMaterial.cs, revit/src/MantlePlace.Revit.Addin/TerrainProbeCommand.SubDivisionMaterial.cs

**Typing a fresh cut for the drape is contained in one try and a rolled-back sub-transaction.** A failure then costs only that cut's fast path and never turns a created cut into a declined one.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.DrapeMaterial.cs

**A subdivision already on a layered imagery type only has its thin layer's material changed.** Splitting that layer again would stack a second one or be refused as too thin.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.DrapeMaterial.cs

**Texture distances are converted using the property's own declared unit and read back.** Appearance-asset distances are not obviously feet, and Revit may clamp values in ways that look like unit mistakes.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.DrapeMaterial.cs

### Planting families and tree placement

**Planting families are authored in code on Revit 2025, the floor.** A family loads only in the Revit that saved it and later ones. The run is not ready until measurement logs agree, since unlabelled dimensions would still save and place.
Source: revit/src/MantlePlace.Revit.Addin/PlantingFamilyAuthoring.cs

**Flex results are combined with non-short-circuit `&`.** Every flex then runs and is logged even after one measurement disagrees.
Source: revit/src/MantlePlace.Revit.Addin/PlantingFamilyAuthoring.cs

**A saved family is refused if it embeds a user-profile folder.** Revit writes the full save path into the file, which is committed to a public repository and carries the author's login name.
Source: revit/src/MantlePlace.Revit.Addin/PlantingFamilyAuthoring.cs

**The shrub dome is two stacked blends meeting at the widest circle, not a revolve.** Labelling a size needs a radial dimension on every sketch arc, which a revolve's profile lacks.
Source: revit/src/MantlePlace.Revit.Addin/PlantingFamilyAuthoring.cs

**Every arc in a sketch circle is labelled with the radius parameter.** A circle is two arcs each with its own radius, and labelling one leaves the other fixed so the loop tears.
Source: revit/src/MantlePlace.Revit.Addin/PlantingFamilyAuthoring.cs

**A planting family already in the project is used as it stands and never reloaded over.** A reload would lose a curator's render substitutions or materials. The cost is that newer family builds never replace it automatically.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.Vegetation.cs

**The tree fallback uses two extrusion-family primitives, not a revolve.** A revolve depends on which frame axis Revit treats as its axis. The crown top is a small radius because a zero-radius loop is not a curve loop.
Source: revit/src/MantlePlace.Revit.Addin/RevitBundleImporter.Vegetation.cs

**A tree's ground elevation is written to the instance's offset from its level, not the location Z.** A level-based family takes elevation from the level and treats the location Z only as a hint.
Source: revit/src/MantlePlace.Revit.Addin/TreeInstances.cs

**Family parameters are read by opening the family document with `EditFamily` and closing it unsaved, once per import and outside any transaction.** A project shows only type parameters, and `EditFamily` requires no open transaction.
Source: revit/src/MantlePlace.Revit.Addin/TreeInstances.cs

### Terrain probe

**The terrain probe is diagnostic scaffolding that rolls back every transaction.** That makes it safe in a curator's project. It is deleted once its base-plane, subdivision-material and smooth-shading questions are settled by recorded runs.
Source: revit/src/MantlePlace.Revit.Addin/TerrainProbeCommand.cs

**The noise-floor control tessellates at detail 0.3, not 0.0.** It must not land on the fine mesh's own vertices, which interpolate exactly and would report a zero floor, while 0.0 can collapse the face to its corners.
Source: revit/src/MantlePlace.Revit.Addin/TerrainProbeCommand.Surface.cs

**Surface comparisons always pass an explicit finest tessellation level, never the parameterless overload.** Separately triangulated sloped surfaces differ by chord height even when geometry is identical. The finest level shrinks but does not remove that error.
Source: revit/src/MantlePlace.Revit.Addin/TerrainProbeCommand.Surface.cs

**The top face is chosen by largest upward normal, never by index.** Revit does not document face order. The lookup lives in one copy so an unverified Revit API shape has a single place to fix.
Source: revit/src/MantlePlace.Revit.Addin/TerrainProbeCommand.Surface.cs
