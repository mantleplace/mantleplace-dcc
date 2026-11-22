# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC

from __future__ import annotations

import zipfile

from helpers import make_zip
from mantleplace_mpb import validate
from test_layout3 import Case, bundle3


class TheZipMembers(Case):
    def zip(self, **methods: int):
        files = bundle3()
        renamed = {name.replace("__", "/"): method for name, method in methods.items()}
        return validate(make_zip(self.tmp / f"b{self.count}.zip", files, methods=renamed),
                        schemas=self.schemas)

    def setUp(self) -> None:
        super().setUp()
        self.count += 1

    def test_a_deflated_zip_passes_and_the_check_ran(self) -> None:
        report = self.zip()
        self.assertEqual("pass", report.verdict, [f.to_dict() for f in report.findings])
        self.assertEqual("pass", report.check_status("zip"))

    def test_a_folder_has_no_zip_to_check(self) -> None:
        self.assertEqual("skipped", self.run_folder(bundle3()).check_status("zip"))

    def test_a_member_in_another_method(self) -> None:
        for method in (zipfile.ZIP_BZIP2, zipfile.ZIP_LZMA):
            with self.subTest(method=method):
                report = self.zip(**{"Elevation__Terrain.glb": method})
                self.assertRejects(report, "zip.compression")
                self.assertEqual(["Elevation/Terrain.glb"],
                                 [f.file for f in report.findings if f.code == "zip.compression"])
                self.assertEqual("fail", report.check_status("zip"))

    def test_a_member_stored_where_deflate_would_shrink_it(self) -> None:
        report = self.zip(**{"Manifest.json": zipfile.ZIP_STORED})
        self.assertRejects(report, "zip.compression")

    def test_a_member_stored_where_deflate_does_not_shrink_it(self) -> None:
        self.assertEqual("pass", self.zip(**{"Elevation__Elevation.tif": zipfile.ZIP_STORED}).verdict)

    def test_the_wrapper_folder_changes_nothing(self) -> None:
        files = bundle3()
        path = make_zip(self.tmp / "w.zip", files, "Bundle/", {"Manifest.json": zipfile.ZIP_LZMA})
        self.assertRejects(validate(path, schemas=self.schemas), "zip.compression")

    def test_a_2x_zip_is_not_held_to_it(self) -> None:
        from helpers import bundle_files, manifest

        files = bundle_files(manifest(version="2.0.0"))
        path = make_zip(self.tmp / "v2.zip", files, methods={"README.md": zipfile.ZIP_STORED})
        self.assertNotIn("zip.compression", self.codes(validate(path, schemas=self.schemas)))
