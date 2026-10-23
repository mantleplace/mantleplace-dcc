# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""`validate()` end to end: a bundle on disk in, a report out."""

from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from helpers import (SYNTHETIC_SCHEMA, bundle_files, make_folder, make_zip, manifest, sha256,
                     write_schema_dir)
from mantleplace_mpb import SchemaSource, validate

RASTER_FRAME = {
    "horizontal_crs": {"code": "EPSG:6350"},
    "vertical_crs": {"code": "EPSG:5703"},
    "unit_to_metres": 1.0,
    "geoid_model": "GEOID18",
    "axis_order": "east-north",
    "axis_mapping": {"x": "east", "y": "north", "z": "up"},
    "coordinate_mode": "absolute",
    "pixel_convention": "pixel-is-area",
    "row_order": "north-up",
}
VECTOR_FRAME = {k: v for k, v in RASTER_FRAME.items() if k not in ("pixel_convention", "row_order")}
MESH_FRAME = {
    "horizontal_crs": {"code": "EPSG:6350"},
    "unit_to_metres": 1.0,
    "axis_order": "east-north",
    "axis_mapping": {"x": "east", "y": "up", "z": "south"},
    "coordinate_mode": "offset",
}
SITE_ORIGIN = {
    "latitude": 39.7392,
    "longitude": -104.9903,
    "ellipsoidal_height_m": 1581.2,
    "datum_height_m": 1609.6,
    "vertical_datum": "NAVD88 (GEOID18)",
}
SOURCE_PD = {"name": "Synthetic public record", "license": "LicenseRef-Synthetic-PublicDomain"}
SOURCE_ODBL = {"name": "Synthetic open map", "license": "ODbL-1.0"}
LAYOUT = {"readme": "README.md", "manifest": "Metadata/manifest.json"}


class Case(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.schemas = SchemaSource(schema_dir=write_schema_dir(self.tmp / "schemas"))
        self.count = 0

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def run_folder(self, files: dict[str, bytes], strict: bool = False, schemas=None):
        self.count += 1
        root = make_folder(self.tmp / f"bundle{self.count}", files)
        return validate(root, schemas=schemas or self.schemas, strict=strict)

    def codes(self, report, severity: str = "error") -> set[str]:
        return {f.code for f in report.findings if f.severity == severity}

    def assertPasses(self, report) -> None:
        self.assertEqual("pass", report.verdict, [f.to_dict() for f in report.findings])


class TheInputForms(Case):
    def test_a_folder_bundle_passes(self) -> None:
        report = self.run_folder(bundle_files())
        self.assertPasses(report)
        self.assertEqual("folder", report.form)
        self.assertEqual("Metadata/manifest.json", report.manifest_path)

    def test_a_zip_bundle_passes(self) -> None:
        report = validate(make_zip(self.tmp / "b.zip", bundle_files()), schemas=self.schemas)
        self.assertPasses(report)
        self.assertEqual("zip", report.form)
        self.assertEqual("", report.root)

    def test_a_wrapper_folder_inside_a_zip_is_unwrapped(self) -> None:
        path = make_zip(self.tmp / "b.zip", bundle_files(), prefix="Site Bundle/")
        report = validate(path, schemas=self.schemas)
        self.assertPasses(report)
        self.assertEqual("Site Bundle/", report.root)

    def test_a_wrapper_folder_on_disk_is_unwrapped(self) -> None:
        root = make_folder(self.tmp / "outer" / "inner", bundle_files()).parent
        report = validate(root, schemas=self.schemas)
        self.assertPasses(report)
        self.assertEqual("inner/", report.root)

    def test_macos_archive_debris_is_ignored(self) -> None:
        files = bundle_files()
        files["__MACOSX/Metadata/._manifest.json"] = b"\x00\x05\x16\x07"
        files[".DS_Store"] = b"\x00\x00\x00\x01Bud1"
        files["Metadata/.DS_Store"] = b"\x00\x00\x00\x01Bud1"
        for form in ("zip", "wrapped"):
            with self.subTest(form=form):
                prefix = "Bundle/" if form == "wrapped" else ""
                wrapped = {prefix + k: v for k, v in files.items()}
                wrapped["__MACOSX/._Bundle"] = b"\x00"
                report = validate(make_zip(self.tmp / f"{form}.zip", wrapped), schemas=self.schemas)
                self.assertPasses(report)
                text = json.dumps(report.to_dict())
                self.assertNotIn("DS_Store", text)
                self.assertNotIn("MACOSX", text)

    def test_a_bundle_without_a_manifest_fails(self) -> None:
        report = self.run_folder({"README.md": b"x"})
        self.assertEqual("fail", report.verdict)
        self.assertIn("archive.no-manifest", self.codes(report))

    def test_a_zip_entry_that_escapes_the_root_fails(self) -> None:
        files = bundle_files()
        files["../outside.txt"] = b"x"
        report = validate(make_zip(self.tmp / "b.zip", files), schemas=self.schemas)
        self.assertIn("archive.unsafe-entry", self.codes(report))

    def test_a_manifest_that_is_not_json_fails(self) -> None:
        files = bundle_files()
        files["Metadata/manifest.json"] = b"{not json"
        report = self.run_folder(files)
        self.assertIn("manifest.not-json", self.codes(report))

    def test_an_input_that_is_neither_a_zip_nor_a_folder_cannot_be_checked(self) -> None:
        (self.tmp / "note.txt").write_text("hello", encoding="utf-8")
        report = validate(self.tmp / "note.txt", schemas=self.schemas)
        self.assertEqual("error", report.verdict)
        self.assertEqual(2, report.exit_code)


class TheDeclaredSchema(Case):
    def setUp(self) -> None:
        super().setUp()
        older = {**SYNTHETIC_SCHEMA, "required": ["version", "layout", "only_in_older"],
                 "properties": {"version": {"const": "1.13.0"}}}
        integer = {**SYNTHETIC_SCHEMA, "properties": {"version": {"const": 19}}}
        self.schemas = SchemaSource(schema_dir=write_schema_dir(
            self.tmp / "series", {"1.13.0": older, "1.14.0": SYNTHETIC_SCHEMA, "v19": integer}))

    def test_the_manifest_is_held_to_the_version_it_declares(self) -> None:
        self.assertPasses(self.run_folder(bundle_files(manifest(version="1.14.0"))))
        report = self.run_folder(bundle_files(manifest(version="1.13.0")))
        self.assertIn("schema.invalid", self.codes(report))
        self.assertEqual("1.13.0", report.manifest_version)
        self.assertTrue(report.schema_url.endswith("/1.13.0.json"))

    def test_a_schema_error_names_where_it_is(self) -> None:
        report = self.run_folder(bundle_files(manifest(layout={"readme": "README.md"})))
        pointers = {f.pointer for f in report.findings if f.code == "schema.invalid"}
        self.assertIn("/layout", pointers)

    def test_an_integer_version_reads_the_integer_era_schema(self) -> None:
        report = self.run_folder(bundle_files(manifest(version=19)))
        self.assertPasses(report)
        self.assertTrue(report.schema_url.endswith("/v19.json"))

    def test_an_unpublished_version_fails(self) -> None:
        report = self.run_folder(bundle_files(manifest(version="1.99.0")))
        self.assertIn("schema.version-unpublished", self.codes(report))
        self.assertEqual(1, report.exit_code)

    def test_a_missing_or_malformed_version_fails(self) -> None:
        document = manifest()
        del document["version"]
        self.assertIn("schema.version-missing", self.codes(self.run_folder(bundle_files(document))))
        for bad in ("1.14", "v1.14.0", "1.14.0-rc.1", "19", True):
            with self.subTest(version=bad):
                report = self.run_folder(bundle_files(manifest(version=bad)))
                self.assertIn("schema.version-invalid", self.codes(report))

    def test_a_schema_that_disagrees_with_the_ledger_cannot_be_checked(self) -> None:
        schemas = SchemaSource(schema_dir=write_schema_dir(
            self.tmp / "tampered", {"1.14.0": SYNTHETIC_SCHEMA}, ledger={"1.14.0": "0" * 64}))
        report = self.run_folder(bundle_files(), schemas=schemas)
        self.assertEqual("error", report.verdict)
        self.assertIn("schema.ledger-mismatch", self.codes(report))
        self.assertEqual(2, report.exit_code)

    def test_a_schema_keyword_the_validator_cannot_evaluate_cannot_be_checked(self) -> None:
        unknown = {**SYNTHETIC_SCHEMA, "unevaluatedProperties": False}
        schemas = SchemaSource(schema_dir=write_schema_dir(self.tmp / "unknown", {"1.14.0": unknown}))
        report = self.run_folder(bundle_files(), schemas=schemas)
        self.assertEqual("error", report.verdict)
        self.assertIn("schema.unsupported", self.codes(report))


class ThePointerRule(Case):
    def test_a_layout_pointer_to_a_missing_file_fails(self) -> None:
        document = manifest(layout={**LAYOUT, "dem": "Elevation/DEM.tif"})
        report = self.run_folder(bundle_files(document))
        missing = [f for f in report.findings if f.code == "pointer.missing"]
        self.assertEqual(["/layout/dem"], [f.pointer for f in missing])
        self.assertEqual("Elevation/DEM.tif", missing[0].file)

    def test_a_null_layout_pointer_is_an_absent_layer(self) -> None:
        document = manifest(layout={**LAYOUT, "imagery": None})
        self.assertPasses(self.run_folder(bundle_files(document)))

    def test_a_per_artifact_path_must_resolve(self) -> None:
        document = manifest(elevation={"steep_slope": {"path": "Elevation/SteepSlopes.gpkg"}})
        report = self.run_folder(bundle_files(document))
        self.assertEqual(["/elevation/steep_slope/path"],
                         [f.pointer for f in report.findings if f.code == "pointer.missing"])
        self.assertPasses(self.run_folder(bundle_files(document) | {"Elevation/SteepSlopes.gpkg": b"x"}))

    def test_a_pointer_that_escapes_the_bundle_fails(self) -> None:
        for bad in ("../outside.tif", "/abs/DEM.tif", "Elevation\\DEM.tif", "C:/DEM.tif",
                    "Elevation/./DEM.tif", ""):
            with self.subTest(pointer=bad):
                document = manifest(layout={**LAYOUT, "dem": bad})
                self.assertIn("pointer.unsafe", self.codes(self.run_folder(bundle_files(document))))

    def test_a_pointer_may_name_a_folder(self) -> None:
        document = manifest(layout={**LAYOUT, "cesium_terrain": "Elevation/Terrain/"})
        files = bundle_files(document) | {"Elevation/Terrain/layer.json": b"{}"}
        self.assertPasses(self.run_folder(files))

    def test_the_mesh_format_list_names_formats_not_files(self) -> None:
        document = manifest(layout={**LAYOUT, "mesh_formats": ["glb", "obj", "usdz"]})
        self.assertPasses(self.run_folder(bundle_files(document)))

    def test_the_paths_a_completeness_check_looked_for_are_not_pointers(self) -> None:
        document = manifest(completeness={"delivered_missing": [{"token": "x", "path": "Gone.tif"}]})
        self.assertPasses(self.run_folder(bundle_files(document)))

    def test_every_file_entry_names_a_file_in_the_bundle(self) -> None:
        document = manifest(file_entries={"Elevation/DEM.tif": {"odbl_role": "none"}})
        report = self.run_folder(bundle_files(document))
        self.assertEqual(["/file_entries/Elevation~1DEM.tif"],
                         [f.pointer for f in report.findings if f.code == "pointer.missing"])

    def test_the_manifest_pointer_names_the_manifest(self) -> None:
        document = manifest(layout={"readme": "README.md", "manifest": "README.md"})
        self.assertIn("pointer.manifest-self", self.codes(self.run_folder(bundle_files(document))))

    def test_a_file_another_view_holds_is_not_missing(self) -> None:
        document = manifest(
            layout={**LAYOUT, "dem": "Elevation/DEM.tif"},
            files={"Elevation/DEM.tif": {"part": "elevation", "size_bytes": 1, "sha256": "a" * 64}},
            view={"kind": "standard", "parts": ["core"]},
        )
        report = self.run_folder(bundle_files(document))
        self.assertPasses(report)
        self.assertIn("pointer.outside-view", self.codes(report, "info"))


class TheHashes(Case):
    def test_a_matching_hash_is_verified(self) -> None:
        data = b"synthetic slope layer\n"
        document = manifest(elevation={"steep_slope": {"path": "Elevation/SteepSlopes.gpkg",
                                                       "sha256": sha256(data)}})
        report = self.run_folder(bundle_files(document) | {"Elevation/SteepSlopes.gpkg": data})
        self.assertPasses(report)
        self.assertEqual({"verified": 1, "mismatched": 0, "unknown": 0}, report.hashes)

    def test_a_mismatched_hash_fails_and_names_the_file(self) -> None:
        document = manifest(elevation={"steep_slope": {"path": "Elevation/SteepSlopes.gpkg",
                                                       "sha256": sha256(b"other bytes")}})
        report = self.run_folder(bundle_files(document) | {"Elevation/SteepSlopes.gpkg": b"x"})
        mismatch = [f for f in report.findings if f.code == "sha256.mismatch"]
        self.assertEqual(["Elevation/SteepSlopes.gpkg"], [f.file for f in mismatch])
        self.assertEqual(1, report.hashes["mismatched"])

    def test_an_absent_hash_is_unknown_not_corrupt(self) -> None:
        document = manifest(elevation={"steep_slope": {"path": "Elevation/SteepSlopes.gpkg",
                                                       "sha256": None}})
        report = self.run_folder(bundle_files(document) | {"Elevation/SteepSlopes.gpkg": b"x"})
        self.assertPasses(report)
        self.assertEqual({"verified": 0, "mismatched": 0, "unknown": 1}, report.hashes)

    def test_a_pointer_without_a_hash_takes_one_from_the_format_tables(self) -> None:
        data = b"synthetic buildings\n"
        document = manifest(
            layout={**LAYOUT, "buildings": "Features/Buildings.glb"},
            buildings={"formats": [{"format": "glb", "path": "Features/Buildings.glb",
                                    "sha256": sha256(data)}]},
        )
        report = self.run_folder(bundle_files(document) | {"Features/Buildings.glb": data})
        self.assertPasses(report)
        self.assertEqual({"verified": 1, "mismatched": 0, "unknown": 0}, report.hashes)

    def test_two_hashes_for_one_file_fail(self) -> None:
        data = b"x"
        document = manifest(
            elevation={"steep_slope": {"path": "Elevation/SteepSlopes.gpkg", "sha256": sha256(data)}},
            hosts={"revit": {"vectors": {"gpkg": {"path": "Elevation/SteepSlopes.gpkg",
                                                  "sha256": "b" * 64}}}},
        )
        report = self.run_folder(bundle_files(document) | {"Elevation/SteepSlopes.gpkg": data})
        self.assertIn("sha256.conflict", self.codes(report))


class TheFrames(Case):
    def entries(self, entries: dict, **extra) -> dict[str, bytes]:
        files = {name: b"synthetic\n" for name in entries}
        return bundle_files(manifest(file_entries=entries, **extra)) | files

    def test_a_whole_raster_frame_passes(self) -> None:
        entry = {"frame": RASTER_FRAME, "odbl_role": "none", "license": "CC-BY-4.0", "sources": []}
        self.assertPasses(self.run_folder(self.entries({"Elevation/DEM.tif": entry})))

    def test_a_frame_is_written_whole_or_not_at_all(self) -> None:
        frame = {k: v for k, v in RASTER_FRAME.items() if k != "axis_mapping"}
        report = self.run_folder(self.entries({"Elevation/DEM.tif": {"frame": frame, "odbl_role": "none"}}))
        partial = [f for f in report.findings if f.code == "frame.partial"]
        self.assertEqual(["/file_entries/Elevation~1DEM.tif/frame"], [f.pointer for f in partial])
        self.assertIn("axis_mapping", partial[0].message)

    def test_a_raster_frame_states_its_pixel_convention_and_row_order(self) -> None:
        frame = {k: v for k, v in RASTER_FRAME.items() if k != "row_order"}
        report = self.run_folder(self.entries({"Imagery/Imagery.png": {"frame": frame, "odbl_role": "none"}}))
        self.assertIn("frame.partial", self.codes(report))

    def test_raster_fields_on_a_mesh_are_incoherent(self) -> None:
        frame = {**MESH_FRAME, "row_order": "north-up"}
        report = self.run_folder(self.entries({"Site/Terrain.glb": {"frame": frame, "odbl_role": "none"}},
                                              location={"site_origin": SITE_ORIGIN}))
        self.assertIn("frame.raster-only-field", self.codes(report))

    def test_an_offset_file_needs_the_site_origin(self) -> None:
        entries = {"Site/Terrain.glb": {"frame": MESH_FRAME, "odbl_role": "none"}}
        self.assertIn("frame.offset-without-site-origin",
                      self.codes(self.run_folder(self.entries(entries))))
        self.assertPasses(self.run_folder(self.entries(entries, location={"site_origin": SITE_ORIGIN})))

    def test_a_geoid_or_vertical_unit_without_a_vertical_crs_is_incoherent(self) -> None:
        base = {k: v for k, v in RASTER_FRAME.items() if k not in ("vertical_crs", "geoid_model")}
        for extra, code in (({"geoid_model": "GEOID18"}, "frame.geoid-without-vertical-crs"),
                            ({"vertical_unit_to_metres": 0.3048},
                             "frame.vertical-unit-without-vertical-crs")):
            with self.subTest(code=code):
                entry = {"frame": {**base, **extra}, "odbl_role": "none"}
                self.assertIn(code, self.codes(self.run_folder(self.entries({"Elevation/DEM.tif": entry}))))

    def test_two_axes_along_one_line_are_incoherent(self) -> None:
        frame = {**RASTER_FRAME, "axis_mapping": {"x": "east", "y": "west", "z": "up"}}
        report = self.run_folder(self.entries({"Elevation/DEM.tif": {"frame": frame, "odbl_role": "none"}}))
        self.assertIn("frame.axis-mapping-degenerate", self.codes(report))

    def test_a_geospatial_file_entry_without_a_frame_is_a_warning(self) -> None:
        report = self.run_folder(self.entries({"Elevation/DEM.tif": {"odbl_role": "none"}}))
        self.assertPasses(report)
        self.assertIn("frame.absent", self.codes(report, "warning"))


class TheRoles(Case):
    def entries(self, entries: dict) -> dict[str, bytes]:
        files = {name: b"synthetic\n" for name in entries}
        return bundle_files(manifest(file_entries=entries)) | files

    def test_a_vector_data_file_is_never_a_produced_work(self) -> None:
        for name in ("Features/Roads.gpkg", "Features/Roads.shp", "Features/Roads.geojson",
                     "Drawings/Linework.dxf", "LandCover/TreePoints.csv"):
            with self.subTest(file=name):
                entry = {"frame": VECTOR_FRAME, "odbl_role": "produced-work", "license": "CC-BY-4.0"}
                report = self.run_folder(self.entries({name: entry}))
                mismatch = [f for f in report.findings if f.code == "role.extension-mismatch"]
                self.assertEqual([name], [f.file for f in mismatch])

    def test_a_raster_may_be_a_produced_work(self) -> None:
        entries = {"Features/StylizedMap.tif": {"frame": RASTER_FRAME, "odbl_role": "produced-work",
                                                "license": "CC-BY-4.0", "sources": [SOURCE_ODBL]}}
        self.assertPasses(self.run_folder(self.entries(entries)))

    def test_a_missing_role_reads_as_database_and_is_reported(self) -> None:
        report = self.run_folder(self.entries({"Elevation/DEM.tif": {"frame": RASTER_FRAME}}))
        self.assertPasses(report)
        self.assertIn("role.absent", self.codes(report, "warning"))

    def test_a_file_with_no_odbl_role_cannot_list_an_odbl_source(self) -> None:
        entry = {"frame": RASTER_FRAME, "odbl_role": "none", "sources": [SOURCE_PD, SOURCE_ODBL]}
        report = self.run_folder(self.entries({"Elevation/DEM.tif": entry}))
        self.assertIn("role.sources-mismatch", self.codes(report))

    def test_a_file_the_entries_do_not_state_is_reported(self) -> None:
        files = self.entries({"Elevation/DEM.tif": {"frame": RASTER_FRAME, "odbl_role": "none"}})
        files["Elevation/Hillshade.tif"] = b"synthetic\n"
        report = self.run_folder(files)
        unstated = [f for f in report.findings if f.code == "role.unstated"]
        self.assertEqual(["Elevation/Hillshade.tif"], [f.file for f in unstated])

    def test_strict_makes_a_warning_fail(self) -> None:
        files = self.entries({"Elevation/DEM.tif": {"frame": RASTER_FRAME}})
        self.assertEqual("pass", self.run_folder(files).verdict)
        strict = self.run_folder(files, strict=True)
        self.assertEqual("fail", strict.verdict)
        self.assertEqual(1, strict.exit_code)


if __name__ == "__main__":
    unittest.main()
