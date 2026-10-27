---
name: blender-host-onboarding
description: Onboarding for the Blender plugin — what it reads (Context.usdz, Features.usdz, location.site_origin) and what it does not (no host block, no sign-in, no vault), the pure core / shim split, the frame it places in, the two test runs and the install slot. Read first for any change under `blender/`.
---

# Mantle Place for Blender — agent onboarding

Read the repo root [`CLAUDE.md`](../CLAUDE.md) first. Usage, the placement model and the build lines
are in [`README.md`](README.md); this file is what a change here must not break.

## Identity

- **Host:** Blender **4.2 or later**, tested in 4.5 and 5.2. 4.2 is the floor because it is the first
  with extensions (`blender_manifest.toml`); `bl_info` is kept for a legacy install through the Add-ons preferences.
- **What it is:** a bundle reader on disk, the smallest consumer of the open format. Of the four
  `HPS-01` layers it has **manifest consumption only**; sign-in, the vault client and the bundle cache
  are not built, so it is not a complete host plugin and says so in its README.
- **What it reads:** the neutral register only — `layout.context_usdz`, `layout.features_usdz`,
  `file_entries`, `files` and `location.site_origin`. There is **no `hosts.blender` block**, and
  the plugin reads no other host's block (`HPS-36`).
- **Frame:** Blender is a **fixed-frame host** (`HPS-54`). Its scene is in metres, Z up, and it
  places one frame: the frame `file_entries` states for `Context.usdz`, as offsets from the site
  origin. It applies `location.site_origin.projected` only when that point is in the Context file's
  CRS, and never reprojects, converts a unit or shifts a datum (`HPS-53`); a file it cannot place is
  a named note, and the geometry still comes in at the origin.
- **Releases:** none yet. A first release is its own track, `blender-<version>`
  ([ADR 0001](../docs/adr/0001-per-host-release-tracks.md)); the column in
  [`docs/host-support.md`](../docs/host-support.md) arrives with it, since every status there links
  a release.

## Layout and the split that matters

```
mantleplace_blender/__init__.py      bl_info and a register() that imports the shim lazily
mantleplace_blender/core/            PURE. No bpy. Every decision; stdlib only.
mantleplace_blender/shim.py          IMPURE. bpy: operators, menus, preferences, USD import.
mantleplace_blender/blender_manifest.toml   the extension manifest
tests/test_*.py                      headless, over the core; CI runs them (ci-blender-tests)
tests/headless/run_headless.py       inside Blender --background, against a real bundle, by hand
tools/                               the HPS-50 install tool and check script
```

**Put logic in `core/`.** If it cannot be asserted without Blender, it is in the wrong file
(`HPS-02`). `__init__.py` must not import `bpy` at module level: the core suite imports the package
on a runner with no Blender.

**The version is stated three times** — `core.VERSION`, `bl_info["version"]` and the manifest's
`version` — for three readers. `tests/test_version.py` holds them to one value; bump all three.

## Rules this tree turns on

- **Pointers, not folders** (spec §3). A missing `layout.context_usdz` falls back to the 2.0 fixed
  name only when the manifest lists that path, and the import says so. An explicit `null` is honest
  absence and is not tried.
- **A higher major is refused before any pointer is read** (spec §11).
- **Integrity** (spec §5). A sha256 in `files` is verified and fails closed, naming the file; an
  absent one is reported as unchecked, never as corrupt.
- **No placement value is derived here.** The origin is applied verbatim; a patch that computes one
  locally is refused at review, however correct the arithmetic.
- **Shared words** (`HPS-51`): `Import Bundle` and `Send Feedback`. The Send Feedback link is
  `core.feedback.feedback_url` (`HPS-56`), and `source` is `dcc-blender`.

## Testing

Run the core suite before every push; run the headless check after any change to the shim or to what
the core hands it. **Always with `--background`**: never open a Blender window from a session.

```bash
python -m unittest discover -s blender/tests -t blender
blender --background --factory-startup --python-exit-code 1 \
    --python blender/tests/headless/run_headless.py -- <bundle.zip>
```

`--factory-startup` keeps the run off the machine's own preferences and add-ons. To try the
extension path without touching them, point `BLENDER_USER_RESOURCES` at a scratch folder before
`blender --command extension install-file -r user_default -e <zip>`.

## Local install

`tools/Install-BlenderAddon.ps1` copies the tree into each Blender's
`extensions/user_default/mantleplace_blender` and writes `MantlePlace.install.json` there;
`tools/Check-BlenderInstall.ps1` compares that stamp with `origin/main`, counting commits under
`blender/` (`HPS-50`, [`tools/local-install/`](../tools/local-install/README.md)). Both take
`-ConfigRoot`, which is how to exercise them without touching a real Blender profile.
