"""Cases for summarise_runs.py: python -m unittest test_summarise_runs (from this folder)."""
from __future__ import annotations

import json
import tempfile
import unittest
from pathlib import Path

import summarise_runs

LOG = """Mantle Place 0.1.0+abc bundle import, started 2026-10-03 10:19:30
10:20:31  (ToposurfaceFromSurfaceTin took 60.9 s.)
10:20:40  [Mantle Place: vegetation] commit took 0.4 s (Committed).
10:21:05  (Vegetation took 1,068.1 s.)
10:21:06  (Vegetation took 2.0 s.)
"""


def make_run(root: Path, name: str, tag: str, year: str, wall: float, log: str = LOG, outcome: str = "done") -> Path:
    d = root / name
    d.mkdir()
    (d / "meta.json").write_text(json.dumps({"tag": tag, "year": year, "sourceCommit": "d4f3453177ff", "outcome": outcome, "elapsedSeconds": wall + 20}))
    (d / "result.json").write_text(json.dumps({"wall_seconds": wall}))
    (d / "import.log").write_text(log, encoding="utf-8")
    return d


class StepSeconds(unittest.TestCase):
    def test_reads_step_end_lines_only_and_sums_repeats(self):
        steps = summarise_runs.step_seconds(LOG)
        self.assertEqual(list(steps), ["ToposurfaceFromSurfaceTin", "Vegetation"])
        self.assertAlmostEqual(steps["Vegetation"], 1070.1)

    def test_commit_lines_are_not_steps(self):
        self.assertNotIn("Mantle", "".join(summarise_runs.step_seconds(LOG)))


class Table(unittest.TestCase):
    def test_one_column_per_run_and_a_spread_per_repeated_year(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            runs = [summarise_runs.Run(make_run(root, "a", "base", "2027", 174.5)),
                    summarise_runs.Run(make_run(root, "b", "base-r2", "2027", 180.0)),
                    summarise_runs.Run(make_run(root, "c", "base", "2025", 142.0))]
            lines = summarise_runs.table(runs)
        self.assertEqual(lines[0], "| step | base 2027 | base-r2 2027 | base 2025 | spread 2027 |")
        wall = next(line for line in lines if line.startswith("| **import wall clock**"))
        self.assertEqual(wall, "| **import wall clock** | 174.5 | 180.0 | 142.0 | 5.5 |")

    def test_a_step_a_run_never_reached_is_a_dash_and_the_run_is_flagged(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            short = "Mantle Place x bundle import, started 2026-10-03 10:00:00\n10:01:00  (ToposurfaceFromSurfaceTin took 60.0 s.)\n"
            runs = [summarise_runs.Run(make_run(root, "a", "full", "2025", 100.0)),
                    summarise_runs.Run(make_run(root, "b", "cut", "2025", 70.0, log=short, outcome="timeout"))]
            lines = summarise_runs.table(runs)
        vegetation = next(line for line in lines if line.startswith("| Vegetation"))
        self.assertTrue(vegetation.endswith("| - | - |"))
        self.assertTrue(any("cut 2025: outcome `timeout`" in line for line in lines))


if __name__ == "__main__":
    unittest.main()
