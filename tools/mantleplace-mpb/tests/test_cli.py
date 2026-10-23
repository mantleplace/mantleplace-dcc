# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""`mpb validate` and `mpb corpus` as a caller meets them: an exit code and a report."""

from __future__ import annotations

import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path

from helpers import bundle_files, make_folder, make_zip, manifest, write_schema_dir
from mantleplace_mpb.cli import main


def run(*argv: str) -> tuple[int, str]:
    out = io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(io.StringIO()):
        code = main(list(argv))
    return code, out.getvalue()


class TheValidateCommand(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.schemas = str(write_schema_dir(self.tmp / "schemas"))

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def test_a_conforming_bundle_exits_zero_with_a_json_report(self) -> None:
        bundle = make_zip(self.tmp / "b.zip", bundle_files())
        code, out = run("validate", str(bundle), "--schema-dir", self.schemas, "--json")
        self.assertEqual(0, code)
        report = json.loads(out)
        self.assertEqual("pass", report["verdict"])
        self.assertEqual("1.14.0", report["manifestVersion"])
        self.assertEqual({"error": 0, "warning": 0, "info": report["counts"]["info"]},
                         report["counts"])
        self.assertIsInstance(report["findings"], list)

    def test_a_nonconforming_bundle_exits_one_and_names_the_reason(self) -> None:
        document = manifest(layout={"readme": "README.md", "manifest": "Metadata/manifest.json",
                                    "dem": "Elevation/DEM.tif"})
        bundle = make_folder(self.tmp / "b", bundle_files(document))
        code, out = run("validate", str(bundle), "--schema-dir", self.schemas, "--json")
        self.assertEqual(1, code)
        report = json.loads(out)
        self.assertEqual("fail", report["verdict"])
        finding = next(f for f in report["findings"] if f["code"] == "pointer.missing")
        self.assertEqual({"check", "code", "severity", "message", "pointer", "file"}, set(finding))
        self.assertEqual("pointers", finding["check"])

    def test_the_text_report_states_the_verdict_and_each_reason(self) -> None:
        document = manifest(layout={"readme": "README.md", "manifest": "Metadata/manifest.json",
                                    "dem": "Elevation/DEM.tif"})
        bundle = make_folder(self.tmp / "b", bundle_files(document))
        code, out = run("validate", str(bundle), "--schema-dir", self.schemas)
        self.assertEqual(1, code)
        self.assertIn("FAIL", out)
        self.assertIn("pointer.missing", out)
        self.assertIn("Elevation/DEM.tif", out)

    def test_a_missing_input_cannot_be_checked(self) -> None:
        code, out = run("validate", str(self.tmp / "absent.zip"), "--schema-dir", self.schemas, "--json")
        self.assertEqual(2, code)
        self.assertEqual("error", json.loads(out)["verdict"])


class TheCorpusCommand(unittest.TestCase):
    """The runner that holds the validator to the shared corpus's `bundle` group."""

    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.schemas = str(write_schema_dir(self.tmp / "schemas"))
        self.corpus = self.tmp / "corpus"
        (self.corpus / "bundle").mkdir(parents=True)

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def write(self, cases: list[dict], bodies: dict[str, dict]) -> None:
        (self.corpus / "index.json").write_text(json.dumps({"cases": cases + [
            {"id": "manifest.other", "group": "manifest", "file": "manifest/other.json",
             "expect": "accept", "reason": "another group; the runner skips it"}]}), encoding="utf-8")
        for name, body in bodies.items():
            (self.corpus / name).write_text(json.dumps(body), encoding="utf-8")

    @staticmethod
    def body(document: dict, extra: dict[str, str] | None = None) -> dict:
        files = {"README.md": "A synthetic bundle.\n", "Metadata/manifest.json": document}
        files.update(extra or {})
        return {"files": files}

    def test_every_case_passes_in_every_form(self) -> None:
        missing = manifest(layout={"readme": "README.md", "manifest": "Metadata/manifest.json",
                                   "dem": "Elevation/DEM.tif"})
        self.write(
            [{"id": "bundle.ok", "group": "bundle", "file": "bundle/ok.json", "expect": "accept",
              "reason": "r"},
             {"id": "bundle.missing", "group": "bundle", "file": "bundle/missing.json",
              "expect": "reject", "reason": "r", "expectations": {"errors": ["pointer.missing"]}}],
            {"bundle/ok.json": self.body(manifest()), "bundle/missing.json": self.body(missing)},
        )
        code, out = run("corpus", str(self.corpus), "--schema-dir", self.schemas)
        self.assertEqual(0, code, out)
        self.assertIn("2 case(s)", out)
        self.assertIn("folder, zip, wrapped", out)

    def test_a_case_the_validator_disagrees_with_fails_the_run(self) -> None:
        self.write(
            [{"id": "bundle.wrong", "group": "bundle", "file": "bundle/wrong.json", "expect": "reject",
              "reason": "r", "expectations": {"errors": ["pointer.missing"]}}],
            {"bundle/wrong.json": self.body(manifest())},
        )
        code, out = run("corpus", str(self.corpus), "--schema-dir", self.schemas)
        self.assertEqual(1, code)
        self.assertIn("bundle.wrong", out)

    def test_the_reported_warnings_are_exactly_the_expected_ones(self) -> None:
        document = manifest(file_entries={"Elevation/DEM.tif": {}})
        body = self.body(document, {"Elevation/DEM.tif": "synthetic\n"})
        for warnings, want in ((["frame.absent", "role.absent"], 0), (["role.absent"], 1)):
            with self.subTest(warnings=warnings):
                self.write(
                    [{"id": "bundle.warn", "group": "bundle", "file": "bundle/warn.json",
                      "expect": "accept", "reason": "r",
                      "expectations": {"warnings": warnings, "hashesVerified": 0}}],
                    {"bundle/warn.json": body},
                )
                code, out = run("corpus", str(self.corpus), "--schema-dir", self.schemas)
                self.assertEqual(want, code, out)

    def test_an_expectation_the_runner_does_not_know_fails_the_case(self) -> None:
        self.write(
            [{"id": "bundle.odd", "group": "bundle", "file": "bundle/odd.json", "expect": "accept",
              "reason": "r", "expectations": {"verdictIsGreen": True}}],
            {"bundle/odd.json": self.body(manifest())},
        )
        code, out = run("corpus", str(self.corpus), "--schema-dir", self.schemas)
        self.assertEqual(1, code)
        self.assertIn("verdictIsGreen", out)

    def test_an_empty_bundle_group_fails(self) -> None:
        self.write([], {})
        code, _ = run("corpus", str(self.corpus), "--schema-dir", self.schemas)
        self.assertEqual(1, code)


if __name__ == "__main__":
    unittest.main()
