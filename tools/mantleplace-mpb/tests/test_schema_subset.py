# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""The JSON Schema 2020-12 evaluator, held to the specification's own semantics on the keywords
the published manifest series uses, and to failing closed on every keyword it does not evaluate."""

from __future__ import annotations

import unittest

import helpers  # noqa: F401  (puts the package source on the path)
from mantleplace_mpb.schema_subset import UnsupportedSchema, errors

S = "https://json-schema.org/draft/2020-12/schema"


def ok(schema: dict, instance: object) -> bool:
    return not errors({"$schema": S, **schema}, instance)


class Types(unittest.TestCase):
    def test_an_integer_is_any_number_with_no_fraction_and_never_a_boolean(self) -> None:
        self.assertTrue(ok({"type": "integer"}, 3))
        self.assertTrue(ok({"type": "integer"}, 3.0))
        self.assertFalse(ok({"type": "integer"}, 3.5))
        self.assertFalse(ok({"type": "integer"}, True))
        self.assertFalse(ok({"type": "number"}, False))
        self.assertTrue(ok({"type": ["string", "null"]}, None))

    def test_const_and_enum_never_equate_a_boolean_with_a_number(self) -> None:
        self.assertFalse(ok({"const": 1}, True))
        self.assertFalse(ok({"enum": [0, "a"]}, False))
        self.assertTrue(ok({"const": {"a": [1, 2]}}, {"a": [1, 2]}))
        self.assertTrue(ok({"const": 1}, 1.0))


class Applicators(unittest.TestCase):
    def test_a_ref_resolves_locally_and_applies_beside_its_siblings(self) -> None:
        schema = {"$defs": {"n": {"type": "number"}},
                  "properties": {"a": {"$ref": "#/$defs/n", "minimum": 2}}}
        self.assertTrue(ok(schema, {"a": 3}))
        self.assertFalse(ok(schema, {"a": "x"}))
        self.assertFalse(ok(schema, {"a": 1}))

    def test_one_of_means_exactly_one(self) -> None:
        schema = {"oneOf": [{"required": ["a"]}, {"required": ["b"]}]}
        self.assertTrue(ok(schema, {"a": 1}))
        self.assertFalse(ok(schema, {"a": 1, "b": 2}))
        self.assertFalse(ok(schema, {}))

    def test_if_then_else(self) -> None:
        schema = {"if": {"properties": {"p": {"const": False}}, "required": ["p"]},
                  "then": {"required": ["reason"]}, "else": {"not": {"required": ["reason"]}}}
        self.assertTrue(ok(schema, {"p": False, "reason": "r"}))
        self.assertFalse(ok(schema, {"p": False}))
        self.assertFalse(ok(schema, {"p": True, "reason": "r"}))

    def test_additional_and_pattern_properties(self) -> None:
        schema = {"properties": {"a": {}}, "patternProperties": {"^x-": {"type": "string"}},
                  "additionalProperties": False}
        self.assertTrue(ok(schema, {"a": 1, "x-n": "s"}))
        self.assertFalse(ok(schema, {"x-n": 1}))
        self.assertFalse(ok(schema, {"b": 1}))

    def test_items_applies_to_every_item_after_the_prefix(self) -> None:
        schema = {"prefixItems": [{"type": "string"}], "items": {"type": "number"}}
        self.assertTrue(ok(schema, ["a", 1, 2]))
        self.assertFalse(ok(schema, ["a", "b"]))


class Assertions(unittest.TestCase):
    def test_a_pattern_matches_anywhere_unless_anchored(self) -> None:
        self.assertTrue(ok({"pattern": "b"}, "abc"))
        self.assertFalse(ok({"pattern": "^b"}, "abc"))
        self.assertTrue(ok({"not": {"pattern": "[Oo][Dd][Bb][Ll]"}}, "CC-BY-4.0"))
        self.assertFalse(ok({"not": {"pattern": "[Oo][Dd][Bb][Ll]"}}, "ODbL-1.0"))

    def test_numeric_and_size_bounds(self) -> None:
        self.assertFalse(ok({"exclusiveMinimum": 0}, 0))
        self.assertTrue(ok({"maximum": 5}, 5))
        self.assertFalse(ok({"minItems": 2}, [1]))
        self.assertFalse(ok({"maxItems": 1}, [1, 2]))
        self.assertFalse(ok({"minLength": 2}, "a"))
        self.assertFalse(ok({"uniqueItems": True}, [1, 1.0]))
        self.assertTrue(ok({"uniqueItems": True}, [1, True]))

    def test_dependent_required(self) -> None:
        schema = {"dependentRequired": {"a": ["b"]}}
        self.assertTrue(ok(schema, {"b": 1}))
        self.assertFalse(ok(schema, {"a": 1}))

    def test_format_is_an_annotation(self) -> None:
        self.assertTrue(ok({"format": "date-time"}, "not a date"))

    def test_an_error_names_the_instance_location(self) -> None:
        found = errors({"$schema": S, "properties": {"a": {"properties": {"b/c": {"type": "string"}}}}},
                       {"a": {"b/c": 1}})
        self.assertEqual(["/a/b~1c"], [pointer for pointer, _ in found])


class FailingClosed(unittest.TestCase):
    def test_a_keyword_it_does_not_evaluate_is_refused(self) -> None:
        for keyword in ("unevaluatedProperties", "$dynamicRef", "x-unknown"):
            with self.subTest(keyword=keyword):
                with self.assertRaises(UnsupportedSchema):
                    errors({"$schema": S, keyword: False}, {})

    def test_a_remote_ref_is_refused(self) -> None:
        with self.assertRaises(UnsupportedSchema):
            errors({"$schema": S, "$ref": "https://example.org/other.json"}, {})

    def test_another_dialect_is_refused(self) -> None:
        with self.assertRaises(UnsupportedSchema):
            errors({"$schema": "http://json-schema.org/draft-07/schema#"}, {})


if __name__ == "__main__":
    unittest.main()
