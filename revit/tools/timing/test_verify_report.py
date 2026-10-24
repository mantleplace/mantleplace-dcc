"""Cases for verify_report.py. Run from this folder: python -m unittest test_verify_report"""

import json
import os
import sqlite3
import struct
import tempfile
import unittest

import verify_report


def write(path, text):
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(text)


def run_dir(placement_rows, verify, links_rows=""):
    folder = tempfile.mkdtemp()
    write(os.path.join(folder, "placement.csv"), "index,east_m,north_m,expected_z_m,measured_z_m\n" + placement_rows)
    write(os.path.join(folder, "link-buildings.csv"), "link,guid,centre_east_m,centre_north_m,base_z_m,top_z_m,ground_z_m\n" + links_rows)
    write(os.path.join(folder, "verify.json"), json.dumps(verify))
    return folder


def verify_record(**overrides):
    record = {
        "manifest_version": "2.0.0",
        "origin_projected": {"east": 1000.0, "north": 2000.0, "unit": "m"},
        "internal_origin_in_shared_m": [1000.0, 2000.0, 0.0],
        "links": [{"name": "Model.ifc", "loaded": True, "transform_is_identity": True, "elements_with_ifc_guid": 2}],
        "containers": [{"name": "ODbL c", "live": 3, "by_class": {"FilledRegion/Filled region/view-specific": 3}}],
        "site_context_overrides": [{"filter": "ODbL c", "line_color": "#F27A1A", "line_weight": 5}],
        "handover": {
            "committed": True,
            "planned_removals": ["ODbL c"],
            "filed_total": 3,
            "filed_deleted": 3,
            "filed_left": 0,
            "deleted_total": 3,
            "deleted_alongside_by_class": {},
            "containers_left": [],
            "credit_line": "Added the credit.",
            "attribution_notes": ["(c) OpenStreetMap contributors, ODbL 1.0"],
        },
    }
    record.update(overrides)
    return record


def footprints_gpkg(squares):
    """A GeoPackage with one table of axis-aligned square polygons (centre x, centre y, half side)."""
    path = os.path.join(tempfile.mkdtemp(), "f.gpkg")
    connection = sqlite3.connect(path)
    connection.execute("create table gpkg_geometry_columns (table_name text, column_name text)")
    connection.execute("insert into gpkg_geometry_columns values ('Building', 'geom')")
    connection.execute("create table Building (fid integer primary key, geom blob)")
    for x, y, h in squares:
        ring = [(x - h, y - h), (x + h, y - h), (x + h, y + h), (x - h, y + h), (x - h, y - h)]
        wkb = struct.pack("<BII", 1, 3, 1) + struct.pack("<I", len(ring)) + b"".join(struct.pack("<dd", *p) for p in ring)
        header = b"GP" + bytes([0, 0b00000001]) + struct.pack("<i", 32612)
        connection.execute("insert into Building (geom) values (?)", (header + wkb,))
    connection.commit()
    connection.close()
    return path


class PlacementCases(unittest.TestCase):
    def test_every_point_within_tolerance_passes(self):
        folder = run_dir("0,1,2,100.0,100.2\n1,1,2,50.0,49.7\n", verify_record())
        report = verify_report.judge(folder)
        self.assertTrue(report["placement"]["pass"])
        self.assertEqual(report["placement"]["hits"], 2)
        self.assertAlmostEqual(report["placement"]["max_abs_dz_m"], 0.3, places=6)

    def test_a_miss_fails_and_is_counted(self):
        folder = run_dir("0,1,2,100.0,100.2\n1,1,2,50.0,\n", verify_record())
        report = verify_report.judge(folder)
        self.assertFalse(report["placement"]["pass"])
        self.assertEqual(report["placement"]["misses"], 1)

    def test_a_point_past_tolerance_fails(self):
        folder = run_dir("0,1,2,100.0,102.5\n", verify_record())
        self.assertFalse(verify_report.judge(folder)["placement"]["pass"])

    def test_the_frame_check_compares_the_internal_origin_with_the_manifest_origin(self):
        moved = verify_record(internal_origin_in_shared_m=[1000.0, 2003.0, 0.0])
        self.assertFalse(verify_report.judge(run_dir("0,1,2,1,1\n", moved))["frame"]["pass"])
        self.assertTrue(verify_report.judge(run_dir("0,1,2,1,1\n", verify_record()))["frame"]["pass"])


class LinkCases(unittest.TestCase):
    rows = "Model.ifc,a,1000.0,2000.0,100.2,110,100.0\nModel.ifc,b,1050.0,2050.0,90.0,95,90.5\n"

    def test_buildings_sitting_on_the_ground_pass(self):
        report = verify_report.judge(run_dir("0,1,2,1,1\n", verify_record(), self.rows))
        self.assertTrue(report["link"]["pass"])
        self.assertEqual(report["link"]["buildings"], 2)
        self.assertAlmostEqual(report["link"]["median_base_minus_ground_m"], -0.15, places=6)

    def test_a_link_that_is_not_origin_to_origin_fails(self):
        moved = verify_record(links=[{"name": "Model.ifc", "loaded": True, "transform_is_identity": False, "elements_with_ifc_guid": 2}])
        self.assertFalse(verify_report.judge(run_dir("0,1,2,1,1\n", moved, self.rows))["link"]["pass"])

    def test_no_link_fails(self):
        self.assertFalse(verify_report.judge(run_dir("0,1,2,1,1\n", verify_record(links=[])))["link"]["pass"])

    def test_footprints_are_matched_centre_to_centre(self):
        gpkg = footprints_gpkg([(1000.05, 2000.0, 5), (1050.0, 2050.0, 5), (5000, 5000, 5)])
        report = verify_report.judge(run_dir("0,1,2,1,1\n", verify_record(), self.rows), footprints=gpkg, table="Building")
        self.assertEqual(report["link"]["footprints"], 3)
        self.assertAlmostEqual(report["link"]["median_centre_to_footprint_m"], 0.025, places=6)


class HandoverCases(unittest.TestCase):
    def test_everything_filed_removed_and_the_credit_present_passes(self):
        report = verify_report.judge(run_dir("0,1,2,1,1\n", verify_record()))
        self.assertTrue(report["handover"]["pass"])

    def test_filed_content_left_behind_fails(self):
        record = verify_record()
        record["handover"]["filed_left"] = 1
        self.assertFalse(verify_report.judge(run_dir("0,1,2,1,1\n", record))["handover"]["pass"])

    def test_a_removed_container_still_present_fails(self):
        record = verify_record()
        record["handover"]["containers_left"] = ["ODbL c"]
        self.assertFalse(verify_report.judge(run_dir("0,1,2,1,1\n", record))["handover"]["pass"])

    def test_no_credit_naming_openstreetmap_fails(self):
        record = verify_record()
        record["handover"]["attribution_notes"] = ["Imagery: USDA NAIP"]
        self.assertFalse(verify_report.judge(run_dir("0,1,2,1,1\n", record))["handover"]["pass"])

    def test_nothing_filed_is_named_not_passed(self):
        record = verify_record(containers=[])
        record["handover"].update(planned_removals=[], filed_total=0, filed_deleted=0)
        verdict = verify_report.judge(run_dir("0,1,2,1,1\n", record))["handover"]
        self.assertFalse(verdict["pass"])
        self.assertIn("no ODbL content", verdict["note"])


class MarkdownCases(unittest.TestCase):
    def test_the_markdown_names_each_check(self):
        text = verify_report.markdown(verify_report.judge(run_dir("0,1,2,1,1\n", verify_record())))
        for heading in ("Placement", "Frame", "Linked site model", "ODbL containers", "Prepare for Handover"):
            self.assertIn(heading, text)


if __name__ == "__main__":
    unittest.main()
