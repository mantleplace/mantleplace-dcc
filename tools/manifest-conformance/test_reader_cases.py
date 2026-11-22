from __future__ import annotations

import copy
import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import check_manifest_conformance as gate  # noqa: E402
import reader_cases as reader  # noqa: E402

_CORPUS = Path(__file__).resolve().parent / "corpus"


def _index() -> list[dict]:
    return json.loads((_CORPUS / "index.json").read_text(encoding="utf-8"))["cases"]


def _load(relative: str) -> dict:
    return json.loads((_CORPUS / relative).read_text(encoding="utf-8"))


def _case(case_id: str) -> tuple[dict, dict, dict]:
    entry = next(c for c in _index() if c["id"] == case_id)
    body = _load(entry["file"])
    return copy.deepcopy(entry), copy.deepcopy(body), copy.deepcopy(_load(body["bundle"]))


def _problems(case_id: str, mutate=None, mutate_bundle=None) -> list[str]:
    entry, body, fixture = _case(case_id)
    if mutate:
        mutate(entry, body["result"])
    if mutate_bundle:
        mutate_bundle(fixture["files"])
    return reader.check_case(entry, body, fixture)


class ShippedCasesTest(unittest.TestCase):
    def test_every_shipped_case_holds(self) -> None:
        self.assertEqual(reader.check_reader_cases(_index(), _CORPUS), [])

    def test_the_gate_runs_them(self) -> None:
        self.assertIn(reader.READER_GROUP, gate._CORPUS_GROUPS)
        self.assertEqual(gate.check_corpus(), [])

    def test_the_situations_the_ticket_names_each_have_a_case(self) -> None:
        ids = {c["id"] for c in _index() if c["group"] == "reader"}
        for wanted in ("reader.v3FullMax", "reader.v3FullMed", "reader.v3FullMin",
                       "reader.v3RecordsNotHeld", "reader.v3MedWithoutRecords",
                       "reader.v2Full",
                       "reader.majorAhead",
                       "reader.v3CatalogFileMissing",
                       "reader.v3FramesFeet",
                       "reader.v3FramesUnitUnknown", "reader.v3FramesAxisOrderUnknown",
                       "reader.v3FramesLocalUnitNotMetre", "reader.v3FramesVerticalDatumUnreadable"):
            self.assertIn(wanted, ids)

    def test_the_three_levels_are_each_stated_by_a_full_bundle(self) -> None:
        levels = {_case(f"reader.v3Full{name}")[1]["result"]["level"] for name in ("Max", "Med", "Min")}
        self.assertEqual(levels, {"Max", "Med", "Min"})

    def test_every_accepted_3_x_bundle_of_the_corpus_has_a_reader_case(self) -> None:
        answered = {_load(c["file"])["bundle"] for c in _index() if c["group"] == "reader"}
        for c in _index():
            if c["group"] == "bundle" and c["expect"] == "accept":
                version = next((v.get("version") for k, v in _load(c["file"])["files"].items()
                                if k == "Manifest.json" and isinstance(v, dict)), None)
                if version is not None:
                    self.assertIn(c["file"], answered, f"{c['id']} has no reader case")

    def test_a_refusal_is_a_reject_case_and_a_read_an_accept_case(self) -> None:
        by_id = {c["id"]: c for c in _index()}
        self.assertEqual(by_id["reader.majorAhead"]["expect"], "reject")
        self.assertEqual(by_id["reader.v2Full"]["expect"], "accept")

    def test_a_catalog_name_with_no_file_is_skipped_and_the_rest_is_found(self) -> None:
        _, body, _ = _case("reader.v3CatalogFileMissing")
        self.assertEqual(body["result"]["skipped"],
                         [{"path": "Imagery/Imagery.png", "why": "file-missing", "reason": None}])
        self.assertEqual(body["result"]["outcome"], "read")
        self.assertTrue(body["result"]["files"])

    def test_a_not_held_reason_is_kept_as_written(self) -> None:
        _, body, _ = _case("reader.v3RecordsNotHeld")
        reasons = {row["reason"] for row in body["result"]["skipped"]}
        self.assertEqual(reasons, {"no_data", "pending", "withheld"})

    def test_a_count_is_the_one_the_catalog_states_at_each_level(self) -> None:
        def counts(case_id: str) -> dict[str, int | None]:
            return {r["path"]: r["count"] for r in _case(case_id)[1]["result"]["files"]}
        high, mid, low = counts("reader.v3FullMax"), counts("reader.v3FullMed"), counts("reader.v3FullMin")
        self.assertEqual(high["Elevation/Terrain.glb"], 4 * low["Elevation/Terrain.glb"])
        self.assertEqual(high["Elevation/Terrain.glb"], 2 * mid["Elevation/Terrain.glb"])
        self.assertEqual(high["Elevation/Terrain.dxf"], low["Elevation/Terrain.dxf"])
        self.assertIsNone(high["Elevation/Terrain.mtl"])
        self.assertIsNone(high["Readme.md"])


class FramesTest(unittest.TestCase):
    def assertFails(self, problems: list[str], text: str) -> None:
        self.assertTrue(any(text in p for p in problems), f"no failure mentions {text!r}: {problems}")

    def test_every_3_x_read_states_the_frames_its_manifest_states(self) -> None:
        for entry in (c for c in _index() if c["group"] == "reader"):
            result = _load(entry["file"])["result"]
            self.assertEqual("frames" in result, result["outcome"] == "read" and result["major"] >= 3,
                             entry["id"])

    def test_the_frames_are_the_manifests_with_the_geoid_always_stated(self) -> None:
        _, body, fixture = _case("reader.v3FullMax")
        stated = fixture["files"]["Manifest.json"]["frames"]
        frames = body["result"]["frames"]
        self.assertEqual(frames["georeferenced"]["horizontal_unit"], stated["georeferenced"]["horizontal_unit"])
        self.assertEqual(frames["local"]["origin"], stated["local"]["origin"])
        self.assertIn("geoid_model", frames["local"])

    def test_a_manifest_that_names_no_geoid_is_answered_with_null(self) -> None:
        _, body, _ = _case("reader.v3FramesFeet")
        self.assertIsNone(body["result"]["frames"]["georeferenced"]["geoid_model"])

    def test_frames_that_are_not_the_manifests_are_refused(self) -> None:
        def change(_e, r):
            r["frames"]["georeferenced"]["horizontal_unit"]["to_metres"] = 0.3048
        self.assertFails(_problems("reader.v3FullMax", change), "frames")

    def test_a_read_without_frames_is_refused(self) -> None:
        self.assertFails(_problems("reader.v3FullMax", lambda e, r: r.pop("frames")), "frames")

    def test_frames_on_a_2_x_bundle_are_refused(self) -> None:
        entry, body, fixture = _case("reader.v2Full")
        result = body["result"]
        body["result"] = {**{k: result[k] for k in ("outcome", "major", "version", "manifestPath", "level", "refusal")},
                          "frames": {"georeferenced": {}, "local": {}},
                          "files": result["files"], "skipped": result["skipped"]}
        self.assertFails(reader.check_case(entry, body, fixture), "frames")

    def test_a_read_of_a_manifest_whose_frames_cannot_be_read_is_refused(self) -> None:
        def spoil(files):
            files["Manifest.json"]["frames"]["georeferenced"]["axis_order"] = "south-west"
        entry, body, fixture = _case("reader.v3FullMax")
        spoil(fixture["files"])
        body["result"]["frames"]["georeferenced"]["axis_order"] = "south-west"
        self.assertFails(reader.check_case(entry, body, fixture), "south-west")

    def test_a_frames_refusal_names_a_value_the_manifest_states(self) -> None:
        entry, _, _ = _case("reader.v3FramesUnitUnknown")
        _, body, fixture = _case("reader.v3FramesUnitUnknown")
        self.assertEqual(body["result"]["refusal"], "unreadable")
        self.assertEqual(body["result"]["outcome"], "refused")
        self.assertEqual(entry["expect"], "reject")
        self.assertIn(entry["errorContains"], json.dumps(fixture["files"]["Manifest.json"]["frames"]))

    def test_a_frames_refusal_of_frames_that_can_be_read_is_refused(self) -> None:
        def mend(files):
            files["Manifest.json"]["frames"]["georeferenced"]["horizontal_unit"] = {"name": "metre", "to_metres": 1}
        self.assertFails(_problems("reader.v3FramesUnitUnknown", mutate_bundle=mend), "frames")

    def test_a_frames_refusal_that_names_nothing_the_manifest_states_is_refused(self) -> None:
        self.assertFails(_problems("reader.v3FramesUnitUnknown", lambda e, r: e.update(errorContains="parsec")),
                         "errorContains")

    def test_each_refusal_unit_and_datum_the_reader_does_not_understand_is_one(self) -> None:
        for unit in ({"name": "metre"}, {"name": "x", "to_metres": 0}, {"name": "cm", "to_metres": 0.01},
                     {"name": "", "to_metres": 1}, {"name": "m", "to_metres": "1"}):
            frames = copy.deepcopy(_load("bundle/v3-med-without-records.json")["files"]["Manifest.json"]["frames"])
            frames["georeferenced"]["vertical_unit"] = unit
            self.assertIsNotNone(reader.frames_refusal(frames), unit)
        for crs in ({}, {"code": "no"}, {"projjson": "x"}, {"wkt2": ""}):
            frames = copy.deepcopy(_load("bundle/v3-med-without-records.json")["files"]["Manifest.json"]["frames"])
            frames["local"]["vertical_crs"] = crs
            self.assertIsNotNone(reader.frames_refusal(frames), crs)

    def test_the_corpus_frames_are_readable_except_in_the_refusal_cases(self) -> None:
        refused = {"reader.v3FramesUnitUnknown", "reader.v3FramesAxisOrderUnknown",
                   "reader.v3FramesLocalUnitNotMetre", "reader.v3FramesVerticalDatumUnreadable"}
        for entry in (c for c in _index() if c["group"] == "reader"):
            manifest = _case(entry["id"])[2]["files"].get("Manifest.json")
            if isinstance(manifest, dict) and "frames" in manifest:
                self.assertEqual(reader.frames_refusal(manifest["frames"]) is not None,
                                 entry["id"] in refused, entry["id"])


class ShapeTest(unittest.TestCase):
    def assertFails(self, problems: list[str], text: str) -> None:
        self.assertTrue(any(text in p for p in problems), f"no failure mentions {text!r}: {problems}")

    def test_an_unknown_member_of_the_result_is_refused(self) -> None:
        self.assertFails(_problems("reader.v3FullMax", lambda e, r: r.update(extra=1)), "exactly")

    def test_a_missing_member_of_the_result_is_refused(self) -> None:
        self.assertFails(_problems("reader.v3FullMax", lambda e, r: r.pop("skipped")), "exactly")

    def test_a_case_file_with_a_stray_member_is_refused(self) -> None:
        entry, body, fixture = _case("reader.v3FullMax")
        body["note"] = "x"
        self.assertFails(reader.check_case(entry, body, fixture), "exactly `bundle` and `result`")

    def test_a_level_outside_the_three_is_refused(self) -> None:
        self.assertFails(_problems("reader.v3FullMax", lambda e, r: r.update(level="Raw")), "level")

    def test_a_major_that_is_not_the_versions_is_refused(self) -> None:
        self.assertFails(_problems("reader.v3FullMax", lambda e, r: r.update(major=2)), "major")

    def test_a_manifest_path_in_the_wrong_place_is_refused(self) -> None:
        self.assertFails(_problems("reader.v3FullMax",
                                   lambda e, r: r.update(manifestPath="Metadata/manifest.json")),
                         "manifestPath")

    def test_a_family_and_frame_that_do_not_pair_are_refused(self) -> None:
        def swap(_e, r):
            r["files"][0]["frame"] = "order" if r["files"][0]["frame"] == "site" else "site"
        self.assertFails(_problems("reader.v3FullMax", swap), "frame")

    def test_an_unknown_family_is_refused(self) -> None:
        self.assertFails(_problems("reader.v3FullMax",
                                   lambda e, r: r["files"][0].update(family="engine")), "family")

    def test_a_negative_or_fractional_count_is_refused(self) -> None:
        for bad in (-1, 1.5, "4", True):
            self.assertFails(_problems("reader.v3FullMax",
                                       lambda e, r, bad=bad: r["files"][0].update(count=bad)), "count")

    def test_an_unsorted_list_is_refused(self) -> None:
        self.assertFails(_problems("reader.v3FullMax", lambda e, r: r["files"].reverse()), "sorted")

    def test_a_path_twice_is_refused(self) -> None:
        self.assertFails(_problems("reader.v3FullMax",
                                   lambda e, r: r["files"].insert(0, dict(r["files"][0]))), "sorted")

    def test_an_escaping_path_is_refused(self) -> None:
        self.assertFails(_problems("reader.v3FullMax",
                                   lambda e, r: r["files"][0].update(path="../Basemap.png")),
                         "plain relative path")

    def test_a_skip_for_a_reason_outside_the_vocabulary_is_refused(self) -> None:
        self.assertFails(_problems("reader.v3RecordsNotHeld",
                                   lambda e, r: r["skipped"][0].update(why="unreadable")), "why")

    def test_a_not_held_skip_without_its_reason_is_refused(self) -> None:
        self.assertFails(_problems("reader.v3RecordsNotHeld",
                                   lambda e, r: r["skipped"][0].update(reason=None)), "reason")

    def test_a_file_both_found_and_skipped_is_refused(self) -> None:
        def both(_e, r):
            r["skipped"].append({"path": "Basemap/Basemap.png", "why": "file-missing", "reason": None})
            r["skipped"].sort(key=lambda row: row["path"])
        self.assertFails(_problems("reader.v3FullMax", both), "both finds and skips")

    def test_a_refusal_that_finds_a_file_is_refused(self) -> None:
        self.assertFails(_problems("reader.majorAhead", lambda e, r: r["files"].append(
            {"path": "Elevation/Elevation.tif", "family": "georeferenced", "frame": "order",
             "count": None})), "refusal")

    def test_a_refusal_of_a_major_the_corpus_reads_is_refused(self) -> None:
        def lower(_e, r):
            r.update(major=3, version="3.0.0")
        self.assertFails(_problems("reader.majorAhead", lower), "refusal")

    def test_a_refused_outcome_on_an_accept_case_is_refused(self) -> None:
        self.assertFails(_problems("reader.majorAhead", lambda e, r: e.update(expect="accept")), "expect")

    def test_a_read_outcome_on_a_reject_case_is_refused(self) -> None:
        self.assertFails(_problems("reader.v3FullMax", lambda e, r: e.update(expect="reject")), "expect")


class AnswerTest(unittest.TestCase):
    def assertFails(self, problems: list[str], text: str) -> None:
        self.assertTrue(any(text in p for p in problems), f"no failure mentions {text!r}: {problems}")

    def test_a_count_that_is_not_the_catalogs_is_refused(self) -> None:
        def change(_e, r):
            r["files"][2]["count"] = 7
        self.assertFails(_problems("reader.v3FullMax", change), "count")

    def test_a_count_the_catalog_does_not_state_is_refused(self) -> None:
        def invent(_e, r):
            next(row for row in r["files"] if row["count"] is None)["count"] = 3
        self.assertFails(_problems("reader.v3MedWithoutRecords", invent), "count")

    def test_a_family_the_extension_does_not_state_is_refused(self) -> None:
        def flip(_e, r):
            row = next(x for x in r["files"] if x["path"].endswith(".tif"))
            row.update(family="host-ready", frame="site")
        self.assertFails(_problems("reader.v3FullMax", flip), "extension")

    def test_a_level_other_than_the_manifests_is_refused(self) -> None:
        self.assertFails(_problems("reader.v3FullMed", lambda e, r: r.update(level="Max")), "level")

    def test_a_version_other_than_the_manifests_is_refused(self) -> None:
        self.assertFails(_problems("reader.v3FullMax", lambda e, r: r.update(version="3.1.0")), "declares")

    def test_a_file_the_bundle_lacks_cannot_be_found(self) -> None:
        def drop(files):
            del files["Elevation/Terrain.glb"]
        self.assertFails(_problems("reader.v3FullMax", mutate_bundle=drop), "Terrain.glb")

    def test_a_file_the_catalog_lacks_cannot_be_found(self) -> None:
        def drop(files):
            del files["Manifest.json"]["catalog"]["Elevation/Terrain.glb"]
        self.assertFails(_problems("reader.v3FullMax", mutate_bundle=drop), "Terrain.glb")

    def test_a_not_held_file_the_manifest_does_not_list_is_refused(self) -> None:
        def forget(files):
            held_back = files["Manifest.json"]["not_held"]
            held_back[:] = [row for row in held_back if row["path"] != "Records/Parcels.gpkg"]
        self.assertFails(_problems("reader.v3RecordsNotHeld", mutate_bundle=forget), "skipped")

    def test_a_reason_other_than_the_manifests_is_refused(self) -> None:
        def reword(_e, r):
            r["skipped"][0]["reason"] = "No data."
        self.assertFails(_problems("reader.v3RecordsNotHeld", reword), "skipped")

    def test_a_2_x_family_is_the_files_own_frame_statement(self) -> None:
        def flip(_e, r):
            row = next(x for x in r["files"] if x["family"] == "host-ready")
            row.update(family="georeferenced", frame="order")
        self.assertFails(_problems("reader.v2Full", flip), "files")

    def test_a_2_x_bundle_states_no_level(self) -> None:
        self.assertFails(_problems("reader.v2Full", lambda e, r: r.update(level="Max")), "level")

    def test_a_refusal_names_the_manifests_version(self) -> None:
        self.assertFails(_problems("reader.majorAhead", lambda e, r: r.update(version="98.0.0",
                                                                                 major=98)), "declares")


class IndexTest(unittest.TestCase):
    def test_a_case_whose_bundle_is_not_a_bundle_case_is_refused(self) -> None:
        cases = [c for c in _index() if c["id"] != "bundle.v3Full"]
        failures = reader.check_reader_cases(cases, _CORPUS)
        self.assertTrue(any("not the case file of a `bundle` case" in f for f in failures), failures)

    def test_a_group_other_than_reader_is_not_checked_here(self) -> None:
        cases = [c for c in _index() if c["group"] != "reader"]
        self.assertEqual(reader.check_reader_cases(cases, _CORPUS), [])


if __name__ == "__main__":
    unittest.main()
