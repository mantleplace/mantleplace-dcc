# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""`mpb validate <zip or folder>` and `mpb corpus <corpus dir>`.

Exit 0: conformant. Exit 1: not conformant (each reason is in the report). Exit 2: could not
check: the input is unreadable, or the schema it declares could not be fetched or trusted.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from . import __version__
from .corpus import FORMS, CorpusError, run_corpus
from .report import EXIT_ERROR, EXIT_FAIL, EXIT_PASS, Report
from .schemas import DEFAULT_BASE_URL, SchemaSource
from .validate import validate


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="mpb", description="Validate Mantle Place Bundles (MPB).")
    parser.add_argument("--version", action="version", version=f"mantleplace-mpb {__version__}")
    commands = parser.add_subparsers(dest="command", required=True)

    def schema_options(command: argparse.ArgumentParser) -> None:
        command.add_argument("--schema-dir", type=Path, default=None,
                             help="read <version>.json and frozen.lock.json from this directory "
                                  "instead of fetching the published series")
        command.add_argument("--schema-base-url", default=DEFAULT_BASE_URL,
                             help="where the published schema series is served "
                                  f"(default {DEFAULT_BASE_URL})")
        command.add_argument("--timeout", type=float, default=20.0,
                             help="seconds to wait for each schema fetch")
        command.add_argument("--staged", action="store_true",
                             help="hold a manifest that declares a staged version (served for "
                                  "readers, not yet in the freeze ledger) to the served schema, "
                                  "instead of failing it as unpublished")

    check = commands.add_parser("validate", help="check one bundle: a zip, a folder or a wrapper "
                                                 "folder")
    check.add_argument("bundle", type=Path)
    check.add_argument("--json", action="store_true", help="print the machine-readable report")
    check.add_argument("--strict", action="store_true", help="fail on warnings too")
    schema_options(check)

    corpus = commands.add_parser("corpus", help="run the validator over the conformance corpus's "
                                                "bundle group")
    corpus.add_argument("corpus", type=Path)
    schema_options(corpus)
    return parser


def _text(report: Report) -> str:
    version = report.manifest_version if report.manifest_version is not None else "?"
    lines = [f"{report.verdict.upper()}  {report.input}  ({report.form or 'unread'}, "
             f"manifest version {version})"]
    for finding in report.findings:
        where = " ".join(x for x in (finding.pointer, finding.file) if x)
        lines.append(f"  {finding.severity:<7} {finding.code:<36} {where}")
        lines.append(f"          {finding.message}")
    checks = ", ".join(f"{name} {report.check_status(name)}" for name in
                       ("schema", "pointers", "sha256", "frames", "roles", "names"))
    lines.append(f"checks: {checks}")
    h = report.hashes
    lines.append(f"sha256: {h['verified']} verified, {h['mismatched']} mismatched, "
                 f"{h['unknown']} unknown")
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    args = _parser().parse_args(argv)
    schemas = SchemaSource(schema_dir=args.schema_dir, base_url=args.schema_base_url,
                           timeout=args.timeout, staged=args.staged)
    if args.command == "validate":
        report = validate(args.bundle, schemas=schemas, strict=args.strict)
        print(json.dumps(report.to_dict(), indent=2) if args.json else _text(report))
        return report.exit_code
    try:
        count, failures = run_corpus(args.corpus, schemas)
    except CorpusError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return EXIT_ERROR
    for line in failures:
        print(f"FAIL: {line}")
    if failures:
        print(f"{len(failures)} failure(s) over {count} case(s).")
        return EXIT_FAIL
    print(f"OK: {count} case(s), each as {', '.join(FORMS)}.")
    return EXIT_PASS
