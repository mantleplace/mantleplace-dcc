# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""The scene's anchor and the offset a later bundle takes from it. No Blender here.

Each test hands the core the anchor record the scene holds (or none) and the site origin one more
import applies, and asserts whether that import becomes the anchor, how far it moves, and the one
sentence it owes the curator where it cannot be moved.
"""

import copy
import unittest
from dataclasses import replace

from mantleplace_blender.core.anchor import MAX_OFFSET_M, Placement, bundle_record, decide_placement
from mantleplace_blender.core.bundle import plan_import, scene_properties

from .test_bundle import manifest


def origin(**projected):
    """The Georeference one bundle applies, with its projected point changed by `projected`."""
    document = manifest()
    document["location"]["site_origin"]["projected"].update(projected)
    if "crs" in projected:  # the Context file is in the origin's CRS, or no origin applies at all
        for entry in document["file_entries"].values():
            entry["frame"]["horizontal_crs"] = projected["crs"]
    georeference = plan_import(document).georeference
    assert georeference is not None
    return georeference


def record(georeference):
    """The anchor record the scene holds after `georeference` was imported first."""
    return copy.deepcopy(scene_properties(georeference)["mantleplace_site_origin"])


FIRST = origin()  # easting 620050.0, northing 3348864.5, height 110.672, EPSG:32614, metres
STAYS = Placement(anchor=False, offset=(0.0, 0.0, 0.0), note=None)


class AnchorTests(unittest.TestCase):
    def test_the_first_import_becomes_the_anchor_and_does_not_move(self):
        self.assertEqual(decide_placement(None, FIRST),
                         Placement(anchor=True, offset=(0.0, 0.0, 0.0), note=None))

    def test_a_bundle_without_an_applied_origin_neither_anchors_nor_moves(self):
        # plan_import has already said why the origin was not applied: one warning, not two.
        self.assertEqual(decide_placement(None, None), STAYS)
        self.assertEqual(decide_placement(record(FIRST), None), STAYS)

    def test_a_later_import_never_becomes_the_anchor(self):
        self.assertFalse(decide_placement(record(FIRST), origin(x=620150.0)).anchor)
        self.assertFalse(decide_placement(record(FIRST), origin(crs={"code": "EPSG:32615"})).anchor)

    def test_the_same_origin_again_sits_at_zero(self):
        self.assertEqual(decide_placement(record(FIRST), FIRST), STAYS)


class OffsetTests(unittest.TestCase):
    def test_a_neighbour_is_moved_by_the_difference_of_the_two_projected_points(self):
        placement = decide_placement(record(FIRST), origin(x=620150.0, y=3348764.5, z=112.672))
        self.assertEqual(placement.offset, (100.0, -100.0, 2.0))
        self.assertIsNone(placement.note)

    def test_a_third_bundle_is_measured_from_the_anchor_not_from_the_second(self):
        anchor = record(FIRST)
        second = decide_placement(anchor, origin(x=620150.0))
        third = decide_placement(anchor, origin(x=620250.0))
        self.assertEqual(second.offset[0], 100.0)
        self.assertEqual(third.offset[0], 200.0)

    def test_a_bundle_west_of_the_anchor_moves_negative(self):
        self.assertEqual(decide_placement(record(FIRST), origin(x=619950.0)).offset[0], -100.0)

    def test_each_side_is_read_in_its_own_unit(self):
        # §6: units are read from each side. An anchor in US survey feet and a bundle in metres
        # (same CRS code) are subtracted after each is taken to metres, never assumed alike.
        anchor = record(origin(unit="usft", x=1000.0, y=2000.0, z=10.0))
        metres = origin(x=1000.0 * 1200 / 3937 + 50.0, y=2000.0 * 1200 / 3937, z=10.0 * 1200 / 3937)
        placement = decide_placement(anchor, metres)
        self.assertIsNone(placement.note)
        for got, want in zip(placement.offset, (50.0, 0.0, 0.0)):
            self.assertAlmostEqual(got, want, places=9)

    def test_a_foot_neighbour_of_a_foot_anchor_moves_in_metres(self):
        anchor = record(origin(unit="usft", x=1000.0, y=2000.0, z=10.0))
        placement = decide_placement(anchor, origin(unit="usft", x=1000.0 + 3937.0, y=2000.0, z=10.0))
        self.assertAlmostEqual(placement.offset[0], 1200.0, places=9)

    def test_the_crs_code_compares_without_regard_to_case_or_padding(self):
        anchor = record(FIRST)
        anchor["crs"] = " epsg:32614 "
        self.assertEqual(decide_placement(anchor, origin(x=620150.0)).offset, (100.0, 0.0, 0.0))

    def test_the_limit_is_inclusive(self):
        self.assertEqual(MAX_OFFSET_M, 16384.0)
        at_limit = decide_placement(record(FIRST), origin(x=620050.0 + MAX_OFFSET_M))
        self.assertEqual(at_limit.offset, (MAX_OFFSET_M, 0.0, 0.0))
        self.assertIsNone(at_limit.note)


class RefusalTests(unittest.TestCase):
    """Every refusal leaves the bundle at its own offsets, not the anchor, with one sentence."""

    def assertRefused(self, placement, *words):
        self.assertEqual(placement.offset, (0.0, 0.0, 0.0))
        self.assertFalse(placement.anchor)
        self.assertIsNotNone(placement.note)
        self.assertIn("own offsets", placement.note)
        self.assertNotIn("\n", placement.note)
        for word in words:
            self.assertIn(word, placement.note)

    def test_another_crs_is_refused_and_both_are_named(self):
        placement = decide_placement(record(FIRST), origin(crs={"code": "EPSG:32615"}, x=620150.0))
        self.assertRefused(placement, "EPSG:32615", "EPSG:32614", "reprojection")

    def test_another_z_reference_is_refused_and_both_are_named(self):
        placement = decide_placement(record(FIRST), origin(z_reference="orthometric", x=620150.0))
        self.assertRefused(placement, "orthometric", "ellipsoidal")

    def test_a_crs_without_a_code_cannot_be_compared_and_is_refused(self):
        wkt = origin(crs={"wkt2": 'PROJCRS["x"]'}, x=620150.0)
        self.assertRefused(decide_placement(record(FIRST), wkt), "code")

    def test_an_anchor_recorded_without_a_code_is_refused(self):
        wkt = origin(crs={"wkt2": 'PROJCRS["x"]'})
        self.assertRefused(decide_placement(record(wkt), origin(x=620150.0)), "code")

    def test_beyond_the_limit_is_refused_and_names_the_distance_and_the_limit(self):
        far = origin(x=620050.0 + MAX_OFFSET_M + 1.0)
        self.assertRefused(decide_placement(record(FIRST), far), "16385", "16384")

    def test_the_limit_applies_to_each_axis(self):
        self.assertRefused(decide_placement(record(FIRST), origin(y=3348864.5 - 20000.0)), "metres")
        self.assertRefused(decide_placement(record(FIRST), origin(z=110.672 + 20000.0)), "metres")

    def test_a_bundle_hundreds_of_kilometres_away_is_refused(self):
        self.assertRefused(decide_placement(record(FIRST), origin(x=900000.0)), "metres")

    def test_a_non_finite_coordinate_is_refused(self):
        nan = replace(origin(x=620150.0), x=float("nan"))
        self.assertRefused(decide_placement(record(FIRST), nan), "finite")

    def test_an_unreadable_anchor_record_is_refused_not_trusted(self):
        good = record(FIRST)
        for broken in ({}, {"crs": "EPSG:32614"}, {**good, "x": "620050"}, {**good, "y": True},
                       {**good, "unit": "furlong"}, {**good, "z_reference": None}):
            with self.subTest(broken=broken):
                self.assertRefused(decide_placement(broken, origin(x=620150.0)), "anchor")

    def test_a_record_that_is_not_an_object_is_refused(self):
        self.assertRefused(decide_placement("EPSG:32614", origin(x=620150.0)), "anchor")


class RecordTests(unittest.TestCase):
    def test_each_bundle_keeps_its_own_origin_and_offset(self):
        second = origin(x=620150.0)
        own = bundle_record(second, decide_placement(record(FIRST), second))
        self.assertEqual(own["site_origin"]["x"], 620150.0)
        self.assertEqual(own["offset_m"], [100.0, 0.0, 0.0])
        self.assertFalse(own["is_anchor"])

    def test_the_anchor_bundle_says_so(self):
        own = bundle_record(FIRST, decide_placement(None, FIRST))
        self.assertTrue(own["is_anchor"])
        self.assertEqual(own["offset_m"], [0.0, 0.0, 0.0])

    def test_a_record_is_plain_data_a_custom_property_can_hold(self):
        own = bundle_record(FIRST, decide_placement(None, FIRST))
        self.assertEqual(set(own), {"site_origin", "offset_m", "is_anchor"})
        for number in own["offset_m"]:
            self.assertIsInstance(number, float)
        self.assertIsInstance(own["site_origin"], dict)


if __name__ == "__main__":
    unittest.main()
