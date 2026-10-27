# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""The add-on states its version three times, for three readers; they must say the same thing."""

import ast
import os
import re
import unittest

from mantleplace_blender.core import VERSION

PACKAGE = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                       "mantleplace_blender")


class VersionTests(unittest.TestCase):
    def test_bl_info_states_the_core_version(self):
        with open(os.path.join(PACKAGE, "__init__.py"), encoding="utf-8") as handle:
            tree = ast.parse(handle.read())
        bl_info = next(ast.literal_eval(node.value) for node in tree.body
                       if isinstance(node, ast.Assign) and node.targets[0].id == "bl_info")
        self.assertEqual(".".join(str(part) for part in bl_info["version"]), VERSION)

    def test_the_extension_manifest_states_the_core_version(self):
        with open(os.path.join(PACKAGE, "blender_manifest.toml"), encoding="utf-8") as handle:
            text = handle.read()
        self.assertEqual(re.search(r'^version = "([^"]+)"$', text, re.M).group(1), VERSION)

    def test_the_minimum_blender_agrees(self):
        with open(os.path.join(PACKAGE, "__init__.py"), encoding="utf-8") as handle:
            tree = ast.parse(handle.read())
        bl_info = next(ast.literal_eval(node.value) for node in tree.body
                       if isinstance(node, ast.Assign) and node.targets[0].id == "bl_info")
        with open(os.path.join(PACKAGE, "blender_manifest.toml"), encoding="utf-8") as handle:
            minimum = re.search(r'^blender_version_min = "([^"]+)"$', handle.read(), re.M).group(1)
        self.assertEqual(".".join(str(part) for part in bl_info["blender"]), minimum)


if __name__ == "__main__":
    unittest.main()
