"""Tests for the reader leg of the conformance gate (`reader_cases.py`).

The `reader` corpus group holds, for each synthetic bundle, what a correct reader finds in it, as
language-neutral data. The gate holds every case file to the documented shape and to the bundle it
answers for. These tests prove the shipped cases pass, that the cases the ticket names are all there,
and that a case fails for each way it can be wrong.

Run: `python -m unittest discover -s tools/manifest-conformance`
"""

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
    """(index entry, case file, bundle description), all copies a test may break."""
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
        for wanted in ("reader.v3FullMax", "reader.v3FullMed", "reader.v3FullMin",  # a level each
                       "reader.v3RecordsNotHeld", "reader.v3MedWithoutRecords",  # optional subjects
                       "reader.v2Full",  # the manifest at the 2.x location
                       "reader.majorAhead",  # a major above 3, refused
                       "reader.v3CatalogFileMissing"):  # a catalog name with no file
            self.assertIn(wanted, ids)

    def test_the_three_levels_are_each_stated_by_a_full_bundle(self) -> None:
        levels = {_case(f"reader.v3Full{name}")[1]["result"]["level"] for name in ("Max", "Med", "Min")}
        self.assertEqual(levels, {"Max", "Med", "Min"})

    def test_every_accepted_3_x_bundle_of_the_corpus_has_a_reader_case(self) -> None:
        """Each `bundle` case that conforms and declares 3.x is the answer of some reader case."""
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
        self.assertEqual(high["Elevation/Terrain.dxf"], low["Elevation/Terrain.dxf"])  # georeferenced
        self.assertIsNone(high["Elevation/Terrain.mtl"])
        self.assertIsNone(high["Readme.md"])


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
        """A reader reports what the catalog states and never counts a file for itself."""
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
