---
name: adr-0029-unreal-plugin-auth-cache-and-vault-decisions
description: Decisions recorded from the code comments of the Unreal plugin runtime, covering sign-in and the loopback server, the shared credential store, the bundle cache, the vault client and materialize polling, and foliage scatter. Read before changing any of those parts of the Unreal plugin.
status: accepted
---

# 29. Unreal plugin auth, cache and vault: decisions recorded from code

Date: 2026-11-21

## Status

Accepted. Recorded from the code comments that held these decisions, when the comments were removed; each decision was already in force in the code and none is new.

## Context

These decisions lived in comments in the Unreal plugin runtime module (`unreal/MantlePlace/Source/MantlePlaceRuntime/Private/`): the sign-in and loopback server, the secret store, the bundle cache, the vault client and its logic, and foliage scatter. Comments are being removed from the code, so the decisions and their reasons are recorded here.

## Decisions

### Sign-in logic and browser pages

**The auth layer and the bundle cache carry their own SHA-256 implementation.** The engine's `FPlatformMisc::GetSHA256Signature` asserts on Windows in UE 5.8 and is an unimplemented stub on this build with no platform override, and the layer must run deterministically headless. It serves integrity, not secrecy, and is bounded by int32 `TArray` indexing. The RFC 7636 test vector verifies the auth use, and known-answer and streaming-equivalence tests pin the cache use.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceAuthLogic.cpp`, `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceBundleCacheLogic.cpp`, `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceBundleCacheLogic.h`

**The loopback redirect URI uses `127.0.0.1`, not `localhost`.** RFC 8252 section 8.3 recommends it, because it forces the loopback interface and avoids DNS and hosts-file surprises.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceAuthLogic.cpp`

**The sign-in browser pages use a dark ground and system fonts, with no external references.** The platform's own hand-off page is dark and a white page would read as failure. A brand typeface would need the network, and HPS-08 says the page must not depend on it.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceAuthLogic.cpp`

**The hand-off script probes the done URL with a no-cors fetch and navigates only when it gets an answer, and it ships on the success page only.** A meta refresh would land offline curators on a browser error page, so the hand-off is an upgrade and never a dependency (HPS-08). It never appears on a page that carries server-written text.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceAuthLogic.cpp`

**The tab separator is written as an HTML entity, and the close-tab line is its own paragraph on both pages.** The entity keeps the source ASCII and so avoids encoding mojibake. The separate paragraph is needed because server error text may lack a full stop.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceAuthLogic.cpp`

**HTML escaping replaces the ampersand first.** Escaping less-than first would produce an entity whose ampersand the later pass double-escapes, and the page would show literal entity text.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceAuthLogic.cpp`

**A refresh token is discarded only on a client-error status together with an error code that names the grant.** Network failures say nothing about the credential. Wrongly discarding costs a session, while wrongly keeping costs one extra sign-in.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceAuthLogic.h`

**PKCE randomness comes from CNG on Windows and `/dev/urandom` elsewhere, deliberately not OpenSSL.** OpenSSL headers redefine the type `UI`, which collides with a namespace `UI` in that translation unit.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceAuthSystemBase.cpp`

### Loopback server and port selection

**The callback port is OS-assigned by default.** Windows reserves moving blocks of ports for Hyper-V and WinNAT. A bind into one fails while HTTP.SYS reports the port as in use, so declared port lists fail unpredictably.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceAuthLogic.h`

**The callback port is probed on a plain socket before the HTTP server binds it.** `FHttpListener` asserts on port 0, so it cannot be asked for an OS-assigned port.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceAuthSystemBase.cpp`

**Ephemeral acquisition proposes a fresh port after each failed bind and does not retry one port.** The probe socket closes before the real listener binds, so another process can take the port in between.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceAuthLogic.h`

**The probe socket disables address reuse explicitly.** A project that enables `HTTPServer.DefaultReuseAddressAndPortEnabled` would otherwise let the probe share a port with another process and report a port it does not hold.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceAuthSystemBase.cpp`

**The process reuses its previous loopback port for later sign-ins.** `FHttpServerModule` keeps every bound listener until shutdown, so a fresh ephemeral port per sign-in would leak one listening socket each time.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceAuthSystemBase.cpp`

**HTTP listeners are started before candidate ports are probed.** `GetHttpRouter` only binds when listeners are enabled. Probing first returns routers for any port, the loop succeeds on the first candidate, and the later bind failure strands the browser.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceAuthSystemBase.cpp`

**On both a successful and a failed callback, the browser is answered first and the loopback server is torn down on a later tick.** Teardown is never synchronous from inside the server's own route handler, so the route is not unbound while it is still executing.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceAuthSystemBase.cpp`

### Shared credential store and token refresh

**The refresh-token storage key is spelled exactly as the Revit host spells it.** Both hosts read and write one credential file per OS user, and a one-character difference creates two sessions that never see each other.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceAuthSystemBase.cpp`

**The secret file lives in a per-OS-user Mantle Place auth directory, not per project.** A curator signs in once across projects and the Revit host shares the session. The filename mapping is the same HPS-30 mapping.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceSecretStore.cpp`

**The freshest stored refresh token is read before each refresh, and when memory and store disagree the store wins.** The other host may have rotated the token since this process last refreshed, so the in-memory copy would already be dead, and a rotating platform rejects a superseded token.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceAuthSystemBase.cpp`

**Before a refresh rejection is treated as final, the store is re-read.** If it holds a different token, the other host rotated first, and discarding the session would sign the user out of both hosts.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceAuthSystemBase.cpp`

**A cross-host lock serialises the shared credential file's read-modify-write, waits five seconds, then proceeds unserialised.** A dead lock holder is rare, and refusing sign-in would be a total failure.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceSecretStore.cpp`

**The store lock is a plain named mutex in the session namespace, without a `Global` prefix, and its name is shared rather than derived.** The Revit host's .NET `Mutex` of the same name then joins the same lock.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceSecretStore.h`

**The retry after a superseded-token rejection first leaves the renewal state, and no waiter is notified until the retry settles.** `TryRestoreSession` declines while a renewal is running.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceAuthSystemBase.cpp`

**A dead refresh token reuses the existing `SignOut` event and adds no new one.** The state table is a cross-host corpus fixture that the Revit and Unreal plugins must change together.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceAuthSystemBase.cpp`

### Vault client requests

**Only one token renewal is allowed between successful responses.** A platform that answers 401 to everything would otherwise cause renewal and replay forever.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultClient.cpp`

**Token renewal is single-flight.** When two operations see an expired token they trigger one refresh, because a rotating platform rejects the second as already used and that would discard a session that was never dead.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultClient.cpp`

**The presigned R2 download probe carries no client header and no `Authorization` header.** The URL points at storage, not the Platform API, and the presigned query string carries its own auth.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultClient.cpp`

**The presigned URL probe is a one-byte ranged GET, not a HEAD.** The R2 URL is SigV4-signed for GET, so a HEAD would fail the signature.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultClient.cpp`

### Whole-bundle and level downloads

**The whole-bundle download uses its own token and never the deprecated alias `glb`.** `glb` is also a real artifact format, so the platform would return that mesh, which then fails the archive sha256 check.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultLogic.h`

**The view request asks for level MAX for every host-block entry.** The platform takes one level per entry, and the MED and MIN tree points are row cuts of the MAX file, so a cached re-import needs no second download.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultLogic.h`

**When delivery state is unknown, only a view block proves the order complete, and a view that lacks an outside-the-host-block file the order has falls back to the whole bundle.** Partial views are never imported.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultLogic.h`

**A 3.0 download request names its own promised files by path and never a view.** A 3.0 bundle has no per-host file list for the platform to cut from.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultLogic.h`

**A level download is held against the length and manifest digest the platform states for it, not the listing's size and digest.** The listing's values describe the Max download of every file, not this host's files.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultLogic.cpp`, `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultLogic.h`

**A 404 `file_not_in_bundle` refusal is not an error.** The request is repeated naming only the remaining files. Any other refusal, or one that leaves nothing to ask for, fails.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultLogic.h`

**After download, the manifest decides whether a bundle is completed in the cloud, once per run.** The listing's completeness signal has been wrong before. A second attempt goes to the importer, to avoid looping.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultLogic.h`

### Materialize requests and polling

**Materialize sends the plugin's own explicit token list, not the server's `unreal` keyword.** The keyword expands to only four core tokens and would silently drop the vector and landcover layers the importer reads.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultLogic.h`

**Re-picking the remainder after a joined run ends short builds a body from an arbitrary token list.** The `unreal` keyword would re-ask for tokens already delivered, and the `all` keyword dispatches unwanted compute.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultLogic.h`

**An empty materialize token list is refused.** The platform treats it as repackage-only and answers 201, which leaves the host polling a run that builds none of what it waits for.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultClient.cpp`

**A listing that does not explicitly advertise a glb terrain mesh is treated as incomplete and routed through materialize.** This is safe because an already-materialized bundle answers a no-op with no job, not a coalesced job.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultLogic.cpp`

**Each of the five materialize start responses is recognised by its own marker, never by a missing `jobId`.** The noop and queued successes name no job, and a join without a job id is still followable by order.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultLogic.h`

**A 409 from materialize counts as success.** It signals a single-flight run already in progress, and its body carries the `activeJobId` to poll. Other non-2xx codes are genuine failures.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultClient.cpp`

**The in-flight run check comes before error-body parsing, and an empty job id still joins.** The single-flight response carries both a job fact and error prose, and polling is keyed on the order.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultLogic.cpp`

**A 503 with the code `delivery_state_unknown` is passed on as its own outcome meaning "try the download", not as an error.** The platform also reports it on complete orders when it cannot read what the bundle holds. It is read by code because status alone matches any outage.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultClient.cpp`, `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultLogic.h`

**Polling measures delivery against the effective token set echoed by the start response, not the host's own list.** The server has already deduped that set, and only it knows the expansion of the `all` scope.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultLogic.h`

**The status parser keys on the `delivered` field to recognise the delivery-state document.** `activeJob` is legitimately null on an idle order and the other fields are optional, so keying on them would misread idle as foreign.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultLogic.cpp`

**Completion is derived from delivered tokens against the requested set, because the status endpoint can return a delivery-state document with no status word.** Delivery proves production, while a completed job status can hide failed emits.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultLogic.h`

**Delivery derivation checks rows in a fixed order.** Unknown state comes first and is never Complete. Nothing outstanding is checked before a running job. A terminal attempt with tokens outstanding is a failure whatever it calls itself. The last row is Pending.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultLogic.h`

**An empty requested set yields no verdict.** "Nothing outstanding" is vacuously true against it, so falling through would report Complete for a bundle nobody asked anything of.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultLogic.cpp`

**The non-retryable materialize reasons mirror the platform's own set exactly.** Treating one as outstanding makes a finished bundle poll its whole budget and then time out.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultLogic.cpp`

### Manifest version handling

**Both the vault listing reader and the cache sidecar reader accept `manifestVersion` as the old JSON integer or the new string.** The vault spans both eras, and shipped plugins wrote numbers. Otherwise a pre-MPB bundle would stay valid in the cache and then be refused by the importer. The version is surfaced, not gated on, and an absent version stays unknown (HPS-20).
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultLogic.cpp`, `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceBundleCacheLogic.cpp`

**Cache version checks compare against the semver floor and not numerically.** Sidecars from the integer-version era sort below the floor. An unknown version stays valid and is not treated as old (HPS-20).
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceBundleCacheLogic.cpp`

### Bundle cache

**Cache validity uses the sha and size recorded in the sidecar at download time, not a re-hash and not the listing's whole-archive values.** Hashing a multi-gigabyte file on list refresh would block the editor, and comparing the recorded sha to the vault's current sha detects a re-cut. On-disk size would match itself and miss truncation.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceBundleCache.cpp`, `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceBundleCacheLogic.h`

**Order id sanitizing walks code points, not UTF-16 units.** Treating a surrogate pair as two characters yields two underscores where Revit yields one, which gives two cache directories for one order.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceBundleCacheLogic.cpp`

**Alphanumeric classification uses `FChar::IsAlnum` and is bounded to the BMP (HPS-30).** Everything above U+FFFF and unpaired surrogates are non-alphanumeric on every host, so non-Latin ids keep their cache path and no host needs its own Unicode table.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceBundleCacheLogic.cpp`, `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceBundleCacheLogic.h`

**A lossy order id sanitization appends a short hash of the raw order id.** Distinct ids such as `a/b` and `a:b` then do not collapse into one cache directory, while lossless UUIDs keep clean paths.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceBundleCacheLogic.cpp`

### Foliage scatter

**Foliage thinning hashes each point's position in whole centimetres, not its row.** A re-import that reorders the table then does not reshuffle which trees a forest keeps.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceFoliageScatterLogic.cpp`

**The PCG foliage volume uses generate-on-demand.** Generation happens in the editor at import and is saved with the level, never at load or at runtime, where the rasters' source data does not exist.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceFoliageVolume.cpp`

### Build

**`FJsonObject` is forward-declared in the vault logic header and not included.** A game-target build then cannot silently depend on the editor target resolving the include transitively.
Source: `unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultLogic.h`
