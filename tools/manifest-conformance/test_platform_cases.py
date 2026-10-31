"""Tests for the Platform API leg of the conformance gate (`platform_cases.py`).

The `platform` and `client` corpus groups hold request/response exchanges with the Platform API.
Each one must be an exchange the served spec allows, so the gate validates every case against the
spec's own schemas. These tests prove two things: the shipped cases pass, and a case the spec does
not allow fails, for each way a case can be wrong.

Run: `python -m unittest discover -s tools/manifest-conformance`
"""

from __future__ import annotations

import copy
import json
import sys
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))

import check_manifest_conformance as gate  # noqa: E402
import platform_cases as platform  # noqa: E402

_CORPUS = Path(__file__).resolve().parent / "corpus"
_SPEC = platform.load_vendored_spec()


def _index_cases() -> list[dict]:
    index = json.loads((_CORPUS / "index.json").read_text(encoding="utf-8"))
    return [c for c in index["cases"] if c.get("group") in platform.PLATFORM_GROUPS]


def _case(case_id: str) -> tuple[dict, dict]:
    entry = next(c for c in _index_cases() if c["id"] == case_id)
    exchange = json.loads((_CORPUS / entry["file"]).read_text(encoding="utf-8"))
    return copy.deepcopy(entry), exchange


def _check(entry: dict, exchange: dict) -> list[str]:
    return platform.check_exchange(entry, exchange, _SPEC, "the vendored spec")


class ShippedCasesTest(unittest.TestCase):
    def test_the_ticketed_cases_are_all_present(self) -> None:
        ids = {c["id"] for c in _index_cases()}
        for wanted in (
            "platform.quote.public", "platform.quote.signedIn", "platform.quote.invalidBearerIgnored",
            "platform.quote.invalidExtent", "platform.quote.uncovered",
            "platform.order.priceChanged", "platform.order.insufficientCredit",
            "platform.order.freeCapReached", "platform.order.idempotencyKeyMissing",
            "client.updateRequired",
        ):
            self.assertIn(wanted, ids)

    def test_every_shipped_case_validates_against_the_vendored_spec(self) -> None:
        self.assertEqual(platform.check_platform_cases(_index_cases(), _CORPUS, _SPEC, "vendored"), [])

    def test_each_order_refusal_the_curator_can_resolve_carries_a_handoff_url(self) -> None:
        for case_id in ("platform.order.priceChanged", "platform.order.insufficientCredit",
                        "platform.order.freeCapReached"):
            _, exchange = _case(case_id)
            self.assertTrue(exchange["response"]["body"]["handoffUrl"].startswith("https://"), case_id)

    def test_price_changed_carries_its_quote(self) -> None:
        _, exchange = _case("platform.order.priceChanged")
        self.assertIn("quote", exchange["response"]["body"])


class ACaseTheSpecDoesNotAllowFailsTest(unittest.TestCase):
    def test_a_quote_missing_a_required_member(self) -> None:
        entry, exchange = _case("platform.quote.public")
        del exchange["response"]["body"]["licence"]
        failures = _check(entry, exchange)
        self.assertTrue(any("licence" in f and "required" in f for f in failures), failures)

    def test_a_member_the_schema_closes_out(self) -> None:
        entry, exchange = _case("client.updateRequired")
        exchange["response"]["body"]["retryAfterMs"] = 10
        failures = _check(entry, exchange)
        self.assertTrue(any("retryAfterMs" in f for f in failures), failures)

    def test_a_value_of_the_wrong_type(self) -> None:
        entry, exchange = _case("platform.quote.signedIn")
        exchange["response"]["body"]["creditBalanceCents"] = "5000"
        failures = _check(entry, exchange)
        self.assertTrue(any("creditBalanceCents" in f and "integer" in f for f in failures), failures)

    def test_a_value_outside_an_enum_or_const(self) -> None:
        entry, exchange = _case("platform.quote.public")
        exchange["response"]["body"]["coverage"] = "uncovered"
        exchange["response"]["body"]["currency"] = "eur"
        failures = _check(entry, exchange)
        self.assertTrue(any("coverage" in f for f in failures), failures)
        self.assertTrue(any("currency" in f for f in failures), failures)

    def test_a_refusal_without_its_handoff_url(self) -> None:
        entry, exchange = _case("platform.order.insufficientCredit")
        del exchange["response"]["body"]["handoffUrl"]
        failures = _check(entry, exchange)
        self.assertTrue(any("handoffUrl" in f for f in failures), failures)

    def test_a_status_the_operation_does_not_declare(self) -> None:
        entry, exchange = _case("platform.quote.uncovered")
        exchange["response"]["status"] = 404
        failures = _check(entry, exchange)
        self.assertTrue(any("404" in f for f in failures), failures)

    def test_an_unknown_operation(self) -> None:
        entry, exchange = _case("platform.quote.public")
        exchange["operationId"] = "getQuotes"
        failures = _check(entry, exchange)
        self.assertTrue(any("getQuotes" in f for f in failures), failures)

    def test_a_method_or_path_the_operation_does_not_have(self) -> None:
        entry, exchange = _case("platform.quote.public")
        exchange["request"]["method"] = "POST"
        exchange["request"]["path"] = "/api/v1/quotes"
        failures = _check(entry, exchange)
        self.assertTrue(any("POST" in f for f in failures), failures)
        self.assertTrue(any("/api/v1/quotes" in f for f in failures), failures)

    def test_a_request_body_the_schema_refuses(self) -> None:
        entry, exchange = _case("platform.order.priceChanged")
        exchange["request"]["body"]["payment"] = "card"
        failures = _check(entry, exchange)
        self.assertTrue(any("payment" in f for f in failures), failures)

    def test_an_undeclared_query_parameter_or_header(self) -> None:
        entry, exchange = _case("platform.quote.public")
        exchange["request"]["query"]["bbox"] = "x"
        exchange["request"]["headers"]["X-Extra"] = "1"
        failures = _check(entry, exchange)
        self.assertTrue(any("bbox" in f for f in failures), failures)
        self.assertTrue(any("X-Extra" in f for f in failures), failures)

    def test_a_required_parameter_left_out_without_saying_so(self) -> None:
        entry, exchange = _case("platform.order.idempotencyKeyMissing")
        del exchange["request"]["omits"]
        failures = _check(entry, exchange)
        self.assertTrue(any("Idempotency-Key" in f for f in failures), failures)

    def test_a_declared_omission_the_platform_accepted(self) -> None:
        entry, exchange = _case("platform.order.idempotencyKeyMissing")
        exchange["response"] = {"status": 201, "body": {"orderId": "o", "status": "paid"}}
        entry["expectations"] = {}
        failures = _check(entry, exchange)
        self.assertTrue(any("omits" in f for f in failures), failures)

    def test_a_bearer_on_an_operation_with_no_bearer_security(self) -> None:
        entry, exchange = _case("platform.quote.public")
        exchange["operationId"] = "getRealtimeConfig"
        exchange["request"] = {"method": "GET", "path": "/api/v1/realtime",
                               "headers": {"Authorization": "Bearer t"}}
        failures = _check(entry, exchange)
        self.assertTrue(any("Authorization" in f for f in failures), failures)

    def test_an_expectation_that_disagrees_with_the_response(self) -> None:
        entry, exchange = _case("platform.order.freeCapReached")
        entry["expectations"]["code"] = "price_changed"
        entry["expectations"]["status"] = 402
        failures = _check(entry, exchange)
        self.assertTrue(any("code" in f for f in failures), failures)
        self.assertTrue(any("status" in f for f in failures), failures)

    def test_an_accept_case_must_be_a_success_and_a_reject_case_a_refusal(self) -> None:
        entry, exchange = _case("platform.quote.invalidExtent")
        entry["expect"] = "accept"
        self.assertTrue(any("accept" in f for f in _check(entry, exchange)))

    def test_the_gate_fails_a_case_the_spec_stops_allowing(self) -> None:
        # The same corpus, held to a spec whose 426 code is another: the gate itself goes red.
        moved = copy.deepcopy(_SPEC)
        moved["components"]["schemas"]["ClientUpdateRequired"]["properties"]["code"]["const"] = "client_too_old"
        with mock.patch.object(platform, "load_vendored_spec", return_value=moved):
            failures = gate.check_corpus()
        self.assertTrue(any("'client.updateRequired'" in f and "client_too_old" in f for f in failures), failures)
        self.assertEqual(gate.check_corpus(), [])


class SchemaValidatorTest(unittest.TestCase):
    def test_a_keyword_the_validator_does_not_know_is_loud(self) -> None:
        failures = platform.validate_schema(1, {"type": "integer", "multipleOf": 2}, {}, "$")
        self.assertTrue(any("multipleOf" in f for f in failures), failures)

    def test_a_bool_is_not_an_integer_or_a_number(self) -> None:
        self.assertTrue(platform.validate_schema(True, {"type": "integer"}, {}, "$"))
        self.assertTrue(platform.validate_schema(True, {"type": "number"}, {}, "$"))
        self.assertFalse(platform.validate_schema(3, {"type": "number"}, {}, "$"))

    def test_any_of_passes_when_one_branch_does(self) -> None:
        schema = {"anyOf": [{"type": "string"}, {"type": "null"}]}
        self.assertFalse(platform.validate_schema(None, schema, {}, "$"))
        self.assertTrue(platform.validate_schema(3, schema, {}, "$"))

    def test_a_ref_resolves_into_the_document(self) -> None:
        doc = {"components": {"schemas": {"N": {"type": "integer", "minimum": 1}}}}
        self.assertTrue(platform.validate_schema(0, {"$ref": "#/components/schemas/N"}, doc, "$"))
        self.assertFalse(platform.validate_schema(2, {"$ref": "#/components/schemas/N"}, doc, "$"))


class SpecIdentityTest(unittest.TestCase):
    def test_identity_names_the_version_and_a_digest(self) -> None:
        identity = platform.spec_identity(_SPEC)
        self.assertEqual(identity["version"], _SPEC["info"]["version"])
        self.assertRegex(identity["sha256"], r"^[0-9a-f]{64}$")

    def test_the_digest_follows_the_content_not_the_line_endings(self) -> None:
        text = platform.VENDORED_SPEC.read_text(encoding="utf-8")
        self.assertEqual(platform.spec_identity(json.loads(text.replace("\n", "\r\n"))),
                         platform.spec_identity(json.loads(text)))
        moved = copy.deepcopy(_SPEC)
        moved["info"]["description"] += " More."
        self.assertNotEqual(platform.spec_identity(moved)["sha256"], platform.spec_identity(_SPEC)["sha256"])


class RecordsTest(unittest.TestCase):
    _IDENTITY = {"version": "1", "sha256": "a" * 64}

    def _entries(self, **records) -> list[tuple[str, dict]]:
        return [
            ("claims", {"groups": ["manifest", "platform"], **records}),
            ("ignores", {"groups": ["manifest"]}),
        ]

    def test_matching_records_pass(self) -> None:
        entries = self._entries(verifiedAgainstPlatformApi=dict(self._IDENTITY))
        self.assertEqual(platform.check_records(self._IDENTITY, {"platformApi": dict(self._IDENTITY)}, entries), [])

    def test_a_baseline_that_names_another_spec_fails(self) -> None:
        entries = self._entries(verifiedAgainstPlatformApi=dict(self._IDENTITY))
        failures = platform.check_records(self._IDENTITY, {"platformApi": {"version": "1", "sha256": "b" * 64}}, entries)
        self.assertTrue(any("coverage-baseline.json" in f for f in failures), failures)

    def test_a_baseline_with_no_record_fails(self) -> None:
        entries = self._entries(verifiedAgainstPlatformApi=dict(self._IDENTITY))
        self.assertTrue(platform.check_records(self._IDENTITY, {}, entries))

    def test_a_claiming_host_without_a_record_fails_and_names_itself(self) -> None:
        failures = platform.check_records(self._IDENTITY, {"platformApi": dict(self._IDENTITY)}, self._entries())
        self.assertTrue(any("[claims]" in f for f in failures), failures)
        self.assertFalse(any("[ignores]" in f for f in failures), failures)

    def test_a_claiming_host_on_another_spec_fails(self) -> None:
        entries = self._entries(verifiedAgainstPlatformApi={"version": "1", "sha256": "c" * 64})
        failures = platform.check_records(self._IDENTITY, {"platformApi": dict(self._IDENTITY)}, entries)
        self.assertTrue(any("[claims]" in f for f in failures), failures)

    def test_the_shipped_records_name_the_vendored_spec(self) -> None:
        identity = platform.spec_identity(_SPEC)
        baseline = json.loads(gate._BASELINE.read_text(encoding="utf-8"))
        pinned = json.loads(gate._PINNED.read_text(encoding="utf-8"))
        self.assertEqual(platform.check_records(identity, baseline, gate.host_entries(pinned)), [])

    def test_both_hosts_claim_the_client_group_and_revit_the_platform_group(self) -> None:
        pinned = json.loads(gate._PINNED.read_text(encoding="utf-8"))
        self.assertIn("client", pinned["unreal"]["groups"])
        self.assertIn("client", pinned["revit"]["groups"])
        self.assertIn("platform", pinned["revit"]["groups"])


if __name__ == "__main__":
    unittest.main()
