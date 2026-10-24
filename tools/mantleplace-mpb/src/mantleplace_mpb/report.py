# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""What a validation run found, and the verdict and exit code that follow from it."""

from __future__ import annotations

from dataclasses import dataclass, field

#: The checks, in the order a run makes them and a report lists them.
CHECKS = ("archive", "manifest", "schema", "pointers", "sha256", "frames", "roles", "names")

SEVERITIES = ("error", "warning", "info")

#: Exit codes. 0 conformant, 1 not conformant, 2 could not check (an unreadable input, an
#: unreachable or untrustworthy schema): the same three the repository's conformance gate uses.
EXIT_PASS, EXIT_FAIL, EXIT_ERROR = 0, 1, 2


@dataclass(frozen=True)
class Finding:
    check: str
    code: str
    severity: str
    message: str
    pointer: str | None = None
    file: str | None = None

    def to_dict(self) -> dict:
        return {"check": self.check, "code": self.code, "severity": self.severity,
                "message": self.message, "pointer": self.pointer, "file": self.file}


@dataclass
class Report:
    input: str
    strict: bool = False
    form: str | None = None
    root: str | None = None
    manifest_path: str | None = None
    manifest_version: object = None
    schema_url: str | None = None
    findings: list[Finding] = field(default_factory=list)
    hashes: dict[str, int] = field(default_factory=lambda: {"verified": 0, "mismatched": 0,
                                                             "unknown": 0})
    #: Set when a check could not be made at all; the verdict is then "error", never "pass".
    incomplete: bool = False
    ran: list[str] = field(default_factory=list)

    def add(self, check: str, code: str, severity: str, message: str, *, pointer: str | None = None,
            file: str | None = None) -> None:
        self.findings.append(Finding(check, code, severity, message, pointer, file))

    def cannot_check(self, check: str, code: str, message: str) -> None:
        self.incomplete = True
        self.add(check, code, "error", message)

    def count(self, severity: str) -> int:
        return sum(1 for f in self.findings if f.severity == severity)

    @property
    def verdict(self) -> str:
        if self.incomplete:
            return "error"
        if self.count("error") or (self.strict and self.count("warning")):
            return "fail"
        return "pass"

    @property
    def exit_code(self) -> int:
        return {"pass": EXIT_PASS, "fail": EXIT_FAIL, "error": EXIT_ERROR}[self.verdict]

    def check_status(self, check: str) -> str:
        if check not in self.ran:
            return "skipped"
        if any(f.check == check and f.severity == "error" for f in self.findings):
            return "fail"
        return "pass"

    def to_dict(self) -> dict:
        from . import __version__

        return {
            "validator": f"mantleplace-mpb {__version__}",
            "input": self.input,
            "form": self.form,
            "root": self.root,
            "manifest": self.manifest_path,
            "manifestVersion": self.manifest_version,
            "schema": self.schema_url,
            "strict": self.strict,
            "verdict": self.verdict,
            "exitCode": self.exit_code,
            "counts": {s: self.count(s) for s in SEVERITIES},
            "checks": {c: self.check_status(c) for c in CHECKS},
            "hashes": dict(self.hashes),
            "findings": [f.to_dict() for f in self.findings],
        }
