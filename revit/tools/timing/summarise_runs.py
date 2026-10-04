#!/usr/bin/env python3
"""Build one cross-run step table from several timing-harness run folders.

    python summarise_runs.py <run dir> [<run dir> ...] [--out table.md]

One row per import step, in the order the steps first appear; one column per run, headed with the run's tag and
Revit year; seconds are the plugin log's own `(Kind took N s.)` figures. Below the steps, the import's wall clock
from result.json (`wall_seconds`, from the harness opening the import to the import ending) and the driver's
elapsed time from launching Revit (meta.json `elapsedSeconds`). When two or more runs share a Revit year, a
`spread <year>` column gives the largest minus the smallest figure of that year's runs, row by row: a difference
between two commits smaller than that spread is not a measured difference.

A step a run did not reach (a cutoff, a crash) shows `-`. A run whose outcome is not `done` is flagged under the
table. Standard library only; reads nothing outside the run folders it is given.
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

STEP_END = re.compile(r"^\d\d:\d\d:\d\d  \((\w+) took ([\d,]+(?:\.\d+)?) s\.\)\s*$")


def num(text: str) -> float:
    return float(text.replace(",", ""))


def read_json(path: Path) -> dict:
    try:
        return json.loads(path.read_text(encoding="utf-8-sig"))
    except (OSError, ValueError):
        return {}


def step_seconds(log_text: str) -> dict[str, float]:
    """The `(Kind took N s.)` lines of one plugin log, in order. A step that repeats is summed."""
    steps: dict[str, float] = {}
    for line in log_text.splitlines():
        m = STEP_END.match(line)
        if m:
            steps[m[1]] = steps.get(m[1], 0.0) + num(m[2])
    return steps


class Run:
    def __init__(self, run_dir: Path):
        self.dir = run_dir
        meta = read_json(run_dir / "meta.json")
        result = read_json(run_dir / "result.json")
        self.tag = str(meta.get("tag") or run_dir.name)
        self.year = str(meta.get("year") or result.get("revit_version") or "?")
        self.commit = str(meta.get("sourceCommit") or result.get("source_commit") or "")[:7]
        self.outcome = str(meta.get("outcome") or result.get("outcome") or "unknown")
        self.wall = result.get("wall_seconds")
        self.elapsed = meta.get("elapsedSeconds")
        log = run_dir / "import.log"
        self.steps = step_seconds(log.read_text(encoding="utf-8-sig", errors="replace")) if log.is_file() else {}

    @property
    def label(self) -> str:
        return f"{self.tag} {self.year}"


def fmt(value) -> str:
    return "-" if value is None else f"{float(value):,.1f}"


def table(runs: list[Run]) -> list[str]:
    kinds: list[str] = []
    for run in runs:
        for kind in run.steps:
            if kind not in kinds:
                kinds.append(kind)
    years = sorted({r.year for r in runs if sum(1 for o in runs if o.year == r.year) > 1})
    headers = ["step"] + [r.label for r in runs] + [f"spread {y}" for y in years]
    rows: list[list[str]] = []

    def row(name: str, values: list) -> list[str]:
        cells = [name] + [fmt(v) for v in values]
        for year in years:
            mine = [v for v, r in zip(values, runs) if r.year == year and v is not None]
            cells.append(fmt(max(mine) - min(mine)) if len(mine) > 1 else "-")
        return cells

    for kind in kinds:
        rows.append(row(kind, [r.steps.get(kind) for r in runs]))
    rows.append(row("**import wall clock**", [r.wall for r in runs]))
    rows.append(row("launch to end", [r.elapsed for r in runs]))
    out = ["| " + " | ".join(headers) + " |", "|" + "|".join(["---"] + ["---:"] * (len(headers) - 1)) + "|"]
    out += ["| " + " | ".join(cells) + " |" for cells in rows]
    out.append("")
    out.append("Commits: " + ", ".join(f"{r.label} at {r.commit or 'unknown'}" for r in runs) + ".")
    flagged = [r for r in runs if r.outcome != "done"]
    for r in flagged:
        out.append(f"- {r.label}: outcome `{r.outcome}` (see {r.dir.name}/driver.txt); its figures stop where the run did.")
    return out


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("runs", nargs="+", type=Path)
    parser.add_argument("--out", type=Path, default=None, help="write the table here instead of standard output")
    args = parser.parse_args(argv)
    missing = [str(d) for d in args.runs if not d.is_dir()]
    if missing:
        print("not a run folder: " + ", ".join(missing), file=sys.stderr)
        return 2
    text = "\n".join(table([Run(d) for d in args.runs])) + "\n"
    if args.out:
        args.out.write_text(text, encoding="utf-8")
    else:
        sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
