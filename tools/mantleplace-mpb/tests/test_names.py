# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""The 2.0 layout: the folder allowlist and the naming rules (`format.md` §10.2, §10.3), and the
staged schema a 2.0 manifest is held to before 2.0.0 is frozen."""

from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from helpers import (SYNTHETIC_SCHEMA, SYNTHETIC_SCHEMA_2, bundle_files, make_folder, make_zip,
                     manifest, sha256, write_schema_dir)
from mantleplace_mpb import SchemaSource, validate
from mantleplace_mpb.cli import main


def v2(**extra: object) -> dict:
    return manifest(version="2.0.0", **extra)


class Case(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.schema_dir = write_schema_dir(
            self.tmp / "schemas", {"1.14.0": SYNTHETIC_SCHEMA, "2.0.0": SYNTHETIC_SCHEMA_2})
        self.schemas = SchemaSource(schema_dir=self.schema_dir)
        self.count = 0

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def files(self, extra: dict[str, bytes], document: dict | None = None) -> dict[str, bytes]:
        files = bundle_files(document or v2())
        files.update(extra)
        return files

    def folder(self, extra: dict[str, bytes], document: dict | None = None, schemas=None):
        self.count += 1
        root = make_folder(self.tmp / f"bundle{self.count}", self.files(extra, document))
        return validate(root, schemas=schemas or self.schemas)

    def zipped(self, extra: dict[str, bytes], document: dict | None = None):
        self.count += 1
        path = make_zip(self.tmp / f"bundle{self.count}.zip", self.files(extra, document))
        return validate(path, schemas=self.schemas)

    @staticmethod
    def layout_codes(report, severity: str | None = None) -> set[str]:
        """The 2.0 layout findings only: the rest of the report is other tests' business."""
        return {f.code for f in report.findings
                if f.code.startswith(("name.", "layout."))
                and (severity is None or f.severity == severity)}


class TheStagedSchema(unittest.TestCase):
    """2.0.0 is served before it is frozen. A validator holds a 2.0 manifest to the served
    document only when asked to, says it did, and never mistakes any unledgered version for
    the staged one."""

    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.schema_dir = write_schema_dir(
            self.tmp / "series",
            {"1.14.0": SYNTHETIC_SCHEMA, "1.13.5": SYNTHETIC_SCHEMA, "2.0.0": SYNTHETIC_SCHEMA_2},
            staged=("2.0.0", "1.13.5"))
        self.bundle = make_folder(self.tmp / "bundle", bundle_files(v2()))

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def test_a_staged_version_is_unpublished_unless_asked_for(self) -> None:
        report = validate(self.bundle, schemas=SchemaSource(schema_dir=self.schema_dir))
        self.assertIn("schema.version-unpublished", {f.code for f in report.findings})
        self.assertEqual("fail", report.verdict)

    def test_asked_for_it_is_held_to_the_served_schema_and_says_so(self) -> None:
        report = validate(self.bundle, schemas=SchemaSource(schema_dir=self.schema_dir, staged=True))
        self.assertEqual("pass", report.verdict, [f.to_dict() for f in report.findings])
        staged = [f for f in report.findings if f.code == "schema.staged"]
        self.assertEqual(1, len(staged))
        self.assertEqual("info", staged[0].severity)
        self.assertIn("not yet in the freeze ledger", staged[0].message)

    def test_the_served_schema_still_decides(self) -> None:
        document = v2()
        del document["layout"]
        bundle = make_folder(self.tmp / "invalid", bundle_files(document))
        report = validate(bundle, schemas=SchemaSource(schema_dir=self.schema_dir, staged=True))
        self.assertIn("schema.invalid", {f.code for f in report.findings})

    def test_an_unledgered_version_below_the_ledgers_newest_is_not_staged(self) -> None:
        bundle = make_folder(self.tmp / "old", bundle_files(manifest(version="1.13.5")))
        report = validate(bundle, schemas=SchemaSource(schema_dir=self.schema_dir, staged=True))
        self.assertIn("schema.version-unpublished", {f.code for f in report.findings})

    def test_a_staged_version_that_is_not_served_is_unpublished(self) -> None:
        bundle = make_folder(self.tmp / "next", bundle_files(manifest(version="2.1.0")))
        report = validate(bundle, schemas=SchemaSource(schema_dir=self.schema_dir, staged=True))
        self.assertIn("schema.version-unpublished", {f.code for f in report.findings})

    def test_once_frozen_it_is_published_and_not_staged(self) -> None:
        frozen = write_schema_dir(self.tmp / "frozen", {"2.0.0": SYNTHETIC_SCHEMA_2})
        report = validate(self.bundle, schemas=SchemaSource(schema_dir=frozen, staged=True))
        self.assertEqual("pass", report.verdict)
        self.assertNotIn("schema.staged", {f.code for f in report.findings})

    def test_the_command_takes_a_staged_flag(self) -> None:
        argv = ["validate", str(self.bundle), "--schema-dir", str(self.schema_dir), "--json"]
        self.assertEqual(1, main(argv))
        self.assertEqual(0, main(argv + ["--staged"]))


class TheFolderAllowlist(Case):
    def test_the_eight_folders_and_the_readme_are_the_layout(self) -> None:
        extra = {f"{folder}/Thing{n}.txt": b"x" for n, folder in enumerate(
            ("Site", "Elevation", "Imagery", "LandCover", "Features", "Records", "Drawings"))}
        report = self.folder(extra)
        self.assertEqual(set(), self.layout_codes(report))

    def test_a_folder_outside_the_list_is_a_warning_not_a_failure(self) -> None:
        report = self.folder({"Extras/Notes.txt": b"x"})
        self.assertEqual({"layout.folder-unlisted"}, self.layout_codes(report, "warning"))
        self.assertEqual(set(), self.layout_codes(report, "error"))
        self.assertEqual("pass", report.verdict)
        self.assertEqual(["Extras"], [f.file for f in report.findings
                                      if f.code == "layout.folder-unlisted"])

    def test_a_file_at_the_root_other_than_the_readme_is_a_warning(self) -> None:
        report = self.folder({"LICENSE.txt": b"x"})
        self.assertEqual({"layout.root-file"}, self.layout_codes(report, "warning"))
        self.assertEqual("pass", report.verdict)

    def test_archive_debris_is_ignored_by_every_layout_rule(self) -> None:
        report = self.zipped({"__MACOSX/Elevation/._DEM.tif": b"\x00\x05\x16\x07",
                              "__MACOSX/._README.md": b"\x00", ".DS_Store": b"\x00",
                              "Elevation/.DS_Store": b"\x00"})
        self.assertEqual(set(), self.layout_codes(report))
        self.assertNotIn("MACOSX", json.dumps(report.to_dict()))


class TheNamingRules(Case):
    def assertNamed(self, extra: dict[str, bytes], code: str | None) -> None:
        report = self.folder(extra)
        self.assertEqual({code} if code else set(), self.layout_codes(report, "error"),
                         [f.to_dict() for f in report.findings])

    def test_rule_1_a_basename_is_a_pascal_case_noun_and_the_extension_is_the_format(self) -> None:
        for name in ("Features/roads.gpkg", "Features/Roads", "Elevation/DEM.TIF",
                     "Elevation/.Hidden"):
            with self.subTest(name=name):
                self.assertNamed({name: b"x"}, "name.basename")
        for name in ("Imagery/NDVI.tif", "LandCover/MaterialWeights-1To4.png",
                     "Features/RoadsDraped.gpkg"):
            with self.subTest(name=name):
                self.assertNamed({name: b"x"}, None)

    def test_rule_1_different_data_never_shares_a_basename(self) -> None:
        self.assertNamed({"Features/Water.gpkg": b"x", "Records/Water.gpkg": b"y"},
                         "name.basename-shared")

    def test_rule_1_renditions_share_a_basename_in_one_folder(self) -> None:
        self.assertNamed({"Elevation/Surface.dxf": b"x", "Elevation/Surface.landxml": b"y",
                          "Elevation/Terrain.glb": b"z", "Elevation/Terrain-Med.glb": b"w"}, None)

    def test_rule_3_qualifiers_are_hyphenated(self) -> None:
        for name in ("Elevation/Terrain_Med.glb", "Elevation/Terrain.Med.glb",
                     "Elevation/Terrain-med.glb", "Elevation/Terrain--Med.glb",
                     "Elevation/Terrain-.glb"):
            with self.subTest(name=name):
                self.assertNamed({name: b"x"}, "name.qualifier")

    def test_rule_3_qualifiers_run_subset_then_frame_then_level(self) -> None:
        for name in ("Elevation/Terrain-Med-Engine.glb", "Features/Water-Projected-Lines.gpkg",
                     "Elevation/Terrain-Med-Min.glb", "LandCover/TreePoints-Engine-Engine.csv"):
            with self.subTest(name=name):
                self.assertNamed({name: b"x"}, "name.qualifier-order")
        for name in ("Features/Water-Lines-Projected-Med.gpkg", "Imagery/Imagery-Engine.png",
                     "Drawings/SitePlan-Small.pdf", "Site/Context-Min.usdz"):
            with self.subTest(name=name):
                self.assertNamed({name: b"x"}, None)

    def test_rule_4_fixed_names_and_a_formats_own_names_keep_their_convention(self) -> None:
        self.assertNamed({"Metadata/stac-item.json": b"{}", "Elevation/Terrain/layer.json": b"{}",
                          "Elevation/Terrain/0/0/0.terrain": b"x", "Features/Roads.shp": b"x",
                          "Features/Roads.shx": b"x", "Features/Roads.shp.xml": b"x",
                          "Imagery/Imagery.pgw": b"x", "Imagery/Imagery.prj": b"x"}, None)

    def test_rule_5_names_are_ascii_from_the_portable_set(self) -> None:
        for name in ("Drawings/Site Plan.pdf", "Drawings/Plän.pdf", "Records/Flood+Zones.gpkg",
                     "Elevation/Terrain/0 0/0.terrain"):
            with self.subTest(name=name):
                self.assertNamed({name: b"x"}, "name.charset")

    def test_rule_5_no_two_entries_differ_only_by_case(self) -> None:
        for extra, clash in (
                ({"Features/RoadSurfaces.gpkg": b"x", "Features/Roadsurfaces.gpkg": b"y"},
                 ("Features/RoadSurfaces.gpkg", "Features/Roadsurfaces.gpkg")),
                ({"Features/Roads.gpkg": b"x", "FEATURES/Water.gpkg": b"y"},
                 ("FEATURES", "Features"))):
            with self.subTest(names=sorted(extra)):
                report = self.zipped(extra)
                self.assertIn("name.case-collision", self.layout_codes(report, "error"))
                messages = [f.message for f in report.findings if f.code == "name.case-collision"]
                self.assertEqual(1, len(messages), messages)
                for name in clash:
                    self.assertIn(name, messages[0])

    def test_rule_6_a_reader_matches_regardless_of_case_and_a_writer_uses_the_exact_case(self) -> None:
        data = b"synthetic raster\n"
        document = v2(layout={"readme": "README.md", "manifest": "Metadata/manifest.json",
                              "dem": "Elevation/DEM.tif"},
                      elevation={"dem": {"path": "Elevation/DEM.tif", "sha256": sha256(data)}},
                      file_entries={"Elevation/DEM.tif": {"odbl_role": "none"}})
        report = self.folder({"Elevation/Dem.tif": data}, document)
        codes = {f.code for f in report.findings}
        self.assertIn("name.case-mismatch", codes)
        self.assertNotIn("pointer.missing", codes)
        self.assertNotIn("role.unstated", codes)
        self.assertEqual(1, report.hashes["verified"])


class TheOneXLayout(Case):
    """The 2.0 rules are the 2.0 layout's. A 1.x bundle is held to its own version."""

    def test_a_1x_bundle_is_not_held_to_the_2_0_rules(self) -> None:
        report = self.folder({"Extras/dem_v1.tif": b"x", "LICENSE.txt": b"x"}, manifest())
        self.assertEqual(set(), self.layout_codes(report))
        self.assertEqual("skipped", report.check_status("names"))

    def test_a_1x_pointer_still_matches_exactly(self) -> None:
        document = manifest(layout={"readme": "README.md", "manifest": "Metadata/manifest.json",
                                    "dem": "Elevation/DEM.tif"})
        report = self.folder({"Elevation/Dem.tif": b"x"}, document)
        self.assertIn("pointer.missing", {f.code for f in report.findings})

    def test_a_2_0_bundle_reports_the_names_check(self) -> None:
        self.assertEqual("pass", self.folder({}).check_status("names"))
        self.assertEqual("fail", self.folder({"Features/roads.gpkg": b"x"}).check_status("names"))


class TheUnplacedFrame(Case):
    def test_a_file_with_no_spatial_frame_states_placed_false_and_nothing_else(self) -> None:
        document = v2(file_entries={"README.md": {"frame": {"placed": False}, "odbl_role": "none"},
                                    "Drawings/SitePlan-Small.pdf": {"frame": {"placed": False},
                                                                    "odbl_role": "produced-work"}})
        report = self.folder({"Drawings/SitePlan-Small.pdf": b"%PDF"}, document)
        self.assertNotIn("frame.partial", {f.code for f in report.findings})
        self.assertEqual("pass", report.verdict, [f.to_dict() for f in report.findings])


if __name__ == "__main__":
    unittest.main()
