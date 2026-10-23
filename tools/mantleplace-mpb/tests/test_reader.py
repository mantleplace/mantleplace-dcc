# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""The reader: every file with its frame and role, and how a file sits inside its own frame.

The reader never reprojects. Every case that crosses projections asserts a refusal, not a number.
"""

from __future__ import annotations

import copy
import json
import tempfile
import unittest
from pathlib import Path

from helpers import bundle_files, make_folder, make_zip, manifest
from mantleplace_mpb import BundleError, FrameError, FrameMismatch, read_bundle

CRS_18N = {"code": "EPSG:32618"}
CRS_13N = {"code": "EPSG:32613"}

RASTER_FRAME = {
    "horizontal_crs": CRS_18N,
    "vertical_crs": {"code": "EPSG:5703"},
    "unit_to_metres": 1,
    "geoid_model": "GEOID18",
    "axis_order": "east-north",
    "axis_mapping": {"x": "east", "y": "north", "z": "up"},
    "coordinate_mode": "absolute",
    "pixel_convention": "pixel-is-area",
    "row_order": "north-up",
}
VECTOR_FRAME = {k: v for k, v in RASTER_FRAME.items() if k not in ("pixel_convention", "row_order")}
MESH_FRAME = {
    "horizontal_crs": CRS_18N,
    "vertical_crs": {"code": "EPSG:5703"},
    "unit_to_metres": 1,
    "geoid_model": "GEOID18",
    "axis_order": "east-north",
    "axis_mapping": {"x": "east", "y": "up", "z": "south"},
    "coordinate_mode": "offset",
}
SITE_ORIGIN = {
    "latitude": 40.755,
    "longitude": -73.975,
    "ellipsoidal_height_m": -22.4,
    "datum_height_m": 10.3,
    "vertical_datum": "NAVD88 (GEOID18)",
    "projected": {"crs": CRS_18N, "x": 585000.0, "y": 4512000.0, "unit": "m", "z": 10.3,
                  "z_reference": "vertical_datum"},
}


def entry(frame: dict | None, role: str | None = "none", **extra: object) -> dict:
    out: dict = {}
    if frame is not None:
        out["frame"] = copy.deepcopy(frame)
    if role is not None:
        out["odbl_role"] = role
    out.update(extra)
    return out


def site_manifest(file_entries: dict, origin: dict | None = SITE_ORIGIN) -> dict:
    location = {"site_origin": copy.deepcopy(origin)} if origin is not None else {}
    return manifest(location=location, file_entries=file_entries)


def site_files(file_entries: dict, origin: dict | None = SITE_ORIGIN) -> dict[str, bytes]:
    files = bundle_files(site_manifest(file_entries, origin))
    # Every entry names a file, except under Parts/: those stand for a file another part holds.
    names = {"Elevation/DEM.tif", "Site/Terrain.glb", "Features/Roads.gpkg", "Site/Other.glb"}
    for name in names | {n for n in file_entries if not n.startswith("Parts/")}:
        files[name] = name.encode("utf-8")
    return files


STANDARD = {
    "Elevation/DEM.tif": entry(RASTER_FRAME),
    "Site/Terrain.glb": entry(MESH_FRAME, "produced-work", license="CC0-1.0"),
    "Features/Roads.gpkg": entry(VECTOR_FRAME, "database", license="ODbL-1.0",
                                 sources=[{"name": "OSM", "license": "ODbL-1.0"}]),
}


class ReaderCase(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def read(self, file_entries: dict | None = None, origin: dict | None = SITE_ORIGIN):
        folder = make_folder(self.tmp / "bundle", site_files(
            STANDARD if file_entries is None else file_entries, origin))
        reader = read_bundle(folder)
        self.addCleanup(reader.close)
        return reader


class EveryFileWithItsFrameAndRole(ReaderCase):
    def test_every_file_in_the_bundle_is_returned_with_its_frame_and_role(self) -> None:
        reader = self.read()
        self.assertEqual(
            ["Elevation/DEM.tif", "Features/Roads.gpkg", "Metadata/manifest.json", "README.md",
             "Site/Other.glb", "Site/Terrain.glb"],
            [f.path for f in reader.files])
        dem = reader.file("Elevation/DEM.tif")
        self.assertEqual({"code": "EPSG:32618"}, dem.frame.horizontal_crs)
        self.assertEqual({"code": "EPSG:5703"}, dem.frame.vertical_crs)
        self.assertEqual("GEOID18", dem.frame.geoid_model)
        self.assertEqual("pixel-is-area", dem.frame.pixel_convention)
        self.assertEqual("north-up", dem.frame.row_order)
        self.assertEqual("none", dem.role)
        roads = reader.file("Features/Roads.gpkg")
        self.assertEqual("database", roads.role)
        self.assertEqual("ODbL-1.0", roads.license)
        self.assertEqual("OSM", roads.sources[0]["name"])
        self.assertEqual("produced-work", reader.file("Site/Terrain.glb").role)

    def test_a_file_with_no_entry_has_no_frame_and_reads_as_database(self) -> None:
        other = self.read().file("Site/Other.glb")
        self.assertIsNone(other.frame)
        self.assertEqual("database", other.role)
        self.assertIsNone(other.declared_role)
        self.assertFalse(other.role_declared)

    def test_an_unknown_role_reads_as_database_and_keeps_what_was_declared(self) -> None:
        reader = self.read({"Site/Terrain.glb": entry(MESH_FRAME, "mystery")})
        terrain = reader.file("Site/Terrain.glb")
        self.assertEqual("database", terrain.role)
        self.assertEqual("mystery", terrain.declared_role)

    def test_a_zip_and_a_wrapped_zip_read_the_same_as_a_folder(self) -> None:
        files = site_files(STANDARD)
        for archive in (make_zip(self.tmp / "plain.zip", files),
                        make_zip(self.tmp / "wrapped.zip", files, prefix="Wrapper/")):
            with read_bundle(archive) as reader:
                self.assertEqual(6, len(reader.files), archive.name)
                self.assertEqual("none", reader.file("Elevation/DEM.tif").role)
                self.assertEqual(b"Site/Other.glb", reader.read("Site/Other.glb"))

    def test_a_file_reports_its_size(self) -> None:
        self.assertEqual(len(b"Site/Other.glb"), self.read().file("Site/Other.glb").size)

    def test_the_site_origin_is_read_with_its_projected_point(self) -> None:
        origin = self.read().site_origin
        self.assertEqual(40.755, origin.latitude)
        self.assertEqual("NAVD88 (GEOID18)", origin.vertical_datum)
        self.assertEqual((585000.0, 4512000.0, 10.3), (origin.projected.x, origin.projected.y,
                                                         origin.projected.z))
        self.assertEqual("m", origin.projected.unit)
        self.assertEqual(1.0, origin.projected.unit_to_metres)

    def test_a_bundle_with_no_site_origin_has_none(self) -> None:
        self.assertIsNone(self.read(origin=None).site_origin)

    def test_entries_naming_no_file_are_listed_not_invented(self) -> None:
        reader = self.read({**STANDARD, "Parts/Elsewhere.glb": entry(MESH_FRAME)})
        self.assertEqual(["Parts/Elsewhere.glb"], reader.unmatched_entries)
        self.assertNotIn("Parts/Elsewhere.glb", [f.path for f in reader.files])

    def test_files_group_by_the_frame_they_share(self) -> None:
        reader = self.read()
        groups = reader.by_frame()
        self.assertEqual(1, len(groups))
        self.assertEqual(["Elevation/DEM.tif", "Features/Roads.gpkg", "Site/Terrain.glb"],
                         [f.path for f in groups[0].files])
        self.assertEqual(["Metadata/manifest.json", "README.md", "Site/Other.glb"],
                         [f.path for f in reader.unframed()])


class AFrameThatCannotBeRead(ReaderCase):
    def test_an_unknown_axis_direction_makes_the_file_unplaceable_by_name(self) -> None:
        bad = copy.deepcopy(MESH_FRAME)
        bad["axis_mapping"]["y"] = "sideways"
        terrain = self.read({"Site/Terrain.glb": entry(bad, "produced-work")}).file("Site/Terrain.glb")
        self.assertIsNone(terrain.frame)
        self.assertIn("sideways", terrain.frame_problem)
        self.assertEqual("produced-work", terrain.role)

    def test_an_unknown_coordinate_mode_axis_order_or_row_order_is_refused(self) -> None:
        for key, value in (("coordinate_mode", "relative"), ("axis_order", "up-down")):
            bad = {**copy.deepcopy(VECTOR_FRAME), key: value}
            roads = self.read({"Features/Roads.gpkg": entry(bad)}).file("Features/Roads.gpkg")
            self.assertIsNone(roads.frame, key)
            self.assertIn(value, roads.frame_problem)
        bad = {**copy.deepcopy(RASTER_FRAME), "row_order": "sideways-up"}
        self.assertIsNone(self.read({"Elevation/DEM.tif": entry(bad)}).file("Elevation/DEM.tif").frame)

    def test_a_partial_frame_is_refused_naming_what_is_missing(self) -> None:
        partial = {k: v for k, v in VECTOR_FRAME.items() if k != "axis_mapping"}
        roads = self.read({"Features/Roads.gpkg": entry(partial)}).file("Features/Roads.gpkg")
        self.assertIsNone(roads.frame)
        self.assertIn("axis_mapping", roads.frame_problem)

    def test_an_axis_mapping_with_two_axes_on_one_line_is_refused(self) -> None:
        bad = copy.deepcopy(VECTOR_FRAME)
        bad["axis_mapping"] = {"x": "east", "y": "west", "z": "up"}
        self.assertIsNone(self.read({"Features/Roads.gpkg": entry(bad)}).file("Features/Roads.gpkg").frame)

    def test_a_non_positive_unit_is_refused(self) -> None:
        bad = {**copy.deepcopy(VECTOR_FRAME), "unit_to_metres": 0}
        self.assertIsNone(self.read({"Features/Roads.gpkg": entry(bad)}).file("Features/Roads.gpkg").frame)

    def test_placing_an_unreadable_or_absent_frame_raises(self) -> None:
        reader = self.read()
        with self.assertRaises(FrameError):
            reader.placement("Site/Other.glb")
        with self.assertRaises(KeyError):
            reader.placement("Nope.glb")

    def test_an_unknown_key_beside_the_known_ones_is_tolerated(self) -> None:
        frame = {**copy.deepcopy(VECTOR_FRAME), "from_a_later_minor": 1}
        self.assertIsNotNone(self.read({"Features/Roads.gpkg": entry(frame)})
                             .file("Features/Roads.gpkg").frame)


class TheAxisTransform(ReaderCase):
    def test_a_gltf_frame_maps_x_east_y_up_z_south_onto_east_north_up(self) -> None:
        placement = self.read().placement("Site/Terrain.glb")
        self.assertEqual(((1, 0, 0), (0, 0, -1), (0, 1, 0)), placement.axis_transform)
        self.assertEqual((10, -30, 20), placement.axes_to_enu((10, 20, 30)))

    def test_a_projected_frame_is_the_identity(self) -> None:
        placement = self.read().placement("Elevation/DEM.tif")
        self.assertEqual(((1, 0, 0), (0, 1, 0), (0, 0, 1)), placement.axis_transform)

    def test_a_flipped_and_swapped_mapping_is_a_signed_permutation(self) -> None:
        frame = {**copy.deepcopy(VECTOR_FRAME), "axis_mapping": {"x": "north", "y": "west", "z": "down"}}
        placement = self.read({"Features/Roads.gpkg": entry(frame)}).placement("Features/Roads.gpkg")
        self.assertEqual((-2, 1, -3), placement.axes_to_enu((1, 2, 3)))


class TheOffsetWithinOneFrame(ReaderCase):
    def test_an_absolute_file_needs_no_offset(self) -> None:
        placement = self.read().placement("Elevation/DEM.tif")
        self.assertEqual("absolute", placement.mode)
        self.assertIsNone(placement.offset)
        self.assertIsNone(placement.offset_problem)
        self.assertEqual((585100.0, 4512200.0, 12.0),
                         placement.to_enu_metres((585100.0, 4512200.0, 12.0)))

    def test_an_offset_file_gives_the_site_origin_as_its_offset(self) -> None:
        placement = self.read().placement("Site/Terrain.glb")
        self.assertEqual("offset", placement.mode)
        self.assertEqual((585000.0, 4512000.0, 10.3),
                         (placement.offset.x, placement.offset.y, placement.offset.z))
        self.assertEqual("m", placement.offset.unit)
        self.assertEqual("vertical_datum", placement.offset.z_reference)
        self.assertEqual({"code": "EPSG:32618"}, placement.offset.crs)

    def test_an_offset_point_is_placed_by_axes_unit_and_addition_alone(self) -> None:
        placement = self.read().placement("Site/Terrain.glb")
        east, north, up = placement.to_enu_metres((10.0, 5.0, 20.0))
        self.assertAlmostEqual(585010.0, east)
        self.assertAlmostEqual(4512000.0 - 20.0, north)
        self.assertAlmostEqual(15.3, up)

    def test_the_file_unit_and_the_origin_unit_are_each_read_never_assumed_alike(self) -> None:
        feet = {**copy.deepcopy(SITE_ORIGIN),
                "projected": {**SITE_ORIGIN["projected"], "unit": "ft", "x": 1000.0, "y": 2000.0,
                              "z": 100.0}}
        us_foot_mesh = {**copy.deepcopy(MESH_FRAME), "unit_to_metres": 1200 / 3937}
        placement = self.read({"Site/Terrain.glb": entry(us_foot_mesh)}, feet).placement("Site/Terrain.glb")
        east, north, up = placement.to_enu_metres((100.0, 10.0, 0.0))
        self.assertAlmostEqual(1000 * 0.3048 + 100 * 1200 / 3937, east)
        self.assertAlmostEqual(2000 * 0.3048, north)
        self.assertAlmostEqual(100 * 0.3048 + 10 * 1200 / 3937, up)

    def test_a_distinct_vertical_unit_scales_only_the_heights(self) -> None:
        frame = {**copy.deepcopy(VECTOR_FRAME), "unit_to_metres": 1, "vertical_unit_to_metres": 0.3048}
        placement = self.read({"Features/Roads.gpkg": entry(frame)}).placement("Features/Roads.gpkg")
        self.assertEqual(0.3048, placement.frame.vertical_unit_to_metres)
        self.assertEqual((10.0, 20.0, 30.0 * 0.3048), placement.to_enu_metres((10.0, 20.0, 30.0)))

    def test_an_unknown_origin_unit_is_refused_not_guessed(self) -> None:
        odd = {**copy.deepcopy(SITE_ORIGIN), "projected": {**SITE_ORIGIN["projected"], "unit": "chain"}}
        placement = self.read(origin=odd).placement("Site/Terrain.glb")
        self.assertIsNone(placement.offset)
        self.assertIn("chain", placement.offset_problem)

    def test_with_no_projected_origin_there_is_no_offset_and_no_reprojection(self) -> None:
        bare = {k: v for k, v in SITE_ORIGIN.items() if k != "projected"}
        placement = self.read(origin=bare).placement("Site/Terrain.glb")
        self.assertIsNone(placement.offset)
        self.assertIn("projected", placement.offset_problem)
        with self.assertRaises(FrameError):
            placement.to_enu_metres((0.0, 0.0, 0.0))

    def test_with_no_site_origin_at_all_an_offset_file_has_no_offset(self) -> None:
        placement = self.read(origin=None).placement("Site/Terrain.glb")
        self.assertIsNone(placement.offset)
        self.assertIn("site_origin", placement.offset_problem)

    def test_an_origin_in_another_projection_is_not_converted(self) -> None:
        elsewhere = {**copy.deepcopy(SITE_ORIGIN),
                     "projected": {**SITE_ORIGIN["projected"], "crs": CRS_13N}}
        placement = self.read(origin=elsewhere).placement("Site/Terrain.glb")
        self.assertIsNone(placement.offset)
        self.assertIn("reproject", placement.offset_problem)


class AFileInAnotherFrame(ReaderCase):
    def test_same_frame_ignores_case_in_a_code_and_compares_unlike_forms_as_different(self) -> None:
        reader = self.read({"A.glb": entry(MESH_FRAME), "B.tif": entry(
            {**copy.deepcopy(RASTER_FRAME), "horizontal_crs": {"code": "epsg:32618"}}),
            "C.tif": entry({**copy.deepcopy(RASTER_FRAME), "horizontal_crs": {"wkt2": "PROJCRS[...]"}})})
        a, b, c = (reader.file(p).frame for p in ("A.glb", "B.tif", "C.tif"))
        self.assertTrue(a.same_frame_as(b))
        self.assertFalse(a.same_frame_as(c))

    def test_a_different_vertical_crs_or_geoid_is_a_different_frame(self) -> None:
        other = {**copy.deepcopy(RASTER_FRAME), "geoid_model": "EGM2008"}
        reader = self.read({"A.tif": entry(RASTER_FRAME), "B.tif": entry(other)})
        self.assertFalse(reader.file("A.tif").frame.same_frame_as(reader.file("B.tif").frame))

    def test_a_transform_between_projections_is_refused(self) -> None:
        other = {**copy.deepcopy(RASTER_FRAME), "horizontal_crs": CRS_13N}
        reader = self.read({"A.tif": entry(RASTER_FRAME), "B.tif": entry(other)})
        with self.assertRaises(FrameMismatch) as caught:
            reader.transform("A.tif", "B.tif")
        self.assertIn("reproject", str(caught.exception))
        self.assertIsInstance(caught.exception, FrameError)

    def test_a_transform_between_two_vertical_datums_is_refused(self) -> None:
        other = {**copy.deepcopy(RASTER_FRAME), "vertical_crs": {"code": "EPSG:3855"}}
        reader = self.read({"A.tif": entry(RASTER_FRAME), "B.tif": entry(other)})
        with self.assertRaises(FrameMismatch):
            reader.transform("A.tif", "B.tif")


class TheTransformBetweenFilesOfOneFrame(ReaderCase):
    def test_an_offset_mesh_lands_on_the_raster_it_overlies(self) -> None:
        reader = self.read()
        move = reader.transform("Site/Terrain.glb", "Elevation/DEM.tif")
        self.assertEqual(((1, 0, 0), (0, 0, -1), (0, 1, 0)), move.matrix)
        self.assertEqual((585000.0, 4512000.0, 10.3), move.translation)
        east, north, up = move.apply((10.0, 5.0, 20.0))
        self.assertAlmostEqual(585010.0, east)
        self.assertAlmostEqual(4511980.0, north)
        self.assertAlmostEqual(15.3, up)

    def test_the_reverse_transform_inverts_the_first(self) -> None:
        reader = self.read()
        there = reader.transform("Site/Terrain.glb", "Elevation/DEM.tif")
        back = reader.transform("Elevation/DEM.tif", "Site/Terrain.glb")
        for point in ((0.0, 0.0, 0.0), (10.0, 5.0, 20.0), (-3.0, 8.0, 1.5)):
            got = back.apply(there.apply(point))
            for want, have in zip(point, got):
                self.assertAlmostEqual(want, have)

    def test_two_offset_files_differ_by_axes_and_unit_alone(self) -> None:
        z_up = {**copy.deepcopy(MESH_FRAME), "axis_mapping": {"x": "east", "y": "north", "z": "up"},
                "unit_to_metres": 0.3048}
        reader = self.read({"A.glb": entry(MESH_FRAME), "B.usd": entry(z_up)})
        move = reader.transform("A.glb", "B.usd")
        self.assertEqual((0, 0, 0), move.translation)
        east, north, up = move.apply((1.0, 2.0, 3.0))
        self.assertAlmostEqual(1.0 / 0.3048, east)
        self.assertAlmostEqual(-3.0 / 0.3048, north)
        self.assertAlmostEqual(2.0 / 0.3048, up)

    def test_two_offset_files_need_no_projected_origin(self) -> None:
        bare = {k: v for k, v in SITE_ORIGIN.items() if k != "projected"}
        reader = self.read({"A.glb": entry(MESH_FRAME), "B.glb": entry(MESH_FRAME)}, bare)
        self.assertEqual((0, 0, 0), reader.transform("A.glb", "B.glb").translation)

    def test_an_offset_against_an_absolute_file_needs_the_origin(self) -> None:
        bare = {k: v for k, v in SITE_ORIGIN.items() if k != "projected"}
        reader = self.read(origin=bare)
        with self.assertRaises(FrameError):
            reader.transform("Site/Terrain.glb", "Elevation/DEM.tif")

    def test_an_ellipsoidal_origin_height_is_not_mixed_into_datum_heights(self) -> None:
        ellipsoidal = {**copy.deepcopy(SITE_ORIGIN),
                       "projected": {**SITE_ORIGIN["projected"], "z_reference": "ellipsoidal"}}
        reader = self.read(origin=ellipsoidal)
        with self.assertRaises(FrameError) as caught:
            reader.transform("Site/Terrain.glb", "Elevation/DEM.tif")
        self.assertIn("ellipsoidal", str(caught.exception))

    def test_a_file_with_no_frame_cannot_be_transformed(self) -> None:
        with self.assertRaises(FrameError):
            self.read().transform("Site/Other.glb", "Elevation/DEM.tif")


class WhatIsNotABundle(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def test_a_missing_path_raises_bundle_error(self) -> None:
        with self.assertRaises(BundleError):
            read_bundle(self.tmp / "nope.zip")

    def test_a_zip_with_no_manifest_raises_bundle_error(self) -> None:
        archive = make_zip(self.tmp / "b.zip", {"README.md": b"hi"})
        with self.assertRaises(BundleError) as caught:
            read_bundle(archive)
        self.assertIn("manifest", str(caught.exception))

    def test_a_manifest_that_is_not_a_json_object_raises_bundle_error(self) -> None:
        for body in (b"{not json", b"[1, 2]"):
            archive = make_zip(self.tmp / "c.zip", {"Metadata/manifest.json": body})
            with self.assertRaises(BundleError):
                read_bundle(archive)

    def test_the_reader_does_not_need_a_schema_or_a_network(self) -> None:
        folder = make_folder(self.tmp / "ok", bundle_files(json.loads(json.dumps(manifest()))))
        with read_bundle(folder) as reader:
            self.assertEqual("1.14.0", reader.manifest["version"])


if __name__ == "__main__":
    unittest.main()
