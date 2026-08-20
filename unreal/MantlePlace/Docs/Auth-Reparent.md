# Auth — Blueprint surface reparent steps (human, in-editor)

The auth **logic** now lives in C++: `UMantlePlaceAuthSystemBase`
(`Source/MantlePlaceRuntime/Public/MantlePlaceAuthSystemBase.h`, relative to the plugin root).
A human wires the **surface** in a Blueprint child that subclasses this C++ base — the
C++-base / Blueprint-child bridge.

> Note: `BP_MantlePlaceAuthSystemBase.uasset` now **exists** in the repository
> (`Content/Blueprints/BP_MantlePlaceAuthSystemBase.uasset`, parented to the C++ base;
> the old `UE_Placeholder.uasset` was removed). This doc is the maintained reference for
> its surface wiring — follow the steps below when re-creating or re-wiring it.

## Creating the Blueprint child

1. Open the consuming UE 5.8 project in the editor and make sure the C++ compiled
   (the editor builds the plugin modules on open; or build `MantlePlaceEditor` first).
2. In the Content Browser, browse to **MantlePlace Content → Blueprints**
   (enable *Settings → Show Plugin Content* if the plugin folder is hidden).
3. **Add → Blueprint Class**. In the picker, expand **All Classes**, search
   `MantlePlaceAuthSystemBase`, select it, **Select**.
4. Name it **`BP_MantlePlaceAuthSystemBase`**.
5. Configuration: the public mantle.place routes (`Web Login Url`, `Token Endpoint Url`) are
   compiled into the C++ base — leave them alone unless pointing at a non-production stack.
   `Platform Api Base Url` and `Supabase Anon Key` are **capture-sensitive** and are hydrated
   from the consuming project's `Config/DefaultGame.ini` (a
   `[/Script/MantlePlaceRuntime.MantlePlaceAuthSystemBase]` section) at
   packaging time. **Never set them in the BP's Class Defaults** — the BP ships with the
   plugin, and a value baked there is compiled into every distributed copy.
6. On the **Event Graph**, implement the events the C++ base raises:
   `On Sign In Result`, `On Sign Out Complete`, `On Token Refreshed`,
   `On Auth State Changed` — wire them to your UI (show error widget, transition on
   success, etc.).
   - **Sign in:** call **`Sign In With Browser`** (the modern path — OAuth 2.0 Authorization
     Code Flow + PKCE through the system browser; the app never sees the password). The result
     arrives on `On Sign In Result(bSuccess, Message)`.
   - **Cancel:** call `Cancel Sign In` (e.g. from a "Cancel" button) to abort an in-flight
     browser sign-in; it fires `On Sign In Result(false, …)` and returns to `Unauthenticated`.
   - **Startup:** call `Try Restore Session` once on startup to silently resume a stored session;
     the result arrives on `On Token Refreshed(bSuccess)`.
   - `Sign Out` / `Refresh Token` are unchanged. The legacy `Sign In(email, password)` is
     **disabled** unless `bAllowPasswordGrant` is set true (kept only for dev/automation).

## Division of labor (keep it)

- Auth **logic** changes → C++ pull requests. Reviewable, unit-tested, greppable.
- Auth **surface** changes (which widget, transitions, per-instance config) → Blueprint
  edits, done in the editor.

The C++ base never edits the Blueprint; the Blueprint never holds logic.

## Platform dependency

`Sign In With Browser` requires a companion endpoint on `mantle.place`: a native-login
route (`Web Login Url`) that accepts `code_challenge` / `code_challenge_method=S256` / `state` /
`redirect_uri`, completes Supabase auth, and 302-redirects to the loopback `redirect_uri` with
`?code=…&state=…`; plus a token endpoint (`Token Endpoint Url`) that validates `code` + the app's
`code_verifier` and returns the Supabase token JSON (`access_token`, `refresh_token`, `expires_in`,
`user`). Until that ships, browser sign-in can be exercised against the identity provider directly
by clearing `Token Endpoint Url` (uses `grant_type=pkce`), with the loopback URIs allow-listed
there.

## Live verification (needs the platform companion route / real credentials — not run in CI)

1. Set `PlatformApiBaseUrl` and `SupabaseAnonKey` in the project's `Config/DefaultGame.ini`
   (never the BP class defaults — see step 5 above); the public routes and `LoopbackPorts` have
   compiled defaults. Allow-list the loopback redirect URIs with the identity provider.
2. Construct `BP_MantlePlaceAuthSystemBase`, bind `On Sign In Result`, call
   `Sign In With Browser`. The system browser opens the mantle.place login; after logging in the
   tab shows "you can close this window."
3. Confirm `On Sign In Result(true, …)`, `Get Auth State == Authenticated`, and `Get Access Token`
   returns a non-empty JWT; a vault call (`UMantlePlaceVaultClient`) then succeeds.
4. **Restart the app** and call `Try Restore Session` → `On Token Refreshed(true)` resumes the
   session without re-login. `Sign Out` clears the stored token (`Saved/MantlePlace/secret_refresh_token.bin`)
   so the next launch requires sign-in.

The headless automation tests cover the deterministic core with no network:
`MantlePlace.Auth.Logic` (URL/body construction, base64url + PKCE S256 [RFC 7636 vector],
callback parsing, expiry, state machine) and `MantlePlace.Auth.SecretStore` (DPAPI round-trip, Win64).
