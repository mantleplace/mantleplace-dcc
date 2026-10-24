# Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.
"""The QA helper reads a bundle's manifest regardless of the case of its entry name.

`mantleplace_cesium_stream.py` runs inside the editor, where `import unreal` exists. These tests
stub that module so the pure part (finding the manifest member and reading the origin) runs under
plain Python, the way the repository's other gate tests do.

The rule is MPB 2.0's (spec/format.md 10.3 rule 6): a reader matches a manifest path to an archive
entry regardless of case. The engine's zip reader already does (recorded in unreal/CLAUDE.md); Python's
`zipfile` does not, which is what the helper used to depend on.
"""

import importlib.util
import json
import sys
import tempfile
import types
import unittest
import zipfile
from pathlib import Path

HELPER = (
    Path(__file__).resolve().parent.parent
    / "MantlePlace" / "Content" / "Python" / "mantleplace_cesium_stream.py"
)


def load_helper():
    """Import the helper by path with `unreal` stubbed; the stub is removed again afterwards."""
    had = "unreal" in sys.modules
    previous = sys.modules.get("unreal")
    sys.modules["unreal"] = types.ModuleType("unreal")
    try:
        spec = importlib.util.spec_from_file_location("mantleplace_cesium_stream_under_test", HELPER)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        return module
    finally:
        if had:
            sys.modules["unreal"] = previous
        else:
            del sys.modules["unreal"]


MANIFEST = {
    "version": "2.0.0",
    "hosts": {
        "unreal": {
            "georeference": {"origin": {"lon": -73.975, "lat": 40.755, "ground_orthometric_h_m": 20.0}},
        },
    },
}


class ManifestMemberCase(unittest.TestCase):
    def setUp(self):
        self.helper = load_helper()
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.dir = Path(self._tmp.name)

    def make_zip(self, member_name, manifest=MANIFEST):
        path = self.dir / "bundle.zip"
        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr("README.md", "synthetic\n")
            archive.writestr(member_name, json.dumps(manifest))
        return path

    def test_exact_case_reads(self):
        path = self.make_zip("Metadata/manifest.json")
        self.assertEqual("2.0.0", self.helper._read_manifest(path)["version"])

    def test_a_differently_cased_entry_reads(self):
        for spelling in ("metadata/MANIFEST.json", "METADATA/Manifest.JSON", "Metadata/Manifest.json"):
            with self.subTest(spelling=spelling):
                path = self.make_zip(spelling)
                self.assertEqual("2.0.0", self.helper._read_manifest(path)["version"])

    def test_origin_is_read_from_a_differently_cased_entry(self):
        path = self.make_zip("metadata/MANIFEST.json")
        self.assertEqual((-73.975, 40.755, 20.0), self.helper._manifest_origin(path))

    def test_a_1x_manifest_reads_the_same_way(self):
        manifest = dict(MANIFEST, version="1.15.0")
        path = self.make_zip("Metadata/manifest.json", manifest)
        self.assertEqual("1.15.0", self.helper._read_manifest(path)["version"])
        self.assertEqual((-73.975, 40.755, 20.0), self.helper._manifest_origin(path))

    def test_a_bundle_with_no_manifest_is_still_a_missing_entry(self):
        path = self.dir / "bundle.zip"
        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr("README.md", "synthetic\n")
        with self.assertRaises(KeyError):
            self.helper._read_manifest(path)

    def test_a_name_that_only_contains_the_manifest_is_not_the_manifest(self):
        path = self.make_zip("Metadata/manifest.json.bak")
        with self.assertRaises(KeyError):
            self.helper._read_manifest(path)


if __name__ == "__main__":
    unittest.main()
