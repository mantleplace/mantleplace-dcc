# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""The pure core's reading of a manifest: the version, the two pointers, and the site origin.

No Blender here. Each test hands the core a manifest dict shaped like a 2.0 bundle's and asserts
what the add-on will import and how it will place it.
"""

import copy
import unittest

from mantleplace_blender.core.bundle import (
    BundleRefused,
    plan_import,
    read_major,
    scene_properties,
    wrapper_stage,
)

FRAME = {
    "horizontal_crs": {"code": "EPSG:32614"},
    "unit_to_metres": 1.0,
    "axis_order": "east-north",
    "axis_mapping": {"x": "east", "y": "north", "z": "up"},
    "coordinate_mode": "offset",
}

MANIFEST = {
    "version": "2.0.0",
    "layout": {
        "manifest": "Metadata/manifest.json",
        "context_usdz": "Site/Context.usdz",
        "features_usdz": "Site/Features.usdz",
    },
    "location": {
        "site_origin": {
            "latitude": 30.2655,
            "longitude": -97.752,
            "ellipsoidal_height_m": 110.672,
            "datum_height_m": 137.792,
            "vertical_datum": "NAVD88 (GEOID18)",
            "geodetic_crs": {"code": "EPSG:4979"},
            "projected": {
                "crs": {"code": "EPSG:32614"},
                "x": 620050.0,
                "y": 3348864.5,
                "unit": "m",
                "z": 110.672,
                "z_reference": "ellipsoidal",
            },
        }
    },
    "file_entries": {
        "Site/Context.usdz": {"frame": dict(FRAME), "odbl_role": "produced-work"},
        "Site/Features.usdz": {"frame": dict(FRAME), "odbl_role": "database"},
    },
    "files": {
        "Site/Context.usdz": {"part": "context_usdz", "sha256": "a" * 64},
        "Site/Features.usdz": {"part": "vector.usdz@MAX"},
    },
}


def manifest(**changes):
    document = copy.deepcopy(MANIFEST)
    for path, value in changes.items():
        node = document
        keys = path.split("__")
        for key in keys[:-1]:
            node = node[key]
        if value is None:
            node.pop(keys[-1], None)
        else:
            node[keys[-1]] = value
    return document


class VersionTests(unittest.TestCase):
    def test_a_2x_bundle_is_read(self):
        self.assertEqual(read_major(manifest()), 2)
        self.assertEqual(read_major(manifest(version="2.7.1")), 2)

    def test_a_higher_major_is_refused_and_names_both_versions(self):
        with self.assertRaises(BundleRefused) as caught:
            read_major(manifest(version="3.0.0"))
        message = str(caught.exception)
        self.assertIn("3.0.0", message)
        self.assertIn("2.x", message)
        self.assertIn("update", message.lower())

    def test_a_1x_bundle_is_refused_because_it_carries_no_context_file(self):
        with self.assertRaises(BundleRefused) as caught:
            read_major(manifest(version="1.14.0"))
        self.assertIn("1.14.0", str(caught.exception))

    def test_an_integer_version_is_the_pre_history_and_refused(self):
        with self.assertRaises(BundleRefused):
            read_major(manifest(version=3))

    def test_an_unparsable_version_is_refused(self):
        for bad in ("", "two", "2", None):
            with self.subTest(bad=bad), self.assertRaises(BundleRefused):
                read_major({"version": bad} if bad is not None else {})

    def test_a_higher_major_is_refused_before_any_pointer_is_read(self):
        # §11: a reader of a higher major reads `version` and nothing else.
        with self.assertRaises(BundleRefused) as caught:
            plan_import({"version": "3.0.0", "layout": "not even an object"})
        self.assertIn("3.0.0", str(caught.exception))


class PointerTests(unittest.TestCase):
    def test_both_files_come_from_their_layout_pointers(self):
        plan = plan_import(manifest())
        self.assertEqual(plan.context_path, "Site/Context.usdz")
        self.assertEqual(plan.features_path, "Site/Features.usdz")
        self.assertEqual(plan.notes, ())

    def test_a_moved_pointer_is_followed_not_the_folder_convention(self):
        document = manifest(layout__context_usdz="Elsewhere/Ctx.usdz")
        document["file_entries"]["Elsewhere/Ctx.usdz"] = {"frame": dict(FRAME)}
        self.assertEqual(plan_import(document).context_path, "Elsewhere/Ctx.usdz")

    def test_features_left_out_on_request(self):
        plan = plan_import(manifest(), include_features=False)
        self.assertIsNone(plan.features_path)
        self.assertEqual(plan.notes, ())

    def test_no_context_pointer_falls_back_to_the_fixed_name_the_manifest_lists(self):
        plan = plan_import(manifest(layout__context_usdz=None, layout__features_usdz=None))
        self.assertEqual(plan.context_path, "Site/Context.usdz")
        self.assertEqual(plan.features_path, "Site/Features.usdz")
        self.assertTrue(any("context_usdz" in note for note in plan.notes))

    def test_no_context_pointer_and_no_listing_refuses_the_bundle(self):
        document = manifest(layout__context_usdz=None)
        document["file_entries"].pop("Site/Context.usdz")
        document["files"].pop("Site/Context.usdz")
        with self.assertRaises(BundleRefused) as caught:
            plan_import(document)
        self.assertIn("Context", str(caught.exception))

    def test_a_missing_features_file_is_a_named_skip_not_a_refusal(self):
        document = manifest(layout__features_usdz=None)
        document["file_entries"].pop("Site/Features.usdz")
        document["files"].pop("Site/Features.usdz")
        plan = plan_import(document)
        self.assertIsNone(plan.features_path)
        self.assertTrue(any("Features" in note for note in plan.notes))

    def test_an_explicitly_null_features_pointer_is_honest_absence(self):
        # §6.1: null says the layer is absent, so the fixed name is not tried even if listed.
        document = manifest()
        document["layout"]["features_usdz"] = None
        plan = plan_import(document)
        self.assertIsNone(plan.features_path)
        self.assertTrue(any("carries no Features" in note for note in plan.notes))

    def test_a_pointer_that_escapes_the_bundle_is_refused(self):
        for bad in ("../Context.usdz", "/etc/Context.usdz", "C:/Context.usdz", "Site/../../x.usdz",
                    "Site\\..\\..\\x.usdz"):
            with self.subTest(bad=bad), self.assertRaises(BundleRefused):
                plan_import(manifest(layout__context_usdz=bad))

    def test_a_features_file_in_another_frame_is_skipped_by_name(self):
        document = manifest()
        document["file_entries"]["Site/Features.usdz"]["frame"]["horizontal_crs"] = {"code": "EPSG:6543"}
        plan = plan_import(document)
        self.assertIsNone(plan.features_path)
        self.assertTrue(any("Site/Features.usdz" in note and "EPSG:6543" in note for note in plan.notes))

    def test_the_digest_comes_from_the_files_member_and_absence_is_unknown(self):
        plan = plan_import(manifest())
        self.assertEqual(plan.digests["Site/Context.usdz"], "a" * 64)
        self.assertIsNone(plan.digests["Site/Features.usdz"])


class OriginTests(unittest.TestCase):
    def test_the_projected_origin_is_applied_verbatim(self):
        geo = plan_import(manifest()).georeference
        self.assertIsNotNone(geo)
        self.assertEqual((geo.x, geo.y, geo.z), (620050.0, 3348864.5, 110.672))
        self.assertEqual(geo.crs_code, "EPSG:32614")
        self.assertEqual(geo.unit, "m")
        self.assertEqual(geo.z_reference, "ellipsoidal")
        self.assertEqual(geo.vertical_datum, "NAVD88 (GEOID18)")

    def test_no_projected_point_is_a_named_skip_never_a_reprojection(self):
        plan = plan_import(manifest(location__site_origin__projected=None))
        self.assertIsNone(plan.georeference)
        self.assertTrue(any("reproject" in note for note in plan.notes))

    def test_no_site_origin_is_a_named_skip(self):
        plan = plan_import(manifest(location__site_origin=None))
        self.assertIsNone(plan.georeference)
        self.assertTrue(any("site_origin" in note for note in plan.notes))

    def test_an_origin_in_another_crs_than_the_context_file_is_not_applied(self):
        plan = plan_import(manifest(location__site_origin__projected__crs={"code": "EPSG:6543"}))
        self.assertIsNone(plan.georeference)
        self.assertTrue(any("EPSG:6543" in note for note in plan.notes))

    def test_crs_codes_compare_case_insensitively(self):
        plan = plan_import(manifest(location__site_origin__projected__crs={"code": "epsg:32614"}))
        self.assertIsNotNone(plan.georeference)

    def test_an_absolute_context_file_needs_no_origin(self):
        document = manifest()
        document["file_entries"]["Site/Context.usdz"]["frame"]["coordinate_mode"] = "absolute"
        plan = plan_import(document)
        self.assertIsNone(plan.georeference)
        self.assertTrue(any("absolute" in note for note in plan.notes))

    def test_an_unknown_frame_value_fails_closed(self):
        for key, value in (("coordinate_mode", "relative"), ("axis_order", "up-first"),
                           ("unit_to_metres", "metre"), ("unit_to_metres", 0)):
            document = manifest()
            document["file_entries"]["Site/Context.usdz"]["frame"][key] = value
            with self.subTest(key=key, value=value):
                plan = plan_import(document)
                self.assertIsNone(plan.georeference)
                self.assertTrue(any("Site/Context.usdz" in note for note in plan.notes))

    def test_axes_other_than_east_north_up_are_not_turned(self):
        document = manifest()
        document["file_entries"]["Site/Context.usdz"]["frame"]["axis_mapping"] = {
            "x": "east", "y": "up", "z": "south"}
        plan = plan_import(document)
        self.assertIsNone(plan.georeference)
        self.assertTrue(any("axis" in note for note in plan.notes))

    def test_a_context_file_with_no_stated_frame_is_not_placed(self):
        document = manifest()
        document["file_entries"].pop("Site/Context.usdz")
        plan = plan_import(document)
        self.assertEqual(plan.context_path, "Site/Context.usdz")
        self.assertIsNone(plan.georeference)
        self.assertTrue(any("frame" in note for note in plan.notes))

    def test_an_unknown_origin_unit_is_not_applied(self):
        plan = plan_import(manifest(location__site_origin__projected__unit="chain"))
        self.assertIsNone(plan.georeference)
        self.assertTrue(any("chain" in note for note in plan.notes))


class ScenePropertyTests(unittest.TestCase):
    def test_a_metre_origin_writes_the_gis_keys_and_the_full_origin(self):
        props = scene_properties(plan_import(manifest()).georeference)
        self.assertEqual(props["SRID"], "EPSG:32614")
        self.assertEqual(props["crs x"], 620050.0)
        self.assertEqual(props["crs y"], 3348864.5)
        self.assertEqual(props["latitude"], 30.2655)
        self.assertEqual(props["longitude"], -97.752)
        origin = props["mantleplace_site_origin"]
        self.assertEqual(origin["z"], 110.672)
        self.assertEqual(origin["z_reference"], "ellipsoidal")
        self.assertEqual(origin["unit"], "m")
        self.assertEqual(origin["vertical_datum"], "NAVD88 (GEOID18)")
        self.assertEqual(origin["datum_height_m"], 137.792)

    def test_a_foot_origin_keeps_the_record_and_drops_the_metre_scene_keys(self):
        # Blender's scene is in metres, so "crs x" in feet would place every later GIS import off
        # by a factor of 3.28: the keys are left out, and nothing is scaled to make them fit.
        props = scene_properties(plan_import(
            manifest(location__site_origin__projected__unit="usft")).georeference)
        self.assertNotIn("crs x", props)
        self.assertNotIn("SRID", props)
        self.assertEqual(props["mantleplace_site_origin"]["x"], 620050.0)
        self.assertEqual(props["mantleplace_site_origin"]["unit"], "usft")


class WrapperStageTests(unittest.TestCase):
    def test_features_composes_over_context_as_a_sublayer(self):
        text = wrapper_stage("C:/x/Site/Context.usdz", "C:/x/Site/Features.usdz")
        self.assertTrue(text.startswith("#usda 1.0"))
        self.assertIn('defaultPrim = "Site"', text)
        self.assertIn('upAxis = "Z"', text)
        self.assertIn("metersPerUnit = 1", text)
        self.assertLess(text.index("@C:/x/Site/Features.usdz@"), text.index("@C:/x/Site/Context.usdz@"))

    def test_context_alone(self):
        text = wrapper_stage("/b/Site/Context.usdz", None)
        self.assertIn("@/b/Site/Context.usdz@", text)
        self.assertNotIn("Features", text)

    def test_windows_separators_become_forward_slashes(self):
        text = wrapper_stage("C:\\b\\Site\\Context.usdz", None)
        self.assertIn("@C:/b/Site/Context.usdz@", text)

    def test_an_at_sign_in_a_path_uses_the_triple_delimiter(self):
        text = wrapper_stage("/b/me@site/Context.usdz", None)
        self.assertIn("@@@/b/me@site/Context.usdz@@@", text)


if __name__ == "__main__":
    unittest.main()
