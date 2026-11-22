# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC

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
