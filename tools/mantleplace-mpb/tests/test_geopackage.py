# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""A 2.0 GeoPackage row that lists no `tables` holds exactly one feature table (`format.md` §6.3)."""

from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from helpers import (SYNTHETIC_SCHEMA, SYNTHETIC_SCHEMA_2, bundle_files, geopackage, make_folder,
                     make_zip, manifest, write_schema_dir)
from mantleplace_mpb import SchemaSource, validate

ROADS = "Features/RoadSplines.gpkg"
TWIN = "Features/RoadSplines-Projected.gpkg"


def v2(rows: list[dict], twins: dict[str, str] | None = None, version: str = "2.0.0") -> dict:
    layout = {"readme": "README.md", "manifest": "Metadata/manifest.json"}
    if twins:
        layout["projected_variants"] = twins
    return manifest(version=version, layout=layout,
                    vector={"present": True, "layers": [{"name": "road_splines", "formats": rows}]})


def row(path: str = ROADS, **extra: object) -> dict:
    return {"path": path, "format": "gpkg", **extra}


class TheOneTableRule(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.schemas = SchemaSource(schema_dir=write_schema_dir(
            self.tmp / "schemas", {"1.14.0": SYNTHETIC_SCHEMA, "2.0.0": SYNTHETIC_SCHEMA_2}))
        self.count = 0

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def run_bundle(self, document: dict, files: dict[str, bytes], zipped: bool = False):
        self.count += 1
        every = bundle_files(document) | files
        if zipped:
            path = make_zip(self.tmp / f"bundle{self.count}.zip", every)
        else:
            path = make_folder(self.tmp / f"bundle{self.count}", every)
        return validate(path, schemas=self.schemas)

    @staticmethod
    def codes(report, prefix: str = "gpkg.") -> set[str]:
        return {f.code for f in report.findings if f.code.startswith(prefix)}

    def test_the_one_table_may_be_named_in_camel_case(self) -> None:
        for zipped in (False, True):
            report = self.run_bundle(v2([row()]), {ROADS: geopackage("RoadSplines")}, zipped)
            self.assertEqual(set(), self.codes(report))
            self.assertEqual("pass", report.verdict)
            self.assertEqual("pass", report.check_status("geopackage"))

    def test_a_file_of_two_tables_is_refused_where_the_row_lists_none(self) -> None:
        for zipped in (False, True):
            report = self.run_bundle(v2([row()]), {ROADS: geopackage("RoadSplines", "Water")}, zipped)
            self.assertEqual({"gpkg.tables-several"}, self.codes(report))
            self.assertEqual("fail", report.verdict)
            finding = next(f for f in report.findings if f.code == "gpkg.tables-several")
            self.assertEqual("error", finding.severity)
            self.assertEqual(ROADS, finding.file)
            self.assertIn("RoadSplines", finding.message)
            self.assertIn("Water", finding.message)

    def test_a_row_that_lists_its_tables_may_hold_several(self) -> None:
        report = self.run_bundle(
            v2([row(tables=["RoadSplines", "Water"])]), {ROADS: geopackage("RoadSplines", "Water")})
        self.assertEqual(set(), self.codes(report))
        self.assertEqual("pass", report.verdict)

    def test_an_empty_tables_list_lists_none(self) -> None:
        report = self.run_bundle(v2([row(tables=[])]), {ROADS: geopackage("A", "B")})
        self.assertEqual({"gpkg.tables-several"}, self.codes(report))

    def test_a_file_with_no_feature_table_is_refused(self) -> None:
        report = self.run_bundle(v2([row()]), {ROADS: geopackage()})
        self.assertEqual({"gpkg.table-none"}, self.codes(report))
        self.assertEqual("fail", report.verdict)

    def test_the_projected_twin_of_an_untabled_row_is_held_to_the_same_rule(self) -> None:
        files = {ROADS: geopackage("RoadSplines"), TWIN: geopackage("RoadSplines", "Water")}
        report = self.run_bundle(v2([row()], {ROADS: TWIN}), files)
        self.assertEqual({"gpkg.tables-several"}, self.codes(report))
        self.assertEqual([TWIN], [f.file for f in report.findings if f.code == "gpkg.tables-several"])

    def test_a_row_of_another_format_is_not_read(self) -> None:
        document = v2([{"path": "Features/RoadSplines.geojson", "format": "geojson"}])
        report = self.run_bundle(document, {"Features/RoadSplines.geojson": b"{}"})
        self.assertEqual(set(), self.codes(report))
        self.assertEqual("pass", report.check_status("geopackage"))

    def test_a_file_that_is_not_a_geopackage_is_noted_and_not_failed(self) -> None:
        report = self.run_bundle(v2([row()]), {ROADS: b"synthetic gpkg\n"})
        self.assertEqual({"gpkg.unreadable"}, self.codes(report))
        self.assertEqual({"info"}, {f.severity for f in report.findings if f.code == "gpkg.unreadable"})
        self.assertEqual("pass", report.verdict)

    def test_a_row_whose_file_is_missing_is_the_pointer_checks_business(self) -> None:
        report = self.run_bundle(v2([row()]), {})
        self.assertEqual(set(), self.codes(report))

    def test_the_path_is_found_regardless_of_case(self) -> None:
        report = self.run_bundle(v2([row("Features/roadsplines.gpkg")]),
                                 {ROADS: geopackage("A", "B")})
        self.assertEqual({"gpkg.tables-several"}, self.codes(report))

    def test_a_one_x_bundle_is_held_to_its_own_version(self) -> None:
        document = v2([row()], version="1.14.0")
        report = self.run_bundle(document, {ROADS: geopackage("A", "B")})
        self.assertEqual(set(), self.codes(report))
        self.assertEqual("skipped", report.check_status("geopackage"))


if __name__ == "__main__":
    unittest.main()
