# Copyright 2026 Mantle Place LLC. Licensed under the Apache License, Version 2.0.

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


class StreamOriginCase(unittest.TestCase):
    def setUp(self):
        self.helper = load_helper()

    def test_a_3x_stream_reports_its_origin_as_stated(self):
        info = types.SimpleNamespace(has_origin=True, origin_longitude_deg=-97.7, origin_latitude_deg=30.3)
        self.assertEqual(self.helper._info_origin(info), (-97.7, 30.3))

    def test_a_2x_stream_reports_none_so_the_manifest_is_read(self):
        info = types.SimpleNamespace(has_origin=False, origin_longitude_deg=0.0, origin_latitude_deg=0.0)
        self.assertIsNone(self.helper._info_origin(info))


if __name__ == "__main__":
    unittest.main()
