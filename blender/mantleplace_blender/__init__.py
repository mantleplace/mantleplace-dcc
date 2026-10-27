# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""Mantle Place for Blender: import a bundle's site, placed at its site origin.

This file is the add-on's entry point and nothing else. Blender is imported only when the add-on
registers, inside `register`, so `mantleplace_blender.core` imports on a machine with no Blender at
all, which is what lets the headless suite test every decision the add-on makes (HPS-02).

`bl_info` serves a legacy add-on install (Preferences, Add-ons, Install from Disk on the package
folder's zip in Blender before 4.2); `blender_manifest.toml` beside it serves an extension install.
The two state one version, and a test holds them to it.
"""

bl_info = {
    "name": "Mantle Place",
    "author": "Mantle Place",
    "version": (0, 1, 0),
    "blender": (4, 2, 0),
    "location": "File > Import > Mantle Place Bundle",
    "description": "Import a Mantle Place bundle's site (Context.usdz, Features.usdz), placed at "
                   "its site origin",
    "doc_url": "https://github.com/mantleplace/mantleplace-dcc/blob/main/blender/README.md",
    "category": "Import-Export",
}


def register() -> None:
    from . import shim

    shim.register()


def unregister() -> None:
    from . import shim

    shim.unregister()
