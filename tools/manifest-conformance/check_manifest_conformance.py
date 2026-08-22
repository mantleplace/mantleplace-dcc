"""Consumer-conformance against the canonical PUBLISHED bundle-manifest schema.

The Mantle Place platform runs the *producer* leg of this gate: it asserts its live packager emits
manifests that validate against the canonical schema, and that the schema's `version.const` matches
the producer's manifest version.

This is the *consumer* leg. It deliberately does **not** duplicate the schema — it fetches the
published copy from `https://mantle.place/.well-known/schemas/bundle-manifest/`, which is the
artifact the Mantle Place platform publishes, served publicly. One contract, one source, checked
from both ends.

**Two version families.** The manifest contract has an integer pre-history (`version: 19`, published
at `v19.json`) and a semver era (`version: "1.0.0"`, published at `1.0.0.json` — no `v` prefix; the
`v` belonged to the integer era). Consumers tell them apart by JSON type. Both families are served
forever, so this gate reads both, orders them (the whole integer era precedes the whole semver era,
it being pre-history by definition), and holds a host to whichever one it pins.

What it catches: the platform ships a new manifest version and a host consumer has never been
verified against that shape. This is the gate that scales as hosts go from one to four — each new
DCC host adds its own entry to `verified-against.json` and is held to the same line.

**N hosts, one gate.** Nothing here knows what a host is written in. Every host declares, in its own
`verified-against.json` entry, where its version floor lives (`floorSource.path` +
`floorSource.pattern`) and which test file a reviewer should touch when the pin moves
(`tests`). A C++ header, a C# constant and a Python module are all just a file and a regex
(`HPS-39`).

It also checks the shared conformance corpus (`corpus/`) for integrity, offline: every case the
index names must exist, parse, and agree with the index about its declared manifest version. Host
test suites consume the same corpus (`HPS-40`), so a corpus that has rotted breaks every host at
once and should break here first.

Two further offline legs:

- **Self-test corpus** (`corpus/self-test/`, `HPS-46`): the deliberately broken fixtures every
  host READER must reject. This gate verifies the set is *well-formed-broken* — each fixture wrong
  in exactly its declared way — so a host suite that trusts it is trusting something true.
- **Coverage ratchet** (`coverage-baseline.json`): per-case host coverage is derived from each
  host's claimed `groups` × the case's `appliesTo`, printed on every run, and compared against the
  committed baseline. Any difference fails; `--update-baseline` records a deliberate change as a
  reviewable diff. Coverage drift surfaced in review once; it surfaces at the gate now.

Exit 0 == conformant. Exit 1 == drift. Exit 2 == could not check (network/parse).
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import urllib.error
import urllib.request
from pathlib import Path

SCHEMA_BASE_URL = "https://mantle.place/.well-known/schemas/bundle-manifest"
SCHEMA_URL = SCHEMA_BASE_URL + "/{version}.json"

#: The platform's append-only freeze ledger, served beside the schemas: one sha256 entry per
#: PUBLISHED version, the current one included. It is the published index of what exists.
#:
#: This is how "is anything newer published?" is answered. The integer era could be probed by
#: counting (`v19` then `v20`, `v21`, …); semver cannot — nothing about `1.0.0` tells you whether
#: the next release is `1.0.1`, `1.1.0` or `2.0.0`, and a walk that guesses one axis silently
#: misses the others. A missed bump is exactly the silent failure this gate exists to prevent, so
#: it reads the index rather than guessing at it.
LEDGER_URL = SCHEMA_BASE_URL + "/frozen.lock.json"

_SEMVER_RE = re.compile(r"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$")
_INTEGER_KEY_RE = re.compile(r"^v(\d+)$")

_REPO_ROOT = Path(__file__).resolve().parents[2]
_HERE = Path(__file__).resolve().parent
_PINNED = _HERE / "verified-against.json"
_CORPUS = _HERE / "corpus"
_BASELINE = _HERE / "coverage-baseline.json"

#: The one-way-broken classes a self-test fixture may declare (HPS-46). Anything else is a typo.
_SELFTEST_CLASSES = frozenset({
    "unknownExpectationKey", "wrongTypeExpectation", "nestedUnreadExpectation", "missingFile",
    "malformedCase", "duplicateId",
})

#: Keys in verified-against.json that are documentation, not hosts.
_NON_HOST_KEYS = frozenset({"$comment"})

#: The documented corpus groups. A typo'd group silently excludes a case from every host suite
#: that dispatches on it, so it is checked rather than trusted.
_CORPUS_GROUPS = frozenset({"manifest", "vault", "auth", "cache", "digest", "projection"})

#: Every host entry must carry these. A host that omits one is a silent hole in the gate, so this
#: is checked before anything is fetched.
_REQUIRED_HOST_FIELDS = ("verifiedAgainstManifestVersion", "verifiedAgainstCorpusVersion",
                         "evidence", "consumer", "floorSource", "tests", "owner", "groups")

_schema_cache: dict[str, dict | None] = {}
_ledger_cache: list[str] | None = None


def version_key(value: object) -> str | None:
    """The published identity of a manifest version const, or None if it is neither family.

    `19` (a JSON number) -> `"v19"`; `"1.0.0"` (a JSON string) -> `"1.0.0"`. The key IS the mirror
    filename stem, so it is also what the URL and the ledger are keyed by. Returns None rather than
    raising: every caller here is reporting drift, not crashing on it.

    An integer-as-string (`"19"`), a partial semver (`"1.0"`) and any pre-release tag are all None
    — published versions carry none of those.
    """
    if isinstance(value, bool):
        return None
    if isinstance(value, int):
        return f"v{value}"
    if isinstance(value, str) and _SEMVER_RE.match(value):
        return value
    return None


def sort_key(key: str) -> tuple[int, tuple[int, ...]]:
    """Total order over version keys.

    The whole integer era precedes the whole semver era — it is pre-history by definition, so a
    leading rank of 0 vs 1 decides it before any component comparison. Within a family, numeric
    component order (so `v7 < v12`, and `1.9.0 < 1.10.0` rather than the lexical opposite).
    """
    integer = _INTEGER_KEY_RE.match(key)
    if integer:
        return (0, (int(integer.group(1)),))
    return (1, tuple(int(part) for part in key.split(".")))


def describe(key: str) -> str:
    """A version key as it should read in a message: `v19`, or `MPB 1.0.0`.

    The integer era's key already carries its own `v` marker; the semver era's does not, and a bare
    `1.0.0` in a sentence about a bundle reads as a plugin version as easily as a manifest one.
    """
    return key if _INTEGER_KEY_RE.match(key) else f"MPB {key}"


def fetch_schema(version: str, timeout: int = 20) -> dict | None:
    """Return the published schema for version key `version`, or None if it is not published.

    Memoized: with N hosts pinned at the same version the lookups overlap heavily, and the weekly
    scheduled run should stay one-cheap-GET-per-version regardless of host count.
    """
    if version in _schema_cache:
        return _schema_cache[version]
    url = SCHEMA_URL.format(version=version)
    req = urllib.request.Request(url, headers={"User-Agent": "mantleplace-dcc-conformance"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            result = json.loads(resp.read().decode("utf-8")) if resp.status == 200 else None
    except urllib.error.HTTPError as exc:
        if exc.code != 404:
            # Anything other than "not published" means we could not CHECK, which is exit 2.
            # Letting it propagate would surface as exit 1 == drift, and a CDN 503 would read
            # as a failed contract rather than a gate that never ran.
            print(f"error: {url} returned HTTP {exc.code}: {exc.reason}", file=sys.stderr)
            raise SystemExit(2)
        result = None
    except urllib.error.URLError as exc:
        print(f"error: cannot reach {url}: {exc}", file=sys.stderr)
        raise SystemExit(2)
    _schema_cache[version] = result
    return result


def fetch_published_versions(timeout: int = 20) -> list[str]:
    """Every published version key, newest last, from the platform's freeze ledger.

    Unreachable or unparseable is exit 2 (could not CHECK), never exit 1 (drift) — the same
    distinction `fetch_schema` draws. A gate that reported "no newer version" because the network
    was down would be silently green in exactly the situation it cannot see.
    """
    global _ledger_cache
    if _ledger_cache is not None:
        return _ledger_cache
    req = urllib.request.Request(LEDGER_URL, headers={"User-Agent": "mantleplace-dcc-conformance"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            if resp.status != 200:
                print(f"error: {LEDGER_URL} returned HTTP {resp.status}", file=sys.stderr)
                raise SystemExit(2)
            ledger = json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as exc:
        print(f"error: {LEDGER_URL} returned HTTP {exc.code}: {exc.reason}", file=sys.stderr)
        raise SystemExit(2)
    except urllib.error.URLError as exc:
        print(f"error: cannot reach {LEDGER_URL}: {exc}", file=sys.stderr)
        raise SystemExit(2)
    except json.JSONDecodeError as exc:
        print(f"error: {LEDGER_URL} is not readable JSON: {exc}", file=sys.stderr)
        raise SystemExit(2)

    frozen = ledger.get("frozen")
    if not isinstance(frozen, dict) or not frozen:
        print(f"error: {LEDGER_URL} carries no 'frozen' map", file=sys.stderr)
        raise SystemExit(2)

    keys: list[str] = []
    for key in frozen:
        if version_key(key) is not None or _INTEGER_KEY_RE.match(key):
            keys.append(key)
        elif not is_documentation_key(key):
            # A frozen key in neither family — a bare "19", a partial "1.0" — may be a published
            # version this gate cannot place in its total order, which means a version it cannot
            # see past. Silently dropping it would turn ledger rot into a green run, which is the
            # exact failure the ledger read exists to prevent — so it is loud, and exit 2 (could
            # not CHECK), never exit 1 (drift).
            print(
                f"error: {LEDGER_URL} 'frozen' names {key!r}, which is not a version key in "
                "either family (integer-era keys read 'v19'; semver keys read '1.0.0'). A key "
                "this gate cannot order may hide a newer published version — refusing to guess.",
                file=sys.stderr,
            )
            raise SystemExit(2)
    if not keys:
        print(f"error: {LEDGER_URL} names no recognizable version", file=sys.stderr)
        raise SystemExit(2)
    _ledger_cache = sorted(keys, key=sort_key)
    return _ledger_cache


def newest_published(start: str) -> str:
    """Highest published version key at or above `start`.

    `start` itself is the floor of the answer: a host pinned above everything the ledger names is
    reported by the pin check, not silently downgraded here.
    """
    published = [key for key in fetch_published_versions() if sort_key(key) > sort_key(start)]
    return published[-1] if published else start


def read_declared_floor(host: str, entry: dict) -> tuple[str | None, Path, str]:
    """Parse a host's minimum-supported manifest version out of the file the host declares.

    Returns `(floor, resolved_path, pattern)` where `floor` is a version KEY; it is None when the
    file is missing, the pattern does not match, or the captured text is not a version in either
    family — all of which the caller reports as drift.

    The capture is read as source text, so it arrives as a string either way: a semver floor
    captures as `1.0.0` and is already its own key, while an integer floor captures as `18` and
    becomes `v18`. That asymmetry lives here so a host's `floorSource.pattern` stays a plain regex
    over its own constant and never has to encode which era it is in.

    Deliberately a regex over source rather than a build-time export: this check must run on a cheap
    hosted runner with no Unreal Engine, no Revit and no Blender present. The cost of that choice is
    that a host which moves or renames its floor constant must update its own entry here — which is
    exactly the failure this gate is meant to make loud.
    """
    source = entry["floorSource"]
    path = (_REPO_ROOT / source["path"]).resolve()
    pattern = source["pattern"]
    if not path.is_file():
        return None, path, pattern
    text = path.read_text(encoding="utf-8", errors="replace")
    match = re.search(pattern, text)
    if match is None or not match.groups():
        return None, path, pattern
    captured = match.group(1)
    if _SEMVER_RE.match(captured):
        return captured, path, pattern
    if captured.isdigit():
        return version_key(int(captured)), path, pattern
    return None, path, pattern


def host_entries(pinned_doc: dict) -> list[tuple[str, dict]]:
    """Every host key in `verified-against.json`, in a stable order."""
    return [(k, v) for k, v in sorted(pinned_doc.items()) if k not in _NON_HOST_KEYS]


def read_corpus_version() -> int | None:
    """The corpus's own top-level `corpusVersion`, or None if unreadable.

    Distinct from `manifestVersion`: this tracks the shape of the corpus's case fields, not the
    bundle-manifest contract. A host whose `verifiedAgainstCorpusVersion` trails this is a host
    that has not reviewed a corpus-shape change — exactly the drift `check_host` reports.
    """
    index_path = _CORPUS / "index.json"
    try:
        index = json.loads(index_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None
    version = index.get("corpusVersion")
    return int(version) if isinstance(version, int) else None


def check_corpus(entries: list[tuple[str, dict]] | None = None,
                 update_baseline: bool = False) -> list[str]:
    """Offline integrity check of the shared conformance corpus. Returns failure lines.

    With `entries` (the FULL host roster, never a `--host`-filtered slice), also validates the
    self-test corpus and computes the per-case coverage report + ratchet. Bare calls keep the
    integrity-only behaviour so a synthetic corpus can be checked without a host roster.
    """
    failures: list[str] = []
    index_path = _CORPUS / "index.json"
    if not index_path.is_file():
        return [f"FAIL: conformance corpus index missing at {index_path}."]

    try:
        index = json.loads(index_path.read_text(encoding="utf-8"))
        raw_cases = index["cases"]
    except (json.JSONDecodeError, KeyError) as exc:
        return [f"FAIL: {index_path} is not a readable corpus index: {exc}"]

    seen: set[str] = set()
    for position, case in enumerate(raw_cases):
        missing = [f for f in ("id", "group", "file", "expect", "reason") if not case.get(f)]
        if missing:
            failures.append(
                f"FAIL: corpus case at index {position} is missing {', '.join(missing)}. "
                "Every case states what it is and why it exists."
            )
            continue
        if case["group"] not in _CORPUS_GROUPS:
            failures.append(
                f"FAIL: corpus case '{case['id']}' has group={case['group']!r}; "
                f"expected one of {', '.join(sorted(_CORPUS_GROUPS))}."
            )
        case_id = case["id"]
        if case_id in seen:
            failures.append(f"FAIL: corpus case id '{case_id}' is declared twice in index.json.")
        seen.add(case_id)

        case_path = _CORPUS / case["file"]
        if not case_path.is_file():
            failures.append(f"FAIL: corpus case '{case_id}' names a missing file: {case['file']}.")
            continue
        try:
            body = json.loads(case_path.read_text(encoding="utf-8"))
        except json.JSONDecodeError as exc:
            if case.get("malformedJson"):
                continue  # A case whose whole point is to be unparseable.
            failures.append(f"FAIL: corpus case '{case_id}' ({case['file']}) is not valid JSON: {exc}")
            continue

        declared = case.get("manifestVersion")
        actual = body.get("version") if isinstance(body, dict) else None
        if declared is not None and actual != declared:
            failures.append(
                f"FAIL: corpus case '{case_id}' declares manifestVersion={declared} but "
                f"{case['file']} carries version={actual!r}."
            )
        if case["expect"] not in ("accept", "reject", "vector"):
            failures.append(
                f"FAIL: corpus case '{case_id}' has expect={case['expect']!r}; "
                "only 'accept', 'reject' and 'vector' are meaningful to a host suite."
            )

    # `self-test/` is excluded from THIS sweep on purpose: it is a nested corpus with its own
    # index.json, and `check_selftest` runs its own recursive sweep over it — one that honours the
    # index's `orphanFiles` declaration, which this sweep would misreport as rot (the deliberate
    # orphan fixture is declared, not forgotten).
    orphans = sorted(
        p.relative_to(_CORPUS).as_posix()
        for p in _CORPUS.rglob("*")
        if p.is_file() and p != index_path and p.name != "README.md"
        and not p.relative_to(_CORPUS).as_posix().startswith("self-test/")
        and p.relative_to(_CORPUS).as_posix() not in {c["file"] for c in raw_cases if c.get("file")}
    )
    for orphan in orphans:
        failures.append(
            f"FAIL: corpus file '{orphan}' is not listed in index.json. Host suites iterate the "
            "index, so an unlisted case is a test nobody runs."
        )

    failures.extend(check_selftest(required=entries is not None))
    if entries is not None:
        # Never record a baseline from a corpus that does not pass its own integrity checks: a
        # case dropped for a bad `appliesTo` would be written out MISSING and become permanently
        # invisible to the ratchet — a silent hole in the very file that exists to catch holes.
        if update_baseline and failures:
            failures.append(
                "FAIL: refusing to write coverage-baseline.json while the corpus has integrity "
                "failures. Fix the failures above, then rerun with --update-baseline."
            )
            return failures
        failures.extend(check_coverage(raw_cases, entries, update_baseline))
    return failures


def is_documentation_key(key: str) -> bool:
    """Whether `key` is prose for a human rather than an assertion for a host (HPS-46).

    The convention, at every depth: `$comment`, or a name ending in `Note`.
    """
    return key == "$comment" or key.endswith("Note")


def nested_expectation_entries(expectations: dict) -> list[tuple[str, str, object]]:
    """Every `(path, key, value)` BELOW the top level of an `expectations` object (HPS-46b).

    The top level is `HPS-46`'s and is excluded. Paths follow `HPS-46a`'s precedent
    (`items[1].hasManifestVersion`) so a failure names something actionable. A documentation key
    is yielded — the caller decides what that means — but its subtree is not walked, because
    prose is exempt along with everything under it.
    """
    found: list[tuple[str, str, object]] = []

    def walk(value: object, path: str, depth: int) -> None:
        if isinstance(value, dict):
            for key, child in value.items():
                child_path = f"{path}.{key}" if path else key
                if depth > 0:
                    found.append((child_path, key, child))
                if not is_documentation_key(key):
                    walk(child, child_path, depth + 1)
        elif isinstance(value, list):
            for index, child in enumerate(value):
                walk(child, f"{path}[{index}]", depth + 1)

    walk(expectations, "", 0)
    return found


def _corpus_manifest_version() -> object | None:
    """The corpus proper's top-level `manifestVersion`, or None if unreadable or absent.

    Kept verbatim (int for the pre-history, string for the MPB era) so the self-test cross-check
    compares JSON values, never coerced ones — coercion across the era break is the reader bug the
    whole gate exists to catch.
    """
    try:
        index = json.loads((_CORPUS / "index.json").read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None
    return index.get("manifestVersion")


def check_selftest(required: bool = True) -> list[str]:
    """Verify `corpus/self-test/` is *well-formed-broken*: each fixture wrong in exactly its
    declared way (HPS-46). A host suite asserts these fixtures are rejected; that assertion is
    only worth anything if the breakage it detects is really there.
    """
    selftest = _CORPUS / "self-test"
    index_path = selftest / "index.json"
    if not index_path.is_file():
        if not required:
            return []
        return [f"FAIL: self-test corpus index missing at {index_path} (HPS-46)."]

    try:
        index = json.loads(index_path.read_text(encoding="utf-8"))
        cases = index["cases"]
    except (json.JSONDecodeError, KeyError) as exc:
        return [f"FAIL: {index_path} is not a readable self-test index: {exc}"]

    failures: list[str] = []

    # The self-test fixtures must be written in the dialect the host readers actually speak, or an
    # `expect: accept` fixture is refused on the VERSION GATE and its self-test passes for the
    # wrong reason — the reader never reaches the breakage the fixture declares. The corpus
    # proper's `manifestVersion` is the pin the readers are held to, so the self-test index is
    # cross-checked against it whenever both declare one.
    corpus_pin = _corpus_manifest_version()
    selftest_pin = index.get("manifestVersion")
    if corpus_pin is not None and selftest_pin is not None and selftest_pin != corpus_pin:
        failures.append(
            f"FAIL: self-test index declares manifestVersion={selftest_pin!r} but the corpus "
            f"proper is pinned at {corpus_pin!r}. Fixtures in a dialect the readers refuse make "
            "every accept-shaped self-test pass on the version gate instead of on its declared "
            "breakage (HPS-46)."
        )
    id_counts: dict[str, int] = {}
    for case in cases:
        id_counts[case.get("id", "")] = id_counts.get(case.get("id", ""), 0) + 1

    listed_files = {c.get("file") for c in cases}
    for case in cases:
        case_id = case.get("id", "<no id>")
        declared = case.get("selfTestFailure")
        path = selftest / case.get("file", "")

        if declared not in _SELFTEST_CLASSES:
            failures.append(
                f"FAIL: self-test case '{case_id}' declares selfTestFailure={declared!r}; "
                f"expected one of {', '.join(sorted(_SELFTEST_CLASSES))}."
            )
            continue

        parsed: dict | None = None
        parse_error = False
        if path.is_file():
            try:
                parsed = json.loads(path.read_text(encoding="utf-8"))
            except json.JSONDecodeError:
                parse_error = True

        # The same declared-vs-actual cross-check the corpus proper gets in `check_corpus`: a
        # self-test case whose `manifestVersion` disagrees with its fixture's own `version` is rot,
        # and rot here is worse than in the corpus proper — the fixture's breakage stops being the
        # reason the reader rejects it.
        declared_version = case.get("manifestVersion")
        if declared_version is not None and isinstance(parsed, dict) \
                and parsed.get("version") != declared_version:
            failures.append(
                f"FAIL: self-test case '{case_id}' declares manifestVersion={declared_version!r} "
                f"but {case.get('file')} carries version={parsed.get('version')!r}."
            )

        if declared == "missingFile":
            if path.is_file():
                failures.append(
                    f"FAIL: self-test case '{case_id}' declares missingFile but {case['file']} "
                    "exists — the fixture is not broken the way it claims."
                )
        elif declared == "malformedCase":
            if not path.is_file() or not parse_error:
                failures.append(
                    f"FAIL: self-test case '{case_id}' declares malformedCase but {case['file']} "
                    "is missing or parses cleanly."
                )
            if case.get("malformedJson"):
                failures.append(
                    f"FAIL: self-test case '{case_id}' declares BOTH malformedCase and "
                    "malformedJson — declared malformation is data, not a reader failure."
                )
        elif declared == "duplicateId":
            if id_counts.get(case_id, 0) < 2:
                failures.append(
                    f"FAIL: self-test case '{case_id}' declares duplicateId but the id appears "
                    "only once."
                )
            if not path.is_file() or parse_error or parsed is None:
                failures.append(
                    f"FAIL: self-test case '{case_id}' ({case['file']}) must itself be a valid "
                    "case file — the duplication is the only intended breakage."
                )
        elif declared == "unknownExpectationKey":
            keys = (case.get("expectations") or {}).keys()
            if not any(k.startswith("selfTest") for k in keys):
                failures.append(
                    f"FAIL: self-test case '{case_id}' declares unknownExpectationKey but no "
                    "expectations key uses the reserved `selfTest` prefix."
                )
            if not path.is_file() or parse_error:
                failures.append(
                    f"FAIL: self-test case '{case_id}' ({case['file']}) must be a valid case "
                    "file — the unknown key is the only intended breakage."
                )
        elif declared == "wrongTypeExpectation":
            order_id = (case.get("expectations") or {}).get("orderId")
            if order_id is None or isinstance(order_id, str):
                failures.append(
                    f"FAIL: self-test case '{case_id}' declares wrongTypeExpectation but "
                    "expectations.orderId is absent or already a string."
                )
            if not path.is_file() or parse_error:
                failures.append(
                    f"FAIL: self-test case '{case_id}' ({case['file']}) must be a valid case "
                    "file — the wrong-typed expectation is the only intended breakage."
                )
        elif declared == "nestedUnreadExpectation":
            expectations = case.get("expectations") or {}
            nested = nested_expectation_entries(expectations)
            if not any(isinstance(v, (dict, list)) for v in expectations.values()):
                failures.append(
                    f"FAIL: self-test case '{case_id}' declares nestedUnreadExpectation but no "
                    "top-level expectation holds a container — the breakage must sit BELOW a key "
                    "the reader can assert, or the top-level rule catches it first."
                )
            # A `*Note` key IS documentation and exempt, so it can never be the breakage. Without
            # this the fixture would still pass with its only reserved key renamed to prose.
            if not any(k.startswith("selfTest") and not is_documentation_key(k)
                       for _, k, _ in nested):
                failures.append(
                    f"FAIL: self-test case '{case_id}' declares nestedUnreadExpectation but no "
                    "NESTED key uses the reserved `selfTest` prefix (a documentation key cannot "
                    "be the breakage — it is exempt at every depth)."
                )
            if not any(k == "status" and not isinstance(v, str) for _, k, v in nested):
                failures.append(
                    f"FAIL: self-test case '{case_id}' declares nestedUnreadExpectation but no "
                    "nested `status` is declared with a non-string type — the coercion half is "
                    "missing, and a host reading it through a coercing accessor would pass."
                )
            if not path.is_file() or parse_error:
                failures.append(
                    f"FAIL: self-test case '{case_id}' ({case['file']}) must be a valid case "
                    "file — the unread nested keys are the only intended breakage."
                )

    for orphan in index.get("orphanFiles", []):
        if not (selftest / orphan).is_file():
            failures.append(
                f"FAIL: self-test orphanFiles names '{orphan}' but it does not exist on disk."
            )
        if orphan in listed_files:
            failures.append(
                f"FAIL: self-test '{orphan}' is declared an orphan AND listed as a case file — "
                "it cannot be both."
            )

    # Recursive, and skipping any subdirectory that carries its own index.json (a nested corpus:
    # the broken-index-* dirs). The main sweep excludes this whole subtree, so anything shallower
    # than the host readers' own sweep is a hole they would report and this gate would not —
    # which is the asymmetry HPS-46 exists to prevent, inverted.
    declared_orphans = set(index.get("orphanFiles", []))
    for path in sorted(selftest.rglob("*")):
        if not path.is_file():
            continue
        rel = path.relative_to(selftest).as_posix()
        if rel == "index.json":
            continue
        parts = rel.split("/")
        if any((selftest.joinpath(*parts[:depth]) / "index.json").is_file()
               for depth in range(1, len(parts))):
            continue  # inside a nested corpus — not this index's to sweep
        if rel not in listed_files and rel not in declared_orphans:
            failures.append(
                f"FAIL: self-test file '{rel}' is neither listed as a case nor declared in "
                "orphanFiles — an UNdeclared orphan is rot, not a fixture."
            )

    broken_json = selftest / "broken-index-json" / "index.json"
    if not broken_json.is_file():
        failures.append(f"FAIL: self-test broken-index-json/index.json is missing.")
    else:
        try:
            json.loads(broken_json.read_text(encoding="utf-8"))
            failures.append(
                "FAIL: self-test broken-index-json/index.json parses cleanly — it exists to be "
                "unparseable."
            )
        except json.JSONDecodeError:
            pass

    broken_schema = selftest / "broken-index-schema" / "index.json"
    if not broken_schema.is_file():
        failures.append(f"FAIL: self-test broken-index-schema/index.json is missing.")
    else:
        try:
            doc = json.loads(broken_schema.read_text(encoding="utf-8"))
            if "cases" in doc:
                failures.append(
                    "FAIL: self-test broken-index-schema/index.json carries a `cases` key — it "
                    "exists to be a parseable non-index."
                )
        except json.JSONDecodeError:
            failures.append(
                "FAIL: self-test broken-index-schema/index.json does not parse — that is "
                "broken-index-json's job; this one must be parseable and schema-broken."
            )

    return failures


def check_coverage(raw_cases: list[dict], entries: list[tuple[str, dict]],
                   update_baseline: bool = False) -> list[str]:
    """Derive per-case host coverage from claimed `groups` × `appliesTo`, print it, and ratchet
    it against the committed baseline.

    The derivation is mechanical on purpose: a host covers a case iff the case's group is in the
    host's claimed `groups` and the case is not scoped to another host. HPS-41 makes both inputs
    binding (a claimed group is run in full; `appliesTo` scopes a case to one host), so the gate
    can compute coverage instead of trusting a review to notice it.
    """
    failures: list[str] = []
    hosts: dict[str, set[str]] = {}
    for host, entry in entries:
        groups = entry.get("groups")
        if not isinstance(groups, list) or not groups:
            failures.append(
                f"FAIL [{host}]: `groups` must be a non-empty list of claimed corpus groups "
                "(HPS-41); coverage cannot be derived without it."
            )
            continue
        unknown = set(groups) - _CORPUS_GROUPS
        if unknown:
            failures.append(
                f"FAIL [{host}]: `groups` names unknown corpus group(s): "
                f"{', '.join(sorted(unknown))}."
            )
        hosts[host] = set(groups)
    if failures:
        return failures

    coverage: dict[str, list[str]] = {}
    group_totals: dict[str, int] = {}
    group_covered: dict[str, dict[str, int]] = {}
    for case in raw_cases:
        case_id, group = case.get("id"), case.get("group")
        if not case_id or group not in _CORPUS_GROUPS:
            continue  # integrity failures already reported by check_corpus
        applies_to = case.get("appliesTo")
        if applies_to is not None and applies_to not in hosts:
            failures.append(
                f"FAIL: corpus case '{case_id}' has appliesTo={applies_to!r}, which is not a "
                "registered host. A scoped case must name a real entry in verified-against.json."
            )
            continue
        covered = sorted(
            host for host, groups in hosts.items()
            if group in groups and applies_to in (None, host)
        )
        coverage[case_id] = covered
        group_totals[group] = group_totals.get(group, 0) + 1
        for host in covered:
            group_covered.setdefault(group, {}).setdefault(host, 0)
            group_covered[group][host] += 1

    all_hosts = sorted(hosts)
    full = sum(1 for v in coverage.values() if v == all_hosts)
    print(f"corpus coverage: {full}/{len(coverage)} cases asserted by every registered host "
          f"({', '.join(all_hosts)}).")
    for group in sorted(group_totals):
        per_host = ", ".join(
            f"{host} {group_covered.get(group, {}).get(host, 0)}/{group_totals[group]}"
            for host in all_hosts
        )
        print(f"    {group}: {per_host}")
    for host in all_hosts:
        unclaimed = sorted(_CORPUS_GROUPS - hosts[host])
        if unclaimed:
            print(f"    {host} does not claim: {', '.join(unclaimed)}")

    if update_baseline:
        if failures:
            # A case dropped here (an `appliesTo` naming no registered host) would be written out
            # missing, not wrong — and a missing case is one the ratchet can never regress.
            failures.append(
                "FAIL: refusing to write coverage-baseline.json while cases fail coverage "
                "derivation. Fix the failures above, then rerun with --update-baseline."
            )
            return failures
        _BASELINE.write_text(
            json.dumps(
                {
                    "$comment": [
                        "Per-case host coverage, derived by check_manifest_conformance.py from",
                        "each host's claimed `groups` and each case's `appliesTo`. The",
                        "gate fails on ANY difference from this file — a host losing a case is a",
                        "regression, a new covered case is an unrecorded improvement. Both are",
                        "recorded deliberately: rerun with --update-baseline and commit the diff.",
                        "Maintainer-owned: propose changes by pull request.",
                    ],
                    "cases": {k: coverage[k] for k in sorted(coverage)},
                },
                indent=2,
            ) + "\n",
            encoding="utf-8",
        )
        print(f"coverage baseline written: {_BASELINE.name} ({len(coverage)} cases).")
        return failures

    if not _BASELINE.is_file():
        failures.append(
            f"FAIL: coverage baseline missing at {_BASELINE}. Run with --update-baseline and "
            "commit the file."
        )
        return failures
    try:
        baseline = json.loads(_BASELINE.read_text(encoding="utf-8"))["cases"]
    except (json.JSONDecodeError, KeyError) as exc:
        failures.append(f"FAIL: {_BASELINE} is not a readable coverage baseline: {exc}")
        return failures

    for case_id, recorded in sorted(baseline.items()):
        if case_id not in coverage:
            failures.append(
                f"FAIL: coverage regression — case '{case_id}' is in coverage-baseline.json but "
                "no longer in the corpus. If its removal was deliberate, rerun with "
                "--update-baseline and commit the diff."
            )
            continue
        lost = sorted(set(recorded) - set(coverage[case_id]))
        for host in lost:
            failures.append(
                f"FAIL: coverage regression — host '{host}' no longer asserts case '{case_id}' "
                "(recorded in coverage-baseline.json). A host losing a case it once asserted is "
                "drift; if deliberate, rerun with --update-baseline and commit the diff."
            )
    for case_id, covered in sorted(coverage.items()):
        recorded = baseline.get(case_id)
        gained = sorted(set(covered) - set(recorded or []))
        if recorded is None or gained:
            what = "is new" if recorded is None else f"gained {', '.join(gained)}"
            failures.append(
                f"FAIL: coverage-baseline.json is stale — case '{case_id}' {what}. Improvements "
                "are recorded deliberately: rerun with --update-baseline and commit the diff."
            )
    return failures


def check_host(host: str, entry: dict, corpus_version: int | None = None) -> tuple[list[str], int | None]:
    """Verify one host. Returns `(failure lines, pinned version or None)`."""
    failures: list[str] = []

    missing = [f for f in _REQUIRED_HOST_FIELDS if f not in entry]
    if missing:
        return [
            f"FAIL: host '{host}' in verified-against.json is missing {', '.join(missing)}.\n"
            f"      Every host declares its own floor source and tests (HPS-39)."
        ], None

    pinned = version_key(entry["verifiedAgainstManifestVersion"])
    tests = entry["tests"]
    if pinned is None:
        return [
            f"FAIL [{host}]: verifiedAgainstManifestVersion="
            f"{entry['verifiedAgainstManifestVersion']!r} is not a manifest version.\n"
            "      Use the integer pre-history form (19) or the MPB semver form (\"1.0.0\")."
        ], None

    pinned_corpus = int(entry["verifiedAgainstCorpusVersion"])
    if corpus_version is not None and pinned_corpus < corpus_version:
        failures.append(
            f"FAIL [{host}]: corpus is at corpusVersion={corpus_version}; {host} is only "
            f"reviewed against corpusVersion={pinned_corpus}.\n"
            f"      A corpus-shape bump can change what a case field means. Confirm {tests}\n"
            f"      still reads the corpus correctly, then raise verifiedAgainstCorpusVersion in\n"
            f"      the '{host}' entry of verified-against.json."
        )
    elif corpus_version is not None and pinned_corpus > corpus_version:
        failures.append(
            f"FAIL [{host}]: {host} claims verifiedAgainstCorpusVersion={pinned_corpus}, above "
            f"the corpus's actual corpusVersion={corpus_version}. The pin cannot be ahead of the "
            "corpus it describes."
        )

    schema = fetch_schema(pinned)
    if schema is None:
        failures.append(
            f"FAIL [{host}]: pinned manifest {describe(pinned)} is not published at "
            f"{SCHEMA_URL.format(version=pinned)}.\n"
            "      Either the pin is wrong or a published schema was withdrawn."
        )
        return failures, pinned

    schema_const = schema.get("properties", {}).get("version", {}).get("const")
    if version_key(schema_const) != pinned:
        failures.append(
            f"FAIL [{host}]: schema {describe(pinned)} declares version.const={schema_const!r}. "
            "The published artifact is not self-consistent."
        )

    floor, floor_path, pattern = read_declared_floor(host, entry)
    if floor is None:
        failures.append(
            f"FAIL [{host}]: could not read the version floor from {floor_path}\n"
            f"      using pattern {pattern!r}.\n"
            f"      The consumer moved or was renamed; update this host's floorSource with it."
        )
    elif sort_key(floor) > sort_key(pinned):
        failures.append(
            f"FAIL [{host}]: consumer floor {describe(floor)} is above the verified version "
            f"{describe(pinned)}. The consumer rejects manifests it claims to support."
        )
    elif _INTEGER_KEY_RE.match(floor) and not _INTEGER_KEY_RE.match(pinned):
        # `floor <= pinned` meant "reads everything from the floor up" while there was one family.
        # Across the era break it does not: a reader whose floor is an integer compares the version
        # NUMERICALLY, so a semver string reads as 0 (or fails to parse) and is refused below the
        # floor like any ancient bundle. Such a host would pass every check above while refusing
        # every manifest it claims to be verified against — the ordering says it is in range, and
        # the reader never agrees.
        failures.append(
            f"FAIL [{host}]: consumer floor {describe(floor)} is in the integer pre-history while "
            f"the pin {describe(pinned)} is in the MPB semver era.\n"
            "      Ordering alone would allow this, but a reader gating on an integer floor reads a\n"
            "      semver version as 0 and refuses it. Move the floor across the break with the pin\n"
            f"      ({entry['floorSource']['path']}), or pin this host back to the era it reads."
        )

    newest = newest_published(pinned)
    if sort_key(newest) > sort_key(pinned):
        failures.append(
            f"FAIL [{host}]: the platform publishes manifest {describe(newest)}; {host} is only "
            f"verified against {describe(pinned)}.\n"
            f"      Confirm the {host} parser handles the {describe(newest)} shape (add a case to\n"
            f"      {tests}), then raise verifiedAgainstManifestVersion in the '{host}' entry of\n"
            f"      verified-against.json and refresh its `evidence` to say what was exercised."
        )

    if not failures:
        print(f"OK [{host}]: verified against manifest {describe(pinned)}; nothing newer published.")
        print(f"    consumer floor = {describe(floor)} (from {entry['floorSource']['path']})")
    return failures, pinned


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Native manifest-contract conformance, all hosts.")
    parser.add_argument(
        "--host",
        action="append",
        dest="hosts",
        metavar="NAME",
        help="Check only this host (repeatable). Default: every host in verified-against.json.",
    )
    parser.add_argument(
        "--verified-against",
        type=Path,
        default=_PINNED,
        help="Path to verified-against.json. Exists so the multi-host loop is testable.",
    )
    parser.add_argument(
        "--skip-corpus",
        action="store_true",
        help="Skip the offline conformance-corpus integrity check.",
    )
    parser.add_argument(
        "--update-baseline",
        action="store_true",
        help="Recompute coverage-baseline.json from the corpus and host roster, then exit. "
             "Offline; coverage changes are recorded as a reviewable diff.",
    )
    args = parser.parse_args(argv)

    pinned_doc = json.loads(args.verified_against.read_text(encoding="utf-8"))
    entries = host_entries(pinned_doc)
    full_entries = entries

    if args.update_baseline:
        failures = check_corpus(full_entries, update_baseline=True)
        for line in failures:
            print(line, file=sys.stderr)
        return 1 if failures else 0
    if args.hosts:
        wanted = set(args.hosts)
        unknown = wanted - {h for h, _ in entries}
        if unknown:
            print(f"error: no such host in {args.verified_against}: {', '.join(sorted(unknown))}",
                  file=sys.stderr)
            return 2
        entries = [(h, e) for h, e in entries if h in wanted]

    if not entries:
        print(
            f"FAIL: {args.verified_against} declares no hosts. At least one consumer must be held "
            "to the contract.",
            file=sys.stderr,
        )
        return 1

    corpus_version = None if args.skip_corpus else read_corpus_version()

    failures: list[str] = []
    for host, entry in entries:
        host_failures, _ = check_host(host, entry, corpus_version)
        failures.extend(host_failures)

    if not args.skip_corpus:
        # The full roster, never the --host-filtered slice: coverage is a property of the whole
        # corpus, and a filtered run must not read as a coverage regression.
        failures.extend(check_corpus(full_entries))

    if failures:
        for line in failures:
            print(line, file=sys.stderr)
        print(
            f"\n{len(failures)} conformance failure(s) across {len(entries)} host(s).",
            file=sys.stderr,
        )
        return 1

    corpus_note = "corpus skipped" if args.skip_corpus else "conformance corpus intact"
    print(f"OK: {len(entries)} host(s) conformant; {corpus_note}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
