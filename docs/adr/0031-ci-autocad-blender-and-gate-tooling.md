---
name: adr-0031-ci-autocad-blender-and-gate-tooling
description: Decisions recorded from code comments in the CI workflows, the AutoCAD add-in and its packaging, the Blender shim, the brand-asset tooling, the local-install module and the manifest-conformance and public-hygiene gates. Read before changing a workflow trigger, the AutoCAD or Blender import path, a deploy or packaging script, or a gate check.
status: accepted
---

# 31. CI, AutoCAD, Blender and gate tooling: decisions recorded from code

Date: 2026-11-21

## Status

Accepted. Recorded from the code comments that held these decisions, when the comments were removed; each decision was already in force in the code and none is new.

## Context

These decisions lived in comments in the CI workflows under `.github/workflows/`, the AutoCAD tree under `autocad/`, the Blender add-in under `blender/`, and the tooling under `tools/` (brand assets, local install, manifest conformance, public hygiene). Comments are being removed from the code, so the decisions and their reasons are recorded here.

## Decisions

### CI workflows

**Host add-in shells are deliberately not built in CI.** Hosted runners have no AutoCAD or Revit, so the AutoCAD add-in shell and Civil 3D part, and the Revit add-in shell, are compiled on developer machines and proven by launch or by the tester import gate. The shared pure core runs in CI as the plugin, and skipping the shells is intentional, not an oversight.
Source: .github/workflows/ci-autocad-tests.yml, .github/workflows/ci-revit-tests.yml

**The .NET 8.0 runtime is installed before 10.0, and the suite runs on both.** Installing 8.0 first keeps the 10.0 SDK from global.json the one that drives the build while both runtimes stay available to the tests. The Revit suite runs on both because an un-updated Revit 2025 or 2026 loads the add-in on .NET 8 and later versions load it on .NET 10.
Source: .github/workflows/ci-autocad-tests.yml, .github/workflows/ci-revit-tests.yml

**Blender CI pins Python 3.11.** Blender 4.2, the oldest supported version, embeds Python 3.11, and a newer Python would accept constructs that fail to import in the host. The shim is proven only by hand inside Blender.
Source: .github/workflows/ci-blender-tests.yml

**The brand-asset CI job is deliberately only half a gate.** The canonical mark is a private file, so committed PNGs cannot be regenerated or compared on a runner. Only the geometry rules in mark_geometry.py are tested.
Source: .github/workflows/ci-brand-assets.yml

**The manifest-conformance job is the consumer leg of the manifest contract.** Host consumers are verified against the newest published schema, which is never copied into this repository, so one source of truth is checked from both ends.
Source: .github/workflows/ci-manifest-conformance.yml

**Pull requests are checked only against the pinned verified-against.json and fetch nothing.** The platform publishing ahead of the pin is not the fault of the change under review. Live comparison runs on main, weekly and on demand.
Source: .github/workflows/ci-manifest-conformance.yml

**The generated Platform API client sync runs on main, weekly and on demand, never on pull requests.** It reads a live URL, and a platform deploy moves the spec without any change here.
Source: .github/workflows/ci-platform-api.yml

**The public-hygiene pull_request trigger lists the edited type explicitly.** The default types omit it, and without it a title or body edited after the checks went green would never be re-checked.
Source: .github/workflows/ci-public-hygiene.yml

**Untrusted text reaches the shell only through env, never interpolated into run:.** Pull request title, body and head_ref (attacker-controlled on forks), and issue and comment text (written by anyone on the internet), would otherwise give a hostile author a shell on the runner.
Source: .github/workflows/ci-public-hygiene.yml, .github/workflows/ci-tracker-hygiene.yml

**Commit messages are checked with `head --not base origin/main`, not base..head.** A branch that merged newer main would otherwise drag in older history containing messages that predate the gate.
Source: .github/workflows/ci-public-hygiene.yml

**Required status checks have no path filter on pull_request.** A path-filtered required check never reports for pull requests outside its paths, sits pending forever and blocks merging.
Source: .github/workflows/ci-revit-tests.yml

**The Windows run is a separate job, not a matrix leg.** Matrixing renames the required pure-core check and strands branch protection. Windows is needed because HttpListener binds differently there, which reproduces the loopback sign-in failure.
Source: .github/workflows/ci-revit-tests.yml

**Tracker hygiene detects private references in issues but cannot prevent them, and its report is redacted.** The text is already public when the job starts, and a comment quoting the token would be a second permanent copy.
Source: .github/workflows/ci-tracker-hygiene.yml

**The issue_comment trigger deliberately fires for pull request comments too.** They are as world-readable as issue comments, and ci-public-hygiene covers only a pull request's title and body.
Source: .github/workflows/ci-tracker-hygiene.yml

**The tracker-hygiene workflow skips comments authored by github-actions[bot].** Its own report comment would otherwise re-trigger it in an infinite loop. The author is the guard because the bot comment legitimately contains the rule's vocabulary.
Source: .github/workflows/ci-tracker-hygiene.yml

**A text-only CI gate enforces Unreal naming.** Nothing in public CI compiles the plugin, and a self-hosted runner would let fork pull requests execute on the build machine, so naming regressions would otherwise surface only in user outliners.
Source: .github/workflows/ci-unreal-naming.yml

**The layout check requires third-party Unreal content under Source/ThirdParty/.** Fab's review requires that placement and treats a licence text as the marker of third-party content.
Source: .github/workflows/ci-unreal-naming.yml

**mpb releases publish to PyPI through OIDC trusted publishing with no token stored anywhere.** Only the publish job may mint the identity token, and the pypi environment's reviewers approve each tag release.
Source: .github/workflows/release-mpb.yml

### AutoCAD build, packaging and deploy

**The AutoCAD tree takes no NuGet packages, and Autodesk APIs are referenced from a local install.** The pure core therefore restores offline on a hosted runner. Autodesk assemblies are never vendored or redistributed, and the directories are overridable.
Source: autocad/Directory.Build.props

**The .NET 8 build compiles against the oldest supported AutoCAD 2025 API.** A .NET 8 project cannot reference an assembly built for .NET 10 (CS1705), so an updated install needs AutoCadApiDirNet8 pointed at a .NET 8 API.
Source: autocad/Directory.Build.props

**Install.cmd wraps the installer script and runs it with Bypass under powershell.exe 5.1.** A .ps1 in a downloaded zip cannot be run by a curator, because execution policy Restricted and Mark-of-the-Web block it. Group Policy cases fall back to a manual copy.
Source: autocad/packaging/Install.cmd

**The AutoCAD shell references no Civil 3D assembly; the Civil 3D part is built beside it and shipped in the same folder, never referenced.** A reference would make AutoCAD resolve Civil 3D assemblies when the shell loads, and a wrong presence verdict would throw FileNotFoundException for AeccDbMgd. The shell therefore loads in plain AutoCAD and in Civil 3D's as-AutoCAD mode, and every part load failure is reported as text so the shell stays up. Autodesk assemblies use Private=false so none is copied into the bundle, being non-redistributable.
Source: autocad/src/MantlePlace.AutoCad.Addin/Civil3dPartLoader.cs, autocad/src/MantlePlace.AutoCad.Addin/MantlePlace.AutoCad.Addin.csproj

**The bundle manifest has one Components group per year.** A live run in Civil 3D 2027 loaded nothing from a single group holding all three years' requirements, while per-year groups, as Autodesk bundles use, loaded.
Source: autocad/tests/MantlePlace.AutoCad.Core.Tests/AutoCadBundleManifestTests.cs

**Debug symbols (.pdb) ship in the release bundle on purpose.** A stack trace from a curator's AutoCAD is worth far more than its size, and the plugin has no telemetry.
Source: autocad/tools/Package-MantlePlaceAutoCad.ps1

**The release zip is written entry by entry with forward slashes, not with Compress-Archive.** Windows PowerShell 5.1 stores backslash names that some extractors keep literally, so a bundle's Contents folder would load nothing.
Source: autocad/tools/Package-MantlePlaceAutoCad.ps1

**Invoke-Git discards stderr inside a function-scoped ErrorActionPreference.** Windows PowerShell 5.1 turns redirected native stderr into a terminating error under Stop, so a plain git call outside a repository would crash instead of yielding null. The function is duplicated in the AutoCAD deploy script, which ships alone in the release zip, and in the local-install module.
Source: autocad/tools/Deploy-MantlePlaceAutoCad.ps1, tools/local-install/LocalInstall.psm1

**Invoke-Git is a simple function, not an advanced one.** [Parameter()] or CmdletBinding would bind git's own -e, -c and -C arguments to PowerShell common parameters instead of passing them to git.
Source: autocad/tools/Deploy-MantlePlaceAutoCad.ps1, tools/local-install/LocalInstall.psm1

**The deploy refuses to run while acad.exe or accoreconsole.exe is open.** A locked plugin DLL makes the replace fail partway and leaves a silently mixed build, which is worse than a stale one.
Source: autocad/tools/Deploy-MantlePlaceAutoCad.ps1

**The deploy resolves the payload path only from PayloadDirectory, never from the parent of PSScriptRoot.** In a release the script sits in an extracted zip whose parent is a Downloads folder.
Source: autocad/tools/Deploy-MantlePlaceAutoCad.ps1

**The install stamp is written with an explicit no-BOM UTF8 encoding.** Windows PowerShell 5.1's -Encoding utf8 adds a byte-order mark that would corrupt the JSON for other readers.
Source: autocad/tools/Deploy-MantlePlaceAutoCad.ps1, blender/tools/Install-BlenderAddon.ps1

**The AutoCAD install stamp is written before the bundle files are copied.** A copy that fails partway still leaves a folder the script recognises as its own and may replace next time.
Source: autocad/tools/Deploy-MantlePlaceAutoCad.ps1

**The Civil 3D probe points GDAL at its own projlib for the call.** Another installation's proj.db on PROJ_LIB, such as the one PostGIS ships, breaks GDAL's EPSG lookup.
Source: autocad/tools/civil-3d-spike/Invoke-Civil3dProbe.ps1

### AutoCAD add-in

**Arrived orders are marshalled onto AutoCAD's UI thread before ArrivalNotifier runs, and every ArrivalNotifier member runs on that thread.** VaultNewsChecker.Arrived is raised on a thread-pool thread and the notifier touches windows, so MantlePlaceApplication hops threads before calling in and reads vault-open state after the hop. The UI dispatcher is captured in Initialize because that is the only moment the plugin is certain to be on AutoCAD's UI thread and to see the dispatcher whose unhandled exceptions it must guard.
Source: autocad/src/MantlePlace.AutoCad.Addin/ArrivalNotifier.cs, autocad/src/MantlePlace.AutoCad.Addin/MantlePlaceApplication.cs

**Both export-host calls must not wait on AutoCAD.** They arrive off AutoCAD's thread under the connected host's lock, so the slot is a lock-guarded field and the import is posted to the UI thread.
Source: autocad/src/MantlePlace.AutoCad.Addin/AutoCadExportImportHost.cs

**The sign-in task is deliberately discarded in the command.** The outcome is reported on the window and RunAsync lets no exception escape, and awaiting it in the command would reintroduce the AutoCAD freeze.
Source: autocad/src/MantlePlace.AutoCad.Addin/PluginCommands.cs

**The 426 client-update prompt is shown once per session, as a WPF message box.** Several calls can be refused within a second, and the prompt can arrive on the thread pool and needs no AutoCAD API context.
Source: autocad/src/MantlePlace.AutoCad.Addin/ClientUpdateDialog.cs

**A drawing with no geographic location is read as the none case, not an error.** AutoCAD signals it by throwing eNotInDatabase or eKeyNotFound.
Source: autocad/src/MantlePlace.AutoCad.Addin/DrawingObservations.cs

**The host id is set in a module initializer.** Shared Core and Client are one assembly for every .NET host, and the initializer runs before any add-in type is touched, so no static can reach the platform first.
Source: autocad/src/MantlePlace.AutoCad.Addin/HostRegistration.cs

**MantlePlaceApplication exposes static accessors and holds one session per process.** AutoCAD constructs the command class itself with no injection, and two sessions would each hold a token, so a sign-out in one would leave the other authenticated.
Source: autocad/src/MantlePlace.AutoCad.Addin/MantlePlaceApplication.cs

**The stored session is restored fire-and-forget.** Initialize runs on AutoCAD's thread during startup, and blocking on the network would delay AutoCAD. The worst case is the plugin starting signed out.
Source: autocad/src/MantlePlace.AutoCad.Addin/MantlePlaceApplication.cs

**The release year is mapped from AutoCAD's version through HostReleaseYear, never derived from the number.** AutoCAD's version does not count years: 2025 is R25.0, 2026 is R25.1 and 2027 is R26.0. An unsupported version gives an empty label rather than a guessed year.
Source: autocad/src/MantlePlace.AutoCad.Addin/MantlePlaceApplication.cs, autocad/src/MantlePlace.AutoCad.Core/HostReleaseYear.cs

**Every placed number is the bundle's, each part is one transaction, and everything is verified before drawing.** No coordinate is converted, offset or reprojected, and the drawing's units are never read or set. One transaction per part lets cancel land between parts.
Source: autocad/src/MantlePlace.AutoCad.Addin/OrderImporter.cs

**A LandXML surface is built only when its units and CRS match the order's frame and the drawing's units match.** For a 3.x file the stated linear unit and EPSG code must equal the order's frame, and the drawing's Civil 3D units must match the order's. Civil 3D converts the LandXML to drawing units on read, and a metre file in a feet drawing lands 3.28 times off. Nothing is assumed or scaled.
Source: autocad/src/MantlePlace.AutoCad.Addin/OrderImporter.cs, autocad/src/MantlePlace.AutoCad.Core/LandXmlSurface.cs

**No command waits on the network or throws out to AutoCAD.** A browser round-trip takes minutes and would freeze AutoCAD. Command names are registered in the shell and in the pure core, and the two lists must be kept in step.
Source: autocad/src/MantlePlace.AutoCad.Addin/PluginCommands.cs

**The ribbon panel is built in code on the plugin's own Mantle Place tab, found by identifier, not translated title.** A partial CUIX produces a second Add-ins tab. The panel is re-placed on first idle and on each WorkspaceLoaded because the host rebuilds the ribbon and drops added panels. Moving to the host tab is a single choice in Place.
Source: autocad/src/MantlePlace.AutoCad.Addin/PluginRibbon.cs, autocad/src/MantlePlace.AutoCad.Core/PlugInsPanel.cs

**The plugin version keeps the commit sha from the informational version.** Dev builds between releases share one version number, and the sha is the only way to tell deployed builds apart from a log file.
Source: autocad/src/MantlePlace.AutoCad.Addin/PluginVersion.cs

**Each import step is one transaction posted at Background priority, and the window runs on its own thread.** AutoCAD then pumps messages and the window's higher-priority Cancel is heard between steps and never inside one, while the window keeps painting.
Source: autocad/src/MantlePlace.AutoCad.Addin/WindowedImport.cs

**The import planner uses only block values and refuses on a coordinate-system mismatch.** It compares zones by AutoCAD's EPSG code, never a plugin-mapped spelling, and never sets the drawing's location or zone for the user.
Source: autocad/src/MantlePlace.AutoCad.Core/AutoCadImportPlan.cs

**A record DXF the reader cannot place exactly is refused alone, while other layers still go in.** An example is POINT entities in contamination and historic-places files. All files are read before drawing, so no layer is half placed.
Source: autocad/src/MantlePlace.AutoCad.Core/AutoCadRecordSet.cs

**Civil 3D presence is decided by AeccLand.dbx being a loaded module, never by files on disk, PRODUCT or AeccDbMgd.** The as-AutoCAD launcher runs the same executable and folder, PRODUCT reads AutoCAD in Civil 3D too, and AeccDbMgd is not yet loaded in its console, so only the module separates it from the as-AutoCAD launcher.
Source: autocad/src/MantlePlace.AutoCad.Core/Presence.cs, autocad/tools/civil-3d-spike/Shared/Presence.cs

### Blender add-in

**The first applied bundle becomes the scene anchor and stays at its offsets; later bundles move only by their small difference from the anchor.** 32-bit float transforms are too coarse at projected northings.
Source: blender/mantleplace_blender/shim.py

**A later bundle is moved at most 16384 m from the anchor.** Blender stores locations as 32-bit floats, whose spacing past 2**14 m exceeds a millimetre and would open seams between adjacent tiles.
Source: blender/mantleplace_blender/core/anchor.py

**Anchor and bundle coordinates are subtracted before unit scaling when their units agree.** The difference is then exact in the CRS's own unit, and rounding two large coordinates never opens a seam.
Source: blender/mantleplace_blender/core/anchor.py

**Scene-origin keys that Blender's GIS add-ons read are written only for a metre origin with a CRS code.** Blender's scene is in metres, and a foot origin would need a scaled copy of unpublished numbers.
Source: blender/mantleplace_blender/core/bundle.py

**A generated USD root layer sublayers Features over Context, with the stronger layer first.** Blender's importer opens only one file, and the Features file composes onto Context as a sublayer.
Source: blender/mantleplace_blender/core/bundle.py

**Extracted bytes go to a temporary name and are renamed only once their digest is verified.** A refused file then leaves nothing a later import could mistake for a good one.
Source: blender/mantleplace_blender/core/source.py

**The view layer is updated after moving imported objects.** Blender's world matrices stay stale until the depsgraph runs, which would make later position reads wrong.
Source: blender/mantleplace_blender/shim.py

### Brand-asset tooling

**Monogram matting uses a minimum-channel floor of 40.** The canonical mark holds exactly three colours with minimum channels 16, 0 and 255, so one comparison rejects tile and extrude with room for antialiasing.
Source: tools/brand-assets/mark_geometry.py

**The optical box scan uses row-wise bytes.translate and bytes.find, not a per-pixel loop.** The buffer is sixteen million pixels and the answer is needed in under a second.
Source: tools/brand-assets/mark_geometry.py

**Host renders recentre the monogram on its optical box with a 12 percent margin.** Without the extrude that balanced it, the canonical mark's uneven margins would render as a lopsided mark.
Source: tools/brand-assets/mark_geometry.py

**Edge hardening applies only at 24 px and below.** There stems are near one pixel and antialiasing blurs the mark into grey mush. Above that size the same hardening produces stairs.
Source: tools/brand-assets/mark_geometry.py

**Pixel rounding is half away from zero, not Python's half-to-even.** The committed binary renders then do not depend on pixel parity.
Source: tools/brand-assets/mark_geometry.py

### Local install

**The install verdict reports only whether main moved when origin lacks the install's branch.** This repository squash-merges, so a never-pushed branch and a merged-and-deleted one cannot be told apart. A squash-merged preview's installed commit is never on main and its origin branch is deleted, so the verdict must not read it as a healthy preview forever.
Source: tools/local-install/LocalInstall.psm1, tools/local-install/LocalInstall.Tests.ps1

### Manifest conformance gate

**Newer published versions are discovered from the platform's freeze ledger, not probed.** Semver gives no way to guess whether the next release is a patch, minor or major, and a missed bump is the silent failure the gate prevents.
Source: tools/manifest-conformance/check_manifest_conformance.py

**A schema fetch failure other than not-published exits 2 (could not check), not 1 (drift).** A CDN 503 must not read as a failed contract.
Source: tools/manifest-conformance/check_manifest_conformance.py

**A host's version floor is read by regex over source, not from a build-time export.** The gate then runs on a cheap hosted runner without Unreal or Revit. Hosts must update their entry when the constant moves.
Source: tools/manifest-conformance/check_manifest_conformance.py

**The main corpus orphan sweep excludes self-test/.** It is a nested corpus with its own index that check_selftest sweeps while honouring orphanFiles, which the main sweep would misreport as rot.
Source: tools/manifest-conformance/check_manifest_conformance.py

**The self-test orphan sweep is recursive and skips nested corpora.** The main sweep excludes the whole subtree, and anything shallower than the host readers' sweep would be a hole this gate misses.
Source: tools/manifest-conformance/check_manifest_conformance.py

**The coverage baseline is never written from a corpus failing integrity checks.** A case dropped for a bad appliesTo would be written missing and become permanently invisible to the ratchet.
Source: tools/manifest-conformance/check_manifest_conformance.py

**The corpus manifestVersion is kept verbatim, as int or string.** Self-test cross-checks then compare JSON values uncoerced, since coercion across the era break is the reader bug the gate exists to catch.
Source: tools/manifest-conformance/check_manifest_conformance.py

**Self-test fixtures are written in the dialect host readers speak, and the self-test index is cross-checked against the corpus pin.** Otherwise an accept fixture is refused at the version gate and passes for the wrong reason.
Source: tools/manifest-conformance/check_manifest_conformance.py

**A host whose version floor is an integer is rejected for a semver pin.** It compares numerically, reads a semver string as 0 and refuses every manifest it claims to be verified against, though ordering says the pin is in range.
Source: tools/manifest-conformance/check_manifest_conformance.py

**A newer PATCH version is exempt from drift only after the two published schemas are compared with prose and version stamp stripped.** A mislabelled editorial release is then caught rather than trusted on its number. Demanding re-pins for patches would contradict the spec.
Source: tools/manifest-conformance/check_manifest_conformance.py

**The conformance gate's spec fetch names its caller in the User-Agent.** The edge in front of the platform refuses Python's default user agent.
Source: tools/manifest-conformance/platform_cases.py

**Nested-unread self-test fixtures need a wrong-typed nested value.** A host reading through Unreal's coercing TryGet field functions reads 404 back as the string 404 and would pass the fixture.
Source: tools/manifest-conformance/test_check_manifest_conformance.py

### Public-hygiene gates

**The link scanner matches the inline link's target half and allows balanced parentheses.** Images inside links yield both targets, filenames with parentheses are not cut, and footnote definitions are not read as links. False findings get a gate switched off.
Source: tools/public-hygiene/check_docs_integrity.py

**The ADR index check counts only bold numbers in the CLAUDE.md bullet as entries.** The same paragraph mentions other ADR numbers in passing, and a mention is not a listing.
Source: tools/public-hygiene/check_docs_integrity.py

**The reference gate exempts exactly one path, its own test corpus, and not the directory.** The gate's own script must be held to the same prose rule, while the corpus has to contain forbidden strings.
Source: tools/public-hygiene/check_public_references.py

**The qualified-reference and private-sibling patterns are structural and kept disjoint from the bare form.** They wave through single-letter language versions like C-sharp 12 and allow only two tracked log filenames, so one token yields one finding.
Source: tools/public-hygiene/check_public_references.py

**The file surface allows PascalCase brand strings only from a closed, exact-case set matched as whole tokens or documented prefixes.** Any other tail is treated as a sibling repository name and refused.
Source: tools/public-hygiene/check_public_references.py

**Every HPS rule id cited in the repository must be stated in the published standard, and a rule counts as defined only when its id starts a statement followed by an em dash.** A cited id with no published text looks authoritative to a stranger but resolves nowhere, and unchecked rules decay silently. A loose mention would license citing unwritten rules. Examples of rule ids cannot appear in the script and live in the test corpus.
Source: tools/public-hygiene/check_public_references.py

**Findings that will be posted publicly redact the refused token.** The tracker gate comments on the offending issue, and quoting the token would make a second permanent copy that editing the issue cannot clean up. The CI log keeps it.
Source: tools/public-hygiene/check_public_references.py

**The hex-colour exemption reads forward from the hash in the line, not from the matched token.** The digit-only match stops at the first letter and would turn colours like 0B0C10 with a hash into false private-tracker citations.
Source: tools/public-hygiene/check_public_references.py

**The gate dispatches on whether the branch-name flag was given, never on its truthiness.** An empty value falling through to the tree scan would print a false green for a name that was never checked.
Source: tools/public-hygiene/check_public_references.py

**The commit-message hook reads the file with errors replaced.** An undecodable byte from a commit-encoding setting or a Windows editor must not crash the hook, while the ASCII patterns still scan.
Source: tools/public-hygiene/check_public_references.py

**The vocabulary gate skips accepted ADRs and the changelog.** Decision records are append-only and the changelog is a dated log, so a term retired later is not a reason to rewrite either.
Source: tools/public-hygiene/check_vocabulary.py
