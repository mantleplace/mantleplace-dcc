# Revit 1.3.0: release notes

The body the `revit-1.3.0` release takes, titled `revit-1.3.0 — Mantle Place for Revit` and written to
[`docs/releases.md`](../../docs/releases.md). The founder confirms what is in it before the cut. The
**Verification** section holds the release gate's results on the packaged zip; the published-asset and storefront
lines are filled in when the release is cut and the zip is submitted. What an MPB 3.0 import places and leaves
out is unchanged from 1.2.0 and stays in [`release-notes-1.2.0.md`](release-notes-1.2.0.md). The published body
cites each pull request by its bare number, as [`docs/releases.md`](../../docs/releases.md) asks; this file links
each number instead, because a bare number in a tracked file is refused by the public-reference gate.

## The release body

```markdown
**Revit 2025.5 or later, 2026.5 or later, and 2027, Windows 64-bit.** One build serves all three. Close Revit, extract the zip and run `MantlePlace.bundle\Install.cmd`, or copy the files directly inside `MantlePlace.bundle\Contents` into your add-ins folder as `README.txt` describes. The plugin is unsigned, so each Revit version asks once: choose **Always Load**. [Install guide](https://github.com/mantleplace/mantleplace-dcc/blob/main/revit/README.md) · [mantle.place/revit](https://mantle.place/revit)
Upgrade by closing Revit and running the new `Install.cmd` over the old one; nothing else to do.

## What's new

- **Road surfaces from an MPB 3.0 order import.** A download from the vault now asks for the road surface file, which it left out before ([748](https://github.com/mantleplace/mantleplace-dcc/pull/748)).
- **Road and water surfaces are cut in batches**, polygons near each other in one subdivision, so an import of a thousand roads no longer takes most of an hour or grows Revit by tens of gigabytes ([762](https://github.com/mantleplace/mantleplace-dcc/pull/762), [777](https://github.com/mantleplace/mantleplace-dcc/pull/777)).
- **The vault and import windows list only what an order includes**, with no row for an Extra the order does not include and no wording about paid sites ([773](https://github.com/mantleplace/mantleplace-dcc/pull/773)).
- **Open in Curator says where it goes** in its tooltip before it is pressed: mantle.place in the browser, where sites are drawn and ordered ([772](https://github.com/mantleplace/mantleplace-dcc/pull/772)).
- **The round Mantle Place mark** is on the ribbon buttons, the window title bars and the bundle's icon, the same mark mantle.place shows ([753](https://github.com/mantleplace/mantleplace-dcc/pull/753)).
- **Sign In refuses a configuration that splits it across two servers**, naming both, and names a server that does not answer ([751](https://github.com/mantleplace/mantleplace-dcc/pull/751)).
- **An origin stated in another coordinate system** names each layer it leaves out ([762](https://github.com/mantleplace/mantleplace-dcc/pull/762)), and a re-import of a bundle on a rotated grid is placed at a zero offset instead of being refused ([739](https://github.com/mantleplace/mantleplace-dcc/pull/739)).

## Known issues

- **Revit asks before loading the plugin**, because it is not signed: choose **Always Load**.
- **Import an MPB 3.0 order into a project that holds no earlier import**, as in 1.2.0 ([677](https://github.com/mantleplace/mantleplace-dcc/issues/677)).

## Verification

- Built from `f5185af`: `main` at `67f10d4` with the version set to 1.3.0. Release gate (2026-11-24) at that commit, against this zip installed clean by the host harness in Revit 2025, Revit 2026 and Revit 2027, signed in on the dev platform where a check signs in: the plugin loaded with its ribbon; its sign-in, vault, About and notice windows opened and read as the plugin's labels say; every command on the ribbon ran from the ribbon, Open in Curator included; the Austin Downtown sample from mantle.place/samples, downloaded at Min, was imported from disk with Import Bundle, every layer, with no account; and a free 3.0.0 order on the dev platform was chosen in the vault window and imported at Min, every layer.

  - `revit.plugin-loads`: pass, 8 of 8 in each year
  - `revit.plugin-windows`: pass, 50 of 50 in each year
  - `revit.ribbon-commands`: pass, 43 of 43 in each year
  - `revit.import-check`, a free 3.0.0 order at Min through the vault window: pass, 41 of 41 in each year
  - `revit.import-check`, the Austin Downtown sample at Min (bundle 3.0.0) from disk, no account: pass, 19 of 19 in Revit 2025; in Revit 2026 and 2027 the import finished and the 10 assertions on it passed, and the check then ended as an error at its view export, which the harness gives 60 s, on a project of about 188,000 elements; a rerun in Revit 2027 ended the same way

  The Austin Downtown import at Min, every layer, took 1,428 s (about 24 minutes) in Revit 2025, 1,936 s (about 32 minutes) in Revit 2026 and 796 s (about 13 minutes) in Revit 2027; the road surfaces are 61 to 76 % of it. With Road Subdivisions unticked, the path the reviewer guide gives, the same import through the import window took 257 s in Revit 2025, 456 s in Revit 2026 and 421 s in Revit 2027 (2026-11-24, the 1.3.0 source at `55e39f3`, whose Revit code is the gated commit's). Each step's time is in [`docs/import-timing.md`](https://github.com/mantleplace/mantleplace-dcc/blob/main/docs/import-timing.md).

  The vault window was captured with the free order chosen in Revit 2025, 2026 and 2027 (2026-11-24, this zip installed clean by the host harness, signed in on the dev platform), drawn by `PrintWindow` with the gate machine's desktop connected. Each image shows the free order's row selected and the level control reading `Max — 69.2 MB (recommended)` above the order list, with no locked row and no wording about paid sites, under the window's title bar and the Mantle Place mark.
- Published asset: <check from step 4 and its date>.
- `MantlePlace-Revit-1.3.0.zip` sha256 `39321d2e7e89dfe9179924a538290fdc5ad81f8ae78b1d7d63b2dde38583a9e1`
- Storefront: <submitted to the Autodesk storefront on date>, from the asset above.

Previous: [revit-1.2.0](https://github.com/mantleplace/mantleplace-dcc/releases/tag/revit-1.2.0)
```
