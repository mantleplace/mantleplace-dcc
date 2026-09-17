"""Tests for the pure half of the mark renderer.

Stdlib only, and no image library: every function under test takes plain buffers and numbers,
which is the whole reason the geometry lives apart from the Pillow shim. These run in CI; the
renderer itself never can, because its input is a private file (see README.md).
"""

import unittest

import mark_geometry as g


class MatteRule(unittest.TestCase):
    """The monogram is separated from the tile by luminance floor, not by colour matching.

    The canonical mark holds exactly three colours: the tile 0xFF7110 (min channel 16), a black
    extrude (min channel 0) and a white monogram (min channel 255). A floor above
    the tile's minimum therefore rejects both the tile and the extrude in one rule.
    """

    def test_white_is_fully_opaque(self):
        self.assertEqual(g.matte_value(255), 255)

    def test_the_tile_is_fully_transparent(self):
        self.assertEqual(g.matte_value(16), 0)

    def test_the_extrude_is_fully_transparent(self):
        self.assertEqual(g.matte_value(0), 0)

    def test_the_floor_itself_is_transparent(self):
        self.assertEqual(g.matte_value(g.MATTE_FLOOR), 0)

    def test_antialiased_edges_land_between(self):
        midpoint = (g.MATTE_FLOOR + 255) // 2
        self.assertTrue(0 < g.matte_value(midpoint) < 255)

    def test_the_rule_never_leaves_byte_range(self):
        for channel in range(256):
            self.assertTrue(0 <= g.matte_value(channel) <= 255)

    def test_the_rule_rises_monotonically(self):
        values = [g.matte_value(c) for c in range(256)]
        self.assertEqual(values, sorted(values))

    def test_the_lookup_table_agrees_with_the_rule(self):
        table = g.matte_lut()
        self.assertEqual(len(table), 256)
        self.assertEqual(list(table), [g.matte_value(c) for c in range(256)])


class BoundingBox(unittest.TestCase):
    """The optical box of the monogram, found on an alpha buffer."""

    @staticmethod
    def buffer(width, height, opaque):
        cells = bytearray(width * height)
        for x, y in opaque:
            cells[y * width + x] = 255
        return bytes(cells)

    def test_a_single_pixel_is_its_own_box(self):
        buf = self.buffer(4, 4, [(2, 1)])
        self.assertEqual(g.bounding_box(buf, 4), (2, 1, 3, 2))

    def test_the_box_spans_every_opaque_pixel(self):
        buf = self.buffer(5, 5, [(1, 1), (3, 2), (2, 4)])
        self.assertEqual(g.bounding_box(buf, 5), (1, 1, 4, 5))

    def test_a_full_buffer_boxes_the_whole_image(self):
        buf = b"\xff" * 9
        self.assertEqual(g.bounding_box(buf, 3), (0, 0, 3, 3))

    def test_an_empty_buffer_has_no_box(self):
        self.assertIsNone(g.bounding_box(bytes(9), 3))

    def test_pixels_below_the_threshold_are_not_in_the_box(self):
        buf = bytes([0, 40, 0, 0, 255, 0, 0, 0, 0])
        self.assertEqual(g.bounding_box(buf, 3, threshold=128), (1, 1, 2, 2))

    def test_the_threshold_is_inclusive(self):
        buf = bytes([0, 0, 0, 0, 128, 0, 0, 0, 0])
        self.assertEqual(g.bounding_box(buf, 3, threshold=128), (1, 1, 2, 2))


class Placement(unittest.TestCase):
    """Scaling the optical box into a tile, centred, with a margin on every side.

    Recentring is not cosmetic. Stripped of its extrude the monogram sits high and left
    (left margin 18.6% against right 14.2%, top 22.9% against bottom 18.6%), because the
    extrude was balancing the composition. See ADR 0009.
    """

    def test_a_square_box_fills_the_margin_exactly(self):
        p = g.place(100, 100, size=50, margin=0.10)
        self.assertEqual((p.width, p.height), (40, 40))
        self.assertEqual((p.left, p.top), (5, 5))

    def test_a_wide_box_is_limited_by_width(self):
        p = g.place(200, 100, size=50, margin=0.10)
        self.assertEqual((p.width, p.height), (40, 20))
        self.assertEqual(p.left, 5)
        self.assertEqual(p.top, 15)

    def test_a_tall_box_is_limited_by_height(self):
        p = g.place(100, 200, size=50, margin=0.10)
        self.assertEqual((p.width, p.height), (20, 40))
        self.assertEqual(p.left, 15)
        self.assertEqual(p.top, 5)

    def test_the_artwork_never_leaves_the_tile(self):
        for size in g.HOST_SIZES:
            p = g.place(2753, 2396, size=size, margin=g.MARGIN)
            self.assertGreaterEqual(p.left, 0)
            self.assertGreaterEqual(p.top, 0)
            self.assertLessEqual(p.left + p.width, size)
            self.assertLessEqual(p.top + p.height, size)

    def test_aspect_ratio_survives_the_smallest_size(self):
        p = g.place(2753, 2396, size=16, margin=g.MARGIN)
        self.assertAlmostEqual(p.width / p.height, 2753 / 2396, delta=0.12)

    def test_no_dimension_collapses_to_nothing(self):
        p = g.place(4000, 1, size=16, margin=g.MARGIN)
        self.assertGreaterEqual(p.width, 1)
        self.assertGreaterEqual(p.height, 1)

    def test_a_margin_of_zero_is_full_bleed(self):
        p = g.place(10, 10, size=64, margin=0.0)
        self.assertEqual((p.width, p.height, p.left, p.top), (64, 64, 0, 0))


class SnapCurve(unittest.TestCase):
    """Below 32 px the antialiased edge is wider than the stem it softens, so it is hardened."""

    def test_the_pivot_is_unmoved(self):
        self.assertEqual(g.snap_value(128), 128)

    def test_opaque_stays_opaque(self):
        self.assertEqual(g.snap_value(255), 255)

    def test_transparent_stays_transparent(self):
        self.assertEqual(g.snap_value(0), 0)

    def test_edges_are_pushed_away_from_the_pivot(self):
        self.assertLess(g.snap_value(100), 100)
        self.assertGreater(g.snap_value(160), 160)

    def test_the_curve_never_leaves_byte_range(self):
        for alpha in range(256):
            self.assertTrue(0 <= g.snap_value(alpha) <= 255)

    def test_the_curve_rises_monotonically(self):
        values = [g.snap_value(a) for a in range(256)]
        self.assertEqual(values, sorted(values))

    def test_the_lookup_table_agrees_with_the_curve(self):
        table = g.snap_lut()
        self.assertEqual(len(table), 256)
        self.assertEqual(list(table), [g.snap_value(a) for a in range(256)])


class SizePolicy(unittest.TestCase):
    """Which treatment each size gets. The evidence is in ADR 0009."""

    def test_the_small_sizes_are_snapped(self):
        self.assertTrue(g.uses_snapping(16))
        self.assertTrue(g.uses_snapping(24))

    def test_the_large_sizes_keep_their_antialiasing(self):
        self.assertFalse(g.uses_snapping(32))
        self.assertFalse(g.uses_snapping(48))
        self.assertFalse(g.uses_snapping(64))

    def test_every_host_size_has_a_treatment(self):
        for size in g.HOST_SIZES:
            self.assertIsInstance(g.uses_snapping(size), bool)

    def test_the_revit_set_covers_the_documented_display_scales(self):
        for scale in (1.0, 1.5, 2.0):
            for base in (16, 32):
                self.assertIn(round(base * scale), g.HOST_SIZES)


if __name__ == "__main__":
    unittest.main()
