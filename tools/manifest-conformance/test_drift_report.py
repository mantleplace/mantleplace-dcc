"""The drift report: what the scheduled job files when the published schema is ahead of the pins."""

from __future__ import annotations

import json
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import check_manifest_conformance as gate  # noqa: E402
import drift_report  # noqa: E402

DOC = {
    "$comment": ["prose"],
    "unreal": {"verifiedAgainstManifestVersion": "1.0.0"},
    "revit": {"verifiedAgainstManifestVersion": "1.0.0"},
}


class DriftReportTest(unittest.TestCase):
    def setUp(self) -> None:
        self._real = gate.fetch_published_versions
        self.addCleanup(lambda: setattr(gate, "fetch_published_versions", self._real))

    def _ledger(self, keys: list[str]) -> None:
        gate.fetch_published_versions = lambda timeout=20: sorted(keys, key=gate.sort_key)

    def test_equal_reports_nothing(self) -> None:
        self._ledger(["v19", "1.0.0"])
        self.assertIsNone(drift_report.build_report(DOC))

    def test_a_patch_over_the_pin_reports_nothing(self) -> None:
        self._ledger(["1.0.0", "1.0.1"])
        self.assertIsNone(drift_report.build_report(DOC))

    def test_ahead_names_the_new_version_and_hosts(self) -> None:
        self._ledger(["1.0.0", "1.1.0"])
        report = drift_report.build_report(DOC)
        self.assertEqual("1.1.0", report["newest"])
        self.assertEqual(["revit", "unreal"], report["hosts"])
        self.assertIn("MPB 1.1.0", report["title"])
        self.assertIn(drift_report.MARKER, report["body"])

    def test_title_is_stable_in_kind_so_a_second_run_finds_the_same_issue(self) -> None:
        self._ledger(["1.0.0", "1.1.0"])
        first = drift_report.build_report(DOC)
        second = drift_report.build_report(DOC)
        self.assertEqual(first, second)

    def test_main_prints_nothing_when_equal(self) -> None:
        self._ledger(["1.0.0"])
        self.assertEqual("", drift_report.render(DOC))

    def test_main_prints_json_when_ahead(self) -> None:
        self._ledger(["1.0.0", "2.0.0"])
        self.assertEqual("2.0.0", json.loads(drift_report.render(DOC))["newest"])


if __name__ == "__main__":
    unittest.main()
