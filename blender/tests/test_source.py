# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""Opening a bundle from its zip or from an extracted folder, and taking one file out of it.

Real files in a temporary directory, no Blender: the zip handling, the case rule, the escape guard
and the digest check are all plain I/O.
"""

import hashlib
import json
import os
import tempfile
import unittest
import zipfile

from mantleplace_blender.core.bundle import BundleRefused
from mantleplace_blender.core.source import open_bundle

PAYLOAD = b"#usda 1.0\n"
DIGEST = hashlib.sha256(PAYLOAD).hexdigest()
MANIFEST = {"version": "2.0.0", "layout": {"manifest": "Metadata/manifest.json"}}


class SourceTestCase(unittest.TestCase):
    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.root = self._tmp.name
        self.out = os.path.join(self.root, "out")
        os.makedirs(self.out)

    def tearDown(self):
        self._tmp.cleanup()

    def make_zip(self, entries):
        path = os.path.join(self.root, "bundle.zip")
        with zipfile.ZipFile(path, "w") as archive:
            for name, data in entries.items():
                archive.writestr(name, data)
        return path

    def make_folder(self, entries):
        top = os.path.join(self.root, "extracted")
        for name, data in entries.items():
            target = os.path.join(top, *name.split("/"))
            os.makedirs(os.path.dirname(target), exist_ok=True)
            with open(target, "wb") as handle:
                handle.write(data)
        return os.path.join(top, "Metadata", "manifest.json")


class ZipTests(SourceTestCase):
    def test_the_manifest_is_read_from_its_fixed_name(self):
        path = self.make_zip({"Metadata/manifest.json": json.dumps(MANIFEST),
                              "Site/Context.usdz": PAYLOAD})
        with open_bundle(path) as bundle:
            self.assertEqual(bundle.manifest["version"], "2.0.0")

    def test_a_zip_with_no_manifest_is_refused(self):
        with self.assertRaises(BundleRefused):
            open_bundle(self.make_zip({"Site/Context.usdz": PAYLOAD}))

    def test_a_file_is_extracted_and_its_digest_verified(self):
        path = self.make_zip({"Metadata/manifest.json": json.dumps(MANIFEST),
                              "Site/Context.usdz": PAYLOAD})
        with open_bundle(path) as bundle:
            local, verified = bundle.materialise("Site/Context.usdz", self.out, DIGEST)
        self.assertTrue(verified)
        with open(local, "rb") as handle:
            self.assertEqual(handle.read(), PAYLOAD)
        self.assertTrue(os.path.abspath(local).startswith(os.path.abspath(self.out)))

    def test_a_missing_digest_is_unknown_not_corrupt(self):
        path = self.make_zip({"Metadata/manifest.json": json.dumps(MANIFEST),
                              "Site/Context.usdz": PAYLOAD})
        with open_bundle(path) as bundle:
            local, verified = bundle.materialise("Site/Context.usdz", self.out, None)
        self.assertFalse(verified)
        self.assertTrue(os.path.isfile(local))

    def test_a_digest_mismatch_refuses_and_leaves_nothing_behind(self):
        path = self.make_zip({"Metadata/manifest.json": json.dumps(MANIFEST),
                              "Site/Context.usdz": PAYLOAD})
        with open_bundle(path) as bundle, self.assertRaises(BundleRefused) as caught:
            bundle.materialise("Site/Context.usdz", self.out, "0" * 64)
        self.assertIn("Site/Context.usdz", str(caught.exception))
        leftovers = [f for _, _, files in os.walk(self.out) for f in files]
        self.assertEqual(leftovers, [])

    def test_the_manifest_path_matches_an_entry_regardless_of_case(self):
        path = self.make_zip({"Metadata/manifest.json": json.dumps(MANIFEST),
                              "site/context.USDZ": PAYLOAD})
        with open_bundle(path) as bundle:
            local, _ = bundle.materialise("Site/Context.usdz", self.out, DIGEST)
        self.assertTrue(os.path.isfile(local))

    def test_a_pointer_to_an_entry_the_zip_lacks_is_refused_by_name(self):
        path = self.make_zip({"Metadata/manifest.json": json.dumps(MANIFEST)})
        with open_bundle(path) as bundle, self.assertRaises(BundleRefused) as caught:
            bundle.materialise("Site/Context.usdz", self.out, None)
        self.assertIn("Site/Context.usdz", str(caught.exception))

    def test_a_path_that_escapes_the_extraction_root_is_refused(self):
        path = self.make_zip({"Metadata/manifest.json": json.dumps(MANIFEST),
                              "../evil.usdz": PAYLOAD})
        with open_bundle(path) as bundle, self.assertRaises(BundleRefused):
            bundle.materialise("../evil.usdz", self.out, None)


class FolderTests(SourceTestCase):
    def test_an_extracted_bundle_opens_from_its_manifest(self):
        manifest_path = self.make_folder({"Metadata/manifest.json": json.dumps(MANIFEST).encode(),
                                          "Site/Context.usdz": PAYLOAD})
        with open_bundle(manifest_path) as bundle:
            local, verified = bundle.materialise("Site/Context.usdz", self.out, DIGEST)
        self.assertTrue(verified)
        # An extracted bundle is read in place: nothing is copied.
        self.assertEqual(os.path.normcase(os.path.abspath(local)),
                         os.path.normcase(os.path.join(self.root, "extracted", "Site", "Context.usdz")))

    def test_a_digest_mismatch_in_a_folder_is_refused(self):
        manifest_path = self.make_folder({"Metadata/manifest.json": json.dumps(MANIFEST).encode(),
                                          "Site/Context.usdz": PAYLOAD})
        with open_bundle(manifest_path) as bundle, self.assertRaises(BundleRefused):
            bundle.materialise("Site/Context.usdz", self.out, "0" * 64)

    def test_a_missing_file_in_a_folder_is_refused_by_name(self):
        manifest_path = self.make_folder({"Metadata/manifest.json": json.dumps(MANIFEST).encode()})
        with open_bundle(manifest_path) as bundle, self.assertRaises(BundleRefused) as caught:
            bundle.materialise("Site/Context.usdz", self.out, None)
        self.assertIn("Site/Context.usdz", str(caught.exception))

    def test_a_json_that_is_not_a_manifest_is_refused(self):
        stray = os.path.join(self.root, "notes.json")
        with open(stray, "w", encoding="utf-8") as handle:
            handle.write("[1, 2]")
        with self.assertRaises(BundleRefused):
            open_bundle(stray)

    def test_another_file_type_is_refused(self):
        stray = os.path.join(self.root, "Context.usdz")
        with open(stray, "wb") as handle:
            handle.write(PAYLOAD)
        with self.assertRaises(BundleRefused) as caught:
            open_bundle(stray)
        self.assertIn("manifest.json", str(caught.exception))


if __name__ == "__main__":
    unittest.main()
