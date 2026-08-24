"""Tests for the multi-host conformance gate.

Two jobs:

1. **Prove the loop.** The gate was single-host for its whole life — `pinned_doc["unreal"]`, a
   hardcoded C++ header regex, and remediation text naming a `.cpp` file. Those are exactly the
   bugs that stay invisible while only one host exists, so a *synthetic second host* is checked in
   here permanently. It is not a placeholder for Revit; it is the fixture that fails the day
   someone re-hardcodes a host name.
2. **Guard the shipped `verified-against.json` offline.** Every host's `floorSource` must actually
   resolve, on a runner with no engine installed. A pattern that silently stops matching turns the
   floor check into a no-op, so it is asserted here rather than only in the network path.

Stdlib `unittest` on purpose: this runs on a bare hosted runner in the same job as the gate, with
no `pip install` step to go stale.

Run: `python -m unittest discover -s tools/manifest-conformance`
"""

from __future__ import annotations

import contextlib
import io
import json
import re
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))

import check_manifest_conformance as gate  # noqa: E402

_REPO_ROOT = Path(__file__).resolve().parents[2]
_REAL_FETCH = gate.fetch_schema
_REAL_LEDGER = gate.fetch_published_versions

#: A published-schema stub, in the shape `check_host` reads. Takes the version CONST as the
#: platform publishes it — an int for the pre-history family, a semver string for the MPB era —
#: because the const's JSON type is exactly what tells the two apart.
def _schema(const: int | str) -> dict:
    return {"properties": {"version": {"const": const}}}


#: A fuller published-schema stub. `_schema` above is a version const and nothing else, which every
#: editorial comparison would call identical for the wrong reason; these tests need a document with
#: prose, constraints and examples so "only the prose moved" is a real assertion.
def _semver_schema(const: str, description: str = "the contract") -> dict:
    return {
        "$id": f"https://mantle.place/.well-known/schemas/bundle-manifest/{const}.json",
        "title": f"Mantle Place Bundle (MPB) manifest {const}",
        "description": description,
        "type": "object",
        "additionalProperties": True,
        "required": ["version", "bbox"],
        "properties": {
            "version": {"const": const, "description": f"pinned to {const}"},
            "bbox": {
                "type": "object",
                "description": "the AOI",
                "required": ["west"],
                "properties": {"west": {"type": "number", "description": "west edge"}},
            },
        },
        "examples": [{"version": const, "bbox": {"west": -105.0}}],
    }


def _host_entry(**overrides) -> dict:
    entry = {
        "verifiedAgainstManifestVersion": 17,
        "verifiedAgainstCorpusVersion": 2,
        "evidence": "exercised against the v17 shape",
        "consumer": "somewhere/Consumer.ext",
        "floorSource": {"path": "", "pattern": r"FLOOR\s*=\s*(\d+)"},
        "tests": "somewhere/ConsumerTest.ext",
        "owner": "a test",
        "groups": ["manifest"],
    }
    entry.update(overrides)
    return entry


class VersionFamilyTest(unittest.TestCase):
    """The two version families (integer pre-history, MPB semver) and the order over them."""

    def test_version_key_tells_the_families_apart_by_json_type(self) -> None:
        self.assertEqual("v19", gate.version_key(19))
        self.assertEqual("1.0.0", gate.version_key("1.0.0"))

    def test_version_key_refuses_the_near_misses(self) -> None:
        # An integer-as-string is the one that would silently publish under the wrong filename.
        self.assertIsNone(gate.version_key("19"))
        self.assertIsNone(gate.version_key("1.0"))
        self.assertIsNone(gate.version_key("1.0.0-rc1"))
        self.assertIsNone(gate.version_key("01.0.0"))
        self.assertIsNone(gate.version_key(None))
        # `True` is an int in Python, and would key as `vTrue` without the guard.
        self.assertIsNone(gate.version_key(True))

    def test_the_whole_integer_era_precedes_the_whole_semver_era(self) -> None:
        self.assertLess(gate.sort_key("v19"), gate.sort_key("1.0.0"))
        self.assertLess(gate.sort_key("v7"), gate.sort_key("1.0.0"))

    def test_components_order_numerically_not_lexically(self) -> None:
        self.assertLess(gate.sort_key("v7"), gate.sort_key("v12"))
        self.assertLess(gate.sort_key("1.9.0"), gate.sort_key("1.10.0"))
        self.assertLess(gate.sort_key("1.10.3"), gate.sort_key("2.0.0"))

    def test_describe_marks_the_semver_era_so_a_message_reads_unambiguously(self) -> None:
        self.assertEqual("v19", gate.describe("v19"))
        self.assertEqual("MPB 1.0.0", gate.describe("1.0.0"))

    def test_schema_url_drops_the_v_prefix_only_for_semver(self) -> None:
        self.assertTrue(gate.SCHEMA_URL.format(version="v19").endswith("/v19.json"))
        self.assertTrue(gate.SCHEMA_URL.format(version="1.0.0").endswith("/1.0.0.json"))


class NewestPublishedTest(unittest.TestCase):
    """Discovery reads the published ledger. The integer era could be found by counting; semver
    cannot, and a walk that guesses one axis misses bumps on the others."""

    def setUp(self) -> None:
        self._real = gate.fetch_published_versions
        gate._ledger_cache = None
        self.addCleanup(lambda: setattr(gate, "fetch_published_versions", self._real))
        self.addCleanup(lambda: setattr(gate, "_ledger_cache", None))

    def _ledger(self, keys: list[str]) -> None:
        gate.fetch_published_versions = lambda timeout=20: sorted(keys, key=gate.sort_key)

    def test_finds_a_semver_release_above_an_integer_pin(self) -> None:
        self._ledger(["v18", "v19", "1.0.0"])
        self.assertEqual("1.0.0", gate.newest_published("v19"))

    def test_finds_a_minor_bump_a_patch_walk_would_have_missed(self) -> None:
        self._ledger(["v19", "1.0.0", "1.1.0"])
        self.assertEqual("1.1.0", gate.newest_published("1.0.0"))

    def test_finds_a_major_bump(self) -> None:
        self._ledger(["v19", "1.0.0", "2.0.0"])
        self.assertEqual("2.0.0", gate.newest_published("1.0.0"))

    def test_a_current_pin_reports_itself(self) -> None:
        self._ledger(["v19", "1.0.0"])
        self.assertEqual("1.0.0", gate.newest_published("1.0.0"))

    def test_a_pin_above_everything_published_is_not_silently_downgraded(self) -> None:
        """That disagreement is the pin check's to report, with its own message."""
        self._ledger(["v19", "1.0.0"])
        self.assertEqual("1.1.0", gate.newest_published("1.1.0"))


class _FakeLedgerResponse:
    """Just enough of an http.client response for `fetch_published_versions`."""

    status = 200

    def __init__(self, payload: bytes) -> None:
        self._payload = payload

    def read(self) -> bytes:
        return self._payload

    def __enter__(self) -> "_FakeLedgerResponse":
        return self

    def __exit__(self, *exc: object) -> bool:
        return False


class LedgerMalformedKeyTest(unittest.TestCase):
    """The real ledger read. A frozen key in neither family may HIDE a published version, so it
    must be loud (exit 2, could-not-check), never silently dropped."""

    def setUp(self) -> None:
        gate._ledger_cache = None
        self.addCleanup(lambda: setattr(gate, "_ledger_cache", None))

    def _with_ledger(self, frozen: dict):
        payload = json.dumps({"frozen": frozen}).encode("utf-8")
        return mock.patch("urllib.request.urlopen",
                          return_value=_FakeLedgerResponse(payload))

    def test_a_bare_number_string_key_is_loud_not_silently_dropped(self) -> None:
        with self._with_ledger({"v19": "a", "19": "b"}):
            with contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit) as caught:
                    gate.fetch_published_versions()
        self.assertEqual(2, caught.exception.code, "malformed ledger is could-not-check, not drift")

    def test_the_failure_names_the_malformed_key(self) -> None:
        err = io.StringIO()
        with self._with_ledger({"v19": "a", "19": "b"}):
            with contextlib.redirect_stderr(err):
                with self.assertRaises(SystemExit):
                    gate.fetch_published_versions()
        self.assertIn("'19'", err.getvalue())

    def test_other_near_miss_shapes_are_loud_too(self) -> None:
        for bad in ("1.0", "1.0.0-rc1", "01.0.0"):
            gate._ledger_cache = None
            with self.subTest(key=bad):
                with self._with_ledger({"v19": "a", bad: "b"}):
                    with contextlib.redirect_stderr(io.StringIO()):
                        with self.assertRaises(SystemExit) as caught:
                            gate.fetch_published_versions()
                self.assertEqual(2, caught.exception.code)

    def test_documentation_keys_stay_exempt(self) -> None:
        with self._with_ledger({"v19": "a", "1.0.0": "b", "$comment": "prose"}):
            self.assertEqual(["v19", "1.0.0"], gate.fetch_published_versions())


class MultiHostGateTest(unittest.TestCase):
    """The generalized loop, driven by a synthetic two-host `verified-against.json`."""

    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.addCleanup(self._tmp.cleanup)

        # Two hosts in different languages, each declaring its own floor location.
        (self.tmp / "Cpp.h").write_text("inline constexpr int FLOOR = 17;\n", encoding="utf-8")
        (self.tmp / "Dotnet.cs").write_text("public const int Floor = 17;\n", encoding="utf-8")

        # `read_declared_floor` resolves floorSource.path against the repo root; point it at tmp.
        self._real_root = gate._REPO_ROOT
        gate._REPO_ROOT = self.tmp
        self.addCleanup(lambda: setattr(gate, "_REPO_ROOT", self._real_root))

        gate._schema_cache.clear()
        self.addCleanup(gate._schema_cache.clear)

        # Keyed by version KEY (`v17`, `1.0.0`) — the mirror filename stem, which is what the gate
        # resolves a pin to before it fetches anything.
        self.published = {"v17": _schema(17)}
        gate.fetch_schema = lambda version, timeout=20: self.published.get(version)  # type: ignore[assignment]
        self.addCleanup(lambda: setattr(gate, "fetch_schema", _REAL_FETCH))

        # "What else is published" comes from the platform's freeze ledger over the network. Stub
        # it from the same dict so a test that publishes a version does not also have to remember
        # to tell the ledger about it.
        gate._ledger_cache = None
        gate.fetch_published_versions = lambda timeout=20: sorted(  # type: ignore[assignment]
            self.published, key=gate.sort_key)
        self.addCleanup(lambda: setattr(gate, "fetch_published_versions", _REAL_LEDGER))
        self.addCleanup(lambda: setattr(gate, "_ledger_cache", None))

    def _write_doc(self, hosts: dict) -> Path:
        path = self.tmp / "verified-against.json"
        path.write_text(json.dumps({"$comment": ["ignored"], **hosts}), encoding="utf-8")
        return path

    def _run(self, hosts: dict) -> int:
        return gate.main([
            "--verified-against", str(self._write_doc(hosts)),
            "--skip-corpus",
        ])

    def _two_hosts(self, **synthetic_overrides) -> dict:
        return {
            "unreal": _host_entry(
                floorSource={"path": "Cpp.h", "pattern": r"FLOOR\s*=\s*(\d+)"},
                tests="unreal/…/MantlePlaceImportManifestTest.cpp",
            ),
            "synthetic": _host_entry(
                floorSource={"path": "Dotnet.cs", "pattern": r"Floor\s*=\s*(\d+)"},
                tests="synthetic/Tests/ManifestReaderTests.cs",
                **synthetic_overrides,
            ),
        }

    def test_every_host_is_checked_not_just_unreal(self) -> None:
        self.assertEqual(0, self._run(self._two_hosts()))

    def test_a_second_hosts_failure_fails_the_gate(self) -> None:
        """The regression that motivates this file: a non-`unreal` key used to be unreachable."""
        hosts = self._two_hosts()
        (self.tmp / "Dotnet.cs").write_text("public const int Floor = 18;\n", encoding="utf-8")
        self.assertEqual(1, self._run(hosts))

    def test_failures_name_the_offending_host(self) -> None:
        hosts = self._two_hosts()
        (self.tmp / "Dotnet.cs").write_text("public const int Floor = 18;\n", encoding="utf-8")
        failures, _ = gate.check_host("synthetic", hosts["synthetic"])
        self.assertTrue(failures)
        self.assertIn("[synthetic]", failures[0])

    def test_remediation_names_the_hosts_own_tests_not_unreals(self) -> None:
        """The old message told a .NET author to edit a .cpp file."""
        self.published["v18"] = _schema(18)
        failures, _ = gate.check_host("synthetic", self._two_hosts()["synthetic"])
        joined = "\n".join(failures)
        self.assertIn("synthetic/Tests/ManifestReaderTests.cs", joined)
        self.assertNotIn("MantlePlaceImportManifestTest.cpp", joined)
        self.assertNotIn(".cpp", joined)

    def test_an_integer_floor_under_a_semver_pin_is_drift(self) -> None:
        """The cross-era hole: ordering says v18 <= 1.0.0, but a reader gating on an INTEGER floor
        reads a semver version as 0 and refuses it. Without this the host passes every check while
        refusing every manifest it claims to be verified against."""
        self.published["1.0.0"] = _schema("1.0.0")
        entry = _host_entry(
            verifiedAgainstManifestVersion="1.0.0",
            floorSource={"path": "Cpp.h", "pattern": r"FLOOR\s*=\s*(\d+)"},  # still `= 17`
        )
        failures, _ = gate.check_host("synthetic", entry)
        self.assertTrue(any("integer pre-history" in f for f in failures), failures)

    def test_a_semver_floor_under_a_semver_pin_is_fine(self) -> None:
        self.published["1.0.0"] = _schema("1.0.0")
        (self.tmp / "Semver.h").write_text('FLOOR = TEXT("1.0.0");\n', encoding="utf-8")
        entry = _host_entry(
            verifiedAgainstManifestVersion="1.0.0",
            floorSource={"path": "Semver.h", "pattern": r'FLOOR\s*=\s*TEXT\("([0-9.]+)"\)'},
        )
        failures, _ = gate.check_host("synthetic", entry)
        self.assertEqual([], failures)

    def test_an_integer_floor_under_an_integer_pin_is_still_fine(self) -> None:
        """The pre-history keeps working exactly as it did — this check must not retire it."""
        failures, _ = gate.check_host("unreal", self._two_hosts()["unreal"])
        self.assertEqual([], failures)

    def test_floor_pattern_that_stops_matching_is_drift_not_a_pass(self) -> None:
        entry = _host_entry(floorSource={"path": "Cpp.h", "pattern": r"RENAMED\s*=\s*(\d+)"})
        failures, _ = gate.check_host("synthetic", entry)
        self.assertTrue(any("could not read the version floor" in f for f in failures))

    def test_missing_required_field_is_rejected_before_any_fetch(self) -> None:
        fetched: list[int] = []
        gate.fetch_schema = lambda version, timeout=20: (  # type: ignore[assignment]
            fetched.append(version) or self.published.get(version))
        entry = _host_entry()
        del entry["floorSource"]
        failures, pinned = gate.check_host("synthetic", entry)
        self.assertIsNone(pinned)
        self.assertIn("floorSource", failures[0])
        self.assertEqual([], fetched, "a malformed entry must not cost a network round trip")

    def test_comment_key_is_not_treated_as_a_host(self) -> None:
        self.assertEqual(
            ["unreal"],
            [h for h, _ in gate.host_entries({"$comment": ["x"], "unreal": _host_entry()})],
        )

    def test_a_host_behind_the_corpus_version_fails(self) -> None:
        entry = _host_entry(verifiedAgainstCorpusVersion=1)
        failures, _ = gate.check_host("synthetic", entry, corpus_version=2)
        self.assertTrue(any("corpusVersion" in f for f in failures))

    def test_a_host_at_the_corpus_version_passes(self) -> None:
        entry = _host_entry(verifiedAgainstCorpusVersion=2)
        failures, _ = gate.check_host("synthetic", entry, corpus_version=2)
        self.assertEqual([], [f for f in failures if "corpusVersion" in f])

    def test_a_host_ahead_of_the_corpus_version_fails(self) -> None:
        entry = _host_entry(verifiedAgainstCorpusVersion=3)
        failures, _ = gate.check_host("synthetic", entry, corpus_version=2)
        self.assertTrue(any("above the corpus's actual corpusVersion" in f for f in failures))

    def test_corpus_version_not_checked_when_unknown(self) -> None:
        """`--skip-corpus` passes corpus_version=None; the drift check is a no-op, not a failure."""
        entry = _host_entry(verifiedAgainstCorpusVersion=1)
        failures, _ = gate.check_host("synthetic", entry, corpus_version=None)
        self.assertEqual([], [f for f in failures if "corpusVersion" in f])

    def test_a_host_only_run_still_works(self) -> None:
        hosts = self._two_hosts()
        (self.tmp / "Dotnet.cs").write_text("public const int Floor = 18;\n", encoding="utf-8")
        doc = self._write_doc(hosts)
        self.assertEqual(0, gate.main(["--verified-against", str(doc), "--skip-corpus",
                                       "--host", "unreal"]))


class PatchExemptionTest(unittest.TestCase):
    """A newer PATCH is not drift — but only if it really is editorial.

    `spec/compatibility.md` §2 says a patch is "documentation and description text only" and
    "obliges a host to nothing: no re-pin and no re-verification". The gate used to fail on ANY
    newer published key, which made it contradict the spec it enforces and turned every editorial
    republication into two red hosts. The exemption added here is earned per release: the two
    published documents are compared with prose and their own version stamp stripped.
    """

    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.tmp = Path(self._tmp.name)
        self.addCleanup(self._tmp.cleanup)

        (self.tmp / "Semver.h").write_text('FLOOR = TEXT("1.0.0");\n', encoding="utf-8")
        self._real_root = gate._REPO_ROOT
        gate._REPO_ROOT = self.tmp
        self.addCleanup(lambda: setattr(gate, "_REPO_ROOT", self._real_root))

        gate._schema_cache.clear()
        self.addCleanup(gate._schema_cache.clear)

        self.published = {"1.0.0": _semver_schema("1.0.0")}
        gate.fetch_schema = lambda version, timeout=20: self.published.get(version)  # type: ignore[assignment]
        self.addCleanup(lambda: setattr(gate, "fetch_schema", _REAL_FETCH))

        gate._ledger_cache = None
        gate.fetch_published_versions = lambda timeout=20: sorted(  # type: ignore[assignment]
            self.published, key=gate.sort_key)
        self.addCleanup(lambda: setattr(gate, "fetch_published_versions", _REAL_LEDGER))
        self.addCleanup(lambda: setattr(gate, "_ledger_cache", None))

    def _entry(self, **overrides) -> dict:
        return _host_entry(
            verifiedAgainstManifestVersion="1.0.0",
            floorSource={"path": "Semver.h", "pattern": r'FLOOR\s*=\s*TEXT\("([0-9.]+)"\)'},
            tests="synthetic/Tests/ManifestReaderTests.cs",
            **overrides,
        )

    # ── the exemption ────────────────────────────────────────────────────────

    def test_a_newer_editorial_patch_is_not_drift(self) -> None:
        """The case this exists for: 1.0.1 corrects prose and nothing else."""
        self.published["1.0.1"] = _semver_schema("1.0.1", description="corrected prose")
        failures, _ = gate.check_host("synthetic", self._entry())
        self.assertEqual([], failures)

    def test_the_exemption_is_reported_rather_than_silent(self) -> None:
        """A host that skipped a published version should say so — silence would read as
        'nothing newer published', which is a different fact."""
        self.published["1.0.1"] = _semver_schema("1.0.1", description="corrected prose")
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            gate.check_host("synthetic", self._entry())
        printed = out.getvalue()
        self.assertIn("1.0.1", printed)
        self.assertIn("editorial patch", printed)
        self.assertNotIn("nothing newer published", printed)

    def test_prose_at_any_depth_is_editorial(self) -> None:
        """Descriptions live on every field, not just the document root."""
        newer = _semver_schema("1.0.1")
        newer["properties"]["bbox"]["description"] = "reworded"
        newer["properties"]["bbox"]["properties"]["west"]["description"] = "reworded too"
        self.published["1.0.1"] = newer
        failures, _ = gate.check_host("synthetic", self._entry())
        self.assertEqual([], failures)

    def test_reworked_examples_are_editorial(self) -> None:
        """`examples` are non-normative and carry the version stamp, so they move with a patch."""
        newer = _semver_schema("1.0.1")
        newer["examples"] = [{"version": "1.0.1", "anything": "at all"}]
        self.published["1.0.1"] = newer
        failures, _ = gate.check_host("synthetic", self._entry())
        self.assertEqual([], failures)

    # ── what the exemption must NOT wave through ─────────────────────────────

    def test_a_patch_that_changes_what_validates_is_drift(self) -> None:
        newer = _semver_schema("1.0.1")
        newer["required"].append("brand_new_field")
        self.published["1.0.1"] = newer
        failures, _ = gate.check_host("synthetic", self._entry())
        self.assertTrue(any("changes" in f and "validates" in f for f in failures), failures)

    def test_a_patch_that_narrows_a_nested_constraint_is_drift(self) -> None:
        """The quiet half: a restriction deep in the tree still validates the same documents on
        the happy path, which is exactly why it needs a machine to notice."""
        newer = _semver_schema("1.0.1")
        newer["properties"]["bbox"]["properties"]["west"]["maximum"] = 0
        self.published["1.0.1"] = newer
        failures, _ = gate.check_host("synthetic", self._entry())
        self.assertTrue(any("changes" in f and "validates" in f for f in failures), failures)

    def test_a_newer_minor_is_still_drift(self) -> None:
        self.published["1.1.0"] = _semver_schema("1.1.0")
        failures, _ = gate.check_host("synthetic", self._entry())
        self.assertTrue(any("is only verified against" in f for f in failures), failures)

    def test_a_newer_major_is_still_drift(self) -> None:
        self.published["2.0.0"] = _semver_schema("2.0.0")
        failures, _ = gate.check_host("synthetic", self._entry())
        self.assertTrue(any("is only verified against" in f for f in failures), failures)

    def test_a_ledger_entry_that_is_not_served_is_drift(self) -> None:
        """The ledger and the published set are two artifacts; if they disagree the gate has to
        say so rather than treat the unfetchable version as a passing patch."""
        gate.fetch_published_versions = lambda timeout=20: ["1.0.0", "1.0.1"]  # type: ignore[assignment]
        failures, _ = gate.check_host("synthetic", self._entry())
        self.assertTrue(any("not served at" in f for f in failures), failures)


class PatchRuleUnitTest(unittest.TestCase):
    """The two pure helpers behind the exemption."""

    def test_a_higher_patch_is_a_patch(self) -> None:
        self.assertTrue(gate.is_patch_over("1.0.0", "1.0.1"))
        self.assertTrue(gate.is_patch_over("1.2.3", "1.2.10"))

    def test_a_minor_or_major_is_not_a_patch(self) -> None:
        self.assertFalse(gate.is_patch_over("1.0.0", "1.1.0"))
        self.assertFalse(gate.is_patch_over("1.0.0", "2.0.0"))

    def test_the_same_version_is_not_a_patch_over_itself(self) -> None:
        self.assertFalse(gate.is_patch_over("1.0.0", "1.0.0"))

    def test_a_lower_patch_is_not_a_patch_over(self) -> None:
        self.assertFalse(gate.is_patch_over("1.0.1", "1.0.0"))

    def test_the_integer_era_has_no_patch_component(self) -> None:
        """`v19` -> `v20` is a whole contract apart, and the era break most of all."""
        self.assertFalse(gate.is_patch_over("v19", "v20"))
        self.assertFalse(gate.is_patch_over("v19", "1.0.0"))
        self.assertFalse(gate.is_patch_over("v19", "v19"))

    def test_identical_documents_are_editorial(self) -> None:
        self.assertTrue(
            gate.patch_is_editorial(_semver_schema("1.0.0"), _semver_schema("1.0.1")))

    def test_a_changed_constraint_is_not_editorial(self) -> None:
        newer = _semver_schema("1.0.1")
        newer["additionalProperties"] = False
        self.assertFalse(gate.patch_is_editorial(_semver_schema("1.0.0"), newer))

    def test_comparison_does_not_mutate_its_inputs(self) -> None:
        """`check_host` reuses the memoized schema dicts, so a destructive compare would corrupt
        the next host's check — and with two hosts pinned at the same version, always the second."""
        pinned = _semver_schema("1.0.0")
        newer = _semver_schema("1.0.1")
        gate.patch_is_editorial(pinned, newer)
        self.assertEqual("1.0.0", pinned["properties"]["version"]["const"])
        self.assertEqual("1.0.1", newer["properties"]["version"]["const"])
        self.assertIn("description", pinned)


class ShippedRegistryTest(unittest.TestCase):
    """The real `verified-against.json`, checked without touching the network."""

    def setUp(self) -> None:
        self.doc = json.loads(gate._PINNED.read_text(encoding="utf-8"))
        self.hosts = gate.host_entries(self.doc)

    def test_at_least_one_host_is_registered(self) -> None:
        self.assertTrue(self.hosts)

    def test_every_host_declares_every_required_field(self) -> None:
        for host, entry in self.hosts:
            with self.subTest(host=host):
                for field in gate._REQUIRED_HOST_FIELDS:
                    self.assertIn(field, entry)

    def test_every_declared_path_exists(self) -> None:
        for host, entry in self.hosts:
            with self.subTest(host=host):
                for key in ("consumer", "tests"):
                    self.assertTrue((_REPO_ROOT / entry[key]).is_file(),
                                    f"{host}.{key} points at a missing file: {entry[key]}")

    def test_every_floor_source_still_resolves(self) -> None:
        """A floorSource that stops matching turns the floor check into a silent no-op."""
        for host, entry in self.hosts:
            with self.subTest(host=host):
                floor, path, pattern = gate.read_declared_floor(host, entry)
                self.assertIsNotNone(floor, f"{host}: {pattern!r} no longer matches {path}")
                pinned = gate.version_key(entry["verifiedAgainstManifestVersion"])
                self.assertIsNotNone(pinned, f"{host}: pin is not a manifest version")
                self.assertLessEqual(gate.sort_key(floor), gate.sort_key(pinned))

    def test_every_floor_source_captures_a_recognizable_version(self) -> None:
        """A pattern can match and still capture nonsense — `read_declared_floor` returns None for
        a capture in neither family, and a None floor is drift, not a pass."""
        for host, entry in self.hosts:
            with self.subTest(host=host):
                floor, _path, _pattern = gate.read_declared_floor(host, entry)
                self.assertIsNotNone(gate.version_key(floor) or gate.sort_key(floor))

    def test_floor_patterns_capture_exactly_one_group(self) -> None:
        for host, entry in self.hosts:
            with self.subTest(host=host):
                self.assertEqual(1, re.compile(entry["floorSource"]["pattern"]).groups)

    def test_every_shipped_host_is_at_the_shipped_corpus_version(self) -> None:
        """A host pinned below (or above) the corpus's actual version is exactly the drift
        ENF-01 exists to catch. Compared directly rather than via `check_host`, which would also
        hit the network for the manifest-version leg -- this class stays offline by design."""
        corpus_version = gate.read_corpus_version()
        self.assertIsNotNone(corpus_version)
        for host, entry in self.hosts:
            with self.subTest(host=host):
                self.assertEqual(corpus_version, entry["verifiedAgainstCorpusVersion"])


class CorpusTest(unittest.TestCase):
    """The shared corpus, which every host suite consumes (HPS-40)."""

    def test_shipped_corpus_is_intact(self) -> None:
        self.assertEqual([], gate.check_corpus())

    def test_shipped_corpus_passes_the_full_check_with_the_real_roster(self) -> None:
        """Integrity + self-test + coverage ratchet, exactly as `main()` runs it."""
        doc = json.loads(gate._PINNED.read_text(encoding="utf-8"))
        self.assertEqual([], gate.check_corpus(gate.host_entries(doc)))

    def test_shipped_selftest_is_well_formed_broken(self) -> None:
        """Each self-test fixture must be wrong in exactly its declared way (HPS-46) — a host
        suite asserts these are rejected, which is only meaningful if the breakage is real."""
        self.assertEqual([], gate.check_selftest())

    def test_index_declares_both_accept_and_reject_cases(self) -> None:
        index = json.loads((gate._CORPUS / "index.json").read_text(encoding="utf-8"))
        expectations = {c["expect"] for c in index["cases"]}
        self.assertIn("accept", expectations)
        self.assertIn("reject", expectations)

    def test_a_malformed_index_reports_rather_than_crashes(self) -> None:
        """The gate's contract is exit codes; a traceback is not one of them."""
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        root = Path(tmp.name)
        real = gate._CORPUS
        gate._CORPUS = root
        self.addCleanup(lambda: setattr(gate, "_CORPUS", real))

        (root / "index.json").write_text('{"cases":[{"id":"x","group":"manifest"}]}',
                                         encoding="utf-8")
        failures = gate.check_corpus()
        self.assertTrue(failures)
        self.assertTrue(all(f.startswith("FAIL:") for f in failures))

        (root / "index.json").write_text("{ not json", encoding="utf-8")
        self.assertTrue(gate.check_corpus()[0].startswith("FAIL:"))

    def test_an_unlisted_non_json_vector_is_still_an_orphan(self) -> None:
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        root = Path(tmp.name)
        real = gate._CORPUS
        gate._CORPUS = root
        self.addCleanup(lambda: setattr(gate, "_CORPUS", real))

        (root / "index.json").write_text('{"cases":[]}', encoding="utf-8")
        (root / "README.md").write_text("docs are not vectors\n", encoding="utf-8")
        (root / "stray.csv").write_text("a,b\n", encoding="utf-8")
        failures = gate.check_corpus()
        self.assertEqual(1, len(failures), failures)
        self.assertIn("stray.csv", failures[0])

    def test_every_case_states_why_it_exists(self) -> None:
        index = json.loads((gate._CORPUS / "index.json").read_text(encoding="utf-8"))
        for case in index["cases"]:
            with self.subTest(case=case["id"]):
                self.assertTrue(case.get("reason", "").strip(),
                                "a case with no stated reason rots into an unexplained fixture")

    def test_read_corpus_version_matches_the_shipped_corpus(self) -> None:
        index = json.loads((gate._CORPUS / "index.json").read_text(encoding="utf-8"))
        self.assertEqual(index["corpusVersion"], gate.read_corpus_version())


class CoverageRatchetTest(unittest.TestCase):
    """Per-case host coverage: derived from `groups` x `appliesTo`, ratcheted against the
    committed baseline. Any difference fails; --update-baseline records it."""

    _CASES = [
        {"id": "manifest.a", "group": "manifest", "file": "a.json", "expect": "vector",
         "reason": "r"},
        {"id": "manifest.b", "group": "manifest", "file": "b.json", "expect": "vector",
         "reason": "r", "appliesTo": "unreal"},
        {"id": "vault.c", "group": "vault", "file": "c.json", "expect": "vector", "reason": "r"},
    ]

    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self._real_baseline = gate._BASELINE
        gate._BASELINE = Path(self._tmp.name) / "coverage-baseline.json"
        self.addCleanup(lambda: setattr(gate, "_BASELINE", self._real_baseline))
        self.entries = [
            ("revit", _host_entry(groups=["manifest"])),
            ("unreal", _host_entry(groups=["manifest", "vault"])),
        ]

    def _write_baseline(self, cases: dict) -> None:
        gate._BASELINE.write_text(json.dumps({"cases": cases}), encoding="utf-8")

    def _expected(self) -> dict:
        return {"manifest.a": ["revit", "unreal"], "manifest.b": ["unreal"],
                "vault.c": ["unreal"]}

    def test_matching_baseline_passes(self) -> None:
        self._write_baseline(self._expected())
        self.assertEqual([], gate.check_coverage(self._CASES, self.entries))

    def test_a_host_losing_a_case_is_a_regression(self) -> None:
        recorded = self._expected()
        recorded["vault.c"] = ["revit", "unreal"]  # revit once covered it; now it cannot
        self._write_baseline(recorded)
        failures = gate.check_coverage(self._CASES, self.entries)
        self.assertTrue(any("coverage regression" in f and "revit" in f for f in failures))

    def test_an_unrecorded_improvement_is_a_stale_baseline(self) -> None:
        recorded = self._expected()
        recorded["manifest.a"] = ["unreal"]  # revit's coverage is real but unrecorded
        self._write_baseline(recorded)
        failures = gate.check_coverage(self._CASES, self.entries)
        self.assertTrue(any("stale" in f and "manifest.a" in f for f in failures))

    def test_a_missing_baseline_tells_you_to_create_one(self) -> None:
        failures = gate.check_coverage(self._CASES, self.entries)
        self.assertTrue(any("--update-baseline" in f for f in failures))

    def test_update_baseline_writes_the_derived_matrix(self) -> None:
        self.assertEqual([], gate.check_coverage(self._CASES, self.entries,
                                                 update_baseline=True))
        written = json.loads(gate._BASELINE.read_text(encoding="utf-8"))
        self.assertEqual(self._expected(), written["cases"])
        # And the file it wrote passes the ratchet it feeds.
        self.assertEqual([], gate.check_coverage(self._CASES, self.entries))

    def test_applies_to_must_name_a_registered_host(self) -> None:
        cases = [{"id": "manifest.x", "group": "manifest", "file": "x.json", "expect": "vector",
                  "reason": "r", "appliesTo": "rhino"}]
        failures = gate.check_coverage(cases, self.entries, update_baseline=True)
        self.assertTrue(any("appliesTo" in f and "rhino" in f for f in failures))

    def test_a_host_without_groups_cannot_be_covered(self) -> None:
        entry = _host_entry()
        del entry["groups"]
        failures = gate.check_coverage(self._CASES, [("synthetic", entry)])
        self.assertTrue(any("groups" in f for f in failures))

    def test_update_baseline_refuses_to_write_a_derivation_that_failed(self) -> None:
        """A case dropped for a bad `appliesTo` would be written out MISSING, and a case the
        baseline never mentions is one the ratchet can never regress."""
        cases = self._CASES + [{"id": "manifest.rot", "group": "manifest", "file": "r.json",
                                "expect": "vector", "reason": "r", "appliesTo": "rhino"}]
        failures = gate.check_coverage(cases, self.entries, update_baseline=True)
        self.assertTrue(any("refusing to write" in f for f in failures))
        self.assertFalse(gate._BASELINE.exists(), "no baseline may be written from a failed run")


class SelfTestSweepTest(unittest.TestCase):
    """The self-test corpus's own orphan sweep. The MAIN sweep excludes `self-test/` wholesale,
    so anything this one misses is invisible to the gate while the host readers' recursive sweeps
    still flag it — the HPS-46 asymmetry, inverted."""

    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.root = Path(self._tmp.name)
        real = gate._CORPUS
        gate._CORPUS = self.root
        self.addCleanup(lambda: setattr(gate, "_CORPUS", real))
        # A minimal well-formed-broken self-test tree: one fixture, declared broken its one way.
        selftest = self.root / "self-test"
        (selftest / "cases").mkdir(parents=True)
        (selftest / "cases" / "missing-is-fine.json").write_text("{}", encoding="utf-8")
        (selftest / "index.json").write_text(json.dumps({
            "orphanFiles": ["cases/missing-is-fine.json"],
            "cases": [{"id": "selfTest.missingFile", "group": "manifest",
                       "file": "cases/does-not-exist.json", "expect": "accept",
                       "selfTestFailure": "missingFile", "reason": "r"}],
        }), encoding="utf-8")
        (selftest / "broken-index-json").mkdir()
        (selftest / "broken-index-json" / "index.json").write_text("{ not json", encoding="utf-8")
        (selftest / "broken-index-schema").mkdir()
        (selftest / "broken-index-schema" / "index.json").write_text('{"corpusVersion":2}',
                                                                     encoding="utf-8")
        self.selftest = selftest

    def test_the_fixture_tree_is_clean_to_start_with(self) -> None:
        self.assertEqual([], gate.check_selftest())

    def test_a_stray_file_deeper_than_cases_is_still_flagged(self) -> None:
        """The sweep used to be one non-recursive listing of `cases/`."""
        nested = self.selftest / "cases" / "nested"
        nested.mkdir()
        (nested / "stray.json").write_text("{}", encoding="utf-8")
        failures = gate.check_selftest()
        self.assertTrue(any("cases/nested/stray.json" in f for f in failures), failures)

    def test_a_stray_file_beside_the_index_is_flagged(self) -> None:
        (self.selftest / "notes.json").write_text("{}", encoding="utf-8")
        failures = gate.check_selftest()
        self.assertTrue(any("notes.json" in f for f in failures), failures)

    def test_files_inside_a_nested_corpus_are_not_swept(self) -> None:
        """A directory carrying its own index.json is another index's to sweep, not this one's."""
        (self.selftest / "broken-index-schema" / "extra.json").write_text("{}", encoding="utf-8")
        self.assertEqual([], gate.check_selftest())

    def test_a_fixture_disagreeing_with_its_declared_manifest_version_is_rot(self) -> None:
        """The same declared-vs-actual cross-check the corpus proper gets — without it a self-test
        fixture can sit in a dialect the readers refuse, and its case passes on the version gate
        rather than on its declared breakage."""
        (self.selftest / "cases" / "unknown-key.json").write_text(
            json.dumps({"version": "1.0.0"}), encoding="utf-8")
        (self.selftest / "index.json").write_text(json.dumps({
            "orphanFiles": ["cases/missing-is-fine.json"],
            "cases": [{"id": "selfTest.unknownExpectationKey", "group": "manifest",
                       "file": "cases/unknown-key.json", "expect": "accept",
                       "selfTestFailure": "unknownExpectationKey", "reason": "r",
                       "manifestVersion": 18,
                       "expectations": {"orderId": "ord", "selfTestNeverConsumed": True}}],
        }), encoding="utf-8")
        failures = gate.check_selftest()
        self.assertTrue(any("manifestVersion=18" in f and "'1.0.0'" in f for f in failures),
                        failures)

    def test_a_selftest_pin_that_trails_the_corpus_pin_is_rot(self) -> None:
        """The rot that motivated the check: fixtures internally consistent at a version the
        readers no longer speak. Caught by comparing the self-test index's own pin to the corpus
        proper's."""
        (self.root / "index.json").write_text(json.dumps({
            "corpusVersion": 3, "manifestVersion": "1.0.0", "cases": [],
        }), encoding="utf-8")
        stale = json.loads((self.selftest / "index.json").read_text(encoding="utf-8"))
        stale["manifestVersion"] = 18
        (self.selftest / "index.json").write_text(json.dumps(stale), encoding="utf-8")
        failures = gate.check_selftest()
        self.assertTrue(any("self-test index declares manifestVersion=18" in f
                            for f in failures), failures)

    def test_matching_pins_pass(self) -> None:
        (self.root / "index.json").write_text(json.dumps({
            "corpusVersion": 3, "manifestVersion": "1.0.0", "cases": [],
        }), encoding="utf-8")
        aligned = json.loads((self.selftest / "index.json").read_text(encoding="utf-8"))
        aligned["manifestVersion"] = "1.0.0"
        (self.selftest / "index.json").write_text(json.dumps(aligned), encoding="utf-8")
        self.assertEqual([], gate.check_selftest())


class NestedUnreadFixtureTest(unittest.TestCase):
    """`nestedUnreadExpectation` must be broken exactly its declared way (HPS-46b).

    A host suite asserts its reader REJECTS this fixture. That assertion is worth nothing if the
    unread nested keys quietly stopped being unread — which is the single way this fixture rots.
    """

    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self._tmp.cleanup)
        self.root = Path(self._tmp.name)
        real = gate._CORPUS
        gate._CORPUS = self.root
        self.addCleanup(lambda: setattr(gate, "_CORPUS", real))
        selftest = self.root / "self-test"
        (selftest / "cases").mkdir(parents=True)
        (selftest / "cases" / "nested.json").write_text("{}", encoding="utf-8")
        (selftest / "broken-index-json").mkdir()
        (selftest / "broken-index-json" / "index.json").write_text("{ not json", encoding="utf-8")
        (selftest / "broken-index-schema").mkdir()
        (selftest / "broken-index-schema" / "index.json").write_text('{"corpusVersion":2}',
                                                                     encoding="utf-8")
        self.selftest = selftest

    def write(self, expectations: dict) -> list[str]:
        (self.selftest / "index.json").write_text(json.dumps({
            "cases": [{"id": "selfTest.nestedUnreadExpectation", "group": "manifest",
                       "file": "cases/nested.json", "expect": "accept",
                       "selfTestFailure": "nestedUnreadExpectation", "reason": "r",
                       "expectations": expectations}],
        }), encoding="utf-8")
        return gate.check_selftest()

    WELL_FORMED = {
        "items": [{"orderId": "ord", "status": 404, "selfTestNeverReadNested": True}],
    }

    def test_a_well_formed_fixture_passes(self) -> None:
        self.assertEqual([], self.write(self.WELL_FORMED))

    def test_a_reserved_key_that_is_prose_cannot_be_the_breakage(self) -> None:
        """`selfTestNote` ends in `Note`, so it is documentation and exempt at every depth — a
        fixture whose only reserved key is prose asserts nothing and must not pass as broken."""
        failures = self.write({
            "items": [{"orderId": "ord", "status": 404, "selfTestNote": "prose"}],
        })
        self.assertTrue(any("reserved `selfTest` prefix" in f for f in failures), failures)

    def test_the_coercion_half_is_required(self) -> None:
        """Without a wrong-typed nested value, a host reading through UE's coercing
        `TryGet*Field` — which reads 404 back as "404" — passes the fixture (one level down)."""
        failures = self.write({
            "items": [{"orderId": "ord", "status": "Available", "selfTestNeverReadNested": True}],
        })
        self.assertTrue(any("coercion half" in f for f in failures), failures)

    def test_the_breakage_must_sit_below_an_assertable_key(self) -> None:
        """All-scalar expectations put the breakage at the top level, where HPS-46 already
        catches it — such a fixture proves nothing about nesting."""
        failures = self.write({"status": 404, "selfTestNeverReadNested": True})
        self.assertTrue(any("top-level expectation holds a container" in f for f in failures),
                        failures)


class NestedExpectationEntriesTest(unittest.TestCase):
    """The walk HPS-46b's obligation is derived from."""

    def test_the_top_level_is_excluded(self) -> None:
        """It is HPS-46's, and double-reporting it would make one gap read as two."""
        entries = gate.nested_expectation_entries({"itemCount": 2, "items": [{"orderId": "a"}]})
        self.assertEqual(["items[0].orderId"], [p for p, _, _ in entries])

    def test_paths_name_the_row(self) -> None:
        entries = gate.nested_expectation_entries(
            {"items": [{"a": 1}, {"hasManifestVersion": False}]})
        self.assertIn("items[1].hasManifestVersion", [p for p, _, _ in entries])

    def test_prose_is_yielded_but_never_walked(self) -> None:
        """A `*Note` value that happens to be an object is documentation all the way down."""
        entries = gate.nested_expectation_entries(
            {"items": [{"layersNote": {"buried": True}}]})
        self.assertEqual(["items[0].layersNote"], [p for p, _, _ in entries])

    def test_documentation_keys_follow_the_convention_at_every_depth(self) -> None:
        self.assertTrue(gate.is_documentation_key("$comment"))
        self.assertTrue(gate.is_documentation_key("layersNote"))
        self.assertFalse(gate.is_documentation_key("noteworthy"))
        self.assertFalse(gate.is_documentation_key("orderId"))


if __name__ == "__main__":
    unittest.main()
