---
name: adr-0034-no-plugin-upsells
description: The plugins carry no price, no paid-tier wording and no locked row for content an order lacks. A locked Extra is left out, a refusal says "Not included in this bundle.", and Open in Curator stays because the website is where prices belong. Supersedes the locked-row decision in ADR 0030. Read before adding any copy about what an order does not include.
status: accepted
---

# 34. No plugin upsells

Date: 2026-11-23

## Status

Accepted. Decided by the founder. Supersedes the locked-row decision in [ADR 0030](0030-unreal-plugin-auth-vault-and-cesium.md): a locked Extra is no longer shown as a locked row.

## Context

The plugins listed the plan sheets and the terrain tiles of a free order as locked rows reading `More Extras come with paid sites`, with nothing to download. A plugin storefront reads paid-tier copy inside a plugin as a trial, and a trial is not what the plugins are: they are free, and the founder decided on 2026-11-23 that they stay free with nothing to sell inside them.

Without a recorded reason, the next ticket asks for paid-tier copy and an agent builds it, as happened once already.

## Decision

**A plugin never upsells.**

- **No price, no paid-tier wording, no locked row.** Nothing a plugin shows names a price, a plan, or what a paid order would add, and an Extra an order lacks has no row.
- **A locked Extra is left out.** The listing still marks each Extra `locked`; a reader leaves a locked Extra out of the vault and the import window, and never requests it.
- **A refusal is neutral.** When the platform refuses a request for an Extra (HTTP 403, code `extra_locked`), the reader says `Not included in this bundle.` and nothing more.
- **Open in Curator stays.** It opens the order on the website, and the website is where prices belong.

## Consequences

The locked-row copy comes out of the Revit and AutoCAD documentation, and the reader guide tells a new reader to leave a locked Extra out. A kept or paid order lists its Extras unlocked at once, so nothing changes for it.
