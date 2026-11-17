# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""A 3.x bundle (`spec/format.md`): the tree, the names, one level and the catalog.

Most runs read a schema directory that holds none for 3.0.0, so the manifest is held to the format
text's rules alone (`schema.pending`, at info) and each rule is seen on its own. The members are the
served 3.0.0 schema's: the catalog in `catalog`, the level in `level`.
"""

from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

from helpers import (SYNTHETIC_SCHEMA_2, SYNTHETIC_SCHEMA_3, bundle_files, make_folder, make_zip,
                     manifest, sha256, write_schema_dir)
from mantleplace_mpb import SchemaSource, validate

TERMS = {"odbl_role": "none", "license": "LicenseRef-MantlePlace-Terms", "sources": []}
ODBL = {"odbl_role": "database", "license": "ODbL-1.0",
        "sources": [{"name": "Synthetic open map", "license": "ODbL-1.0"}]}


def bundle3(extra: dict[str, bytes] | None = None, *, level: object = "Max",
            drop_entries: tuple[str, ...] = (), add_entries: tuple[str, ...] = (),
            **manifest_extra: object) -> dict[str, bytes]:
    """A 3.0 bundle: the root pair, three files of the tree, and a catalog that matches them."""
    files: dict[str, bytes] = {
        "Readme.md": b"A synthetic bundle.\n",
        "Elevation/Elevation.tif": b"elevation",
        "Elevation/Terrain.glb": b"terrain",
        "Basemap/Roads.gpkg": b"roads",
    }
    files.update(extra or {})
    entries: dict[str, dict] = {"Manifest.json": dict(TERMS)}
    for path, data in files.items():
        terms = ODBL if path.endswith(".gpkg") else TERMS
        entries[path] = {**terms, "sha256": sha256(data)}
        if path != "Readme.md":
            entries[path]["count"] = {"kind": "pixels", "value": 1}
    for path in add_entries:
        entries[path] = {**TERMS, "sha256": "0" * 64, "count": {"kind": "pixels", "value": 1}}
    for path in drop_entries:
        entries.pop(path, None)
    document: dict = {"version": "3.0.0", "catalog": entries, "not_held": []}
    if level is not None:
        document["level"] = level
    document.update(manifest_extra)
    files["Manifest.json"] = json.dumps(document, indent=2).encode("utf-8")
    return files


class Case(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.schemas = SchemaSource(schema_dir=write_schema_dir(
            self.tmp / "schemas", {"2.0.0": SYNTHETIC_SCHEMA_2}))
        self.count = 0

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def run_folder(self, files: dict[str, bytes], schemas=None):
        self.count += 1
        root = make_folder(self.tmp / f"bundle{self.count}", files)
        return validate(root, schemas=schemas or self.schemas)

    def codes(self, report, severity: str = "error") -> set[str]:
        return {f.code for f in report.findings if f.severity == severity}

    def assertRejects(self, report, *codes: str) -> None:
        self.assertEqual("fail", report.verdict, [f.to_dict() for f in report.findings])
        self.assertEqual(set(codes), self.codes(report), [f.to_dict() for f in report.findings])


class AValidBundle(Case):
    def test_passes_as_a_folder(self) -> None:
        report = self.run_folder(bundle3())
        self.assertEqual("pass", report.verdict, [f.to_dict() for f in report.findings])
        self.assertEqual("Manifest.json", report.manifest_path)
        self.assertEqual(set(), self.codes(report, "warning"))
        self.assertEqual(4, report.hashes["verified"])

    def test_passes_as_a_zip_and_inside_a_wrapper(self) -> None:
        for prefix in ("", "Bundle/"):
            report = validate(make_zip(self.tmp / f"b{len(prefix)}.zip", bundle3(), prefix),
                              schemas=self.schemas)
            self.assertEqual("pass", report.verdict, [f.to_dict() for f in report.findings])

    def test_the_unpublished_schema_is_named_at_info(self) -> None:
        report = self.run_folder(bundle3())
        self.assertIn("schema.pending", self.codes(report, "info"))

    def test_a_served_3x_schema_is_still_held_to(self) -> None:
        schema = {"$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object",
                  "required": ["bundle"]}
        schemas = SchemaSource(schema_dir=write_schema_dir(self.tmp / "s3", {"3.0.0": schema}))
        self.assertIn("schema.invalid", self.codes(self.run_folder(bundle3(), schemas)))

    def test_a_staged_3x_schema_is_unpublished_without_staged(self) -> None:
        schema = {"$schema": "https://json-schema.org/draft/2020-12/schema", "type": "object"}
        schemas = SchemaSource(schema_dir=write_schema_dir(
            self.tmp / "s3s", {"2.0.0": SYNTHETIC_SCHEMA_2, "3.0.0": schema}, staged=("3.0.0",)))
        self.assertIn("schema.version-unpublished", self.codes(self.run_folder(bundle3(), schemas)))

    def test_a_staged_3x_schema_is_held_to_with_staged(self) -> None:
        schemas = SchemaSource(schema_dir=write_schema_dir(
            self.tmp / "s3t", {"2.0.0": SYNTHETIC_SCHEMA_2, "3.0.0": SYNTHETIC_SCHEMA_3},
            staged=("3.0.0",)), staged=True)
        report = self.run_folder(bundle3(), schemas)
        self.assertEqual("pass", report.verdict, [f.to_dict() for f in report.findings])
        self.assertIn("schema.staged", self.codes(report, "info"))
        self.assertNotIn("schema.pending", self.codes(report, "info"))
        self.assertIn("schema.invalid", self.codes(self.run_folder(bundle3(level=None), schemas)))

    def test_a_later_major_with_no_schema_is_unpublished(self) -> None:
        files = bundle3()
        document = json.loads(files["Manifest.json"])
        document["version"] = "4.0.0"
        files["Manifest.json"] = json.dumps(document).encode("utf-8")
        self.assertIn("schema.version-unpublished", self.codes(self.run_folder(files)))

    def test_a_record_the_aoi_lacks_is_simply_absent(self) -> None:
        self.assertEqual("pass", self.run_folder(bundle3()).verdict)  # no Records pair at all

    def test_every_level_is_one_level(self) -> None:
        for level in ("Max", "Med", "Min"):
            self.assertEqual("pass", self.run_folder(bundle3(level=level)).verdict, level)

    def test_a_2x_bundle_still_passes(self) -> None:
        document = manifest(version="2.0.0")
        report = self.run_folder(bundle_files(document))
        self.assertEqual("pass", report.verdict, [f.to_dict() for f in report.findings])
        self.assertEqual("Metadata/manifest.json", report.manifest_path)


class TheTree(Case):
    def test_a_subfolder(self) -> None:
        self.assertRejects(self.run_folder(bundle3({"Elevation/Tiles/Terrain.glb": b"t"})),
                           "tree.subfolder")

    def test_a_file_removed_in_3_0(self) -> None:
        self.assertRejects(self.run_folder(bundle3({"Imagery/NDVI.tif": b"n"})), "tree.file-removed")

    def test_a_removed_format(self) -> None:
        self.assertRejects(self.run_folder(bundle3({"Basemap/Roads.shp": b"s"})), "tree.file-removed")

    def test_a_removed_2x_folder_names_its_files(self) -> None:
        report = self.run_folder(bundle3({"Features/Buildings.glb": b"b"}))
        self.assertRejects(report, "tree.file-removed")

    def test_a_third_root_file(self) -> None:
        self.assertRejects(self.run_folder(bundle3({"Notes.md": b"n"})), "tree.root-file")

    def test_a_file_the_3_0_table_does_not_list(self) -> None:
        self.assertRejects(self.run_folder(bundle3({"Records/Easements.gpkg": b"e"})),
                           "tree.file-unknown")

    def test_a_folder_outside_the_seven(self) -> None:
        self.assertRejects(self.run_folder(bundle3({"Climate/Rainfall.tif": b"r"})),
                           "tree.folder-unknown")

    def test_a_later_minor_may_add_a_file(self) -> None:
        report = self.run_folder(bundle3({"Records/Easements.gpkg": b"e"}, version="3.1.0"))
        self.assertEqual("pass", report.verdict, [f.to_dict() for f in report.findings])
        self.assertEqual({"tree.file-unknown"}, self.codes(report, "warning"))
        self.assertIn("schema.pending", self.codes(report, "info"))

    def test_a_later_minor_still_follows_the_names(self) -> None:
        self.assertRejects(self.run_folder(bundle3({"Records/Zoning-Old.gpkg": b"z"},
                                                   version="3.1.0")), "name.hyphen")


class TheNames(Case):
    def test_a_hyphen(self) -> None:
        self.assertRejects(self.run_folder(bundle3({"Basemap/Bus-Stops.gpkg": b"b"})),
                           "name.hyphen")

    def test_a_number(self) -> None:
        self.assertRejects(self.run_folder(bundle3({"Records/Zoning2.gpkg": b"z"})), "name.number")

    def test_a_suffix(self) -> None:
        for name in ("Elevation/TerrainMed.glb", "Elevation/Terrain_Med.glb",
                     "Elevation/Terrain.med.glb"):
            self.assertRejects(self.run_folder(bundle3({name: b"t"})), "name.suffix")

    def test_not_one_capitalised_word(self) -> None:
        for name in ("Elevation/DSM.tif", "Basemap/roadsurface.gpkg", "Basemap/RoadSurface.gpkg"):
            self.assertRejects(self.run_folder(bundle3({name: b"d"})), "name.word")

    def test_an_upper_case_extension(self) -> None:
        self.assertRejects(self.run_folder(bundle3({"Elevation/Relief.TIF": b"s"})),
                           "name.extension")

    def test_a_tree_name_in_the_wrong_case(self) -> None:
        self.assertRejects(self.run_folder(bundle3({"Elevation/terrain.obj": b"t"})),
                           "name.case-mismatch")

    def test_a_folder_name_breaks_the_rules_too(self) -> None:
        self.assertRejects(self.run_folder(bundle3({"Land-Use/Landuse.gpkg": b"l"})),
                           "name.hyphen")


class TheLevel(Case):
    def test_no_level(self) -> None:
        self.assertRejects(self.run_folder(bundle3(level=None)), "level.missing")

    def test_two_levels(self) -> None:
        self.assertRejects(self.run_folder(bundle3(level=["Max", "Med"])), "level.several")

    def test_an_unknown_level(self) -> None:
        for level in ("Raw", "max", 2, ["Max"]):
            self.assertRejects(self.run_folder(bundle3(level=level)), "level.unknown")


class TheCatalog(Case):
    def test_an_entry_with_no_file(self) -> None:
        self.assertRejects(self.run_folder(bundle3(add_entries=("Imagery/Imagery.png",))),
                           "catalog.file-missing")

    def test_a_file_with_no_entry(self) -> None:
        self.assertRejects(self.run_folder(bundle3(drop_entries=("Basemap/Roads.gpkg",))),
                           "catalog.entry-missing")

    def test_the_manifest_has_an_entry_too(self) -> None:
        self.assertRejects(self.run_folder(bundle3(drop_entries=("Manifest.json",))),
                           "catalog.entry-missing")

    def test_no_catalog(self) -> None:
        files = bundle3()
        document = json.loads(files["Manifest.json"])
        del document["catalog"]
        files["Manifest.json"] = json.dumps(document).encode("utf-8")
        self.assertRejects(self.run_folder(files), "catalog.absent")

    def test_the_2x_member_name_is_not_a_3x_catalog(self) -> None:
        """The catalog is the schema's `catalog`; a 3.x manifest that states `file_entries` has none."""
        files = bundle3()
        document = json.loads(files["Manifest.json"])
        document["file_entries"] = document.pop("catalog")
        files["Manifest.json"] = json.dumps(document).encode("utf-8")
        self.assertRejects(self.run_folder(files), "catalog.absent")

    def test_an_entry_matched_only_regardless_of_case(self) -> None:
        files = bundle3()
        document = json.loads(files["Manifest.json"])
        document["catalog"]["Elevation/terrain.glb"] = \
            document["catalog"].pop("Elevation/Terrain.glb")
        files["Manifest.json"] = json.dumps(document).encode("utf-8")
        self.assertRejects(self.run_folder(files), "name.case-mismatch")

    def test_an_entry_with_no_count(self) -> None:
        files = bundle3()
        document = json.loads(files["Manifest.json"])
        del document["catalog"]["Elevation/Terrain.glb"]["count"]
        files["Manifest.json"] = json.dumps(document).encode("utf-8")
        report = self.run_folder(files)
        self.assertRejects(report, "catalog.count-missing")
        self.assertEqual(["/catalog/Elevation~1Terrain.glb/count"],
                         [f.pointer for f in report.findings if f.code == "catalog.count-missing"])

    def test_a_count_that_is_not_an_object_is_no_count(self) -> None:
        files = bundle3()
        document = json.loads(files["Manifest.json"])
        document["catalog"]["Elevation/Terrain.glb"]["count"] = 3
        files["Manifest.json"] = json.dumps(document).encode("utf-8")
        self.assertRejects(self.run_folder(files), "catalog.count-missing")

    def test_a_row_that_gives_no_count_needs_none(self) -> None:
        """`Readme.md`, `Manifest.json` and `Terrain.mtl` say `None` in the file table."""
        files = bundle3({"Elevation/Terrain.mtl": b"material"})
        document = json.loads(files["Manifest.json"])
        document["catalog"]["Elevation/Terrain.mtl"] = {**TERMS, "sha256": sha256(b"material")}
        files["Manifest.json"] = json.dumps(document).encode("utf-8")
        self.assertEqual("pass", self.run_folder(files).verdict)

    def test_a_file_the_table_does_not_list_needs_no_count(self) -> None:
        files = bundle3({"Basemap/Zones.gpkg": b"zones"}, **{})
        document = json.loads(files["Manifest.json"])
        del document["catalog"]["Basemap/Zones.gpkg"]["count"]
        files["Manifest.json"] = json.dumps(document).encode("utf-8")
        self.assertNotIn("catalog.count-missing", self.codes(self.run_folder(files)))

    def test_a_hash_mismatch(self) -> None:
        files = bundle3()
        files["Elevation/Terrain.glb"] = b"changed"
        self.assertRejects(self.run_folder(files), "sha256.mismatch")

    def test_an_entry_with_no_hash(self) -> None:
        files = bundle3()
        document = json.loads(files["Manifest.json"])
        del document["catalog"]["Elevation/Terrain.glb"]["sha256"]
        files["Manifest.json"] = json.dumps(document).encode("utf-8")
        self.assertRejects(self.run_folder(files), "catalog.sha256-missing")

    def test_an_unsafe_entry(self) -> None:
        self.assertRejects(self.run_folder(bundle3(add_entries=("../Escape.tif",))),
                           "pointer.unsafe")

    def test_a_vector_layer_is_never_a_produced_work(self) -> None:
        files = bundle3()
        document = json.loads(files["Manifest.json"])
        document["catalog"]["Basemap/Roads.gpkg"]["odbl_role"] = "produced-work"
        files["Manifest.json"] = json.dumps(document).encode("utf-8")
        report = self.run_folder(files)
        self.assertRejects(report, "role.extension-mismatch")
        self.assertEqual(["/catalog/Basemap~1Roads.gpkg/odbl_role"],
                         [f.pointer for f in report.findings if f.code == "role.extension-mismatch"])


class NoPerHostBlock(Case):
    def test_a_hosts_member(self) -> None:
        report = self.run_folder(bundle3(hosts={"unreal": {"file_frame": {}}}))
        self.assertRejects(report, "manifest.host-block")
        self.assertEqual(["/hosts"], [f.pointer for f in report.findings
                                      if f.code == "manifest.host-block"])

    def test_a_member_named_for_a_host(self) -> None:
        for host in ("unreal", "revit", "autocad", "blender"):
            with self.subTest(host=host):
                self.assertRejects(self.run_folder(bundle3(**{host: {"origin": {}}})),
                                   "manifest.host-block")

    def test_a_credits_member_is_no_host_block(self) -> None:
        self.assertEqual("pass", self.run_folder(bundle3(credits=[])).verdict)


class TheManifestsPlace(Case):
    def test_a_3x_manifest_in_the_2x_place(self) -> None:
        files = bundle3()
        files["Metadata/manifest.json"] = files.pop("Manifest.json")
        report = self.run_folder(files)
        self.assertIn("manifest.place", self.codes(report))

    def test_a_2x_manifest_at_the_3x_root(self) -> None:
        files = bundle_files(manifest(version="2.0.0"))
        files["Manifest.json"] = files.pop("Metadata/manifest.json")
        self.assertIn("manifest.place", self.codes(self.run_folder(files)))

    def test_no_manifest_in_either_place(self) -> None:
        report = self.run_folder({"Readme.md": b"r"})
        self.assertIn("archive.no-manifest", self.codes(report))


if __name__ == "__main__":
    unittest.main()
