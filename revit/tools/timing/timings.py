#!/usr/bin/env python3
"""Turn one timing-harness run dir (plugin log + Revit journal + window readings) into timings.md.

    python timings.py runs\\<tag>-<year> [--out timings-v2.md]

`--out NAME` writes NAME in the run dir instead of timings.md (Invoke-TimingRun.ps1 never passes it, so a run still
ends with timings.md; use it to re-render an old run without overwriting the earlier table).

Inputs read from the run dir, all optional except the log:
    import.log        the plugin's log (a copy of <zip>.mantleplace-import.log)
    journal.txt       the Revit journal of the process that ran the import
    meta.json         the driver's record (tag, source commit, timestamps, outcome)
    result.json       the in-Revit harness's record (wall seconds, Revit build, failure)
    window.txt        the window phase's readings, one tab-separated line per sample
    timeout.txt       present when the run was cut off

Per step (one row): start, end, seconds, how many commits the journal saw inside it, their seconds,
what was outside commits (before the first, between, after the last), and the largest commit. A
commit's seconds are the journal's own: the leading number of the depth-1
`EndOrAbortUndoTransaction();DOPT;` line, named from `"Transaction Successful", "<name>"` (else the
`requestingFinish of transaction ...` line, else the plugin log's commit line at the same position in the
step). A commit is placed in the step whose log start/end brackets its end time, so an unnamed
transaction lands in the right step by order and time. Log times are whole seconds, so the pre/post
figures carry about one second of rounding; the commit seconds do not. A log stamp of hh:mm:ss means
"some moment in that second", so a step's end is inclusive of its whole last second: a commit that ends
in the second the step's `(Kind took N s.)` line carries belongs to that step, never to the next row.
Numbers read from window.txt may carry thousands commas (older drivers wrote `2,754.0`); every one is
parsed with `num()`, which drops them.
"""
from __future__ import annotations

import json
import re
import sys
from dataclasses import dataclass, field
from datetime import datetime, timedelta
from pathlib import Path

LOG_LINE = re.compile(r"^(\d\d):(\d\d):(\d\d)  (.*)$")
LOG_HEADER = re.compile(r"^(?P<version>Mantle Place \S+) bundle import, started (?P<date>\d{4}-\d\d-\d\d) (?P<time>\d\d:\d\d:\d\d)")
STEP_START = re.compile(r"^\[(\w+)\] started\.$")
STEP_END = re.compile(r"^\((\w+) took ([\d,]+(?:\.\d+)?) s\.\)$")
STEP_NOTRUN = re.compile(r"^\[(\w+)\] not run")
COMMIT_LOG = re.compile(r"^\[([^\]]+)\] commit took ([\d,]+(?:\.\d+)?) s \((\w+)\)\.?$")

J_TS = re.compile(r"^\s*'[CEH] (\d\d-[A-Za-z]{3}-\d{4} \d\d:\d\d:\d\d\.\d{3});")
J_H = re.compile(r"^'H (\d\d-[A-Za-z]{3}-\d{4} \d\d:\d\d:\d\d\.\d{3});\s+(\d+):<")
J_TXDATA = re.compile(r'^\s+"Transaction ([^"]+)"\s*,\s*"(.*)"\s*$')
J_REQ = re.compile(r"^'requestingFinish of transaction \S+ \(start\):-?\d+ (.*)$")
J_DOPT = re.compile(r"^'\s*(\d+\.\d+)!*\s+(\d+):(?:<<|!!!BIG_GAP )EndOrAbortUndoTransaction\(\);DOPT;")
J_STARTED = re.compile(r"'C (\d\d-[A-Za-z]{3}-\d{4} \d\d:\d\d:\d\d\.\d{3});\s+started recording journal file")


def num(text: str) -> float:
    """A number as the plugin log or the driver wrote it: `2,754.0` (N1 in an en-US session) or `2754.0`."""
    return float(text.strip().replace(",", ""))


def maybe_num(text: str | None) -> float | None:
    """`num`, or None when the field is blank or is not a number."""
    if text is None or not text.strip():
        return None
    try:
        return num(text)
    except ValueError:
        return None


def jts(text: str) -> datetime:
    return datetime.strptime(text, "%d-%b-%Y %H:%M:%S.%f")


def fmt(seconds: float | None, digits: int = 1) -> str:
    if seconds is None:
        return "-"
    return f"{seconds:,.{digits}f}"


def hms(moment: datetime | None) -> str:
    return "-" if moment is None else moment.strftime("%H:%M:%S")


@dataclass
class Step:
    kind: str
    start: datetime
    end: datetime | None = None
    took: float | None = None  # the log's own "(Kind took N s.)"
    not_run: bool = False
    cut_off: bool = False
    end_exact: bool = False  # end is a real clock reading (cutoff, harness ended_at), not a floored log second
    log_commits: list[tuple[datetime, str, float, str]] = field(default_factory=list)
    commits: list["Commit"] = field(default_factory=list)


@dataclass
class Commit:
    end: datetime
    seconds: float
    name: str | None
    status: str
    name_source: str = "journal"
    depth: int = 1
    reported: bool = True  # False: the journal named the transaction but printed no DOPT timing (Revit omits it below ~0.05 s)

    @property
    def start(self) -> datetime:
        return self.end - timedelta(seconds=self.seconds)


def read_text(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig", errors="replace")


def parse_log(path: Path):
    lines = read_text(path).splitlines()
    header = LOG_HEADER.match(lines[0]) if lines else None
    if not header:
        raise SystemExit(f"{path}: first line is not a bundle-import header: {lines[0][:120] if lines else '(empty)'}")
    day = datetime.strptime(header["date"], "%Y-%m-%d")
    previous = datetime.strptime(f"{header['date']} {header['time']}", "%Y-%m-%d %H:%M:%S")
    steps: list[Step] = []
    open_step: Step | None = None
    stamped: list[tuple[datetime, str]] = []
    finished_line = None
    for line in lines[1:]:
        m = LOG_LINE.match(line)
        if not m:
            if line.startswith(("Bundle imported", "Bundle import ")):
                finished_line = line
            continue
        moment = day.replace(hour=int(m[1]), minute=int(m[2]), second=int(m[3]))
        while moment < previous - timedelta(minutes=5):
            moment += timedelta(days=1)
            day += timedelta(days=1)
        previous = moment
        text = m[4]
        stamped.append((moment, text))
        s = STEP_START.match(text)
        if s:
            open_step = Step(kind=s[1], start=moment)
            steps.append(open_step)
            continue
        e = STEP_END.match(text)
        if e and open_step and open_step.kind == e[1]:
            open_step.end = moment
            open_step.took = num(e[2])
            open_step = None
            continue
        n = STEP_NOTRUN.match(text)
        if n:
            for st in reversed(steps):
                if st.kind == n[1]:
                    st.not_run = True
                    st.end = st.end or moment
                    break
            open_step = None
            continue
        c = COMMIT_LOG.match(text)
        if c and open_step:
            open_step.log_commits.append((moment, c[1], num(c[2]), c[3]))
    started = datetime.strptime(f"{header['date']} {header['time']}", "%Y-%m-%d %H:%M:%S")
    return header["version"], started, steps, stamped, finished_line, lines


def parse_journal(path: Path):
    """The commits of one journal.

    A commit is a `Jrn.Data "Transaction <status>", "<name>"` pair, stamped by the `'H` line above it. Its
    seconds are the `EndOrAbortUndoTransaction();DOPT;` line that follows at the SAME nesting depth (the
    depth is 1 for a transaction run from an ExternalEvent and 2 from ApplicationInitialized, and nested
    lines of the same text sit deeper). Revit prints no DOPT line for a commit under about 0.05 s, so
    such a commit is listed with `reported=False`. A DOPT line with no name before it, at the depth the
    named commits use, is an unnamed transaction.
    """
    info: dict[str, str] = {}
    commits: list[Commit] = []
    orphans: list[Commit] = []
    pend_ts = last_ts = None
    pend_depth = 1
    req_name = None
    untimed: Commit | None = None
    untimed_line = 0
    with path.open(encoding="utf-8-sig", errors="replace") as handle:
        for lineno, line in enumerate(handle):
            if not info.get("started"):
                m = J_STARTED.search(line)
                if m:
                    info["started"] = m[1]
            if line.startswith("' Build:"):
                info["build"] = line.split(":", 1)[1].strip()
            elif line.startswith("' Branch:"):
                info["branch"] = line.split(":", 1)[1].strip()
            elif line.startswith("' Release:"):
                info["release"] = line.split(":", 1)[1].strip()
            m = J_TS.match(line)
            if m:
                last_ts = m[1]
            m = J_H.match(line)
            if m:
                pend_ts, pend_depth = m[1], int(m[2])
                continue
            m = J_TXDATA.match(line)
            if m:
                stamp = pend_ts or last_ts
                if stamp:
                    untimed = Commit(end=jts(stamp), seconds=0.0, name=m[2], status=m[1], depth=pend_depth, reported=False)
                    untimed_line = lineno
                    commits.append(untimed)
                pend_ts = None
                continue
            m = J_REQ.match(line)
            if m:
                req_name = m[1].strip()
                continue
            m = J_DOPT.match(line)
            if m:
                depth = int(m[2])
                if untimed is not None and depth == untimed.depth and lineno - untimed_line <= 6:
                    untimed.seconds, untimed.reported = float(m[1]), True
                    untimed = None
                else:
                    stamp = pend_ts or last_ts
                    if stamp:
                        orphans.append(Commit(end=jts(stamp), seconds=float(m[1]), name=req_name, status="?", depth=depth))
                pend_ts = req_name = None
    depths = [c.depth for c in commits if c.name and c.name.startswith("Mantle Place:")]
    base = max(set(depths), key=depths.count) if depths else 1
    commits.extend(o for o in orphans if o.depth == base)
    commits.sort(key=lambda c: c.end)
    info["base_depth"] = str(base)
    return info, commits


def assign(steps: list[Step], commits: list[Commit], cutoff: datetime | None):
    """Place every journal commit in a step.

    The plugin log writes a `[name] commit took N s` line the instant a commit returns, inside its step, so
    those lines are anchors: a journal commit with the same name (or none), the same seconds and an end
    time within a second and a half of the log line's is that commit, and it belongs to the step the log
    line sits in. What is left (Revit's own transactions such as an IFC import, and unnamed ones that no
    log line explains) is placed by its end time between the steps of its nearest anchors.

    The seconds must agree within 0.15 s plus 0.1 % of the commit: the log's figure is taken around the
    Commit() call and the journal's is Revit's own, and on a 1,262 s commit they differ by 0.2 s. A name
    that matches exactly is what makes the pairing; the seconds only rule out a neighbour.

    A log stamp is a whole second (hh:mm:ss, floored), so a step's true end lies anywhere in the second its
    `(Kind took N s.)` line carries. Placement by time therefore treats the step as ending at the END of that
    second: a commit that finished in the step's last second belongs to the step, and the earlier of two
    steps that share a boundary second wins it.
    """
    for step in steps:
        if step.end is None:
            step.end = cutoff
            step.cut_off = True
    commits = sorted(commits, key=lambda c: c.end)
    owner: dict[int, int] = {}
    for si, step in enumerate(steps):
        for stamp, lname, lsec, _status in step.log_commits:
            centre = stamp + timedelta(seconds=0.5)
            best = None
            for ci, c in enumerate(commits):
                if ci in owner or c.name not in (None, lname):
                    continue
                if (abs(c.seconds - lsec) > 0.15 + 0.001 * lsec) if c.reported else (lsec > 0.25):
                    continue
                d = abs((c.end - centre).total_seconds())
                if d <= 1.6 and (best is None or d < best[0]):
                    best = (d, ci)
            if best:
                owner[best[1]] = si
                if commits[best[1]].name is None:
                    commits[best[1]].name, commits[best[1]].name_source = lname, "log"
    loose: list[Commit] = []
    for ci, c in enumerate(commits):
        if ci in owner:
            continue
        before = max((owner[j] for j in range(ci) if j in owner), default=0)
        after = min((owner[j] for j in range(ci + 1, len(commits)) if j in owner), default=len(steps) - 1)
        strict = fuzzy = None
        for si in range(before, min(after, len(steps) - 1) + 1):
            step = steps[si]
            if step.not_run or step.end is None:
                if not step.cut_off and step.end is None:
                    continue
            # the step's end stamp is a floored second: its true end is anywhere before the next second starts
            whole_second_end = step.end if (step.cut_off or step.end_exact) else step.end + timedelta(seconds=1)
            if step.start <= c.end < whole_second_end:
                strict = si if strict is None else strict
            elif step.start <= c.end <= step.end + timedelta(seconds=1.999):
                fuzzy = si if fuzzy is None else fuzzy
        chosen = strict if strict is not None else fuzzy
        if chosen is None:
            loose.append(c)
        else:
            owner[ci] = chosen
    for ci, si in owner.items():
        steps[si].commits.append(commits[ci])
    for step in steps:
        step.commits.sort(key=lambda c: c.end)
    return loose


def outside(step: Step):
    """(before the first commit, between commits, after the last, largest single gap and where)."""
    if step.end is None:
        return None, None, None, (None, None)
    if not step.commits:
        total = (step.end - step.start).total_seconds()
        return total, 0.0, 0.0, (total, "no commit")
    pre = max(0.0, (step.commits[0].start - step.start).total_seconds())
    post = max(0.0, (step.end - step.commits[-1].end).total_seconds())
    between = 0.0
    largest = (pre, step.commits[0].name or "(unnamed)")
    for prev, cur in zip(step.commits, step.commits[1:]):
        gap = max(0.0, (cur.start - prev.end).total_seconds())
        between += gap
        if gap > largest[0]:
            largest = (gap, cur.name or "(unnamed)")
    if post > largest[0]:
        largest = (post, "after the last commit")
    return pre, between, post, largest


def table(headers: list[str], rows: list[list[str]], right: set[int] | None = None) -> list[str]:
    right = right or set()
    out = ["| " + " | ".join(headers) + " |"]
    out.append("|" + "|".join(("---:" if i in right else "---") for i in range(len(headers))) + "|")
    for row in rows:
        out.append("| " + " | ".join(str(c).replace("|", "\\|") for c in row) + " |")
    return out


def window_section(path: Path) -> list[str]:
    rows = [line.rstrip("\n").split("\t") for line in read_text(path).splitlines() if line and not line.startswith("#")]
    if not rows:
        return ["## Window readings", "", "No readings were taken."]
    samples = [r for r in rows if len(r) >= 10 and not r[-1].strip().startswith("final reading")]
    def col(r, i):
        return r[i] if i < len(r) else ""
    hung = [r for r in samples if col(r, 8) == "1" or col(r, 7) == "1"]
    stale_values = [v for v in (maybe_num(col(r, 10)) for r in samples) if v is not None]
    def shown(text: str, digits: int) -> str:
        """A window.txt number as one style, whether the driver wrote `2,754.0` or `2754.0`; anything else stays as written."""
        value = maybe_num(text)
        return fmt(value, digits) if value is not None else text
    transitions = []
    last = object()
    for r in samples:
        step = col(r, 2)
        if step != last:
            transitions.append([shown(col(r, 0), 1), col(r, 1), step or "(none)", col(r, 3)])
            last = step
    out = ["## Window readings", ""]
    out.append(f"- {len(samples)} readings in `window.txt`; {len(hung)} taken while the window was not responding (IsHungAppWindow), when the last reading is repeated and `stale_s` counts up.")
    if stale_values:
        # `stale_s` is the age of the newest completed reading at each 5 s sample, so its maximum stops one sample
        # short of the next fresh reading. The reading's own time (elapsed - stale_s) gives the span between two
        # completed readings, which is what the window actually went without.
        made_at: list[float] = []
        for r in samples:
            t, age = maybe_num(col(r, 0)), maybe_num(col(r, 10))
            if t is not None and age is not None and (not made_at or (t - age) - made_at[-1] > 2.0):
                made_at.append(t - age)
        spans = [(b - a, a, b) for a, b in zip(made_at, made_at[1:])]
        last_t = maybe_num(col(samples[-1], 0))
        if made_at and last_t is not None:
            spans.append((last_t - made_at[-1], made_at[-1], None))
        line = f"- Longest time the window went without a fresh reading: {max(stale_values):,.0f} s by `stale_s`"
        if spans:
            gap, a, b = max(spans)
            line += (f"; measured between completed readings it is {gap:,.0f} s (the reading {a:,.0f} s after Import, then "
                     + (f"the next {b:,.0f} s after Import)." if b is not None else "none before the last sample).")
                     )
        else:
            line += "."
        out.append(line)
    if samples:
        out.append(f"- Last reading: {shown(col(samples[-1], 0), 1)} s after Import; close button {col(samples[-1], 6) or '-'}; bar {col(samples[-1], 5) or '-'}.")
    out += ["", "The step the window named as current, each time it changed (a window inside a long commit cannot be read, so this lags; the next table is the plugin log's view):", ""]
    out += table(["elapsed s", "clock", "current step", "status line"], transitions, right={0})
    log_transitions = []
    last_log = object()
    for r in samples:
        step = col(r, 11)
        if step != last_log:
            log_transitions.append([shown(col(r, 0), 1), col(r, 1), step or "-", (shown(col(r, 10), 0) + " s") if col(r, 10) else "-"])
            last_log = step
    out += ["", "The step the plugin log said was in flight, each time it changed (`age` is how old the window reading beside it was):", ""]
    out += table(["elapsed s", "clock", "log step", "window reading age"], log_transitions, right={0})
    return out


def revit_line(run_dir: Path, result: dict) -> str:
    if result.get("revit_build"):
        return f"{result.get('revit_name', '?')} build {result['revit_build']} ({result.get('revit_subversion', result.get('revit_version', '?'))})"
    harness = run_dir / "harness.txt"  # a run cut off before it wrote result.json still logged this line
    if harness.exists():
        m = re.search(r"Revit (\S+) (.*?) build (\S+), pid", read_text(harness))
        if m:
            return f"{m[2]} build {m[3]} (from harness.txt)"
    return "?"


def main(run_dir: Path, out_name: str = "timings.md") -> None:
    log_path = run_dir / "import.log"
    if not log_path.exists():
        raise SystemExit(f"{log_path} is missing: the run left no plugin log")
    meta = json.loads(read_text(run_dir / "meta.json")) if (run_dir / "meta.json").exists() else {}
    result = json.loads(read_text(run_dir / "result.json")) if (run_dir / "result.json").exists() else {}
    version, log_started, steps, stamped, finished_line, _ = parse_log(log_path)

    cutoff = None
    if meta.get("outcome") in ("timeout", "revit-exited") and meta.get("endedAt"):
        cutoff = datetime.strptime(meta["endedAt"][:19], "%Y-%m-%dT%H:%M:%S")
    elif steps and steps[-1].end is None and stamped:
        cutoff = stamped[-1][0]

    journal_info, commits = ({}, [])
    jpath = run_dir / "journal.txt"
    if jpath.exists():
        journal_info, commits = parse_journal(jpath)
    # What runs after the last step's own end line (the smooth-shading commit, the summary) is real wall clock
    # of the import, so it gets a row of its own, ending when the harness saw the importer let go.
    if steps and steps[-1].end is not None and not steps[-1].cut_off and result.get("ended_at"):
        tail_start = steps[-1].end
        tail_end = datetime.fromisoformat(result["ended_at"])
        tail_commits = []
        last_kind = steps[-1].kind
        for stamp, text in reversed(stamped):  # file order: only what follows the last "(Kind took N s.)" line
            e = STEP_END.match(text)
            if e and e[1] == last_kind:
                break
            m = COMMIT_LOG.match(text)
            if m:
                tail_commits.insert(0, (stamp, m[1], num(m[2]), m[3]))
        if tail_end > tail_start + timedelta(seconds=0.5):
            finish = Step(kind="(finish, after the last step)", start=tail_start, end=tail_end, log_commits=tail_commits, end_exact=True)
            finish.took = (tail_end - tail_start).total_seconds()
            steps.append(finish)
    loose = assign(steps, commits, cutoff)

    out: list[str] = []
    title = meta.get("tag", run_dir.name)
    out.append(f"# Import timings: {run_dir.name}")
    out.append("")
    out.append("## Run")
    out.append("")
    facts = [
        ("Tag / year / phase", f"{meta.get('tag', '?')} / {meta.get('year', '?')} / {meta.get('phase', '?')}"),
        ("Source", f"{meta.get('sourceCommit', result.get('source_commit', '?'))} (`{meta.get('sourceRoot', '?')}`)"),
        ("Plugin build in the log", version),
        ("Revit build (journal header)", f"{journal_info.get('build', '?')}, release {journal_info.get('release', '?')}, branch {journal_info.get('branch', '?')}"),
        ("Revit (harness)", revit_line(run_dir, result)),
        ("Bundle", f"sha256 {str(meta.get('bundleSha256', '?'))[:16]}… ({meta.get('bundleBytes', '?')} bytes)"),
        ("Outcome", str(meta.get("outcome", result.get("outcome", "?")))),
    ]
    if meta.get("phase") == "layers" or result.get("layers"):
        facts.append(("Layers", ", ".join(result.get("layers", [])) or str(meta.get("layers"))))
    if meta.get("phase") == "window":
        facts.append(("Boxes", str(meta.get("boxes"))))
    ifc = meta.get("ifcLink")
    if isinstance(ifc, dict):
        facts.append(("IFC link at launch", str(ifc.get("summary") or ("cold" if ifc.get("cold") else "warm"))))
        if ifc.get("movedAside") or ifc.get("restored") is not None:
            facts.append(("IFC cache after the run", str(ifc.get("restoreStatus", "?"))))
        if ifc.get("runFileKeptAt"):
            facts.append(("The run's own Site file", f"kept at `{ifc['runFileKeptAt']}` ({ifc.get('runFileBytes', '?')} bytes, sha256 {str(ifc.get('runFileSha256', '?'))[:16]})"))
    elif meta:
        facts.append(("IFC link at launch", "not recorded (a run from before the driver recorded it; the site IFC's companion .rvt may have been reused)"))
    if meta.get("openCheckpoint"):
        facts.append(("Opened checkpoint copy", meta["openCheckpoint"]))
    if meta.get("checkpointWritten"):
        facts.append(("Checkpoint written", meta["checkpointWritten"]))
    if result.get("stop_before_layer"):
        if result.get("stop_before_reached"):
            ran = result.get("steps_ran") or []
            facts.append(("Stopped before", f"{result.get('stop_before_step', '?')} (layer {result['stop_before_layer']}, step {result.get('stop_before_step_number', '?')} of {result.get('stop_before_step_count', '?')}); {len(ran)} steps ran, the named step and {max(0, len(result.get('steps_not_run') or []) - 1)} after it did not; clean = {result.get('stop_before_clean')}"))
        else:
            facts.append(("Stopped before", f"{result['stop_before_layer']} was NOT reached: {result.get('stop_before_message', '?')}"))
    if result.get("wall_seconds") is not None:
        if meta.get("phase") == "window":
            label = "Import wall clock, Import press to the handler letting go"
        elif result.get("stop_before_layer"):
            label = "Import wall clock, Begin + stepping to the stop"
        else:
            label = "Import wall clock, Begin + RunToEnd"
        facts.append((label, f"{fmt(result['wall_seconds'])} s"))
    if result.get("open_seconds") is not None:
        facts.append(("ActiveImport.Open (plan + integrity check)", f"{fmt(result['open_seconds'])} s"))
    if result.get("checkpoint_save_seconds") is not None:
        facts.append(("Checkpoint save", f"{fmt(result['checkpoint_save_seconds'])} s, {result.get('checkpoint_bytes', 0):,} bytes"))
    if meta.get("pressedAt") and result.get("began_at"):
        latency = (datetime.fromisoformat(result["began_at"]) - datetime.fromisoformat(meta["pressedAt"])).total_seconds()
        facts.append(("Import press to the harness seeing the plan staged", f"{fmt(latency, 2)} s"))
    if meta.get("pressedAt") and result.get("ended_at"):
        press_to_end = (datetime.fromisoformat(result["ended_at"]) - datetime.fromisoformat(meta["pressedAt"])).total_seconds()
        facts.append(("Import press to the summary (driver clock to harness clock)", f"{fmt(press_to_end)} s"))
    if finished_line:
        facts.append(("Log's closing line", finished_line))
    if result.get("failed"):
        facts.append(("Import failed", str(result.get("summary_head"))))
    for k, v in facts:
        out.append(f"- **{k}:** {v}")
    if result.get("ifc_cache_restore_failed"):
        out += ["", "> **THE USER'S IFC CACHE WAS NOT RESTORED.** " + str(result.get("ifc_cache_restore_message", "")) + " See `driver.txt` and `ifc-cache.json`."]
    if (run_dir / "timeout.txt").exists():
        out += ["", "> **Cut off.** " + read_text(run_dir / "timeout.txt").splitlines()[0].rstrip(".") + ". See `timeout.txt`: the step the log and the window were in, and the journal's last lines."]

    first = steps[0].start if steps else None
    last_end = max((s.end for s in steps if s.end), default=None)
    out += ["", "## Steps", ""]
    rows = []
    total_log = total_commit = 0.0
    for i, s in enumerate(steps, 1):
        seconds = s.took if s.took is not None else ((s.end - s.start).total_seconds() if s.end else None)
        commit_s = sum(c.seconds for c in s.commits)
        total_log += seconds or 0
        total_commit += commit_s
        pre, between, post, largest = outside(s)
        out_total = None if seconds is None else max(0.0, seconds - commit_s)
        big = max(s.commits, key=lambda c: c.seconds, default=None)
        label = s.kind + (" (cut off)" if s.cut_off else "") + (" (not run)" if s.not_run else "")
        rows.append([i, label, hms(s.start), hms(s.end), fmt(seconds), len(s.commits), fmt(commit_s), fmt(out_total),
                     f"{fmt(big.seconds)} {big.name or '(unnamed)'}" if big else "-"])
    out += table(["#", "Step", "Start", "End", "Seconds", "Commits", "Commit s", "Outside commits s", "Largest commit"], rows, right={0, 4, 5, 6, 7})
    span = (last_end - first).total_seconds() if first and last_end else None
    out += ["", f"Steps total {fmt(total_log)} s by the log's own clock, of which {fmt(total_commit)} s inside journal commits and {fmt(total_log - total_commit)} s outside them. First step start to last step end: {fmt(span)} s. Journal commits found: {len(commits)} ({len(loose)} outside every step)."]

    out += ["", "## Where the time outside commits went", "",
            "Before the first commit of the step, between commits, after the last one, and the single largest gap. A gap runs from the end of one commit to the start of the next (a commit starts at its end time minus its journal seconds). Log times are whole seconds, so the before/after figures carry about one second of rounding.", ""]
    rows = []
    for i, s in enumerate(steps, 1):
        pre, between, post, largest = outside(s)
        rows.append([i, s.kind, fmt(pre), fmt(between), fmt(post), f"{fmt(largest[0])} before {largest[1]}" if largest[0] is not None else "-"])
    out += table(["#", "Step", "Before first commit s", "Between commits s", "After last commit s", "Largest single gap s"], rows, right={0, 2, 3, 4})

    out += ["", "## Commits", "",
            "Every commit of a step with 12 or fewer, the 10 longest of the others. `Gap before` is the time since the previous commit ended.", ""]
    rows = []
    previous_end = None
    for step in steps:
        # the gap before a commit runs from the previous commit's end, or from the step's start if that is later
        for c in step.commits:
            floor = max(step.start, previous_end) if previous_end else step.start
            c._gap = max(0.0, (c.start - floor).total_seconds())  # type: ignore[attr-defined]
            previous_end = c.end
    for i, s in enumerate(steps, 1):
        shown = s.commits if len(s.commits) <= 12 else sorted(s.commits, key=lambda c: -c.seconds)[:10]
        for c in sorted(shown, key=lambda c: c.end):
            note = "" if c.name_source == "journal" else f" ({c.name_source})"
            status = "" if c.status in ("Successful", "?") else f" [{c.status}]"
            rows.append([i, s.kind, hms(c.end), fmt(c.seconds) if c.reported else "<0.05", (c.name or "(unnamed)") + note + status, fmt(getattr(c, "_gap", None))])
        if len(s.commits) > 12:
            rows.append([i, s.kind, "", "", f"… {len(s.commits) - 10} more of {len(s.commits)} commits, {fmt(sum(c.seconds for c in s.commits))} s in all", ""])
    out += table(["#", "Step", "Commit ended", "Seconds", "Name", "Gap before s"], rows, right={0, 3, 5})
    if loose:
        out += ["", f"Commits outside every step ({len(loose)}): " + "; ".join(f"{hms(c.end)} {fmt(c.seconds)} s {c.name or '(unnamed)'}" for c in loose[:12]) + (" …" if len(loose) > 12 else "")]

    # journal vs the plugin log's own commit lines (Revit's own transactions, such as an IFC import, are not the plugin's)
    mismatched = []
    for i, s in enumerate(steps, 1):
        mine = [c for c in s.commits if c.name and c.name.startswith("Mantle Place:")]
        jsum = sum(c.seconds for c in mine)
        lsum = sum(c[2] for c in s.log_commits)
        if (mine or s.log_commits) and (abs(jsum - lsum) > 1.0 + 0.02 * max(jsum, lsum) or len(mine) != len(s.log_commits)):
            mismatched.append(f"step {i} {s.kind}: journal {len(mine)} plugin commits / {fmt(jsum)} s, log {len(s.log_commits)} commits / {fmt(lsum)} s")
    out += ["", "## Cross-check: journal against the plugin log's commit lines", ""]
    if not commits:
        out.append("No journal was available, so the commit columns are empty; the plugin log's own commit lines are in `import.log`.")
    elif mismatched:
        out.append("The journal and the log disagree in these steps:")
        out += [f"- {m}" for m in mismatched]
    else:
        out.append("Every step's plugin commits in the journal match the log's commit lines in count and seconds (Revit's own transactions inside a step, such as an IFC import, are listed under Commits but not compared).")

    if (run_dir / "window.txt").exists():
        out += [""] + window_section(run_dir / "window.txt")

    (run_dir / out_name).write_text("\n".join(out) + "\n", encoding="utf-8")
    print(f"wrote {run_dir / out_name} ({len(steps)} steps, {len(commits)} journal commits)")


if __name__ == "__main__":
    args = sys.argv[1:]
    out_name = "timings.md"
    if len(args) == 3 and args[1] == "--out":
        out_name = args[2]
        if Path(out_name).name != out_name:
            raise SystemExit("--out takes a file name, not a path: it is written in the run dir")
        args = args[:1]
    if len(args) != 1:
        raise SystemExit(__doc__)
    main(Path(args[0]), out_name)
