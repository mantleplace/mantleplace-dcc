---
name: host-plugin-standard
description: The normative rulebook every Mantle Place DCC host plugin implements — the four-layer shape, sign-in and the secret store, the vault client, the bundle cache and its integrity rules, manifest consumption, the frames a host may place content from, and the conformance obligations that hold each host to them. Read before touching auth, the vault client, the cache, manifest consumption or placement in any host, and before starting a plugin for a new DCC host. Rule ids are `HPS-NN` and every one cited anywhere in this repository resolves here.
---

# Host Plugin Standard — what every Mantle Place DCC plugin must do

> **Reach for this when:** you are touching auth, the vault client, the bundle cache and its
> integrity rules, or manifest consumption in any host plugin — or starting a plugin for a new DCC
> host and needing the behaviour and conformance obligations it inherits unchanged.
>
> The single owner of the **behaviour every host plugin implements**. Cross-host: a Revit, Rhino,
> Blender or Max plugin is bound by this document **unchanged**, in whatever language fits the host.
>
> **Rule ids are `HPS-NN`, append-only, and stable.** They are cited from source comments, tests,
> READMEs and the conformance corpus throughout this repository, and `ci-public-hygiene` fails when
> one of them resolves nowhere here. That gate is the reason this document exists: the ids were
> being cited in files written for strangers while the text behind them was not published, which
> made them read as dangling references.

## What this document is, and what it is not

**It is the rulebook, and it decides.** Where an implementation and this document disagree, one of
them is a defect.

**It is not the bundle format.** How a Mantle Place Bundle is laid out, what the manifest contains
and what each field means is [`spec/`](../spec/README.md), which is the public specification of the
format and is authoritative on it. This document never restates a field's shape; it states what a
host must *do* with one — apply it verbatim, fail closed where the contract requires it, refuse a
version it does not support. Read them together: `spec/` says what is in the box, this says how a
conforming plugin opens it.

**It is not the sign-in contract's server half.** [`docs/platform-auth-contract.md`](platform-auth-contract.md)
states what the platform must serve — the routes, the token response, which rejections mean *this
credential is dead*. Sections 2 and 3 below are the client's obligations against that contract, and
the two are complements rather than overlaps. Neither supersedes the other, and neither restates it.

⛔ **The client code that implements sections 2 and 3 is closed to outside patches.** A defect in
PKCE, the loopback listener, the token grant, the auth state machine or a secret store is a private
security report, not a pull request — see [`SECURITY.md`](../SECURITY.md). A fix in a public PR *is*
the disclosure, and it lands before anyone can ship the mitigation. Publishing the rules does not
open that door; it exists so a reader can check the claims a README makes about them.

## Version history

Rule ids are append-only, so a rule's number never means two things. The suite has grown as each
host met the contract for real:

| | |
| --- | --- |
| **v1.0** | `HPS-01` … `HPS-45` — the original suite, written from the reference implementation's four layers |
| **v1.1** | `HPS-46` … `HPS-48` — reader semantics, materialization signals, error-body precedence |
| **v1.2** | `HPS-46a`/`HPS-46b` — vector-row and nested-expectation coverage, found by the second host completing the `auth`, `vault`, `cache` and `digest` groups; `HPS-46b` became ⛔ once the corpus obstruction it named was gone |
| **v1.3** | `HPS-06` requires an OS-assigned callback port, `HPS-06a` adds the declared-list override — the declared range it previously mandated was unbindable on any machine whose reserved blocks happened to cover it |
| **v1.4** | `HPS-24` rewritten over the platform's full four-outcome start response, `HPS-25a` added for delivery-derived polling. The old wording described a two-outcome start and a job-status poll, and **both hosts were built to it** — so a materialize that named no job read as a failure, and a poll carrying no status word read as five errors in a row |
| **v1.5** | ⛔`HPS-49` — the presign request states its payload explicitly. The same flow, once past the start and the poll, died on a schema rejection: one host had been sending an empty body and the other a deprecated whole-bundle alias. Both had been green throughout, because the corpus pinned presign RESPONSES and never the request |
| **v1.6** | ⛔`HPS-50` — a host's local install is a single slot that tracks `main`, and says what it holds. The installed Revit add-in on the maintainer machine predated a full day of ribbon commits and the consuming project's Unreal checkout sat twenty commits behind; nothing detected either, because nothing said what was installed |
| **v1.7** | `HPS-51` — the shared user-facing actions carry one set of words in every host, and a host construct carries the host's own noun. Unreal's panel said `Sign In` and `Sign Out`, Revit's ribbon said `Sign in` and `Sign out`, and Revit's vault window said `Remove download`; nothing said which of those words were shared, so each host went on naming the same actions by itself |
| **v1.8** | `HPS-51` gains an eighth action, the window that shows a bundle import running and stops it. The Revit import ran as one call on the host's thread, so Revit reported "not responding" for minutes with nothing to show and nothing to cancel; the staged import that fixed it has a window whose surface the reference host will need too, so its words were fixed here before the second host named them differently |
| **v1.9** | `HPS-51` gains a ninth action, choosing what a bundle import brings in before its steps run. A Revit import brought in every step the planner found, and a curator who wanted the terrain alone had no way to leave the rest out but to hold a bundle that lacked it; the checklist that fixed it is a surface the reference host's roadmapped picker will share, so its words were fixed here before either host named them — and `Layers`, the word both hosts' developers use for the rows, was kept off the screen, because it already names three other things in the glossary and two more in the reference host's editor |
| **v1.10** | ⛔`HPS-52` and ⛔`HPS-53`, plus `HPS-54` — place from the host block first, place only what can be shown to be in the host frame, and declare which kind of frame the host has; `HPS-45` narrowed in the same pass to a fallback that reaches a UTM origin and nothing else. The suite had assumed every host frame was a UTM zone and never said so, and two hosts met a State Plane foot delivery and did opposite things: Revit refused each layer it could not place and named the reason, while Unreal's tree-point reader subtracted its metric UTM origin from foot State Plane coordinates and reported success with every tree a thousand kilometres off site. Both were defensible readings of `HPS-33`, `HPS-35` and `HPS-45` as written, which is what made this a hole in the standard rather than one host's bug |
| **v1.11** | `HPS-51` gains the words for what a bundle is delivered in — unit system, linear unit, delivery CRS. Neither host said it anywhere: a curator learned an order was in feet by opening the zip, and an Unreal user on an imperial order met foot GIS and CAD files beside metric content with nothing on screen having said so. Both hosts were about to print the same three facts, so their words were fixed here before either did |
| **v1.12** | ⛔`HPS-53` names the one statement that stands in for an unstated unit: `delivery.linear_unit`, for a file the format says follows the delivered unit. Revit stored the tree points' `ground_z` as metres, and on a State Plane delivery the column is feet, so every tree stood hundreds of metres above its terrain; bundles cut before MPB 1.3.0 state no unit beside that pointer, and read literally the rule left a host a choice between refusing every such tree file and keeping the bug. The terrain's points had always resolved their unit this way, so the precedent was written down rather than invented |
| **v1.13** | ⛔`HPS-34`'s Revit row gains the MPB 1.3.0 own-copy pointers, `revit.drape` and each layer of `revit.vectors`, whose `sha256` the schema requires; the shared `vector` layers stay optional. The Revit reader began refusing a present own copy with no hash in the change that first placed from it, and the table still listed only the three v19 deliverables |
| **v1.14** | `HPS-51` gains a tenth action, saying before a bundle import runs what the bundle holds and the import cannot offer. Revit's checklist was built from the plan, so a layer the planner skipped never became a row: the curator saw a shorter list and no reason, and read the reason only in the closing report, in the planner's words, EPSG codes included. The list that fixed it speaks to the user and leaves the technical sentence to the log, and the reference host's roadmapped picker will meet the same bundles, so its words were fixed here first |
| **v1.15** | `HPS-55` — a vault listing nobody asked for is bounded. Revit began telling the curator about orders they never prepared from it, which means listing the vault in the background on every signed-in session; nothing in the standard bounded a request the curator did not make, and the only polling rule, `HPS-25`, is about a job someone is waiting on |

Every one of those is a rule that existed only after something shipped wrong, which is why the text
keeps the failure attached to the rule rather than stating the rule alone.

**Pinned contract version:** never restated here — the pin is machine-checked in
[`verified-against.json`](../tools/manifest-conformance/verified-against.json) beside the
conformance gate.

---

## 0. What this document is, and why it is a standard rather than a template

Mantle Place ships one contract to N consumers. The Unreal plugin is the reference implementation:
its four layers each follow a triad — an impure host shim, a pure logic core, and a headless test —
and the pure cores are essentially plain C++ that happens to carry the whole protocol. This
document is a **transcription of those cores into host-invariant specifications**, plus the
machinery that holds every host to them.

We deliberately share **specifications and a conformance suite, not code**. Hosts span C++,
.NET, Python and MaxScript; a shared native core buys little and multiplies FFI maintenance. What
N consumer legs actually need is to be _held_ to one shape — which is why this is normative and why
the numbers below are testable rather than illustrative.

The values, tables and known answers referenced throughout live in
[`tools/manifest-conformance/corpus/`](https://github.com/mantleplace/mantleplace-dcc/blob/main/tools/manifest-conformance/corpus/README.md). Prose
describes the rule; the corpus is the executable form of it.

> **A note on what is dangerous here.** Most of the ⛔ rules below guard the same failure class:
> the plugin appears to work. A dropped false northing, a `null` sha256 read as "corrupt", a
> refresh token wiped by a grant that simply omitted one, a manifest value re-derived instead of
> applied — each of these produces a build that imports successfully and is wrong. There is no
> crash to debug and no red test. That is why they are rules and not advice.

---

## 1. Shape (`HPS-01` … `HPS-03`)

⛔ **`HPS-01` — Every host plugin implements four layers: auth, vault client, bundle cache, and
manifest consumption.** A host may land them in any order — Revit's build order is manifest-first
— but a plugin is not complete until all four exist, and the conformance obligations in §7
apply from the **first commit**, not from completion.

_Enforcer:_ `pr-review`.

**`HPS-02` — Each layer is a triad: an impure host shim, a pure logic core, and a headless test of
the core.** All protocol behaviour — URL construction, parsing, state transitions, validity
decisions — lives in the pure core, which MUST be constructible and testable **without the DCC
application running**. The shim owns only I/O, threading and host API calls.

This is not a style preference. The pure cores are what make the protocol reviewable in a diff and
what make the shared corpus runnable at all: a rule that can only be exercised by launching Revit is
a rule that is exercised once a month.

_Enforcer:_ `automation-test` (per host) + `agent-review`.

**`HPS-03` — The import layer is the host's own, and hosts stay dumb consumers.** Conversion
complexity lives web-side, in per-host-ready emitters; the plugin consumes pre-derived
artifacts. A host plugin MUST NOT carry a geoprocessing stack (GDAL and equivalents are explicitly
rejected) or re-implement conversion the ETL already performed. Beyond the manifest block it
consumes, the import layer's semantics — what a Landscape is, what a toposurface is — are
host-specific and **out of scope for this standard**; they belong to that host's own suite.

_Enforcer:_ `agent-review`.

---

## 2. Authentication (`HPS-04` … `HPS-13`)

Spec source: `FMantlePlaceAuthLogic`
(`unreal/Plugins/MantlePlaceDcc/unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceAuthLogic.{h,cpp}`).
Corpus: `auth.pkceVectors`, `auth.callbackQueryVectors`, `auth.tokenResponseVectors`,
`auth.stateMachine`.

⛔ **`HPS-04` — Sign-in is OAuth 2.0 Authorization Code with PKCE, method `S256`
([RFC 7636](https://www.rfc-editor.org/rfc/rfc7636)).** The code verifier is **≥ 32 bytes of
CSPRNG output**, base64url-encoded per RFC 4648 §5 with `+`→`-`, `/`→`_`, and **all padding
stripped** (32 bytes → 43 characters). The challenge is `base64url(SHA256(utf8(verifier)))`.
`plain` is never used.

Verify against the RFC 7636 Appendix B pair in `auth.pkceVectors` before trusting any
implementation. An encoder that leaves `+`, `/` or `=` in place produces a verifier the server
rejects only at the exchange step, long after the flow looks correct.

_Enforcer:_ `automation-test` + `agent-review`.

**`HPS-05` — Authorization happens in the user's system browser.** Never an embedded webview, and
never a password field in the host UI. Password grant, where it exists at all, is off by default.

_Enforcer:_ `agent-review`.

**`HPS-06` — The redirect target is a loopback listener on the literal IP `127.0.0.1`**, not
`localhost` ([RFC 8252 §8.3](https://www.rfc-editor.org/rfc/rfc8252#section-8.3)). **The port is
chosen by the operating system**, and `redirect_uri` is built from the port that actually bound —
never from one the host intends to bind. The host MUST **start the listener before opening the
browser**; if no port binds, the flow fails with a message naming that cause rather than opening a
browser that will redirect into nothing.

**A declared port range is not conformant as a default.** On Windows, Hyper-V/WinNAT reserve
~100-port blocks that move across reboots; a bind inside one is refused although nothing is
listening, and HTTP.SYS reports that refusal as *"the process cannot access the file because it is
being used by another process"*. So a declared range fails unpredictably **and misdiagnoses
itself** — the reference range `51000`–`51009` sat entirely inside the observed block
`50932`–`51031`, which took sign-in down outright while `netstat` showed the ports free. An
OS-assigned port cannot land in a reserved range, because the allocator skips its own exclusions.

It is also what lets two hosts sign in at once. A fixed list is shared by every host on the
machine — Revit, an Unreal editor, a second editor — and at least one reference host holds its port
for the life of the process, so the list is consumed rather than borrowed.

Where the platform's HTTP server cannot bind port `0` directly — Unreal's `FHttpListener` asserts on
`InListenPort > 0` — the host probes an OS-assigned port on a plain exclusive socket, closes it,
binds the server to that number, and on failure **retries with a freshly proposed port, never the
same one**. A host whose HTTP layer retains listeners for the process lifetime MUST reuse the port
it already bound rather than proposing a new one per sign-in, or it leaks a socket per sign-in.

The failure message MUST NOT attribute the failure to another session of the same plugin unless the
host actually observed one. The OS error says "in use"; the host knows better and must not repeat it.

_Enforcer:_ `agent-review`.

**`HPS-06a` — A declared port list is an explicit opt-in override, never a fallback.** A host MAY
expose configuration pinning the callback to declared ports (Unreal `LoopbackPorts`, Revit
`loopbackPorts`), for a deployment whose `redirect_uri` validator holds a literal allow-list, or for
an endpoint-security product that hooks loopback listeners. When set it **replaces** OS selection
outright — it is not tried after OS selection fails, because a second path failing for a different
reason turns the first path's error message into a guess. `51000` remains the reference value and
the corpus's format example.

The default exchange route applies the [RFC 8252 §7.3](https://www.rfc-editor.org/rfc/rfc8252#section-7.3)
loopback check generically, with **no port allow-list**, so OS-chosen ports are already legal there.
A provider-direct exchange is the case this override exists for: a per-URL Redirect URLs list needs
`http://127.0.0.1:*/callback` or it cannot accept an OS-chosen port at all.

_Enforcer:_ `agent-review`.

⛔ **`HPS-07` — The `state` parameter is CSPRNG-generated, compared case-sensitively, and an empty
expected state is never valid.** `expected == received` is a match only when `expected` is
non-empty. The loopback port is reachable by anything on the machine; a host that treats `"" == ""`
as success accepts any callback that arrives.

On mismatch the flow fails as a possible CSRF, and the authorization code is discarded.

_Enforcer:_ `automation-test` (corpus `auth.callbackQueryVectors.stateValidation`).

**`HPS-08` — The callback is single-consumption, and the browser always gets a page.** The first
matching redirect latches; later or duplicate redirects receive the success page and are otherwise
ignored. Success and failure both render a small self-contained HTML page telling the user to close
the tab. Any message interpolated into that page is HTML-escaped (`&`, `<`, `>` — in that order).
Failure precedence is: an explicit `error` parameter, then state mismatch, then a missing code.

_Enforcer:_ `agent-review`.

**`HPS-09` — Browser sign-in has a timeout, and timing out is a cancellation, not a failure.** The
reference uses 300 s. On expiry the listener is torn down and the state machine takes the `Cancel`
path — a user who wandered off returns to a signed-out plugin, not a latched error.

_Enforcer:_ `agent-review`.

⛔ **`HPS-10` — A grant response never degrades the session.** Two specific rules, both silent
failures:

- **`expires_in` that is absent or ≤ 0 is replaced by the default lifetime** (3600 s). Stamping
  `now + 0` marks a freshly-minted session as already expired, and the plugin logs the user out
  between one call and the next.
- **A response that omits `refresh_token` keeps the prior one.** `chosen = new.isEmpty ? prior :
new`. Wiping the cached token on a refresh that simply did not re-issue one costs the user their
  session at the next restart, hours later, with no visible cause.

_Enforcer:_ `automation-test` (corpus `auth.tokenResponseVectors`).

**`HPS-11` — Expiry is evaluated with a skew of at least 60 seconds, and the boundary is
inclusive**: `expired ⇔ (now + skew) >= expiresAt`. The skew exists so a token does not expire
mid-flight on a slow request.

_Enforcer:_ `automation-test`.

**`HPS-12` — Auth state is a five-state machine** — `Unauthenticated`, `Authenticating`,
`Authenticated`, `Refreshing`, `Failed` — driven by the transition table in corpus
`auth.stateMachine`. **An event with no matching transition leaves the state unchanged.** Notably:
`SignOut` returns to `Unauthenticated` from any state; a second `BeginSignIn` while one is in flight
is ignored rather than queued; `Cancel` never latches `Failed`.

Hosts drive the corpus table rather than re-deriving the guards. An out-of-order callback must not
be able to corrupt the session.

_Enforcer:_ `automation-test` (corpus `auth.stateMachine`).

**`HPS-13` — Session restore is silent about absence and forgiving about network failure.** No
stored token is _not_ an error and causes no state change. A restore attempt that fails clears
in-memory tokens but **deliberately keeps the persisted refresh token** — a network blip at startup
must not force a re-login. Every _successful_ grant of any kind re-persists the refresh token, so
the stored copy never goes stale.

_Enforcer:_ `agent-review`.

---

## 3. Secret store (`HPS-14` … `HPS-17`)

Spec source: `IMantlePlaceSecretStore`
(`unreal/Plugins/MantlePlaceDcc/unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceSecretStore.{h,cpp}`).

⛔ **`HPS-14` — The refresh token is persisted encrypted, scoped to the operating-system user, or
it is not persisted at all.** Plaintext on disk is never an option, and neither is
machine-scoped encryption on a shared workstation. The reference uses Windows DPAPI
(`CryptProtectData` with `CRYPTPROTECT_UI_FORBIDDEN` and **without** `CRYPTPROTECT_LOCAL_MACHINE`,
so the key derives from the logged-in user); a host on another platform uses that platform's
per-user keystore.

_Enforcer:_ `agent-review`.

⛔ **`HPS-15` — The access token is memory-only.** It is never written to disk, never logged, and
never exposed to the host's scripting or reflection surface (in Unreal terms: not a `USTRUCT`, not
Blueprint-visible). It is short-lived and re-minted from the refresh token; there is nothing to gain
by storing it and a bearer JWT to lose.

_Enforcer:_ `agent-review`.

**`HPS-16` — A platform with no secure store gets a null store that fails honestly.** `Save`
returns false and warns; `Load` returns false; `IsPersistent()` returns false so the UI can say
"you will need to sign in again next session". The plugin degrades to memory-only auth. It does
**not** fall back to writing the secret somewhere less safe — that is a downgrade wearing a
fallback's clothes.

_Enforcer:_ `agent-review`.

**`HPS-17` — A decrypt failure means "no stored session", not an error**, and sign-out clears the
store. A blob written by a different OS user, or corrupted, is indistinguishable from absence to the
user and should be treated as such. Storage keys are sanitised before touching the filesystem
(`HPS-30`).

_Enforcer:_ `agent-review`.

---

## 4. Vault client (`HPS-18` … `HPS-25`, `HPS-48`, `HPS-55`)

Spec source: `FMantlePlaceVaultLogic`
(`unreal/Plugins/MantlePlaceDcc/unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceVaultLogic.{h,cpp}`).
Corpus group: `vault`.

**`HPS-18` — The vault surface is list → materialize → poll → re-list → presign → download.** A
bundle that already carries the host's formats skips straight to presign and download. The
**re-list after materialize is not optional**: it is where the host obtains the integrity facts
(sha256, size, manifest version) that the freshly-built bundle now has and the pre-materialize
listing did not.

Those facts describe the **packaged archive**, which is what makes `HPS-49` part of this chain
rather than a detail of the last step: a host that verifies against the archive's digest must have
asked for the archive.

_Enforcer:_ `agent-review`.

**`HPS-19` — Identifiers that reach a URL are URL-encoded, and base URLs are normalised** —
trimmed, with all trailing slashes stripped — and validated to have an `http(s)` scheme and a
non-empty authority before use.

_Enforcer:_ `automation-test`.

⛔ **`HPS-20` — In a listing, `null` means _unknown_, not _absent_ and not _zero_.** Every
optional integrity fact — `layers`, `manifestVersion`, `sizeBytes`, `sha256` — carries a companion
"is known" flag, and a `null` on the wire leaves that flag false.

A host that coerces `null` to `0` will later compare a real 134 MB download against an expected size
of zero and declare a mismatch on a bundle it never knew the size of. See `HPS-27` for the
downstream half of this rule.

_Enforcer:_ `automation-test` (corpus `vault.list.fullAndLegacy`).

⛔ **`HPS-21` — One malformed row must never blank the vault.** An element that is not an object,
or that lacks a non-empty `id`, is **skipped with a warning**; the surviving rows are returned and
the call succeeds. A missing or non-array top-level `bundles` key is a different thing entirely — a
contract violation — and fails closed with the platform's own error message.

The distinction is the point: "one row was odd" and "the response was not a vault listing" have
opposite correct responses, and collapsing them either hides corruption or tells a paying customer
their vault is empty.

_Enforcer:_ `automation-test` (corpus `vault.list.skipsMalformedRows`, `vault.list.wrongTopLevelKey`).

**`HPS-22` — Unrecognised status words map to `Unknown`; they are never parse errors.** Both the
bundle-status and materialize-state vocabularies are open sets with synonym buckets (corpus
`vault.statusWordBuckets`). A host that switches on exact strings silently drops bundles the day the
platform adds a synonym.

This governs a status word **when the platform sends one**. It does not promise that one is sent —
see `HPS-25`, where the materialize poll answers with a delivery-state document carrying no status
word at all.

_Enforcer:_ `automation-test` (corpus `vault.statusWordBuckets`).

⛔ **`HPS-23` — Materialize sends an explicit, client-owned token list — never the server-side
scope keyword.** The plugin owns which layers it needs and enumerates them. The `"unreal"` keyword
expands server-side to a smaller core set and would silently drop the vector and landcover layers;
a host that sends a keyword is trusting the server's idea of what that host wants.

`"all"` remains a valid _user-facing scope_ and is passed through as the `"all"` keyword when the
user explicitly asks for everything. Every other scope, including the host's own name, resolves to
the explicit array. Each host substitutes its own token list; only the shape is normative (corpus
`vault.materializeTokenList`).

_Enforcer:_ `automation-test` (corpus `vault.materializeTokenList`) + `agent-review`.

⛔ **`HPS-24` — A materialize start has FOUR outcomes, each recognised by its own marker in the
body rather than by status code — and never by the absence of a job id.** The platform answers a
start with one of:

| Outcome | Marker | Meaning |
| --- | --- | --- |
| **started** | a job id | a fresh run |
| **joined** | an active-job id, a coalesce flag, or an `active_job` code | a run was already in flight |
| **already-delivered** | a no-op flag, with the delivered set | nothing to build |
| **queued** | a queued flag, with the parked set | the order's core build has not finished; the picks fire on their own |

**Two of the four name no job at all, and a third carries its id under a different key than a fresh
start does.** A host that reads "no job id" as a failure therefore refuses three of the four —
including the one that means *your bundle is ready*. Where the download sits on the far side of that
parse, such a host cannot import at all. Absence is the one thing a growing protocol reassigns for
free; key each outcome on a marker that is present.

A join is a **success**, and it stays a success when the platform reports the running job **without
naming it**. Polling is keyed on the **order**, not the job — the status request never took a job id
— so an unnamed run is fully followable. Refusing there tells a user to retry a build that is
already going, which is the failure this rule exists to prevent. Two users on one order, or one user
who clicked twice, must not queue two ETL jobs.

⛔ The body-shape rule binds the **transport layer too**. A host that reduces every non-2xx to its
error text before the parser sees it has moved the decision to the status code and broken this rule
one call up — the single-flight response carries both an error string and a job fact, and the job
fact is the useful half. Asserting `HPS-24` against the parser alone will not catch it.

_Enforcer:_ `automation-test` (corpus `vault.materialize.alreadyRunning`, `vault.materialize.noop`,
`vault.materialize.queued`, `vault.materialize.coalesced`, `vault.materialize.activeJobWithoutId`)
+ `agent-review` for the transport-layer clause.

**`HPS-25` — Polling is bounded on three axes and progress may be indeterminate.** A host declares:
a poll interval (reference 3 s) with a hard floor, a maximum poll count (reference 200, ≈ 10 min),
and a **consecutive**-failure cap (reference 5) that tolerates transient errors without abandoning a
job. Progress > 1 is a percentage and is divided by 100 then clamped to `[0, 1]`; **absent progress
is indeterminate (-1), not 0** — a progress bar that sits at 0% and a spinner say different things
to a user deciding whether to wait. A `failed` status body is a valid response that parses
successfully and reports state `Failed`.

⛔ **`HPS-25a` — The poll may answer with a delivery-state document rather than a job status, and
completion is then DERIVED: the requested tokens are delivered and no job is in flight.** The
materialize poll returns what the bundle has (delivered, not-delivered with reasons, the in-flight
job if any) and **no status word anywhere**. A host that parses for one gives up on every poll and
abandons the run on its consecutive-failure cap, having never seen a valid response.

Deriving it is the more truthful reading regardless. **A run reports `completed` even when every
requested token failed to emit**, because per-token errors ride a soft-fail envelope. *Delivery is
proof of production; a job status is not.* A host that trusts the word ships the silent loop where a
user regenerates forever.

Three derivations are load-bearing, and each one fails silently if inverted:

- **Unreadable delivery state is never Complete.** When the platform says it could not determine
  what the bundle has, the empty delivered set means *no answer*, not *nothing*. Reading it as
  complete hands over a bundle the platform never confirmed. It is not a terminal state, so the poll
  budget provides the ending.
- **A terminal attempt that left requested tokens undelivered is a failure whatever it called
  itself.** Key the verdict on the tokens; the outcome word only picks the sentence.
- **A deliverable the platform will never produce for this area is a GAP, not outstanding work.**
  Waiting for one is waiting forever: it makes a bundle that is as complete as it will ever be poll
  its whole budget and then time out. Report which, and step over it.

A host with no requested set has no yardstick and must report no verdict — "nothing is outstanding"
is vacuously true of an empty set, and a Complete derived from that tautology is the same failure as
the first bullet.

_Enforcer:_ `automation-test` (corpus `vault.materialize.statusVectors` for the job-status shape,
`vault.materialize.deliveryVectors` for the derivation).

**`HPS-48` — Platform error bodies are reduced to a message in one precedence order, everywhere:
`error_description`, then `msg`, then `message`, then `error_code`, then `error`.** Most-specific
human prose first, machine codes last — showing a user `invalid_grant` when prose was available is
strictly worse. The order binds **every** error-body parser in the plugin, auth and vault alike:
two parsers with two orders is how two hosts come to show different text for the same 410.
Extracting a machine-readable `code` (`refunded` / `revoked`) for UI logic is a separate read
and unaffected by this order.

_Enforcer:_ `automation-test` (corpus `vault.errorBodyPrecedence`).

**`HPS-55` — A vault listing nobody asked for is bounded: a declared interval with a hard floor,
signed in only, never while the host's vault surface is open, and silent when it fails.** A host
that tells the curator about orders they never prepared from it — an order bought on the web that
finishes while the host is open, one built overnight — has to list the vault unasked, since the
platform has no push channel. That listing costs a request on every signed-in session, including
the ones where nobody is waiting on anything, so a host declares:

- an interval (Revit's is 15 min) and a hard floor (5 min) that no configuration goes below — a web
  order takes minutes to hours to build, and nothing is gained below that. One listing at start and
  one at each sign-in come on top of the interval, and the floor does not delay them; a token
  renewal is not a sign-in;
- **no request while signed out**, and none while the vault surface is open, whose own listing is
  the one the curator is reading;
- **silence on failure**: no notice and no sign-in prompt for a request the curator did not make.
  The order is still news at the next listing that succeeds.

A background listing never prepares and never downloads; either would spend build time, bandwidth or
disk nobody asked for. What it announces is an order **first becoming available** in the vault, once
per machine, however many host processes list it. The first listing on a machine, and an account's
first listing on it, are taken as already seen, so installing a host is not a flood of orders bought
months ago.

_Enforcer:_ `agent-review`, plus a pure-core test where a host has one.

---

## 5. Bundle cache and integrity (`HPS-26` … `HPS-30`, `HPS-44`)

Spec source: `FMantlePlaceBundleCacheLogic`
(`unreal/Plugins/MantlePlaceDcc/unreal/MantlePlace/Source/MantlePlaceRuntime/Private/MantlePlaceBundleCacheLogic.{h,cpp}`).
Corpus: `cache.validityTruthTable`, `cache.keySanitisation`, `digest.sha256Vectors`.

⛔ **`HPS-26` — Downloads are written to a `.part` file and promoted by rename only after they
verify.** Stream the response to `<bundle>.part`; on completion hash and stat it; run the validity
decision (`HPS-27`); **only then** rename over the final path. A failed verification deletes the
`.part`. A cancellation deletes the `.part` and fires no completion event.

The rename is what makes the cache crash-safe. A host that streams directly onto the final path
leaves a truncated bundle that looks cached, and the next run imports it.

_Enforcer:_ `agent-review` + `automation-test`.

⛔ **`HPS-27` — Cache validity is decided in this precedence, and a null sha256 means _unknown_,
not _absent_:**

1. file missing → invalid (`Missing`)
2. expected size known and different → invalid (`SizeMismatch`) — cheapest discriminator first
3. a hash was actually computed **and** an expected hash was advertised, and they differ → invalid
   (`Sha256Mismatch`)
4. manifest version known and below the floor → invalid (`ManifestTooOld`)
5. otherwise valid

When no hash was advertised, or none could be computed (a legacy bundle, or a file above the host's
hashing cap), **the integrity check is simply not performed**: the entry is _valid but unverified_,
and the host reports that distinction rather than claiming verification it did not do. Treating
unknown as corrupt makes every legacy bundle un-openable; treating it as verified is a lie. Full
truth table: corpus `cache.validityTruthTable`.

_Enforcer:_ `automation-test` (corpus `cache.validityTruthTable`).

**`HPS-28` — sha256 comparison is trimmed and case-insensitive, over 64 lowercase hex
characters**, and the implementation is pinned by the NIST FIPS 180-4 known answers in corpus
`digest.sha256Vectors` including the streaming-equivalence cases. The reference host already carries
**three** hand-written FIPS 180-4 implementations — the engine primitive asserts on Windows, and a
module-dependency boundary forced a second copy — so a host writing its own is the expected case, not
the exceptional one. The vectors exist so the next one is correct on the first try.

_Enforcer:_ `automation-test` (corpus `digest.sha256Vectors`).

**`HPS-29` — Presigned URLs are minted per import and never used past their expiry.** Expiry
parsing is ISO 8601; the same skew rule as `HPS-11` applies. A presigned URL is not a cacheable
artifact.

_Enforcer:_ `agent-review`.

⛔ **`HPS-49` — The presign REQUEST states which payload the host wants, and states it
unambiguously. There is no default, and an alias whose meaning depends on the order's own data is
not a statement.** The download route validates its body against a schema, so an empty object is a
`400`, not "the usual one". A host that omits the format cannot download at all — and where the
import sits on the far side of that call, it cannot import at all.

Ambiguity is the harder half. A host that wants the packaged archive names **the archive's own
token**. The platform also accepts a deprecated alias that is simultaneously a real artifact format:
asked for that way, it looks up the artifact **first** and falls through to the archive only when the
order happens to carry none. A host asking that way is not stating its intent — it is reading the
server's data, and it receives whichever the data decides. It gets the right bytes until the day an
order carries that artifact, and then it gets a single mesh where it expected a bundle, verifies it
against the archive's digest, and reports corruption on a download that succeeded.

⛔ **The corpus must pin the request, not only the response.** Every case in the download group
asserted parsed *responses*, which is why one host shipped an empty body and another shipped the
alias, and both stayed green through every gate for as long as the endpoint was written. A host
cannot assert its own request against itself; only a shared vector can say what the ask should be.

_Enforcer:_ `automation-test` (corpus `vault.downloadRequestBody`).

⛔ **`HPS-30` — An order id becomes a filesystem path only after sanitisation, and lossy
sanitisation gets a hash suffix.** Keep alphanumerics plus `.`, `_` and `-`; map everything else to
`_`; then map the results `""`, `"."` and `".."` to `_`. If sanitisation changed the string, append
`_` plus the first 8 hex characters of `sha256(utf8(rawOrderId))` so that `a/b` and `a:b` cannot
collide. A **lossless** id (a UUID) gets no suffix, which keeps existing cache paths stable.

"Alphanumeric" means the host platform's **Unicode-aware** classification, not ASCII, **bounded to
the Basic Multilingual Plane**: a non-Latin BMP order id passes through unchanged and is therefore
_not_ lossy, while **every code point above `U+FFFF` is non-alphanumeric on every host and maps to
exactly one underscore**. **Enumeration is over code points, never code units** — a surrogate pair
is one character and earns one underscore, and an unpaired surrogate is not a code point at all. A
host that hardcodes `[A-Za-z0-9]` mangles a Cyrillic id; a host that walks UTF-16 code units writes
two underscores where the rule says one. Either way the same bundle lands in a different directory
than the reference did, on every import, with neither host able to notice.

The BMP bound is a deliberate narrowing. `FChar::IsAlnum` takes a UTF-16 code unit and _cannot_
classify an astral code point, so agreeing to keep `U+1D7CE` (category `Nd`) would take a
hand-maintained Unicode table in every host, and two of those drift — the exact failure this
standard exists to prevent. Nothing that matters is lost: the collision suffix hashes the **raw**
id, so two distinct astral ids still get distinct directories, and the whole cost is a
cosmetically worse directory name. The rule also governs **secret-store key derivation**
(`HPS-14` … `HPS-17`) — a host that derives the blob name for one account two ways stores two
sessions and reliably loads neither — so one sanitiser serves both call sites.

The two halves pull in opposite directions — neutralise traversal, but stay collision-free — and
implementing only the first is the common mistake. Corpus `cache.keySanitisation`.

_Enforcer:_ `automation-test` (corpus `cache.keySanitisation`).

**`HPS-44` — Cache eviction is explicit and per-order.** There is no size-based or LRU eviction: a
purchased bundle stays on disk until the user removes it. That is the anti-streaming guarantee, and
it is a product commitment, not an oversight — a host that "helpfully" reclaims disk space has
silently converted an owned asset into a streamed one.

_Enforcer:_ `agent-review`.

---

## 6. Manifest consumption (`HPS-31` … `HPS-37`, `HPS-45`, `HPS-47`)

Spec source: `MantlePlaceImportManifest`
(`unreal/Plugins/MantlePlaceDcc/unreal/MantlePlace/Source/MantlePlaceEditor/Private/MantlePlaceImportManifest.{h,cpp}`).
Corpus group: `manifest`.

⛔ **`HPS-31` — Clean break: a host supports exactly one manifest version, declares that floor in
exactly one place, and refuses everything below it.** No fallback ladder, no dual-parsing. An
absent `version` reads as 0 and is refused like any other old version. The refusal message tells the
user to re-download the AOI from their vault, because re-procurement — not dual-parsing — is how old
bundles are handled.

The floor constant has a **single home** in the host's source, and that location is what the host
declares as its `floorSource` in `verified-against.json` (`HPS-39`).

**Multi-host corollary (2026-08-10).** The corpus's pre-floor reject set (`manifest.reject.v*`) is
host-invariant — no such case carries an `appliesTo` — so it can only name versions below the
**lowest floor among registered hosts**. A clean break is therefore not complete when the first host
takes it; it is complete when the last one does, and until then the retired version stays out of the
corpus. Adding `manifest.reject.vN` while any host still floors at `N` fails that host's suite for a
version its reader is correctly still accepting, and scoping the case to one host records a
universal invariant as host-specific in `coverage-baseline.json`, where the ratchet then requires a
deliberate diff to undo it. Hosts may repin at different times; the reject set waits for
the slowest.

The corollary is stronger than the reject set alone: **the corpus is written against ONE floor,
so floors move in lockstep while pins move per host.** `cache/validity-truth-table.json` states its
rows in absolute manifest versions, and a row at version `N` must read `valid` for a host floored at
`N` and `ManifestTooOld` for one floored at `N+1` — no single shared fixture is both. So a host may
raise `verifiedAgainstManifestVersion` the moment its parser is exercised, and must NOT raise its
floor until every registered host is ready to take the break together. The gate already permits
exactly this: it fails on `floor > pinned`, never on `floor < pinned`.

The same corollary reaches into each host suite. Both readers cross-check the corpus's
`manifestVersion` against their own floor, and both asserted **equality** — correct while every host
repinned together, and fatal to independent repin: it makes a corpus at v19 fail every host still
floored at 18. The invariant that actually matters is one-sided, so both suites now assert
`corpus manifestVersion >= this host's floor`. A corpus BELOW the floor still fails loudly (every
accept case would be a document the parser refuses); a corpus above it is the normal state of a host
that has not yet taken the break.

_Enforcer:_ `ci-manifest-conformance` + `automation-test` (corpus `manifest.reject.*`).

⛔ **`HPS-32` — Every artifact path comes from a manifest pointer, never from folder convention.**
The host reads the path out of the manifest and extracts that exact entry. Hardcoding a well-known
relative path works until the ETL renames a folder, and then it fails on a customer's bundle rather
than in CI.

_Enforcer:_ `agent-review`.

⛔ **`HPS-33` — Manifest values are applied verbatim and never re-derived.** Scales, offsets,
origins, extents and the placement math are computed by the ETL; the host multiplies them into its
own coordinate convention and does nothing else. A host that recomputes a transform "to be safe" has
built a second implementation of the pipeline that will drift from the first, silently, in the field.

Corpus `manifest.full` carries the expected derived values with explicit tolerances — compare
against those, not against exact floats.

_Enforcer:_ `automation-test` (corpus `manifest.full`) + `agent-review`.

**`HPS-34` — Integrity fields fail closed where the contract requires them and skip where it does
not.** Required hashes are **required** — a missing one is a refusal, because importing unverifiable
bytes is worse than not importing. Optional hashes (mesh, buildings, the shared `vector` layers) absent mean the
check is skipped and the artifact is still valid — the same _unknown ≠ absent_ rule as `HPS-27`.
Where an artifact carries no hash of its own, the host resolves it by matching the path against the
manifest's format table rather than assuming.

WHICH hashes are required is **per host**, because each host's deliverables are published by a
different part of the contract and arrived at different versions:

| Host     | Required-hash artifacts                                           | From  | Required                          |
| -------- | ----------------------------------------------------------------- | ----- | --------------------------------- |
| `unreal` | `unreal.heightmap`, `unreal.imagery_drape`                        | v17   | whenever the manifest is imported |
| `revit`  | `revit.toposurface_points`, `revit.surface_dxf`, `revit.ifc_site` | v19   | when that sub-object is present   |
| `revit`  | `revit.drape`, each layer of `revit.vectors`                      | 1.3.0 | when that sub-object is present   |

The Revit row is conditional because each sub-object is itself optional — a bundle with no Revit
deliverables selected is a well-formed manifest — while the v19 schema lists `sha256` in the
`required` set of every sub-object it does publish. So a present block without one is a producer bug
and a refusal, and the rule is version-gated: below v19 no Revit hash was published at all, and an
absent one stays valid-but-unverified rather than corrupt. A host reads these off its OWN
block (`HPS-33`) — the v19 `revit.*` sub-objects are the only place they are published, so a host
still reading `elevation.points_csv` finds nothing to enforce.

_Enforcer:_ `automation-test` (corpus `manifest.heightmapMissingSha`,
`manifest.meshMissingSha`, `manifest.buildingsShaInFormats`, `manifest.revitArtifactHashes`,
`manifest.reject.revitHashMissing`).

**`HPS-35` — Internal inconsistency and unsupported enum values both fail closed.** Where the
manifest states a value twice over — the reference checks
`component_count_x × (sections_per_component × section_size_quads) + 1 == resolution` — the host
verifies the identity and refuses on mismatch rather than picking one. An enum value the host does
not support (the reference: any `planet_shape` other than `Flat`) is a refusal naming the offending
value, never a silent fallback to the default.

_Enforcer:_ `automation-test` (corpus `manifest.resolutionMismatch`,
`manifest.planetShapeSphere`).

**`HPS-36` — A host reads its own `dcc_readiness.<host>` block and surfaces the stated reason.**
When an expected artifact is absent, the manifest says why; the plugin shows that reason instead of
dead-ending on an empty import. A host reads only its own key.

Per-host keys are the manifest's shape: a sibling host's block is ignored, never merged, and the
retired v17 anonymous `dcc_readiness.mesh_import` is **not** a fallback — reading it would turn a
clean break into dual-parsing.

_Enforcer:_ `automation-test` (corpus `manifest.dccReadinessReason`,
`manifest.dccReadinessIgnoresRetiredFlatKey`).

**`HPS-37` — A bundle with no host block is not a parse failure, and the job id is not the order
id.** A base, not-yet-materialized bundle carries no host block; the host MUST still parse the
top-level facts — bounding box, layout pointers, packaging, **order id** — before returning "not
importable", so streaming and the vault join key survive. The per-rebuild ETL `jobId` and the vault
`orderId` are **not interchangeable**; only the order id joins to the vault.

_Enforcer:_ `automation-test` (corpus `manifest.baseOnDemand`).

**`HPS-45` — Local projection is a fallback, it reaches a UTM origin and nothing else, and where a
host does project it matches the corpus known answers.** `HPS-33` is the default and stays the
default: placement values come pre-computed and are applied verbatim, and `HPS-52` decides which
pointer a host reaches for first. But a host importing geometry whose own coordinates are geographic
— the reference case is a `vector` GeoJSON layer in lon/lat that must land in a UTM-origin frame —
has to project, because the manifest describes the layer rather than every vertex in it. That narrow
case is permitted, and it is the _only_ projection a host may perform: `HPS-03`'s ban on a
geoprocessing stack is unchanged.

**The permitted projection is lon/lat → UTM, so a host whose own origin is not a UTM zone does not
project at all.** That was assumed rather than stated, and the assumption underneath it was that
every host frame is a UTM zone; an order-frame host on a State Plane delivery is the case that breaks
it. Such a host has no projection to perform, so a geographic layer it holds no host-frame copy of
(`HPS-52`) is **skipped with a named reason** while the rest of the import proceeds. **A host never
widens its projection to reach another grid** — not a second forward transform, and not projecting
into UTM and converting across. That is a geoprocessing stack by instalments and is refused at
review even when the arithmetic is correct. The linear unit travels with the origin: a UTM zone is
metric by definition, so a foot unit published on a UTM origin is an internally inconsistent
manifest and fails closed under `HPS-35` rather than being reconciled.

A host that performs no such projection **does not claim the `projection` corpus group** and skips
it — and **records why in its `verified-against.json` evidence prose**, because an unclaimed group
is otherwise indistinguishable from a group nobody got to yet, and the coverage summary reports both
as `0/1`. A host that does claim it MUST match corpus `projection.lonLatToUtm` within its stated
tolerance, including the southern-hemisphere false northing. Getting the zone or the false northing
wrong places geometry kilometres away while every test that does not check numbers still passes.

_Enforcer:_ `automation-test` (corpus `projection.lonLatToUtm`), where the group is claimed — that
case proves the arithmetic of a projection performed. The half added here, **the named skip where a
host's origin is not a UTM zone**, has no corpus case and none is named for it: it wants a bundle
whose origin is on another grid beside a geographic layer, and the corpus carries no such pair. Until
it lands that half is `agent-review`, and the per-host test where a host has one — Revit's
`SiteFrame.CanPlaceGeographic` is the only one today.

**`HPS-47` — A host decides "is this bundle materialized" from the manifest's neutral signals,
never from its own content alone.** The signals, any one of which means materialized: a known host
block at top level, a `dcc_readiness` object, or a non-empty `vector.layers` array. The host-block
roster is corroborative, not load-bearing — a bundle materialized for a host this plugin has never
heard of still carries `dcc_readiness` and its vector layers, so roster staleness degrades nothing.
(The day the roster can be replaced by a structural marker is a v19 `hosts.<hostId>` namespace,
proposed in the decision log.)

"Materialized" and "importable by me" are different questions. A bundle materialized for another
host is a **well-formed manifest** this host parses and then refuses to _import_, with guidance to
materialize its own scope (`HPS-37`) — refusing it as _invalid_, or misreading it as a base bundle,
is the failure this rule exists to prevent, and it is latent in any host that keys materialization
off the presence of its own block.

_Enforcer:_ `automation-test` (corpus `manifest.materializationSignals`).

---

## 7. Conformance (`HPS-38` … `HPS-42`, `HPS-46`)

⛔ **`HPS-38` — A host registers in
[`tools/manifest-conformance/verified-against.json`](https://github.com/mantleplace/mantleplace-dcc/blob/main/tools/manifest-conformance/verified-against.json)
from its first commit.** Not at feature-complete, not at first release — the first commit. The entry
is the mechanism by which the host is told, automatically, that web published a manifest version it
has never been verified against; a host that registers late is unguarded for exactly the period when
it is changing fastest.

Registering is a claim that the parser was **exercised** against that shape and its tests updated —
not that it tolerates it.

_Enforcer:_ `ci-manifest-conformance`.

**`HPS-39` — Each host declares its own `floorSource`, `tests` and `owner`.** `floorSource` is a
repo-relative path plus a regex whose first capture group is the integer floor — a C++ header, a C#
constant and a Python module are all a file and a regex. `tests` is the file a reviewer is told to
touch when the pin moves. `owner` names the session type that owns the key.

`verified-against.json` is owned **per key**: a host session edits its own entry and
needs no coordination to bump its own pin. The file's _schema_, the gate script, and this standard
are shared seams under the single-writer rule.

> **Ordering, for a new host's first commit.** The gate resolves `floorSource` against the working
> tree, so the commit that registers a host MUST also contain that host's version-floor constant. In
> practice `HPS-38`'s "from the first commit" means the first commit _carries a parser with a
> declared floor_ — registering against a file that does not exist yet turns the host's own first CI
> run red. Land the floor constant and the registration together; the rest of the parser can follow.

_Enforcer:_ `ci-manifest-conformance`.

**`HPS-40` — A host's test suite consumes the shared corpus at
[`tools/manifest-conformance/corpus/`](https://github.com/mantleplace/mantleplace-dcc/blob/main/tools/manifest-conformance/corpus/README.md).** It
iterates `index.json` rather than transcribing vectors into host-language literals. Transcribed
vectors are how two hosts come to disagree about the same contract while both test suites stay
green.

> **The reader's semantics are their own rule.** How a reader consumes the corpus — what it must
> assert, how it fails, and the self-test it must pass — is `HPS-46`. The reference reader is
> `unreal/Plugins/MantlePlaceDcc/unreal/MantlePlace/Source/MantlePlaceRuntime/Public/Tests/MantlePlaceConformanceCorpus.h`
> (no dependencies beyond the engine's JSON module); every host re-implements it, permanently
> (`HPS-46` — the reader is carved out of `HPS-43`'s extraction trigger).

_Enforcer:_ `automation-test` (per host).

**`HPS-41` — A host runs every case in the groups it claims, and proposes new cases upstream.** A
host need not consume every group on day one — a manifest-first build order claims `manifest` and
adds `vault`/`auth` as those layers land — but partial coverage _within_ a claimed group is not
conformance. A host that needs a new case adds it to the shared corpus rather than forking a private
copy.

**Coverage is asserted mechanically, not by review.** Groups whose cases each drive a different
parser get dispatched by id rather than in a loop, and a suite that dispatches by id will happily
ignore a case added later. So a host tracks which ids it drove and fails on any it did not — the
reference calls that `UndrivenCases`. Reviewers cannot see a missing `if` in a 400-line test.

**A case carrying `appliesTo` is scoped to that one host's manifest block and is skipped by every
other host.** Placement math, block-specific required hashes and block-specific enum values are
per-host by construction — every host gets its own top-level block — and holding a Revit
plugin to Unreal's `component_count_x` identity would invert that. Cases with no `appliesTo`
are host-invariant and bind everyone — the version gate, the base-bundle partial parse, and the
top-level `vector` pointers among them.

⛔ **A case with no `appliesTo` may not declare an expectation only ONE host can compute.** The
absent `appliesTo` is a claim that every host can produce every value the case declares; a key that
encodes one host's vocabulary makes that claim false for all the others, and it fails quietly. The
other host either asserts the reference host's answer — writing one host's policy into another,
that inversion again — or reads past the key and stays green, which `HPS-46` cannot catch when the key
is nested inside an `expectations` value. Three instances, which is what makes this a rule and not a
fix:

- `vault.list.fullAndLegacy` declared `items[].tierLabel`, whose whole discriminator is the presence
  of `glb`, the Unreal terrain mesh. Host #2's honest answer is "no Revit tier", a different fact
  rather than a disagreement about the same one, so the suite asserted something else in its place
  and recorded why. Split out as `vault.list.tierLabel`, `appliesTo: "unreal"`
- `manifest.roadSplinesGeojsonWins` and `manifest.roadSplinesGpkgOnly` carry no `appliesTo` and are
  `expect: accept`, but the only thing making them acceptable is an `unreal.mesh_alternative` block.
  A host whose verdict keyed off its own content would reject them and fail conformance through no
  fault of its own; resolved by naming `HPS-47`, so validity follows the neutral materialization
  signals.
- The required-hash rule had no Revit half — the ETL published no sha256 for the Revit artifacts, so
  the corpus was relying on a coincidence in host #2's reader rather than stating what it meant

The remedy is one of two, never a third: scope the case with `appliesTo` so each host states its own
answer, or lift the expectation to something host-neutral. Not a substitution token — a template
language in the corpus for one key buys a `corpusVersion` bump and a repin per host, and still
asserts a falsehood for whichever host has no answer to give.

_Enforcer:_ `pr-review`.

**`HPS-42` — Conformance tests run headless, with no DCC application required.** The pure cores are
testable standalone (`HPS-02`), so the corpus runs on a cheap hosted runner. A conformance suite
that needs a licensed Revit install is a suite that runs when someone remembers.

_Enforcer:_ each host's own headless suite — `ci-revit-tests` for Revit, `ci-ue-compile` for
Unreal. Not `ci-manifest-conformance`: that
gate only checks registration, version-floor and corpus integrity — it never runs a host suite.
This rule binds the **pure cores only** (`HPS-02`): the shim half of a triad references the host
application's own assemblies and cannot be built on a hosted runner. `ci-revit-tests` builds and
tests `MantlePlace.Revit.Core` alone; the `MantlePlace.Revit.Addin` shim's gate is a developer
build plus the tester import gate. `ci-ue-compile` solves this differently, with a pinned
self-hosted runner that carries its own honest caveat (if that runner is offline the job
queues rather than failing) — that trade is available to Unreal and is not obviously worth it per
host.

**Guidance for host #3 and host #4 — the three-assembly split, the pattern from Revit:**

| Assembly                   | What lives there                                                                     | CI             |
| -------------------------- | ------------------------------------------------------------------------------------ | -------------- |
| `MantlePlace.Revit.Core`   | pure protocol                                                                        | built + tested |
| `MantlePlace.Revit.Client` | I/O that is **not** Revit — HTTP, loopback listener, bundle cache, secret store, zip | built + tested |
| `MantlePlace.Revit.Addin`  | Revit API only — transactions, ribbon, `ExternalEvent`                               | not built      |

The shim was carrying two kinds of impure — "talks to the DCC" and "talks to the disk and the
network" — and only the first needs a licence to exercise. Reach for the shim only when the code
needs the DCC's own types; everything else belongs in the client assembly, where a hosted Linux
runner can build and test it. Concretely, the split moved `HPS-26`'s cache assertions and
`HPS-14`…`HPS-17`'s null-secret-store path onto `ci-revit-tests`, where DPAPI genuinely does not
exist — coverage a Windows-only suite would never reach.

**The residue after the split is genuinely untestable in CI, and this list is closed, not
partial:** ribbon registration, `Transaction`, `Toposolid.Create`, `Document.Link`,
`RevitLinkType.CreateFromIFC`, `SetProjectPosition`, and whether the modeless window renders. A
per-DCC self-hosted runner, after the split, buys only this residue — the tester import gate is
the instrument for it, and that trade is not obviously worth it per host.

⛔ **`HPS-46` — A corpus reader proves consumption by tracking what it actually asserted: every
`expectations` key of every case the host executes MUST be read by a typed assertion, and a
declared key that was not read fails the suite.** An allow-list of known keys cannot express this
rule — it catches an unknown key but not a known key declared with the wrong JSON type, so
`"orderId": 999` asserts nothing while still counting as covered
(found by host #2 within a day of
landing). "Not read" is one failure with three causes — the host has no assertion for the key, the
case declares it with a type the accessor rejects, or the assertion path never ran — and all three
MUST fail identically. A case skipped wholesale under `appliesTo` (`HPS-41`) is exempt as a unit;
a case the host executes is never partially exemptable.

**`HPS-46` reaches only as far as the top level of `expectations`, and host #2 found both floors
below it.** The rule above is a statement about declared keys; the two amendments below extend the
same idea — _coverage is proven by what was asserted, never by what was loaded_ — to the two places
where a case has no declared keys to bind.

- ⛔ **`HPS-46a` — In a case whose `expect` is `vector`, every leaf value in the vector file MUST be
  read by a typed assertion.** A `vector` case declares no `expectations` at all: the file _is_ the
  payload, so the asserted-keys rule binds nothing. Of the 21 cases a non-projecting host claims,
  **eleven are vectors**, and for `auth`, `cache` and `digest` it is **seven of seven** — precisely
  where the ⛔ rules live. Without this, a suite that drives row 0 of an eleven-row truth table
  passes, and the coverage ratchet records the case as covered. The failure message names the
  unread **path** (`stateValidation[3].expected`), because "something in this file" is not
  actionable.

  An explicit JSON `null` counts as a value and must be read like any other. `sha256: null` is the
  row ⛔`HPS-27` exists for, and a tracker that treats null as nothing-to-track makes it the single
  row a suite can skip for free.

- ⛔ **`HPS-46b` — Every leaf BELOW the top level of an `expectations` value MUST be read by a
  typed assertion too.** `vault.list.fullAndLegacy` declares two top-level keys, `itemCount` and
  `items`; asserting that `items` was _read_ says nothing about the thirty-four leaves inside its
  two rows, and nothing can tell a host that asserted one of them from a host that asserted all
  thirty-four — the same blind spot one level down. The failure names the full path
  (`items[1].hasManifestVersion`), on `HPS-46a`'s precedent.

  Three shapes the walk must get right, each otherwise a leaf a suite skips for free. An explicit
  `null` is a value, for the reason it is one in a vector file. An EMPTY container is its own leaf:
  `formats: []` on the legacy row is the assertion that the row HAS none, and a walk finding
  nothing under it lets the key go unread. And documentation is exempt at every depth, subtree
  included — a reader exempting prose only at the top level reports the corpus's own annotations
  as gaps.

**Documentation keys are exempt, and the corpus MUST make them recognisable.** A vector file carries
normative prose for the human reading it — `$comment`, `note`, `rule`, `definition`, `alphabet`,
`formula`, `boundary`, `default`, `collisionSuffix` — which no host can meaningfully assert. Until a
convention exists, every host maintains that list independently and they drift. **The convention is:
a key is documentation if it is `$comment`, or if its name ends in `Note`.** Existing prose keys are
renamed to fit as the corpus is touched; the enumerated list is the transitional bridge and is
expected to shrink to nothing.

_Enforcer:_ `automation-test`, per host. `HPS-46a` was first implemented in Revit's
`VectorDocument`, proven by deliberately truncating a table and by a self-test suite over synthetic
documents. `HPS-46b` is enforced by each host's own nested-path tracking — Revit's
`ExpectationNode`, Unreal's `ExpectRow*` accessors, re-implemented per host like every other part of
the reader — and proven by `corpus/self-test/`'s `selfTest.nestedUnreadExpectation`, a fixture whose
top level is fully asserted and whose nested keys are not, so a reader tracking only the top level
accepts it. `ci-manifest-conformance` checks that fixture is still broken the way it claims.

Three further obligations, whatever the language:

- **Discovery fails loudly.** A corpus that cannot be found MUST fail the suite, never skip it — a
  reader that quietly resolves to zero cases turns `HPS-40` into a no-op that reports green. A
  claimed group that resolves to zero cases MUST fail the same way. _How_ the reader locates the
  corpus, and whether it pre-parses payloads, are host-local mechanics; asymmetry between two
  hosts' readers there is not drift.
- **The reader passes the self-test corpus.** `corpus/self-test/` holds deliberately broken index
  and case fixtures; each host carries a required test that runs its reader over them and fails if
  any fixture is _accepted_. "The reader is correct" is a case set, not a one-off mutation run.
- **Readers are re-implemented per host, forever.** The reader is part of the conformance
  instrument: two hosts sharing one reader means one reader bug silently binds both, and
  independent re-implementation is what makes host agreement _evidence_. This is a permanent
  carve-out from `HPS-43`'s extraction trigger — a .NET SDK extracted at Rhino kickoff takes
  shipped protocol code, never the corpus reader.

_Enforcer:_ `automation-test` (per host: the suite's unasserted-key failures, plus the self-test
suite).

---

## 8. Shared code — the trigger, and the reason there isn't any yet (`HPS-43`)

**`HPS-43` — No shared shipped-implementation code ships until a second host of the same language
exists, and the .NET SDK is extracted from Revit's shipped implementation when Rhino kicks off —
never speculatively.**

The reasoning is worth keeping attached to the rule: an SDK designed before its
second consumer exists encodes one host's assumptions and then has to be un-encoded. Extracting from
working, shipped Revit code means the abstraction is answering questions two hosts actually asked.

The trigger's scope is shipped protocol code only. The conformance harness is **not** inside it:
corpus readers are re-implemented per host permanently (`HPS-46`), and one .NET host is no design
pressure — the trigger MUST NOT fire early on the harness's account.

Until that trigger fires, the share model is **specifications plus this corpus** — which is what
every rule above is.

_Enforcer:_ `doc-only` (the trigger is a decision-log entry at Rhino kickoff).

---

## 9. Local install (`HPS-50`)

⛔ **`HPS-50` — A host's local install is a single slot that tracks `main`, and the slot says what
it holds.** Every host ships three things beside its shim: an **install tool** that puts a build
into the host application's own plugin location and writes a **stamp** naming the commit, the
branch, the source tree, whether that tree had uncommitted changes, and when; a
**`<host>/tools/Check-<Host>Install.ps1`** that reads the stamp back, compares the commit against
`origin/main` counting only commits under that host's folder, and prints one line under the shared
contract (current, preview, stale, unverified, not installed, not configured — exit 0 for the
first two and the last, 1 otherwise); and a **written inner loop** naming what the host application
reloads live and what needs a restart. What the *source* tree looks like is never a reason for the
install tool to refuse: a deploy from a dirty tree or from a branch is recorded as such, and the
check script is where "unverified" is said. (Refusing over the *destination* — a slot with
uncommitted edits in it — is a different question, and a host may.) A deploy from a branch is a
preview, is stamped as one, and is made only when asked for.

The failure this guards is the plugin that *appears* to be the tree: a maintainer with several
worktrees and one plugin folder runs a build a day older than the source, reproduces a bug against
code that already contains its fix, and has nothing on screen saying so. Revit's add-in manifest
carries one `ClientId` and Revit refuses a duplicate; a consuming Unreal project has one submodule
checkout; so a slot per worktree is not available, and the slot has to be legible instead. The
check is scoped to the host's folder so a docs-only merge never asks for a redeploy, and it fetches
first because the tree it runs in is moved by other sessions.

The shared half — the verdict sentences and the git facts behind them — is
[`tools/local-install/`](https://github.com/mantleplace/mantleplace-dcc/blob/main/tools/local-install/README.md),
whose runner enumerates every host's script and is the session-start check root `CLAUDE.md` asks
for. What a host's install tool *is* stays host-local: Revit copies assemblies into per-version
add-ins folders and Unreal moves a submodule checkout, and the two have nothing in common but the
line they print.

_Enforcer:_ `agent-review` — no hosted runner has a host application or a consuming project, so the
scripts are proven by being run, and the rule by the check script's line at the start of a session.

---

## 10. User-facing vocabulary (`HPS-51`)

**`HPS-51` — The shared actions carry the same words in every host; anything naming a host construct
carries the host's own noun.** A curator who signs in to Revit in the morning and to Unreal in the
afternoon is doing one thing twice, and the plugin that calls it two things has made them learn it
twice. Ten actions are shared, and each carries one set of words:

| The action                                                     | The words            | Revit says it on                                                 | Unreal says it on                                    |
| -------------------------------------------------------------- | -------------------- | ----------------------------------------------------------------- | ------------------------------------------------------ |
| Start the browser sign-in                                      | `Sign In`            | the Account face, and the sign-in window's heading                | the vault panel's auth button                        |
| Drop the session and forget the stored credential              | `Sign Out`           | the Account dropdown                                              | that same button, once authenticated                 |
| Either wait — authenticating in the browser, or refreshing      | `Signing In`         | the Account face, disabled for the wait                           | the auth button, disabled for the wait               |
| Authenticated                                                  | `Signed In`          | the Account face                                                  | the panel's header line                              |
| The surface that lists the vault                               | `Vault`              | the Bundles panel's button, and the vault window's heading        | the panel's heading                                  |
| Import a bundle the user already holds on disk                 | `Import Bundle`      | the Bundles panel's button                                        | the local-import section's button                    |
| Which build this is, and what it is running in                 | `About Mantle Place` | the Account dropdown, and the dialog it opens                     | nothing yet; these are the words when it grows one |
| Show a bundle import running, step by step, and stop it        | `Bundle Import`, `Cancel`; a step reads `Waiting`, `Importing`, `Done`, `Failed`, `Cancelled` or `Not Run` | the import window's heading, its stop button and each step's row | nothing yet; these are the words when it grows one |
| Choose what a bundle import brings in, before its steps run    | `Include` over the list, `Import` to start; a row that cannot be chosen until another is reads `Needs` and that row's name | the import window, before its steps | nothing yet; these are the words when it grows one |
| Say what a bundle holds that an import cannot offer, and why, before its steps run | `Unavailable` over the list; each entry is the row's name and one sentence from the table below — never a box | the import window, below its checklist | nothing yet; these are the words when it grows one |

**A row in that list is named for what it builds, and a step is named the same.** The glossary's
word where it has one — `Terrain`, `Site Model`, and names built on it, as `Land Use Subdivisions`
and `Imagery Drape` are on *subdivision* and *drape* — and the host's own noun only for a host
construct the glossary does not name. The list and
the steps that follow it are one surface, so a curator who ticked `Planting` watches `Planting` import. **Every row starts checked** unless a host has a
stated reason to start one unchecked, and an import nobody is there to choose for brings in
everything. A row that needs another is disabled while that one is unchecked, and says which — a
disabled box with no reason beside it reads as a bug.

**A step a cancel stopped partway reads `Cancelled`; a step it never reached reads `Not Run`.** They
are different facts for a curator deciding whether to import again — the first left stamped work in
the project that a re-import reuses, the second left nothing — so they are different words, and a
host that shows one word for both has hidden which steps a re-import will pick up.

**What a bundle holds and an import cannot offer is said before the import, in two registers.** A
row the host cannot import — a file it cannot place in its frame, a file the manifest names and the
zip lacks, a unit it cannot read, an image it cannot put on the right ground, a deliverable the bundle
was cut without — is not a row that was never there, and a shorter list with no reason reads as a
bug. So it is listed under `Unavailable`, below the rows that can be chosen, and cannot be ticked.
The window speaks to the user: what is missing and what to do, one sentence over every row it is
true of. The host's own technical reason, EPSG codes and paths included, stays in its log, which is
where support reads it. Something the bundle says it has none of — a layer it declares had no
features in the area, imagery it declares unavailable, a derived layer the order never got — is not
withheld, no vault can change it, and it is not listed. A bundle with nothing withheld shows nothing extra, and an import
nobody is there to choose for still imports everything it can and logs the rest. The sentences are
these words, the host's name where the table says *host*, each pluralised for more than one row:

| What stops the row                                               | The words |
| ---------------------------------------------------------------- | --------- |
| the bundle was cut before the format carried it in this host's frame, or carries no origin to place it against | *This bundle was built before* host *could receive this. Download the bundle again from your vault to get it.* |
| the manifest says nothing of it, where the order could have had it | *This bundle does not carry this. Add it to the order in your vault, then download the bundle again.* |
| the manifest names a file the bundle does not contain            | *This bundle is missing the file for this. Download the bundle again from your vault to get it.* |
| a unit this host cannot read                                     | *This bundle measures this in a unit this version of Mantle Place cannot read. Update Mantle Place to import it.* |
| the ground an image covers cannot be confirmed, or the image cannot be read | *Mantle Place could not confirm which ground this covers. Download the bundle again from your vault to get it.* |

Which of a host's skip reasons says which sentence is that host's to decide, and belongs where its
tests reach it; the sentences are not.

**What a bundle is delivered in is said in one set of words too.** A host that tells the user which
unit system, linear unit and delivery CRS a bundle is in reads them verbatim off the manifest's
`delivery` block, for display — no placement value comes from it — and says them as one line, the
three joined by ` · ` in that order, as *Imperial · US survey feet · EPSG:6543*. A host may introduce
the line in its own layout; the three are these words:

| The published value                              | The words            |
| ------------------------------------------------ | -------------------- |
| `unit_system` `metric`, `imperial`               | `Metric`, `Imperial` |
| `linear_unit` `m`, `ftUS`, `ft`                  | `metres`, `US survey feet`, `international feet` |
| `label` present                                  | the label, verbatim  |
| no `label`, `horizontal_epsg` present            | `EPSG:` and the code — a host looks up no CRS names, so it does not rebuild the label a bundle before the label existed lacks |
| neither                                          | `no delivery CRS`    |
| a required value absent                          | `not stated`         |
| a value this table has no word for               | the published value, verbatim — never a guess |

A bundle with no `delivery` block, built before the block existed, gets no line: it is not assumed to
be metric. Revit says the line on its import window; Unreal says it in the import
summary, followed on a foot `linear_unit` by a line of its own saying that its content is metric
(`HPS-54`) — a fixed-frame host's statement about itself, and outside this table.

**Casing is the host's own convention, and this rule does not touch it.** Title Case on a Revit
ribbon, because every Autodesk tab beside ours uses it; the editor's own style in Unreal. So is the
punctuation of a wait: `Signing In…` and `Signing in...` are one word in two hosts' typography and
both conform, while `Authenticating…` is a different word and does not. So is a ribbon face that wraps
`Import Bundle` over two lines, and a status line that ends `Signed in.` in a sentence — same words,
host's own presentation. The rule fixes **which words**, and nothing else.

**A host says what its control shape allows, and the shape is not shared.** Unreal's single button
toggles between `Sign In` and `Sign Out` and reports the session on a header line beside it; Revit's
split button pins `Signed In` to the face and moves `Sign Out` into the dropdown. Both say all four
words; neither borrows the other's control. Saying them is the whole of it — but a state with no word
anywhere on screen is a state the host has not said, which is how the Revit ribbon came to answer "am
I signed in?" with a dialog raised by clicking `Sign in` and reading the refusal.

**A host construct takes the host's own noun and is outside this rule.** The terrain above all:
Unreal builds a `Landscape` and Revit builds a `Toposolid`, and a shared word there would name a
thing neither host has. `HPS-03` already puts the import layer's semantics out of scope for this
standard, and this is the user-facing half of the same boundary. A control that names both — a host
construct built by a shared action — takes the shared word for the action and the host's noun for the
construct, in that host's own grammar.

**Everywhere else, the words are [`CONTEXT.md`](../CONTEXT.md)'s** — *vault*, *bundle*, *bundle
import*, *terrain*, with the avoid-lists that come with them. Tooltips, long descriptions, status
lines and refusals are prose rather than labels, so they are not enumerated above; what binds them is
the glossary, which is cross-host already.

The failure attached to this rule is only visible across hosts, and casing is not it. Revit's
ribbon said `Sign in` and `Sign out` where Unreal said `Sign In` and `Sign Out`, and its vault window
said `Remove download` beside `Prepare for Revit` — all of which the host's own Title Case convention
settled, which is exactly why casing is exempt here. What that pass then found underneath was
word-level and a convention could not have reached it: the Revit ribbon had **no word at all** for
being signed in, because a face that never reported a session had never needed one, and the two hosts
named the local import differently — Unreal's control said `Import` with the word *bundle* left up in
the section heading. Both hosts were naming the same actions from scratch, twice, because nothing
said which of them were the same action — and host #3 would have named them a third time.

_Enforcer:_ `agent-review`, plus whatever half of a host's words sits in a pure core. Revit's auth
faces, its window labels and its delivery line are constants with a headless test —
`AccountRibbon`, `WindowLabels`, `DeliveryHeader` —
because the shim is never built in CI (`HPS-02`, `HPS-42`). The ribbon's own faces are not: `Sign
Out`, `Vault`, `Import Bundle` and `About Mantle Place` are literals in `MantlePlaceApplication`,
where nothing but review reads them, and Unreal's are inline in the Slate panel on the same terms.
Unreal's delivery line is the exception there: it is decided in `MantlePlaceDeliveryLogic`, whose
headless test asserts the words.
Review is the enforcer of record; a test covers what a host has already moved out of its shim.

---

## 11. Frames and placement (`HPS-52` … `HPS-54`)

The glossary's words are load-bearing in this section and are used exactly as it defines them —
*frame*, *host frame*, *delivery tier*, *delivery CRS*, *fixed-frame host*, *order-frame host*
([`CONTEXT.md`](../CONTEXT.md), _Units and frames_). Two of them do most of the work: a **frame**
belongs to a file, and a **host frame** belongs to a host. They are separate words because one bundle
carries files in more than one frame **on purpose** — the same ground, stated for whichever host each
file was made for — so "what frame is this bundle in" is a question with no answer, and a host that
asks it has already gone wrong.

This section is the frames half of `HPS-33` and `HPS-35`. Neither of those said that a file states a
frame, nor what a host does with one whose frame it cannot match, and the silence read as permission:
a host may subtract its own origin from whatever a file happens to hold and call the result a
position. Section 6 says what a host does with a *value*; this says which *file* a host is entitled
to place at all.

⛔ **`HPS-52` — A host places from its own host block first.** Where the host block points at content
already in the host frame, **that pointer is the placement path on every delivery tier** — not a
metric path and an imperial one, and not a path chosen after the fact by what the tier turned out to
be. A host-neutral pointer, and the `HPS-45` projection behind it, are the **fallback**: they are
what a host uses for content its block does not carry, including a bundle produced before the block
carried it. The subtree boundary is unchanged — a host reads exactly its own block and never a
sibling's (`HPS-36`).

The order matters because the two paths are not equally safe. A host-neutral file is in whatever frame
its producer found convenient, which on one delivery tier is the host frame and on the next is not; a
host that reaches for it first is right by coincidence and wrong silently. Reaching for the block
first also makes the gap legible: when the block carries no host-frame copy of something, that is a
**format** gap with a name and a version to fix it in, rather than a host quietly placing the nearest
file it could find.

This already decides live paths rather than a future one. Revit's terrain points come from its own
block, are in its frame on every tier, and import on every tier. Its tree points come from a
**host-neutral** pointer that happens to be in the delivery CRS — so they land correctly on the
order-frame host and the same family does not on the fixed-frame host, which is the coincidence this
ordering exists to stop a host relying on. The imagery drape and the `vector` layers were the gap
until MPB 1.3.0 gave Revit's block a copy of each in its own frame; Revit places those first on every
tier, and the host-neutral drape and the `HPS-45` projection remain the fallback for a bundle whose
block does not carry them.

**A pointer sitting in the host block is not itself the showing `HPS-53` asks for.** A block that
points at content in some other frame is a format defect, and the host still refuses the file by
name rather than placing it on the strength of where the pointer was found.

_Enforcer:_ `agent-review`, and for the Revit host the corpus case
`manifest.revitOwnFramePointers`: a bundle on a State Plane delivery whose block points at host-frame vector and
drape copies beside host-neutral copies of the same content in another frame, with the pointers the
host takes expected to be its own block's. It carries `appliesTo` for that host, because the pair it
states is that block's; a case for the fixed-frame host's own pointers is that host's to add.

⛔ **`HPS-53` — A host places a file only where it can show the file's frame is its host frame, and
otherwise refuses that file by name.** Read the frame from the thing you are placing — never from the
bundle, never from the delivery tier, and never from the last file that worked. A host about to place
projected coordinates reads **both halves** of the frame the format states for them, the CRS and the
linear unit, and where it cannot read both, or reads a CRS that is not its own, the file is
**unplaceable**. An unstated CRS is never assumed to match, and neither is an unstated unit.

**One statement stands in for an unstated unit, and only where the format makes it.** Where the format
says a file's values follow the delivered linear unit — the terrain points, and the tree points'
`ground_z` column before MPB 1.3.0 stated its unit beside the pointer — a host reads
`delivery.linear_unit` as that file's unit, and a bundle stating neither is metric, as every bundle
before the `delivery` block was. That reads a published statement about the file; it does not infer a
unit from the tier, and a file the format does not tie to the delivered unit has no such fallback. A
unit the file does state always wins.

⛔ **The refusal is a named skip, and it is never a conversion.** The host reports which content it
did not place and why, in the words the user is already reading the import in (`HPS-51`), and the
import brings in everything else. It does not reproject, does not scale a unit to reconcile a
mismatch, and does not fall back to a default frame: this is `HPS-35`'s fail-closed rule applied to
frames, and `HPS-33`'s apply-verbatim boundary is what forbids the arithmetic that would "fix" it.
The failure being guarded is arithmetic that succeeds — foot coordinates minus a metre origin is a
number that looks exactly like a position, and every test that does not check where content landed
stays green.

**Where the format states no frame beside a pointer, one substitute is permitted, and it is a
comparison of published values:** content whose coordinates fall outside the extent the host's own
block publishes is not in this frame, and is refused on that evidence alone. **The comparison runs
one way.** Falling inside the extent is not proof of the frame — only the absence of proof against
it — so this is a backstop that refuses, never a licence that places, and a host does not report it
as having shown anything. It stays as the backstop once a stated frame arrives beside the pointer,
because a producer can state a frame wrongly. Comparing two published numbers derives no placement
value, so the thin-client boundary is intact.

_Enforcer:_ `automation-test` per host, wherever the host has moved the decision into a pure core —
Revit's `SiteFrame` is the furthest any host has taken it. `CanPlaceGeographic` and
`CanPlaceProjected` decide the CRS half; `Holds` decides whether the host block's declared
`file_frame` is the origin's frame; and `IsInOriginUnit` decides whether absolute coordinates are in
the unit they are subtracted in. The planner compares each own-block file's `units` with that
`file_frame`'s. `agent-review` covers the rest. Two corpus cases pin it, both carrying `appliesTo` for the
fixed-frame host, because the pointer and the extent they read are that host's block's.
`manifest.treePointsFrame` pins the **extent substitute**: a tree-point file in a foot frame beside a
metric origin, under a pointer that states no frame, refused by name.
`manifest.treePointsStatedFrame` pins the **stated** frame: a CRS and both units read beside the
pointer, a file refused because they are not the host's or are not all there, and the extent still
refusing rows that contradict a frame stated as the host's.

**`HPS-54` — A host is a fixed-frame host or an order-frame host, and says which in its own
`CLAUDE.md`.** Both kinds are the glossary's, and so is which of the two hosts is which; what this
rule adds is that a host **says it itself**, in its own onboarding document, where anyone working in
that tree reads it before touching placement rather than one document further out.

A new host declares it **when its block is designed**, not when it first meets a delivery tier that
makes the difference visible. The declaration is what tells a reader whether a file stated in the
delivery CRS is this host's frame or another host's — the same file, the same tier, opposite answers —
and a host that has not said which it is cannot apply `HPS-52` or `HPS-53` at all, because it has not
said what its host frame is.

_Enforcer:_ `agent-review` — one statement in each host's onboarding document, checkable by reading
two files and checked by nothing else.

---

## Reference-implementation deviations

The shipped code is the version-of-record. This standard is a transcription of it, and
it would read as one even where it is currently _ahead_ of the code — so the open gaps are named
here rather than left for a future reader to discover as a contradiction.

| Rule     | Deviation                                                                                                                                                                                                                                                                                                                                                                                   |
| -------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `HPS-53` | The reference's **tree-point reader**, the call site the rule was written against, is no longer a deviation: `MantlePlaceTreePointsLogic.cpp` reads the CRS and both units the `foliage_points` entry states and refuses the file by name unless they are the host's own (`manifest.treePointsStatedFrame`), and holds every row against the landscape extent its block publishes as the backstop (`manifest.treePointsFrame`). A manifest older than the format version that states the frame meets the extent alone, and there a bundle that ships a terrain mesh and no heightmap still brings in no tree points, because an unstated frame with nothing to hold it against is refused. The **named** half is behind in one place, narrower than it was: `MantlePlaceRoadSplinesLogic.cpp` now refuses the whole layer by name when the origin is not a UTM zone, and reports a layer whose roads all failed to place as such rather than as an empty one, but it still drops a single point its projection refused without naming it — a refusal with no name on it, one layer over. |
| `HPS-30` | The reference's **secret store** derives its blob name with its own per-code-unit walk and its own keep-set (`MantlePlaceSecretStore.cpp`, `ResolveSecretPath`), not with `SanitizeKeySegment`. It is inert today — `refresh_token` is the only key either host stores, and every derivation agrees on it — but the rule's secret-store sentence is ahead of this call site, not behind it. |

Two places where the _silo prose_ was wrong and the code was right went the other way and are
transcribed as shipped: `HPS-23` permits `"all"` as an explicit user-facing scope, and `HPS-44`
records that eviction is deliberately explicit-only.

## Enforcement summary

| Rule class                                                                                                                                                                                                                | Gate                                                   |
| ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------ |
| Version floor, host registration, floor declaration (`HPS-31`, `HPS-38`, `HPS-39`)                                                                                                                                        | `ci-manifest-conformance`                              |
| Headless conformance of the pure cores (`HPS-42`)                                                                                                                                                                         | `ci-revit-tests` (Revit), `ci-ue-compile` (Unreal)     |
| Protocol behaviour with corpus vectors (`HPS-04`, `HPS-07`, `HPS-10` … `HPS-12`, `HPS-19` … `HPS-25`, `HPS-25a`, `HPS-27`, `HPS-28`, `HPS-30`, `HPS-33` … `HPS-35`, `HPS-37`, `HPS-40`, `HPS-45` … `HPS-49`, `HPS-53`)               | `automation-test`, per host, against the shared corpus |
| Secrets, browser flow, cache promotion, pointer-driven paths, dumb-consumer doctrine (`HPS-03` … `HPS-06`, `HPS-06a`, `HPS-08`, `HPS-09`, `HPS-13` … `HPS-18`, `HPS-23`, `HPS-26`, `HPS-29`, `HPS-32`, `HPS-33`, `HPS-36`, `HPS-44`) | `agent-review`                                         |
| The triad — pure cores testable without the DCC (`HPS-02`)                                                                                                                                                                | `automation-test` + `agent-review`                     |
| Four-layer completeness, corpus coverage (`HPS-01`, `HPS-41`)                                                                                                                                                             | `pr-review`                                            |
| Corpus-reader coverage — asserted keys, vector leaves, nested leaves (`HPS-46`, `HPS-46a`, `HPS-46b`)                                                                                                                   | `automation-test` per host + `corpus/self-test/`       |
| The .NET SDK trigger (`HPS-43`)                                                                                                                                                                                           | `doc-only`                                             |
| The local install slot and its check script (`HPS-50`)                                                                                                                                                                    | `agent-review`, proven by running the scripts          |
| The shared user-facing vocabulary (`HPS-51`)                                                                                                                                                              | `agent-review`; a pure-core test where a host has one  |
| Placing from the host block, and refusing a file whose frame the host cannot match (`HPS-52`, `HPS-53`)                                                                                                    | `agent-review`, plus a pure-core test where a host has moved the decision out of its shim; **no corpus case yet** for `HPS-52`; both halves of `HPS-53` have one, for the fixed-frame host |
| The host's frame kind, declared in its own `CLAUDE.md` (`HPS-54`)                                                                                                                                          | `agent-review`                                         |
| The bounds on a vault listing nobody asked for (`HPS-55`)                                                                                                                                                  | `agent-review`; a pure-core test where a host has one  |

Rules with two enforcers (`HPS-02`, `HPS-04`, `HPS-23`, `HPS-24`, `HPS-26`, `HPS-33`) appear in both rows —
the corpus proves the behaviour, review catches the shape a vector cannot see. `HPS-53` appears twice
as well — in the corpus row and in its own — for a narrower reason than the rest: the corpus proves
it for the fixed-frame host's tree points, the extent substitute (`manifest.treePointsFrame`) and the
stated frame (`manifest.treePointsStatedFrame`), while every other file a host places is still that
host's own pure-core test and review.

The rules that **cannot fail loudly** are `HPS-07`, `HPS-10`, `HPS-20`, `HPS-21`, `HPS-23`,
`HPS-24`, `HPS-25a`, `HPS-26`, `HPS-27`, `HPS-30`, `HPS-31`, `HPS-32`, `HPS-33`, `HPS-49`, `HPS-52` and `HPS-53`. Each one produces a plugin that
imports successfully and is wrong. They are the reason this standard is normative. `HPS-46` sits
beside them one level up: its failure mode is a conformance suite that stays green while asserting
nothing, which is how every other silent failure gets back in.

## Contract version

The concrete pin is never restated here: it is machine-checked in
`verified-against.json` beside the conformance gate, both of which live in `mantleplace-dcc` and
run there. What follows is the version history that shaped the rules, kept because the reasoning
recurs at every future bump — it is not a statement of the current pin.

v18 normalised `dcc_readiness` to per-host keys and schema-declared the previously undeclared
emissions. v17 went into the reject set: **clean break, one supported
version** (`HPS-31`).

v19 added the `revit` host block and closed `reason` into one `deliveryReason` enum shared by
`packaging.not_delivered[]` and `dcc_readiness`. The two hosts were not in the
same position on it, and repinned separately:

- **`unreal`** — v19 changed nothing it branches on (`reason` is printed, not switched, `HPS-36`)
  and the `revit` block rode `additionalProperties`. Pin and floor moved to 19 together, on
  regenerated corpus fixtures.
- **`revit`** — the `revit` block was the whole point of v19, and its pin stayed at 18 until the
  reader parsed the block and the `revit` accept cases landed (with the `local_ft` tier fixture,
  which is what made the origin-unit-vs-artifact-unit split pinnable). Raising it sooner would have
  claimed an exercise that could not exist.

Worth stating plainly, because the gate's red reads the other way round: both readers compare
`version < floor`, so a host pinned below the published version **accepts and parses the newer shape
rather than refusing it**. A red pin means _untested_, not _incompatible_ — the more dangerous of
the two, and the reason the gate's message should name which it is.

Corpus case ids are deliberately version-agnostic (`manifest.full`, not `manifest.v18.full`) so a
bump is not a rename sweep.
