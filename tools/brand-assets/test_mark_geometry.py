import unittest
from pathlib import Path

import mark_geometry as g

SOURCE = Path(__file__).resolve().parent / g.SOURCE_NAME


def area(polygon):
    total = 0.0
    for (x1, y1), (x2, y2) in zip(polygon, polygon[1:] + polygon[:1]):
        total += x1 * y2 - x2 * y1
    return abs(total) / 2


class Numbers(unittest.TestCase):
    def test_signs_and_bare_decimals_split_numbers(self):
        self.assertEqual(g.numbers("1.64.96-3.1,2.14-4.4"), [1.64, 0.96, -3.1, 2.14, -4.4])

    def test_exponents_are_one_number(self):
        self.assertEqual(g.numbers("1e2 -2.5E-1"), [100.0, -0.25])


class PathFlattening(unittest.TestCase):
    def test_a_closed_square_is_one_polygon_of_its_corners(self):
        polygons = g.path_polygons("M0,0h10v10h-10Z")
        self.assertEqual(len(polygons), 1)
        self.assertEqual(polygons[0][:4], [(0.0, 0.0), (10.0, 0.0), (10.0, 10.0), (0.0, 10.0)])

    def test_relative_lines_accumulate_from_the_current_point(self):
        polygons = g.path_polygons("M5,5l10,0l0,10z")
        self.assertEqual(polygons[0], [(5.0, 5.0), (15.0, 5.0), (15.0, 15.0)])

    def test_each_moveto_starts_its_own_subpath(self):
        polygons = g.path_polygons("M0,0h10v10h-10ZM2,2h6v6h-6Z")
        self.assertEqual(len(polygons), 2)
        self.assertEqual(polygons[1][0], (2.0, 2.0))

    def test_a_relative_moveto_after_close_starts_from_the_subpath_start(self):
        polygons = g.path_polygons("M10,10h5v5zm1,1h1v1z")
        self.assertEqual(polygons[1][0], (11.0, 11.0))

    def test_a_cubic_ends_on_its_end_point(self):
        polygons = g.path_polygons("M0,0c0,10,10,10,10,0", segments=8)
        self.assertEqual(polygons[0][-1], (10.0, 0.0))
        self.assertEqual(len(polygons[0]), 9)

    def test_a_cubic_passes_through_its_midpoint(self):
        polygons = g.path_polygons("M0,0c0,10,10,10,10,0", segments=2)
        self.assertEqual(polygons[0][1], (5.0, 7.5))

    def test_a_smooth_cubic_reflects_the_previous_control_point(self):
        smooth = g.path_polygons("M0,0c0,10,10,10,10,0s10,-10,10,0", segments=2)[0]
        explicit = g.path_polygons("M0,0c0,10,10,10,10,0c0,-10,10,-10,10,0", segments=2)[0]
        self.assertEqual(smooth, explicit)

    def test_implicit_repeats_reuse_the_command(self):
        self.assertEqual(g.path_polygons("M0,0l1,0 0,1")[0], [(0.0, 0.0), (1.0, 0.0), (1.0, 1.0)])

    def test_an_unknown_command_is_refused(self):
        with self.assertRaises(ValueError):
            g.path_polygons("M0,0a5,5,0,0,1,10,0")


class TheCommittedSource(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.mark = g.read_mark(SOURCE.read_text(encoding="utf-8"))

    def test_it_is_a_square_view(self):
        self.assertEqual(self.mark.view[0], self.mark.view[1])

    def test_it_is_clipped_to_a_disc_that_fills_the_view(self):
        cx, cy, r = self.mark.clip
        self.assertEqual((cx * 2, cy * 2, r * 2), (self.mark.view[0],) * 3)

    def test_the_disc_is_the_brand_orange(self):
        self.assertEqual(self.mark.layers[0].fill, g.MANTLE_ORANGE)
        self.assertEqual(self.mark.layers[0].kind, "circle")

    def test_the_extrude_is_drawn_and_drawn_beneath_the_monogram(self):
        fills = [layer.fill for layer in self.mark.layers]
        self.assertIn(g.BLACK, fills)
        self.assertLess(fills.index(g.BLACK), fills.index(g.WHITE))

    def test_every_layer_has_area(self):
        for layer in self.mark.layers:
            if layer.kind == "path":
                self.assertGreater(sum(area(p) for p in layer.polygons), 1.0)

    def test_the_monogram_and_its_extrude_stay_inside_the_disc(self):
        cx, cy, r = self.mark.clip
        for layer in self.mark.layers[1:]:
            points = [pt for p in layer.polygons for pt in p] if layer.kind == "path" else [(layer.circle[0], layer.circle[1])]
            for x, y in points:
                self.assertLessEqual((x - cx) ** 2 + (y - cy) ** 2, r * r)


class Targets(unittest.TestCase):
    def test_the_revit_set_covers_the_documented_display_scales(self):
        revit = [t.size for t in g.targets() if t.path.startswith(g.REVIT_RESOURCES)]
        self.assertEqual(tuple(revit), (16, 24, 32, 48, 64))

    def test_unreal_gets_its_tab_icon_and_plugin_icon(self):
        unreal = {t.path.rsplit("/", 1)[-1]: t.size for t in g.targets() if t.path.startswith(g.UNREAL_RESOURCES)}
        self.assertEqual(unreal, {"MantlePlaceTabIcon.png": 64, "Icon128.png": 128})

    def test_every_target_already_exists_in_the_tree(self):
        root = Path(__file__).resolve().parent.parent.parent
        for target in g.targets():
            self.assertTrue((root / target.path).is_file(), target.path)


if __name__ == "__main__":
    unittest.main()
