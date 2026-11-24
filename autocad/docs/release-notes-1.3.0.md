# AutoCAD 1.3.0: release notes

The body the `autocad-1.3.0` release takes, titled `autocad-1.3.0 — Mantle Place for AutoCAD` and written to
[`docs/releases.md`](../../docs/releases.md). The founder confirms what is in it before the cut. The
**Verification** section holds the AutoCAD half of the release gate on the packaged zip; the Civil 3D half is
added when it runs, and the published-asset and storefront lines are filled in when the release is cut and the
zip is submitted. What an MPB 3.0 import places and leaves out is unchanged from 1.2.0 and stays in
[`release-notes-1.2.0.md`](release-notes-1.2.0.md). The published body cites each pull request by its bare
number, as [`docs/releases.md`](../../docs/releases.md) asks; this file links each number instead, because a
bare number in a tracked file is refused by the public-reference gate.

## The release body

```markdown
**AutoCAD and Civil 3D 2025, 2026 and 2027, Windows 64-bit.** One package serves both products and all three years: close AutoCAD, extract the zip and run `MantlePlaceAutoCad.bundle\Install.cmd`, or copy the `MantlePlaceAutoCad.bundle` folder into `%APPDATA%\Autodesk\ApplicationPlugins` as `README.txt` describes. The plugin is unsigned, so each AutoCAD version asks once: choose **Always Load**. [Install guide](https://github.com/mantleplace/mantleplace-dcc/blob/main/autocad/README.md)
Upgrade by closing AutoCAD and running the new `Install.cmd` over the old one; nothing else to do.

## What's new

- **The import window lists each step's time** in its closing report, under *Step times (seconds)* ([775](https://github.com/mantleplace/mantleplace-dcc/pull/775)).
- **The vault and import windows list only what an order includes**, with no row for an Extra the order does not include and no wording about paid sites ([773](https://github.com/mantleplace/mantleplace-dcc/pull/773)).
- **The round Mantle Place mark** is on the ribbon buttons, the window title bars and the bundle's icon, the same mark mantle.place shows ([753](https://github.com/mantleplace/mantleplace-dcc/pull/753)).
- **Sign In refuses a configuration that splits it across two servers**, naming both, and names a server that does not answer ([751](https://github.com/mantleplace/mantleplace-dcc/pull/751)).

## Known issues

- **AutoCAD asks before loading the plugin**, because it is not signed: choose **Always Load**.

## Verification

- Built from `de2e981`: `main` at `5d30fb5` with the version set to 1.3.0. Release gate (2026-11-24) at that commit, against this zip installed clean by the host harness in AutoCAD 2025, AutoCAD 2026 and AutoCAD 2027: the plugin loaded with its panel, a button for each command and F1 on each; its sign-in, vault, About and notice windows opened, signed in on the dev platform, and read as the plugin's labels say; the Austin Downtown sample from mantle.place/samples, downloaded at Min, was imported from disk with Import Bundle and no account, with the refusal, version-refusal and second-import passes; and a free 3.0.0 order on the dev platform was chosen in the vault window and imported at Min.

  - `autocad.plugin-loads`: pass, 13 of 13 in each year
  - `autocad.plugin-windows`: pass, 47 of 47 in each year
  - `autocad.import-check`, the Austin Downtown sample at Min (bundle 3.0.0) from disk, no account: pass, 35 of 35 in each year
  - `autocad.import-check`, a free 3.0.0 order at Min through the vault window: pass, 33 of 33 in each year

  With the free order chosen, the vault window's level control offered Max, Med and Min, each with its size, and no other row, in each year. Sign Out and Send Feedback were typed in a session of each year with this zip's bundle installed: Sign Out removed a stored session file placed for the test, and Send Feedback opened the feedback form in the browser, naming the plugin's version and the AutoCAD year. The Austin import took 0.67 s in AutoCAD 2025, 0.95 s in AutoCAD 2026 and 1.12 s in AutoCAD 2027, the sum of the step times its closing report lists ([`docs/import-timing.md`](../../docs/import-timing.md)). AutoCAD 2027 ran as Civil 3D 2027 started as plain AutoCAD, the one AutoCAD 2027 on the gate machine. The first attempt at the Austin import in AutoCAD 2025 timed out before the harness's bridge answered, with nothing imported, and passed when run again.

  The vault window was captured with the free order chosen in AutoCAD 2025, 2026 and 2027 (2026-11-24, this zip installed clean by the host harness, signed in on the dev platform), both as the plugin draws it and with its title bar. Each image shows the level control reading `Max — 69.2 MB (recommended)` above the order list, with no locked row and no wording about paid sites; the control's options read Max, Med and Min and nothing else. In the same session of each year the sign-in, About, notice and import windows were captured too. Every title bar and header in every year shows the round Mantle Place mark, and no window shows the AutoCAD logo; the notice window has no title bar.
- Published asset: <check from step 4 and its date>.
- `MantlePlace-AutoCad-1.3.0.zip` sha256 `f4578adbdd70e66d6452af67b4c76354a910a6712623b334394b00f34e5936e0`
- Storefront: <submitted to the Autodesk storefront on date>, from the asset above.

Previous: [autocad-1.2.0](https://github.com/mantleplace/mantleplace-dcc/releases/tag/autocad-1.2.0)
```
