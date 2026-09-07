# What the platform must serve for the Unreal plugin to sign in

This plugin holds no secret and derives no endpoint. Every route it talks to is a public
`mantle.place` route compiled into `UMantlePlaceAuthSystemBase`, and every one of them has to exist
for a user to sign in, stay signed in, or sign out. This file is the list, so that a route going
missing is a thing someone can look up rather than a thing a curator reports as "it asks me to sign
in every time".

There is no packaging-time configuration for auth. There used to be — refresh and restore were
identity-provider-direct and needed values hydrated into the consuming project's `DefaultGame.ini`,
which meant sign-in worked on a plain clone and *staying* signed in did not. That was one of the
causes of the sign-in-every-session defect, and the fix was to delete the path rather than document
it better.

## The routes

**Native login** — `WebLoginUrl`, default `https://mantle.place/auth/native`.

Accepts `response_type=code`, `code_challenge`, `code_challenge_method=S256`, `state` and
`redirect_uri` as query parameters, completes authentication however it chooses, and 302-redirects
to the supplied loopback `redirect_uri` carrying `?code=…&state=…`. The `redirect_uri` is always
`http://127.0.0.1:<port>/callback` with an OS-assigned port, so whatever validates redirect URIs has
to accept a loopback address on an arbitrary port — this is RFC 8252 §7.3, and pinning the port
instead is what breaks on machines whose reserved port ranges move across reboots.

**Token exchange** — `TokenEndpointUrl`, default `https://mantle.place/api/v1/auth/native/token`.

`POST` with `{"auth_code": …, "code_verifier": …}`. Validates the verifier against the challenge from
the login step and returns the token JSON below.

**Refresh** — `RefreshEndpointUrl`, default `https://mantle.place/api/v1/auth/native/refresh`.

`POST` with `{"refresh_token": …}`, returning the same token JSON. This is the route a curator's
session depends on every time they open the vault after the access token has aged out, and it is the
one that is exercised least during development, because signing in freshly never touches it.

## The token response

`access_token`, `refresh_token`, `expires_in`, and a `user` object. `expires_in` is seconds and
relative; a response that omits it, or gives a non-positive value, is read as one hour. A response
that omits `refresh_token` leaves the stored one in place rather than clearing it.

## What a rejection has to say

The single most consequential thing the platform communicates is the difference between *this
refresh token is dead* and *I could not answer right now*. The plugin discards a stored credential
only on the first, and keeps it on the second, because discarding on a transient failure costs a
session to a dropped packet and keeping on a permanent one produces a client that retries a revoked
credential forever without ever asking the user to sign in.

That decision is made from the HTTP status and an error **code**, never the human-readable
description:

- **Definitive** — status `400` or `401`, with `error`, `error_code` or `code` equal to
  `invalid_grant`, `invalid_refresh_token`, `refresh_token_not_found` or
  `refresh_token_already_used`.
- **Transient** — everything else, including any `5xx`, any body that will not parse as JSON, and a
  `400` whose code is not in that list.

A new spelling for "this grant is dead" is therefore a breaking change on the platform side even
though nothing in the schema moves: the client reads it as transient and keeps retrying.

## Rotation

Both host plugins share one credential per OS user, so both may present the same refresh token. If
the platform rotates on use, the second presenter is rejected for a token that is merely superseded.
The client handles this — it re-reads the store and retries once before believing a rejection — but
it is the reason a rotating platform must keep `refresh_token_already_used` distinguishable from a
revoked session rather than collapsing both into one code.

## Sign-out

Sign-out is currently **local only**: the plugin clears the stored credential and forgets the
session, and the refresh token remains valid at the platform until it ages out. Closing that gap
needs a revocation route, which does not exist yet. It is worth stating plainly rather than leaving
implied, because a user who signs out reasonably believes the credential stopped working.

## Verifying by hand

None of this runs in CI: the Unreal compile is private, and the auth flow needs a real account and a
live platform. What a person can check on a machine with the plugin installed:

1. Sign in from the vault panel. The system browser opens, and the panel lists the vault when it
   returns.
2. Confirm `%LOCALAPPDATA%\MantlePlace\auth\refresh-token.bin` exists, and that its bytes do not
   contain the token in plain text.
3. Restart the editor, open the vault panel, and confirm it lists **without** a sign-in. This is the
   step that would have caught the original defect, and it is the one worth running after any change
   to the auth path.
4. Sign in inside Revit on the same machine and confirm Unreal picks up the same session, and the
   reverse. One machine identity is only real if both directions work.
5. Sign out, and confirm the file is gone and the panel returns to its signed-out state.
