# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""The published manifest schema series: which document a manifest is held to, and fetching it.

A manifest is validated against the schema of the version it **declares**, never against the
newest one. The series is served at `<base>/<key>.json`, where the key is `vN` for the integer
pre-history and the bare semver string for the MPB era, and beside it the append-only freeze
ledger `frozen.lock.json` lists the sha256 of every published version. The ledger answers "is this
version published at all", and it is checked against the bytes fetched, so a schema that is not
the published one is refused rather than trusted.

`schema_dir` reads the same two kinds of file from a local directory, for an offline run.
"""

from __future__ import annotations

import hashlib
import json
import re
import urllib.error
import urllib.request
from pathlib import Path

DEFAULT_BASE_URL = "https://mantle.place/.well-known/schemas/bundle-manifest"
LEDGER_NAME = "frozen.lock.json"
_SEMVER = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")
_USER_AGENT = "mantleplace-mpb"


class SchemaProblem(Exception):
    """A schema could not be had. `cannot_check` separates "the bundle is wrong" from "we could
    not find out"."""

    def __init__(self, code: str, message: str, cannot_check: bool) -> None:
        super().__init__(message)
        self.code = code
        self.cannot_check = cannot_check


def version_key(value: object) -> str | None:
    """`19` (a JSON number) -> `v19`; `"1.14.0"` (a JSON string) -> `1.14.0`; anything else None.

    The JSON type tells the two version families apart, so `"19"`, `"1.14"`, a `v` prefix and a
    pre-release tag are all not versions.
    """
    if isinstance(value, bool):
        return None
    if isinstance(value, int) and value >= 0:
        return f"v{value}"
    if isinstance(value, str) and _SEMVER.match(value):
        return value
    return None


class SchemaSource:
    def __init__(self, schema_dir: str | Path | None = None, base_url: str = DEFAULT_BASE_URL,
                 timeout: float = 20.0) -> None:
        self.schema_dir = Path(schema_dir) if schema_dir is not None else None
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout
        self._ledger: dict[str, str] | None = None
        self._ledger_read = False
        self._schemas: dict[str, dict] = {}

    def url(self, key: str) -> str:
        if self.schema_dir is not None:
            return (self.schema_dir / f"{key}.json").as_posix()
        return f"{self.base_url}/{key}.json"

    def ledger(self) -> dict[str, str] | None:
        """The freeze ledger, or None where an offline directory carries none."""
        if not self._ledger_read:
            raw = self._get(LEDGER_NAME, optional=self.schema_dir is not None)
            if raw is not None:
                try:
                    frozen = json.loads(raw.decode("utf-8"))["frozen"]
                    if not isinstance(frozen, dict):
                        raise TypeError("`frozen` is not an object")
                except (ValueError, KeyError, TypeError) as exc:
                    raise SchemaProblem("schema.unavailable",
                                        f"the freeze ledger is not readable: {exc}", True) from exc
                self._ledger = {str(k): str(v) for k, v in frozen.items()}
            self._ledger_read = True
        return self._ledger

    def load(self, key: str) -> dict:
        if key in self._schemas:
            return self._schemas[key]
        ledger = self.ledger()
        if ledger is not None and key not in ledger:
            raise SchemaProblem("schema.version-unpublished",
                                f"version {key} is not in the published freeze ledger", False)
        raw = self._get(f"{key}.json", optional=False, missing_is_unpublished=True)
        assert raw is not None
        if ledger is not None and hashlib.sha256(raw).hexdigest() != ledger[key].lower():
            raise SchemaProblem("schema.ledger-mismatch",
                                f"the schema read for {key} does not match the sha256 the freeze "
                                "ledger records for it; it is not the published document", True)
        try:
            schema = json.loads(raw.decode("utf-8"))
        except ValueError as exc:
            raise SchemaProblem("schema.unavailable", f"the schema for {key} is not JSON: {exc}",
                                True) from exc
        self._schemas[key] = schema
        return schema

    def _get(self, name: str, optional: bool, missing_is_unpublished: bool = False) -> bytes | None:
        if self.schema_dir is not None:
            path = self.schema_dir / name
            if path.is_file():
                return path.read_bytes()
            if optional:
                return None
            if missing_is_unpublished:
                raise SchemaProblem("schema.version-unpublished",
                                    f"{path} does not exist", False)
            raise SchemaProblem("schema.unavailable", f"{path} does not exist", True)
        url = f"{self.base_url}/{name}"
        request = urllib.request.Request(url, headers={"User-Agent": _USER_AGENT})
        try:
            with urllib.request.urlopen(request, timeout=self.timeout) as response:
                return response.read()
        except urllib.error.HTTPError as exc:
            if exc.code == 404 and missing_is_unpublished:
                raise SchemaProblem("schema.version-unpublished", f"{url} is not served (404)",
                                    False) from exc
            raise SchemaProblem("schema.unavailable", f"{url} returned HTTP {exc.code}",
                                True) from exc
        except (urllib.error.URLError, OSError) as exc:
            raise SchemaProblem("schema.unavailable", f"{url} could not be fetched: {exc}",
                                True) from exc
