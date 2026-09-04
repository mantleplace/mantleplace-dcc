# Mantle Place for Revit

The Revit plugin — host #2, and the Host Plugin Standard's first test outside Unreal. Revit is
maximally unlike Unreal (.NET, document-transactional, BIM semantics), which is exactly why it goes
second: it debugs the standard.

**Status:** all four layers landed — manifest reader, import core, browser sign-in, vault client and
bundle cache. The only unchecked box is a real Revit user completing sign-in → pick bundle → import
end to end. The local-zip path stays as the permanent fallback.

**Supported hosts:** Revit **2025, 2026 and 2027**, from one `net8.0-windows` build. See
[Build and test](#build-and-test) for why the compile target is the oldest of the three.

## What it does today

`Mantle Place ▸ Account ▸ Sign in` opens your system browser (`HPS-05` — never an embedded webview,
never a password field in Revit), captures the redirect on a `127.0.0.1` loopback listener bound
before the browser opens, and exchanges the authorization code with PKCE `S256`. The refresh token
is stored per-OS-user with DPAPI; the access token is memory-only and never written (`HPS-15`). A
machine with no secure store degrades to memory-only auth and the dialog says so, rather than
writing the token somewhere less safe (`HPS-16`).

`Mantle Place ▸ Bundles ▸ Open vault` lists the bundles you own, prepares their Revit deliverables,
downloads them and imports. It is **modeless**: a build can take ten minutes and Revit stays usable
throughout. Closing the window is not cancelling — only the Cancel button cancels; the ETL job keeps
running and reopening the browser rejoins it rather than queueing a second (`HPS-24`).

Downloads are written to `bundle.zip.part`, hashed, and renamed over `bundle.zip` only once they
verify (⛔`HPS-26`). Nothing is ever evicted automatically: a purchased bundle stays until you press
Remove (`HPS-44`).

`Mantle Place ▸ Bundles ▸ Import bundle zip` takes a bundle you already downloaded and:

- refuses anything below manifest **MPB 1.0.0**, and anything whose MAJOR is above the line it
  reads, naming re-download and plugin-update respectively rather than dual-parsing (`HPS-31`).
  The integer pre-history (v7–v19) is below the floor as a family: a bundle cut before the MPB
  re-baseline is not merely old, it is written in a dialect this reader does not speak;
- builds the terrain from the TIN in `Surface/Surface.dxf` — its vertices are placed adaptively,
  dense on slopes and sparse on flats, where `Surface/SurfacePoints.csv` is a perfectly regular
  lattice whose cells are cocircular and therefore degenerate to triangulate, so the triangulator
  picks slivers and fans by tie-break. It is also the cheaper of the two: on the bundle this was
  measured against, 75,203 TIN vertices against the grid's 80,940.
  ⚠ **This is not what made the imported ground read as faceted, and an earlier version of this
  list said it was.** The mosaic outlived the move to the TIN. `Toposolid.Create` takes points and
  re-triangulates, so a toposolid is a triangulated mesh whatever the vertex source is — the faceting
  is Revit shading per face, and the only thing that changes it is the smooth-shading setting below,
  which cannot be used on ground wearing a photograph;
- falls back to the points file when the DXF is missing, or when the bundle publishes no origin to
  reduce its absolute coordinates against — the points file is already local, so it needs none — and
  falls back again to linking the DXF as CAD when neither surface can be built. Whichever tier is
  used, it says which and why;
- links `Site/Site.ifc` as a coordinated reference;
- sets the survey point / shared coordinates from `hosts.revit.georeference.origin.projected` —
  this host's own block — falling back to `delivery.local_origin` on a bundle whose own block
  publishes no usable origin (`HPS-33`);
- draws the road centrelines from the `road_splines` vector layer as DirectShape linework, drapes
  the `land_use` boundaries onto the terrain as toposolid subdivisions, and places the trees from
  `Landcover/TreePoints.csv` at their published height and crown radius — the three rows that closed
  the Forma Site Design Add-In parity gap. All three are positioned from the same
  published origin as the survey point, and a bundle whose origin is in a CRS they cannot be brought
  into is **skipped with that reason** rather than placed ~2000 km out;
- drapes `Imagery/Drape.png` over the terrain as a real-world-scaled material texture — the last
  parity row — on a **duplicated** toposolid type, so the project's own type is
  never repainted. The rectangle the image is pinned to is not taken on trust: the only extent this
  host may read is undeclared by the published schema, so it is used only when the image's own pixel
  grid times `imagery.gsd_m` reproduces it, and refused with a stated reason when it does not;
- turns on Revit 2025's **toposolid smooth shading** and **anchors the photograph for it**. The
  faceting is not lighting: under flat shading Revit maps a real-world-scaled bitmap per face, in each
  face's own plane, so every triangle carries its own slice of the image and the ground reads as a
  mosaic that no view style, sun setting or self-illumination touches. Smooth shading maps it
  continuously — but measures `texture_RealWorldOffset` from the **element's bounding-box corner**
  rather than from the project origin, which is undocumented and is why a drape written for the
  origin renders as four quarters meeting at a cross the moment smoothing is on. Measured by
  exporting one view under both settings and matching every region against the published
  photograph: anchored to the corner, the photograph sits within 1.6 m of the truth everywhere, on
  smooth ground. So the import turns the setting on **first**, reads it back, and writes the offsets
  for the renderer that will draw them — the terrain from its corner, and every site-boundary
  subdivision from its own, each with its own material, since one material carries one offset. The
  log says which origin each was written for and names the ribbon switch
  (Massing & Site ▸ Model Site ▸ Toposolid Smooth Shading) with what turning it off will do to the
  imagery. The setting is project-wide, so the log names the documented costs too (surface patterns
  stop drawing, paint and graphic overrides are ignored), and the plugin never turns it off. Where
  Revit refuses the setting, the photograph is anchored to the origin instead and the log says to
  import again after turning smoothing on by hand;
- refuses to import anything at all when an artifact's bytes do not match the `sha256` its own
  manifest publishes, before a single element is created (⛔`HPS-26`);
- tells you what it did **not** import and why, using the manifest's own
  `hosts.revit.readiness.<path>.reason` where there is one (`HPS-36`).

Setting `MANTLEPLACE_BUNDLE_ZIP` names the zip up front and skips the file picker, so the import
runs unattended from a Revit journal or a tester script. An unattended run raises no dialog — it
writes `<zip>.mantleplace-import.log` beside the bundle instead, because a `TaskDialog` during
journal playback is never dismissed.

## Layout

```
src/MantlePlace.Revit.Core/    pure logic — no Revit API, no I/O, no NuGet; net8.0
src/MantlePlace.Revit.Client/  I/O that is not Revit — HTTP, cache, zip, secrets; net8.0
src/MantlePlace.Revit.Addin/   the Revit shim — transactions, ribbon; net8.0-windows
tests/MantlePlace.Revit.Core.Tests/   headless suite over Core AND Client; net8.0 + net10.0
```

Each layer is a **triad** (`HPS-02`): an impure host shim, a pure logic core, and a headless test of
the core. All protocol behaviour lives in the core, which is constructible and testable with Revit
not installed. That is what lets the conformance suite run on a hosted runner
(`.github/workflows/ci-revit-tests.yml`).

**Why three assemblies and not two.** The shim was carrying two different kinds of impure: "talks to
Revit" and "talks to the disk and the network". Only the first needs a licensed install to exercise,
and CI cannot build the shim at all — so folding the second into it left ⛔`HPS-26` (write to
`.part`, verify, rename) resting on `agent-review` alone, even though the rule names
`automation-test` as its second enforcer. Split out, `MantlePlace.Revit.Client` builds and runs on
the hosted runner like anything else.

## Build and test

**The shim compiles against Revit 2025's API — the oldest version supported, not the newest
installed.** Autodesk's .NET floors are Revit 2025 and 2026 on **.NET 8** and Revit 2027 on
**.NET 10**, and one `net8.0-windows` assembly built against the 2025 API loads in all three. The
reverse does not hold and the compiler says so: a net8.0 project referencing Revit _2027_'s
`RevitAPI.dll` fails with `CS1705`. So the referenced API version is what pins the supported range,
and aiming `RevitApiDir` at a newer install silently drops the older hosts — which is why
[`Directory.Build.props`](./Directory.Build.props) defaults it to 2025 and the shim errors with one
sentence when that install is missing.

Revit 2024 is deliberately out of range: it is .NET Framework 4.8, where `System.Text.Json` is a
NuGet package, and the tree takes no packages.

```bash
# The pure core, the client and the conformance suite, on both runtimes. No Revit needed.
dotnet run --project tests/MantlePlace.Revit.Core.Tests/MantlePlace.Revit.Core.Tests.csproj -f net8.0
dotnet run --project tests/MantlePlace.Revit.Core.Tests/MantlePlace.Revit.Core.Tests.csproj -f net10.0

# Everything, including the add-in shim. Needs Revit 2025 installed.
dotnet build MantlePlace.Revit.slnx

# Build against a different Revit's API — narrows what the output can load into:
dotnet build MantlePlace.Revit.slnx -p:RevitApiDir="C:\Program Files\Autodesk\Revit 2026"
```

If the SDK was installed per-user (`dotnet-install.ps1 -InstallDir "$env:USERPROFILE\.dotnet"`),
put that directory on `PATH` for the session — the machine-wide `dotnet` will report the pinned SDK
as missing.

## Loading it into Revit

```powershell
# Close Revit first -- a loaded add-in DLL is file-locked.
./tools/Deploy-MantlePlaceRevit.ps1
```

Builds the solution and installs `MantlePlace.addin` plus the assemblies into every supported
per-version add-ins folder, printing the timestamp of what it wrote:

```
%APPDATA%\Autodesk\Revit\Addins\2025\
%APPDATA%\Autodesk\Revit\Addins\2026\
%APPDATA%\Autodesk\Revit\Addins\2027\
```

`-Configuration Release`, `-RevitVersions 2025`, and `-SkipBuild` narrow it. Copying by hand works
too, and the script does nothing you could not do with Explorer — but hand-copying is how a machine
ends up running a plugin months older than the source tree. That failure is silent and it reads
exactly like a code bug: a fix present in `git` and absent in the symptom, with a file timestamp as
the only tell. **When a bug reproduces against code that already contains its fix, check the
deployed timestamp before anything else.**

`MantlePlace.addin` names the assembly without a path, so Revit resolves it beside the manifest.

**One build, three hosts, and that is a claim to be tested rather than assumed.** Before a release,
drop the same output into each folder above, launch that Revit, and confirm the ribbon appears and an
import completes. The 2027 leg is the one that matters most: it is the only one where a .NET 8
assembly is loaded by a .NET 10 runtime.

## Conformance

`revit` is registered in
[`tools/manifest-conformance/verified-against.json`](../tools/manifest-conformance/verified-against.json)
from this tree's first commit (`HPS-38`), with its version floor declared as a path plus a regex
(`HPS-39`). The floor has one home: `ManifestVersions.MinSupportedManifestVersion`. Moving or
renaming it means editing that entry in the same commit, or the gate goes red.

The suite reads [`corpus/index.json`](../tools/manifest-conformance/corpus/README.md) at run time
rather than transcribing vectors into C# literals (`HPS-40`). The reader's failure modes are
normative, and each was verified by making it fail on purpose:

- a corpus that cannot be found **fails** the suite; it never skips;
- a claimed group resolving to zero cases fails, and so does a case naming a missing vector file;
- an `expectations` key this host does not recognise fails it too, so a platform-side assertion
  cannot silently bind nobody;
- and a key it _does_ recognise, declared with the wrong JSON type, fails as well. That last one is
  a step past the Unreal reference: tracking which keys were actually read — rather than which are
  on an allow-list — is what catches a corpus typo that would otherwise assert nothing while still
  counting as covered.

Cases carrying another host's `appliesTo` are skipped. Today that binds Revit to the
**18 host-invariant `manifest` cases** — the version gate with its pre-history ladder and semver
near-misses, the base-bundle partial parse, the top-level `vector` pointers and the
materialization signals — plus this host's **own three
`appliesTo: "revit"` cases** (`manifest.revitArtifactHashes`, `manifest.revitOwnGeoreference`,
`manifest.reject.revitHashMissing`), which pin the `hosts.revit` manifest block described under
"The contract gap" below.

`revit` claims **all six groups**: `manifest`, `auth`, `vault`, `cache`, `digest`, `projection`.

**`projection` is claimed for one thing only**, per `HPS-45`: the WGS84 lon/lat → UTM forward
projection behind the `vector` layers (roads, site boundaries) — see `GeoProjection.cs` and
`ProjectionConformanceTests.cs`. Placement is different: the survey point is applied verbatim from
the manifest, and this host computes no easting, northing or zone of its own for it (`HPS-33`).

### Vector cases, and the one thing `HPS-46` does not reach

The asserted-keys rule binds `expectations` keys. A case whose `expect` is `vector` declares none —
the file itself is the payload — and every case in `auth`, `cache` and `digest` is a vector. So a
suite that drives row 0 of an eleven-row table passes, and the coverage ratchet records the case as
covered.

`VectorDocument` applies the same idea one level down: every leaf value in a vector file must be
read by a typed assertion, or the case fails **with the path**. Prose keys are exempt by an
enumerated list, which the proposed `HPS-46` amendment replaces with a naming convention. This is
this host's own stricter reading until that lands, it costs other hosts nothing, and it is proven by
`VectorDocumentSelfTests` making it fail on purpose.

**The same gap exists one level down again, inside `expectations`, and is NOT closed here.** The
asserted-keys rule binds top-level keys, so `vault.list.fullAndLegacy`'s `items` is asserted but the
per-row keys inside it are on the host's honour. This suite now reads every one of them. The one it
did not — `items[].tierLabel`, which `DeriveTierLabel` derives from the presence of `glb` and so can
only ever name the reference host — was a host-specific key on a host-invariant case, and has moved
to its own `appliesTo: "unreal"` case that this host skips as a unit. The nested gap itself is
still open.

## Configuration

**Nothing is required.** Sign-in and token refresh both reach mantle.place on compiled-in routes,
so a curator installs the add-in and signs in with no file to obtain and nothing to configure.

Refresh used to be Supabase-direct only, which meant a machine without the project URL and anon key
could sign in once and then lose the session at access-token expiry — reporting a misconfiguration
naming a file that no packaging step produced. Supabase-direct is still **preferred** when those two
values are present, so an install that already has them is unaffected:

```json
{
  "supabaseUrl": "https://<ref>.supabase.co",
  "supabaseAnonKey": "<anon key>"
}
```

Every other key in that file is an override for a compiled default (`webLoginUrl`,
`tokenEndpointUrl`, `refreshEndpointUrl`, `apiBaseUrl`, `loopbackPorts`, `callbackPath`,
`signInTimeoutSeconds`) and is how a dev build points at a non-production stack. `loopbackPorts` is
the `HPS-06a` override: leave it out and the OS assigns the sign-in callback port, which is what
keeps sign-in clear of Windows' shifting reserved ranges and lets Revit and Unreal be signed in at
the same time. Absent or malformed, the file is ignored and the
defaults stand — this is read during Revit's add-in load, where throwing costs the ribbon button and
explains nothing.

## The contract gap, and how it closed

The plugin will not derive placement values, and it will not read another host's block to get them
(`HPS-33`). Through v18 the packager computed the AOI centroid's easting/northing/EPSG in
every Revit emitter and then **discarded it** — it survived only in `mesh.origin` and
`unreal.georeference.origin`, neither of which is Revit's to read, and in `delivery.local_origin`,
which is emitted on the `local_ft` tier alone. So on every other tier there was nothing this host
was allowed to apply, and the model was imported not georeferenced at all.

**v19 closed it, and the fix was host-side.**
`revit.georeference.origin.projected` publishes the origin addressed to this host by name, and the
reader takes it as the primary source with `delivery.local_origin` as the fallback — own block wins
where both are present, which is what makes reading it a complete fix with no coordinated pipeline
release. Two traps sit under that:

- the origin's `linear_unit` is the **origin's**, not the artifacts'. They differ on the foot tiers,
  where the origin is State Plane feet and the files are international feet, so the unit travels
  with the coordinates rather than being assumed metric;
- `grid_rotation_deg` is zero by construction today — the emitters reproject per-vertex, so
  meridian convergence is already absorbed — but it is **read**, not assumed. The shared corpus case
  `manifest.revitOwnGeoreference` states a non-zero one on purpose, because a fixture that agreed
  with the old hardcoded zero would have been passed by it.

## Where the rules live

- Cross-host, binding: the Host Plugin Standard (`HPS-NN` rule IDs throughout this tree).
- Revit-specific: this folder. Import semantics — what a toposurface is, which topo path wins — are
  this host's own and deliberately not cross-host (`HPS-03`, `DOC-06`).
- Agent onboarding: [`CLAUDE.md`](./CLAUDE.md).
