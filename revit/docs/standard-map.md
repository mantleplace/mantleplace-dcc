# Revit and the Host Plugin Standard

The Host Plugin Standard ([`docs/host-plugin-standard.md`](../../docs/host-plugin-standard.md)) is
**normative** for this tree, in whatever language fits the host. Rules carry `HPS-NN` ids and are
cited by id throughout the code. Before writing auth, the vault client, the bundle cache or anything
touching the manifest, read the relevant section — the ⛔ rules all guard the same failure class:
_the plugin appears to work_.

This table is what each rule this tree turns on means here, and which types carry it. It moved here
from [`revit/CLAUDE.md`](../CLAUDE.md), which keeps the index.

| Rule                  | What it means here                                                                                                                                            |
| --------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `HPS-02`              | every layer is a triad — impure shim / pure core / headless test. Protocol logic never goes in the shim.                                                      |
| `HPS-31`              | one supported manifest version, one home for the floor: `ManifestVersions.MinSupportedManifestVersion`.                                                       |
| `HPS-32`              | artifact paths come from `layout` (or the artifact block), never from folder convention.                                                                      |
| `HPS-33`              | manifest values are applied verbatim. This host does not compute a survey point.                                                                              |
| `HPS-36`              | read the `hosts.revit` subtree only. Never a sibling host's block, never the retired flat keys.                                                               |
| `HPS-38`/`39`         | `revit` is registered in `verified-against.json`, with its floor declared as path + regex.                                                                    |
| `HPS-40`/`41`         | the suite drives the shared corpus at run time and fails on an unknown expectation key.                                                                       |
| `HPS-04` … `13`       | PKCE `S256` in the system browser, loopback on the literal `127.0.0.1`, five-state machine driven from the corpus table.                                      |
| `HPS-14` … `17`       | refresh token via DPAPI, per-OS-user; access token memory-only; no store means memory-only auth, never a less-safe file.                                      |
| `HPS-18` … `25`, `48` | list → materialize → poll → **re-list** → presign → download; explicit token list, never a scope keyword; one error-body precedence for auth and vault alike. |
| `HPS-55`              | the background listing is `VaultNewsChecker`, its interval and floor `VaultNewsCadence`, and which auth transition is a sign-in `SignInEdge` — a startup restore is one, a token renewal is not. Which orders are news is `VaultNews`, over the one per-machine record `AnnouncedOrderStore` shares between every Revit on the machine. It is the **only** background listing: `PrepareRejoiner` re-joins interrupted Prepares from the checker's first listing after a sign-in (`VaultNewsChecker.Listed`) and never lists for itself. |
| `HPS-26` … `30`, `44` | write to `.part`, verify, rename; null sha is unknown not absent; eviction only on request.                                                                   |
| `HPS-45`              | `projection` IS claimed, for one thing only: the lon/lat `vector` layers of a bundle whose block carries no `hosts.revit.vectors`. Nothing else here projects, and the projection reaches a UTM origin only — on a State Plane origin there is no projection to perform and the layer is skipped. |
| `HPS-51`              | signing in and out, the vault, the local import, the import window with its checklist, its unavailable list (`WindowLabels.UnavailableReason`, decided per `SkipReasonCode`) and its unit-system line (`DeliveryHeader`), and the about surface take the standard's words. The casing is this host's; the words are not. |
| `HPS-52`              | placement reads `hosts.revit` first. The terrain points, the surface DXF and the site IFC, the vector layers (`vectors`) and the drape (`drape`) come from that block and are in this frame; only a bundle whose block carries no copy, or no `path` for one, falls back to the shared pointer, `layout` first. The tree points come from the host-neutral `landcover.tree_points` and are in this frame only because the delivery CRS is. |
| `HPS-53`              | `SiteFrame` decides the frame — `CanPlaceGeographic` and `CanPlaceProjected` for the CRS, `Holds` for whether the block's declared `file_frame` is the origin's frame, `IsInOriginUnit` for an absolute file's unit — and the planner's `OwnFrameRefusal` checks each own-block file against that `file_frame`. The refusals are what the tests assert. A file's own `units` is read, never the origin's substituted for it: on a local grid they differ. The datum is the frame's third part: the ground's stamp records the datum it was built in (`TerrainIdentity`), each height step carries the datum its content states (`ImportStep.HeightDatum`), and `HeightDatums.Refusal` skips the step by name where they differ, converting nothing (corpus case `manifest.revitHeightDatum`). |
