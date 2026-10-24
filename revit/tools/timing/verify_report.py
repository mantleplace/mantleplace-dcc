"""Judges one -Verify run folder: placement.csv, link-buildings.csv and verify.json, as Harness/Verify.cs wrote them.

    python verify_report.py <run dir> [--footprints <projected gpkg> --table <name>] [--out verify.md]

The harness measures; this decides. Each check passes, fails, or says why it could not run, and the markdown is
the results table a pull request quotes. Standard library only. Cases: python -m unittest test_verify_report
"""

import argparse
import csv
import json
import math
import os
import sqlite3
import statistics
import struct
import sys

# The ground a TIN import builds is the bundle's surface DXF, decimated; a check point is a sample the importer
# never read. A metre is the bar the Unreal gate holds the same points to.
PLACEMENT_TOLERANCE_M = 1.0
# The internal origin, in shared coordinates, is the survey point the import set from the manifest, verbatim.
FRAME_TOLERANCE_M = 0.01
# A linked building's base against the ground under its plan centre: a building in the wrong place stands on
# ground of another height. Generous, as a base is the lowest corner of its box on sloping ground.
LINK_BASE_TOLERANCE_M = 3.0
CREDIT_WORD = "openstreetmap"


def _floats(row, *keys):
    values = []
    for key in keys:
        text = (row.get(key) or "").strip()
        values.append(float(text) if text else None)
    return values


def _read_csv(path):
    if not os.path.exists(path):
        return []
    with open(path, encoding="utf-8", newline="") as handle:
        return list(csv.DictReader(handle))


def _placement(folder):
    rows = _read_csv(os.path.join(folder, "placement.csv"))
    deltas, misses = [], 0
    for row in rows:
        expected, measured = _floats(row, "expected_z_m", "measured_z_m")
        if measured is None:
            misses += 1
        else:
            deltas.append(measured - expected)
    if not rows:
        return {"pass": False, "note": "no check points were measured"}
    absolute = [abs(d) for d in deltas]
    verdict = {
        "points": len(rows),
        "hits": len(deltas),
        "misses": misses,
        "mean_dz_m": statistics.fmean(deltas) if deltas else None,
        "rms_dz_m": math.sqrt(statistics.fmean([d * d for d in deltas])) if deltas else None,
        "max_abs_dz_m": max(absolute) if absolute else None,
        "tolerance_m": PLACEMENT_TOLERANCE_M,
    }
    verdict["pass"] = misses == 0 and bool(absolute) and max(absolute) <= PLACEMENT_TOLERANCE_M
    return verdict


def _frame(record):
    origin = record.get("origin_projected") or {}
    internal = record.get("internal_origin_in_shared_m") or []
    if "east" not in origin or len(internal) < 2:
        return {"pass": False, "note": "no origin recorded"}
    offset = math.hypot(internal[0] - origin["east"], internal[1] - origin["north"])
    return {"pass": offset <= FRAME_TOLERANCE_M, "offset_m": offset, "tolerance_m": FRAME_TOLERANCE_M}


def _dimensions(kind):
    """Coordinates per point of a WKB geometry type: ISO (1000 Z, 2000 M, 3000 ZM) or extended (Z and M flag bits)."""
    if kind & 0xF0000000:
        return 2 + (1 if kind & 0x80000000 else 0) + (1 if kind & 0x40000000 else 0)
    return {0: 2, 1: 3, 2: 3, 3: 4}[kind // 1000]


def _wkb_rings(blob):
    """The exterior ring of each polygon in a GeoPackage geometry blob (Polygon or MultiPolygon, 2D or Z)."""
    flags = blob[3]
    envelope = {0: 0, 1: 32, 2: 48, 3: 48, 4: 64}[(flags >> 1) & 0b111]
    data = blob[8 + envelope:]
    rings = []

    def polygon(offset):
        order = "<" if data[offset] == 1 else ">"
        dims = _dimensions(struct.unpack_from(order + "I", data, offset + 1)[0])
        count = struct.unpack_from(order + "I", data, offset + 5)[0]
        offset += 9
        for ring_index in range(count):
            points = struct.unpack_from(order + "I", data, offset)[0]
            offset += 4
            coords = [struct.unpack_from(order + "d" * dims, data, offset + i * 8 * dims)[:2] for i in range(points)]
            offset += points * 8 * dims
            if ring_index == 0:
                rings.append(coords)
        return offset

    order = "<" if data[0] == 1 else ">"
    kind = (struct.unpack_from(order + "I", data, 1)[0] & 0x0FFFFFFF) % 1000
    if kind == 3:
        polygon(0)
    elif kind == 6:
        parts = struct.unpack_from(order + "I", data, 5)[0]
        offset = 9
        for _ in range(parts):
            offset = polygon(offset)
    return rings


def _footprint_centres(path, table):
    connection = sqlite3.connect(path)
    try:
        column = connection.execute(
            "select column_name from gpkg_geometry_columns where table_name = ? collate nocase", (table,)
        ).fetchone()[0]
        centres = []
        for (blob,) in connection.execute(f'select "{column}" from "{table}"'):
            for ring in _wkb_rings(blob):
                xs = [p[0] for p in ring]
                ys = [p[1] for p in ring]
                centres.append(((min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2))
        return centres
    finally:
        connection.close()


def _link(folder, record, footprints, table):
    links = record.get("links") or []
    rows = _read_csv(os.path.join(folder, "link-buildings.csv"))
    if not links:
        return {"pass": False, "note": "the project has no linked model"}
    gaps = []
    for row in rows:
        base, ground = _floats(row, "base_z_m", "ground_z_m")
        if base is not None and ground is not None:
            gaps.append(base - ground)
    verdict = {
        "links": [link.get("name") for link in links],
        "origin_to_origin": all(link.get("transform_is_identity") for link in links),
        "loaded": all(link.get("loaded") for link in links),
        "buildings": len(rows),
        "on_ground": len(gaps),
        "median_base_minus_ground_m": statistics.median(gaps) if gaps else None,
        "within_base_tolerance": sum(1 for g in gaps if abs(g) <= LINK_BASE_TOLERANCE_M),
        "tolerance_m": LINK_BASE_TOLERANCE_M,
    }
    if footprints:
        centres = _footprint_centres(footprints, table)
        distances = []
        for row in rows:
            east, north = _floats(row, "centre_east_m", "centre_north_m")
            distances.append(min(math.hypot(east - x, north - y) for x, y in centres))
        verdict["footprints"] = len(centres)
        if distances:
            distances.sort()
            verdict["median_centre_to_footprint_m"] = statistics.median(distances)
            verdict["p90_centre_to_footprint_m"] = distances[min(len(distances) - 1, int(0.9 * len(distances)))]
            verdict["within_1m_of_a_footprint"] = sum(1 for d in distances if d <= 1.0)
    verdict["pass"] = (
        verdict["origin_to_origin"]
        and verdict["loaded"]
        and bool(rows)
        and bool(gaps)
        and verdict["median_base_minus_ground_m"] is not None
        and abs(verdict["median_base_minus_ground_m"]) <= LINK_BASE_TOLERANCE_M
    )
    return verdict


def _handover(record):
    handover = record.get("handover") or {}
    containers = record.get("containers") or []
    if not containers or not handover.get("filed_total"):
        return {"pass": False, "note": "the import filed no ODbL content, so the handover had nothing to remove", **handover}
    removed = set(handover.get("planned_removals") or [])
    credited = any(CREDIT_WORD in (note or "").lower() for note in handover.get("attribution_notes") or [])
    verdict = dict(handover)
    verdict["credit_names_openstreetmap"] = credited
    verdict["pass"] = (
        bool(handover.get("committed"))
        and handover.get("filed_deleted") == handover.get("filed_total")
        and handover.get("filed_left") == 0
        and not removed.intersection(handover.get("containers_left") or [])
        and credited
    )
    return verdict


def judge(folder, footprints=None, table="Building"):
    with open(os.path.join(folder, "verify.json"), encoding="utf-8-sig") as handle:
        record = json.load(handle)
    return {
        "manifest_version": record.get("manifest_version"),
        "placement": _placement(folder),
        "frame": _frame(record),
        "link": _link(folder, record, footprints, table),
        "containers": record.get("containers") or [],
        "overrides": record.get("site_context_overrides") or [],
        "stamped_by_class": record.get("stamped_by_class") or {},
        "handover": _handover(record),
    }


def _number(value, digits=3):
    return "-" if value is None else f"{value:.{digits}f}"


def _mark(verdict):
    return "pass" if verdict.get("pass") else "FAIL"


def markdown(report):
    p, f, k, h = report["placement"], report["frame"], report["link"], report["handover"]
    lines = [f"# Verify: manifest {report.get('manifest_version')}", ""]
    lines += ["## Placement", ""]
    if "points" in p:
        lines.append(
            f"{_mark(p)}: {p['hits']}/{p['points']} check points hit the ground, mean dz {_number(p['mean_dz_m'])} m, "
            f"RMS {_number(p['rms_dz_m'])} m, max |dz| {_number(p['max_abs_dz_m'])} m (tolerance {p['tolerance_m']} m)."
        )
    else:
        lines.append(f"FAIL: {p.get('note')}")
    lines += ["", "## Frame", ""]
    lines.append(f"{_mark(f)}: the internal origin is {_number(f.get('offset_m'), 4)} m from the manifest's origin.")
    lines += ["", "## Linked site model", ""]
    if "buildings" in k:
        text = (
            f"{_mark(k)}: {', '.join(map(str, k['links']))}; origin to origin {k['origin_to_origin']}; "
            f"{k['buildings']} elements with an IFC GlobalId; median base minus ground {_number(k['median_base_minus_ground_m'])} m, "
            f"{k['within_base_tolerance']}/{k['on_ground']} within {k['tolerance_m']} m"
        )
        if "median_centre_to_footprint_m" in k:
            text += (
                f"; centre to nearest footprint median {_number(k['median_centre_to_footprint_m'])} m, "
                f"90th percentile {_number(k['p90_centre_to_footprint_m'])} m, "
                f"{k['within_1m_of_a_footprint']}/{k['buildings']} within 1 m"
            )
        lines.append(text + ".")
    else:
        lines.append(f"FAIL: {k.get('note')}")
    lines += ["", "## ODbL containers", ""]
    for container in report["containers"]:
        classes = ", ".join(f"{name} x{count}" for name, count in sorted(container.get("by_class", {}).items()))
        lines.append(f"- `{container.get('name')}`: {container.get('live')} elements ({classes})")
    for override in report["overrides"]:
        lines.append(f"- override on `{override.get('filter')}`: line {override.get('line_color')}, weight {override.get('line_weight')}")
    if not report["containers"]:
        lines.append("- none")
    lines += ["", "## Prepare for Handover", ""]
    if "planned_removals" in h and h.get("filed_total"):
        alongside = ", ".join(f"{name} x{count}" for name, count in sorted((h.get("deleted_alongside_by_class") or {}).items())) or "nothing"
        lines.append(
            f"{_mark(h)}: committed {h.get('committed')}; removed {h.get('filed_deleted')}/{h.get('filed_total')} filed elements, "
            f"{h.get('filed_left')} left; Delete also took {alongside}; containers left {h.get('containers_left')}; "
            f"credit names OpenStreetMap {h.get('credit_names_openstreetmap')}; \"{h.get('credit_line')}\""
        )
    else:
        lines.append(f"FAIL: {h.get('note')}")
    return "\n".join(lines) + "\n"


def main(argv):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("run_dir")
    parser.add_argument("--footprints")
    parser.add_argument("--table", default="Building")
    parser.add_argument("--out")
    args = parser.parse_args(argv)
    report = judge(args.run_dir, args.footprints, args.table)
    out = args.out or os.path.join(args.run_dir, "verify.md")
    with open(out, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(markdown(report))
    with open(os.path.splitext(out)[0] + ".report.json", "w", encoding="utf-8", newline="\n") as handle:
        json.dump(report, handle, indent=2)
    print(markdown(report))
    checks = ("placement", "frame", "link", "handover")
    return 0 if all(report[name].get("pass") for name in checks) else 1


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
