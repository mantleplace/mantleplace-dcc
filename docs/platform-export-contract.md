---
name: platform-export-contract
description: The cross-host export contract (protocol 1) both host plugins build against to receive an export from the web vault — the per-curator private channel and its presence payload ConnectedHost, how a plugin receives and catches up on exports, the export lifecycle with the transitions each actor may make, the two v1 routes with request and response shapes, the refusal codes and rate limits, how the bundle is then fetched, and the additive-only rule with how the protocol number moves, and where the Realtime socket and its publishable key are published. Read from Unreal or Revit work alike when writing or changing connected-host code.
---

# What the platform must serve for a plugin to receive an export

**Protocol 1.** This document describes what the platform must serve. It is a mirror: the platform's
own copy of the contract is the authority, and where the two disagree the platform's copy wins and
this file is the bug. Like [`platform-auth-contract.md`](platform-auth-contract.md), it is
cross-host and lives at the repository root, because both host plugins build against the same wire.

An **export** hands a kept bundle from the web vault to a **connected host**: a program (Unreal or
Revit) running the Mantle Place plugin, signed in as the same curator. The export carries no file and
no URL. It is a row that names a bundle and a target; the plugin fetches the bundle itself, through
the v1 routes it already uses. Terms are in [`CONTEXT.md`](../CONTEXT.md).

## Where the socket is and which key opens it

The platform publishes both on a public route, [`https://mantle.place/api/v1/realtime`](https://mantle.place/api/v1/realtime).
This file restates neither value: a plugin reads them from that route, and follows the platform if it
moves either. A local configuration override wins over the route, and a connected host that can
reach neither stays off: it joins no channel and tracks no presence. The curator still has Download.

## 1. The channel

The transport is Supabase Realtime, with access policies that admit only the owning curator.

- **Topic:** `vault:<auth user id>`, one channel per curator. The id is the signed-in curator's
  Supabase auth user id.
- **Session:** join with the Supabase session from the plugin's loopback sign-in (a real Supabase
  session). The web joins the same topic with its own session.
- **Join as a private channel.** Set `private: true` in the channel config. The access policies on
  Realtime messages apply only to private joins, so a plugin that joins publicly is outside the
  contract: its presence is not protected and a host written that way is not supported.
- **Presence only.** The policies admit presence on the session's own topic and nothing else. A
  session cannot join or read another curator's topic. There is no Broadcast on this channel; do
  not send or expect any.
- **Presence key:** the connection id (section 2).
- **Authorize the socket.** Give Realtime the session's access token (`setAuth`) before joining and
  again after every refresh; Supabase's client libraries do this when the session is set on them.
- **The join can be refused, and can fail for a moment.** A session that does not own the topic is
  refused with `CHANNEL_ERROR` and an `Unauthorized` message: do not retry that. Any other
  `CHANNEL_ERROR` (seen once, just after UTC midnight, with the message "MissingPartition: Realtime
  was unable to find the expected messages partition") is transient: retry with backoff. The
  Supabase client rejoins on its own; on every `SUBSCRIBED`, track presence again and run the catch-up of section 3.
- **Row changes ride the same channel.** The `postgres_changes` subscriptions of section 3 attach to
  this private channel; a plugin needs no second
  channel for them. A change to a bundle's order (section 3) is a separate subscription.

## 2. Presence: `ConnectedHost`

A plugin tracks one presence entry on the channel under its **connection id**: a random id
generated once per host launch (not per sign-in, and not persisted across launches). The web reads
presence and never writes it.

```ts
interface ConnectedHost {
  protocol: 1; // the wire protocol this plugin speaks
  host: 'unreal' | 'revit' | 'autocad';
  hostVersion: string; // the host program's version
  pluginVersion: string;
  document: string | null; // open project or document; null when none is open
  machine: string; // machine name, shown to the curator
  reads: { min: string; maxMajor: number }; // the manifest versions this plugin reads
}
```

- `reads.min` is the lowest bundle manifest version the plugin reads; `reads.maxMajor` is the
  highest manifest major version. The web greys out a host whose range excludes a bundle's manifest
  version. A bundle whose version is unknown is allowed. The web compares versions by number
  (1.10.0 is above 1.9.0) and counts a pre-history integer manifest version (7 to 19) as below
  1.0.0, so a plugin whose `reads.min` is 1.0.0 or later is greyed for it. A prerelease suffix on either
  version (`2.0.0-rc.1`) is ignored in the comparison: the web greys only on proof, so a
  prerelease of `reads.min` counts as that release.
- Re-track (update the payload) when `document` changes. Untrack, or leave, on shutdown.
- A host that leaves presence before an export finishes can have that export failed by the web
  (`host_disconnected`, section 4; web-client behaviour, not server enforcement).

## 3. Receiving exports

A plugin sees an export in two ways, and uses both:

1. **Live.** Subscribe to Postgres changes on `public.bundle_exports` with the session's token,
   filtered to `target_connection_id=eq.<connection id>`. A new export arrives as an `INSERT` with
   `status = 'sent'`; later status changes arrive as `UPDATE`. Row changes carry snake_case
   columns: `id`, `order_id`, `host`, `target_connection_id`, `target_document`, `target_machine`,
   `target_plugin_version`, `status`, `progress`, `failure_reason`, `failure_message`,
   `created_at`, `updated_at`, `ended_at`. Rows are owner-read: a session receives only its own
   account's changes.
2. **Catch-up on connect.** After joining (and after every reconnect), call
   `GET /api/v1/exports?connectionId=<id>` (section 5) for exports that arrived while the
   subscription was down. Treat a row from the list and a row from a change the same way.

An export is addressed by its `id`. A plugin acts on an export only when `target_connection_id`
equals its own connection id.

**A bundle that is still building.** An export may be created for a paid bundle that is still being
built. The plugin moves it to `waiting_for_bundle` and watches that bundle's order through row
changes on `public.orders` (filter `id=eq.<order_id>`). What the v1 download route answers before the bundle is complete depends on the request:

- `format: "bundle"` with no `view`: `404`, `code: "not_ready"`. The whole archive is never served
  before the bundle seals.
- `format: "bundle"` with a host `view`: `200` with a **partial** view, holding the files landed so
  far and flagged partial in the response's `view` block. It is not an error, and it is not
  importable.
- A single `format` whose artifact is not there yet: `404`, `code: "not_ready"`.

The rule: a plugin must not import, nor advance the export to `importing` or `imported`, until the
bundle is ready, whatever the download route returned. Ready is read from the order row: `status = "fulfilled"`, or `status = "paid"`
with `processing_phase = "ready"`. Keep waiting until the row
shows it, then move on to `awaiting_options`.

## 4. The lifecycle

Statuses, in the order of an export's life: `sent`, `received`, `waiting_for_bundle`,
`awaiting_options`, `importing`, `imported`, `failed`, `cancelled`. The terminal statuses are
`imported`, `failed` and `cancelled`: no transition leaves them.

`progress` is a whole number 0 to 100, set only while `importing` (it starts at 0 on entering
`importing`) and null otherwise. `failure_reason` is set only on `failed`.

### What the plugin may do (bearer session, section 5)

| `status` in the request | From                             | Notes                                                                                        |
| ----------------------- | -------------------------------- | -------------------------------------------------------------------------------------------- |
| `received`              | `sent`                           | Send as soon as the export is seen.                                                          |
| `waiting_for_bundle`    | `received`                       | The bundle is not ready to fetch yet.                                                        |
| `awaiting_options`      | `received`, `waiting_for_bundle` | The bundle is fetchable; the import dialog is open and waiting for the curator.              |
| `importing`             | `awaiting_options`               | The first call starts the import at progress 0 (a `progress` sent with it is ignored).       |
| `importing` (progress)  | `importing`                      | Requires `progress` (0 to 100). Send about every 2 seconds.                                  |
| `imported`              | `importing`                      | The bundle is in the project.                                                                |
| `cancelled`             | `awaiting_options`               | The curator closed the import dialog.                                                        |
| `failed`                | any non-terminal status          | Requires `reason` (a short code); `message` is the plugin's own words, shown to the curator. |

A bundle the plugin cannot read is `failed` with `reason: "unsupported_version"`. Reason codes are
the plugin's to choose, but a `reason` is a code, never free text: it must match
`^[a-z0-9_]{1,64}$` (lower-case letters, digits and underscores, 1 to 64 characters), because it
goes to analytics unchanged. A `reason` outside the pattern is refused `400 invalid_body`. The
plugin's own words go in `message` (stored and shown to the curator, never emitted). Two codes are
the web's, `not_received` and `host_disconnected`: a plugin MUST NOT send them. The server does not
enforce this.

### What the web may do (curator session; not part of the v1 routes)

- Create the export as `sent`.
- Cancel it from `sent`, `received`, `waiting_for_bundle` or `awaiting_options`. A plugin sees this
  as a row change to `cancelled`; stop work and close any open dialog. The web cannot cancel once
  `importing` has begun.
- Fail it with `not_received` (no host acknowledged a `sent` export within 60 seconds) or
  `host_disconnected` (the target left presence before a terminal status and did not rejoin within
  5 seconds), from any non-terminal status. This is web-client behaviour, not server enforcement:
  it runs only
  while the page is open, and the leave check only while the page's own channel is joined, so a
  plugin whose socket drops and rejoins under the same connection id within the grace keeps its
  export.

A plugin must expect a call to be refused because the web moved the export first (for example
cancelled it): section 5, `invalid_transition`.

## 5. The v1 routes

**Auth** (both routes): `Authorization: Bearer <access token>`, the plugin's loopback
session; not the cookie session. The routes are not behind the web's export flag: a plugin that
speaks this protocol can always call them. Refresh the access token as the plugin already does for
the other v1 routes ([`platform-auth-contract.md`](platform-auth-contract.md)).

**Error shape:** `{ "error": string, "code"?: string, "detail"?: string }`.

### `GET /api/v1/exports?connectionId=<id>`

The bearer's exports that target this connection and are not terminal, oldest first.

- Query: `connectionId`, 1 to 200 characters. Required.
- `200`: `{ "exports": ExportDto[] }`. Exports of other accounts are never listed.

### `POST /api/v1/exports/{exportId}/advance`

Move one export forward along section 4.

- Body (strict: unknown keys are refused):

  ```ts
  {
    connectionId: string; // 1-200 chars; this plugin's connection id
    status:
      | 'received' | 'waiting_for_bundle' | 'awaiting_options' | 'importing'
      | 'imported' | 'failed' | 'cancelled';
    progress?: number; // integer 0-100; required on an importing progress report
    reason?: string;   // /^[a-z0-9_]{1,64}$/; required with 'failed'
    message?: string;  // up to 1000 chars; with 'failed'
  }
  ```

- `200`: the updated `ExportDto`.
- A transition applies only if the status is still the one the server read; if the export moved in
  between, the answer is `409 invalid_transition`.

### `ExportDto`

```ts
interface ExportDto {
  id: string;
  orderId: string;
  host: 'unreal' | 'revit' | 'autocad';
  targetConnectionId: string;
  target: { document: string | null; machine: string | null; pluginVersion: string | null };
  status:
    | 'sent'
    | 'received'
    | 'waiting_for_bundle'
    | 'awaiting_options'
    | 'importing'
    | 'imported'
    | 'failed'
    | 'cancelled';
  progress: number | null;
  failureReason: string | null;
  failureMessage: string | null;
  createdAt: string; // ISO 8601
  updatedAt: string;
  endedAt: string | null; // set on a terminal status
}
```

The routes return camelCase; the Realtime row change carries the same fields as snake_case columns
(section 3). Neither carries a file or URL.

### Refusals

| HTTP | `code`               | Meaning                                                                                                                                                                      |
| ---- | -------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 400  | `invalid_query`      | List: `connectionId` missing or malformed.                                                                                                                                   |
| 400  | `invalid_body`       | Advance: the body is not JSON, has an unknown key, or a field is out of range.                                                                                               |
| 401  | none                 | The bearer check failed. See below.                                                                                                                                          |
| 403  | `wrong_target`       | The export belongs to this account but targets another connection id.                                                                                                        |
| 403  | `forbidden`          | Reserved. A plugin cannot trigger it today; the platform admits every event a plugin may send. Do not depend on it.                                                           |
| 404  | `not_found`          | No such export for this account. A missing export and another account's export answer the same.                                                                              |
| 409  | `invalid_transition` | The export's current status does not allow it: out of order, already terminal, an importing report with no `progress`, a `failed` with no `reason`, or it changed under you. |
| 429  | `rate_limited`       | Over the rate limit; the body carries `retryAfterMs`.                                                                                                                        |
| 500  | `internal_error`     | Retry later.                                                                                                                                                                 |

**A 401 carries no `code`.** The body is `{ "error": "Unauthenticated" }` (no bearer token) or
`{ "error": "Invalid or expired token" }`. A plugin tells it apart by status alone: on `401`,
refresh the access token and retry once; if that fails, sign the curator in again.

**Rate limits, per source IP address, over 10 minutes:** `GET /api/v1/exports` 60 requests;
`POST /api/v1/exports/{exportId}/advance` 1200 requests. The limit is checked before the token, so a
`429` can arrive without a valid session. Back off by `retryAfterMs`. A plugin lists once per
connect and reports progress about every 2 seconds, well inside both.

### Who can advance an export

An export is scoped to the account, and the connection id is the only thing that scopes it to a
plugin. **Any bearer session of the same account that knows an export's `targetConnectionId` can
advance that export.** The connection id is not a secret held against the account's other sessions,
and the server does not tie it to the session that tracked it in presence. Treat the connection id
as visible to every signed-in program of the curator. A different account can never see or advance
the export.

## 6. Fetching the bundle

An export names the bundle by `orderId`; it grants nothing. Fetch with the existing v1 vault routes,
on the same bearer session:

- `POST /api/v1/vault/bundles/{orderId}/download` mints a short-lived download link for the whole
  bundle (`format: "bundle"`, optionally with a host `view`) or for one format. Before the bundle is
  complete, `format: "bundle"` without a `view` and a not-yet-built single format answer `404` with
  `code: "not_ready"`, while `format: "bundle"` with a host `view` returns a partial view (section
  3). Do not import until the order row shows the bundle ready.
- `POST /api/v1/vault/bundles/{orderId}/materialize` generates additional on-demand formats, and
  `GET` on the same route reports its status.
- `GET /api/v1/vault/bundles` lists the curator's bundles with `manifestVersion` (null when
  unknown), which a plugin may check against its own readable range before it moves to
  `awaiting_options`.

The same ownership checks apply as for any import. An export of a refunded, failed, expired, deleted
or withheld bundle is refused when the web creates it, so a plugin does not meet one; if the bundle
becomes unavailable afterwards, move the export to `failed` with a reason.

## 7. Compatibility: additive changes only

The contract changes **additively only** within a protocol number:

- New optional fields on `ConnectedHost`, `ExportDto`, request bodies and row changes.
- New optional query parameters.
- New refusal codes and new `reason` codes.

New statuses are **not** additive: a plugin would not know how to handle them. They need a new
protocol number.

A plugin must therefore ignore fields it does not know, and treat an unknown refusal `code` by its
HTTP status class (retry later on `5xx` and `429`; do not retry on other `4xx`).

When a change cannot be additive (a removed or renamed field or status, a changed meaning, a new
required request field), `protocol` increases, and the contract is revised under the new number.
Every `ConnectedHost` announces its `protocol`, so the web can tell which number a host speaks and
decline to offer an export to a host on a number it does not serve.
