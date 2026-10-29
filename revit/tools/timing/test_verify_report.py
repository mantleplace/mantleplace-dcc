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

    def test_revit_2025_holds_its_own_wider_tolerance(self):
        folder = run_dir("0,1,2,100.0,101.6\n", verify_record())
        write(os.path.join(folder, "meta.json"), json.dumps({"year": "2025"}))
        placement = verify_report.judge(folder)["placement"]
        self.assertTrue(placement["pass"])
        self.assertEqual(placement["tolerance_m"], verify_report.PLACEMENT_TOLERANCE_2025_M)

    def test_revit_2025_still_fails_past_its_own_tolerance(self):
        folder = run_dir("0,1,2,100.0,102.5\n", verify_record())
        write(os.path.join(folder, "meta.json"), json.dumps({"year": "2025"}))
        self.assertFalse(verify_report.judge(folder)["placement"]["pass"])

    def test_revit_2026_and_2027_keep_the_one_metre_bar(self):
        for year in ("2026", "2027"):
            folder = run_dir("0,1,2,100.0,101.6\n", verify_record())
            write(os.path.join(folder, "meta.json"), json.dumps({"year": year}))
            placement = verify_report.judge(folder)["placement"]
            self.assertFalse(placement["pass"], year)
            self.assertEqual(placement["tolerance_m"], 1.0)

    def test_a_run_folder_without_a_year_keeps_the_one_metre_bar(self):
        folder = run_dir("0,1,2,100.0,101.6\n", verify_record())
        self.assertFalse(verify_report.judge(folder)["placement"]["pass"])

    def test_the_frame_check_compares_the_internal_origin_with_the_manifest_origin(self):
        moved = verify_record(internal_origin_in_shared_m=[1000.0, 2003.0, 0.0])
        self.assertFalse(verify_report.judge(run_dir("0,1,2,1,1\n", moved))["frame"]["pass"])
        self.assertTrue(verify_report.judge(run_dir("0,1,2,1,1\n", verify_record()))["frame"]["pass"])

    def test_the_frame_check_reads_a_foot_origin_in_its_own_foot(self):
        # A State Plane easting: the two feet differ by about a metre here, so the wrong one fails.
        east, north = 1449621.322926113, 542689.5776930021
        us = verify_record(origin_projected={"east": east, "north": north, "unit": "ftUS"},
                           internal_origin_in_shared_m=[east * 1200 / 3937, north * 1200 / 3937, 0.0])
        self.assertTrue(verify_report.judge(run_dir("0,1,2,1,1\n", us))["frame"]["pass"])
        international = verify_record(origin_projected={"east": east, "north": north, "unit": "ft"},
                                      internal_origin_in_shared_m=[east * 1200 / 3937, north * 1200 / 3937, 0.0])
        self.assertFalse(verify_report.judge(run_dir("0,1,2,1,1\n", international))["frame"]["pass"])


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


def with_log(folder, *lines):
    """The plugin's import log, each line stamped as the importer writes it, then the summary it repeats."""
    stamped = [f"12:00:{i % 60:02d}  {line}" for i, line in enumerate(lines)]
    write(os.path.join(folder, "import.log"), "Mantle Place bundle import.\n" + "\n".join(stamped) + "\n\n" + "\n".join(lines) + "\n")
    return folder


REFUSED = 'The road centrelines file has no "road_splines" table, so it is not the layer the manifest names. Re-download this bundle.'


class VectorCases(unittest.TestCase):
    def folder(self, *lines):
        return with_log(run_dir("0,1,2,1,1\n", verify_record()), *lines)

    def test_each_vector_step_that_drew_or_imported_passes_with_its_count_and_file(self):
        folder = self.folder(
            "[LandUse] started.",
            "[Mantle Place: land use plan] commit took 0.5 s (Committed).",
            'Drew 21 land use region(s) on "Mantle Place Land Use Plan 6a49", a new plan.',
            'Filed 28 element(s) from the "LandUse" step in the selection filter "Mantle Place ODbL c".',
            "(LandUse took 0.9 s.)",
            "[RoadCentrelines] started.",
            "Imported 1,278 road centreline(s) from Features/RoadsDraped-Projected.gpkg.",
            "(RoadCentrelines took 0.1 s.)",
            "[Planting] started.",
            "Imported 8 tree(s).",
            "(Planting took 0.1 s.)",
        )
        vectors = verify_report.judge(folder)["vectors"]
        self.assertTrue(vectors["pass"])
        self.assertEqual([s["step"] for s in vectors["steps"]], ["LandUse", "RoadCentrelines"])
        self.assertEqual(vectors["steps"][0]["count"], 21)
        self.assertIsNone(vectors["steps"][0]["file"])
        self.assertEqual(vectors["steps"][1]["count"], 1278)
        self.assertEqual(vectors["steps"][1]["file"], "Features/RoadsDraped-Projected.gpkg")

    def test_a_refused_step_fails_and_keeps_the_reason(self):
        folder = self.folder("[RoadCentrelines] started.", REFUSED, "(RoadCentrelines took 0.0 s.)")
        vectors = verify_report.judge(folder)["vectors"]
        self.assertFalse(vectors["pass"])
        self.assertIsNone(vectors["steps"][0]["count"])
        self.assertIn('no "road_splines" table', vectors["steps"][0]["note"])

    def test_a_step_that_fell_back_to_the_geojson_copy_fails(self):
        folder = self.folder(
            "[Water] started.",
            f"{REFUSED} Reading the water from the bundle's GeoJSON copy instead.",
            "Imported 1 water subdivision(s) from Features/Water.geojson.",
            "(Water took 0.2 s.)",
        )
        step = verify_report.judge(folder)["vectors"]["steps"][0]
        self.assertTrue(step["fell_back"])
        self.assertFalse(verify_report.judge(folder)["vectors"]["pass"])

    def test_no_vector_step_or_no_log_is_named_not_passed(self):
        self.assertIn("no vector step", verify_report.judge(self.folder("[Terrain] started."))["vectors"]["note"])
        self.assertIn("no import.log", verify_report.judge(run_dir("0,1,2,1,1\n", verify_record()))["vectors"]["note"])


class MarkdownCases(unittest.TestCase):
    def test_the_markdown_names_each_check(self):
        text = verify_report.markdown(verify_report.judge(run_dir("0,1,2,1,1\n", verify_record())))
        for heading in ("Placement", "Frame", "Linked site model", "Vectors", "ODbL containers", "Prepare for Handover"):
            self.assertIn(heading, text)

    def test_the_vectors_section_lists_each_step(self):
        folder = with_log(
            run_dir("0,1,2,1,1\n", verify_record()),
            "[Water] started.",
            "Imported 1 water subdivision(s) from Features/Water-Projected.gpkg.",
            "[RoadPolygons] started.",
            REFUSED.replace("road centrelines", "road surfaces").replace("road_splines", "road_polygons"),
        )
        text = verify_report.markdown(verify_report.judge(folder))
        self.assertIn("- Water: 1 from `Features/Water-Projected.gpkg`", text)
        self.assertIn('- RoadPolygons: not imported: The road surfaces file has no "road_polygons" table', text)


if __name__ == "__main__":
    unittest.main()
