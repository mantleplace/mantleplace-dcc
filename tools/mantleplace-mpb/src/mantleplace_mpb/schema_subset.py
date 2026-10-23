# SPDX-License-Identifier: Apache-2.0
# SPDX-FileCopyrightText: Mantle Place LLC
"""A JSON Schema 2020-12 evaluator for the keywords the published manifest series uses.

Standard library only, so the validator installs anywhere without a compiled dependency. It is
deliberately not a general implementation: it evaluates the vocabulary below with the
specification's semantics, and it **refuses** a schema that uses anything else. A keyword it
skipped would be a rule it silently did not check, so an unknown keyword, a remote `$ref` or
another dialect raises `UnsupportedSchema` before any instance is evaluated, and the caller
reports "could not check" rather than "pass".
"""

from __future__ import annotations

import re
from decimal import Decimal
from urllib.parse import unquote

DIALECT = "https://json-schema.org/draft/2020-12/schema"

#: Keywords that only annotate. Evaluating them is a no-op by the 2020-12 specification
#: (`format` included: it is an annotation unless a validator opts into the format-assertion
#: vocabulary, and this one does not).
_ANNOTATIONS = frozenset({
    "$schema", "$id", "$comment", "$defs", "title", "description", "examples", "default",
    "deprecated", "readOnly", "writeOnly", "format", "contentMediaType", "contentEncoding",
})
_SCHEMA_MAPS = frozenset({"$defs", "properties", "patternProperties", "dependentSchemas"})
_SCHEMA_ONE = frozenset({"additionalProperties", "propertyNames", "items", "contains", "not", "if",
                         "then", "else"})
_SCHEMA_LIST = frozenset({"allOf", "anyOf", "oneOf", "prefixItems"})
_ASSERTIONS = frozenset({
    "$ref", "type", "enum", "const", "multipleOf", "maximum", "exclusiveMaximum", "minimum",
    "exclusiveMinimum", "maxLength", "minLength", "pattern", "maxItems", "minItems", "uniqueItems",
    "maxContains", "minContains", "maxProperties", "minProperties", "required", "dependentRequired",
})
KNOWN_KEYWORDS = _ANNOTATIONS | _SCHEMA_MAPS | _SCHEMA_ONE | _SCHEMA_LIST | _ASSERTIONS


class UnsupportedSchema(Exception):
    """The schema uses something this evaluator does not evaluate; nothing was checked."""


def escape(token: str) -> str:
    """One JSON Pointer reference token (RFC 6901)."""
    return token.replace("~", "~0").replace("/", "~1")


def errors(schema: dict, instance: object) -> list[tuple[str, str]]:
    """Every way `instance` fails `schema`, as (JSON Pointer into the instance, message)."""
    if not isinstance(schema, dict):
        raise UnsupportedSchema("the root schema is not an object")
    dialect = schema.get("$schema")
    if dialect is not None and dialect.rstrip("#") != DIALECT:
        raise UnsupportedSchema(f"dialect {dialect!r} is not JSON Schema 2020-12")
    _audit(schema, schema, "#")
    out: list[tuple[str, str]] = []
    _Evaluator(schema).evaluate(schema, instance, "", out)
    return out


def _audit(root: dict, node: object, where: str) -> None:
    """Refuse, up front, any keyword or reference the evaluator would not honour."""
    if isinstance(node, bool):
        return
    if not isinstance(node, dict):
        raise UnsupportedSchema(f"{where} is not a schema")
    for key, value in node.items():
        if key not in KNOWN_KEYWORDS:
            raise UnsupportedSchema(f"{where} uses the keyword {key!r}, which this validator "
                                    "does not evaluate")
        if key in _SCHEMA_MAPS:
            for name, sub in value.items():
                _audit(root, sub, f"{where}/{key}/{escape(name)}")
        elif key in _SCHEMA_ONE:
            _audit(root, value, f"{where}/{key}")
        elif key in _SCHEMA_LIST:
            for i, sub in enumerate(value):
                _audit(root, sub, f"{where}/{key}/{i}")
        elif key == "$ref":
            _resolve(root, value)
        elif key == "pattern":
            _compile(value)
    for pattern in (node.get("patternProperties") or {}):
        _compile(pattern)


def _resolve(root: dict, ref: str) -> object:
    base = str(root.get("$id", ""))
    fragment = ref
    if base and ref.startswith(base + "#"):
        fragment = ref[len(base):]
    if not fragment.startswith("#"):
        raise UnsupportedSchema(f"$ref {ref!r} is not a reference inside this schema")
    node: object = root
    path = unquote(fragment[1:])
    if path and not path.startswith("/"):
        raise UnsupportedSchema(f"$ref {ref!r} names an anchor, which this validator does not resolve")
    for token in path.split("/")[1:] if path else []:
        token = token.replace("~1", "/").replace("~0", "~")
        if isinstance(node, dict) and token in node:
            node = node[token]
        elif isinstance(node, list) and token.isdigit() and int(token) < len(node):
            node = node[int(token)]
        else:
            raise UnsupportedSchema(f"$ref {ref!r} does not resolve")
    return node


_PATTERNS: dict[str, re.Pattern[str]] = {}


def _compile(pattern: str) -> re.Pattern[str]:
    compiled = _PATTERNS.get(pattern)
    if compiled is None:
        try:
            compiled = re.compile(pattern)
        except re.error as exc:
            raise UnsupportedSchema(f"pattern {pattern!r} does not compile: {exc}") from exc
        _PATTERNS[pattern] = compiled
    return compiled


def _is_number(value: object) -> bool:
    return isinstance(value, (int, float)) and not isinstance(value, bool)


def _type_of(value: object) -> str:
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "boolean"
    if isinstance(value, int):
        return "integer"
    if isinstance(value, float):
        return "integer" if value.is_integer() else "number"
    if isinstance(value, str):
        return "string"
    if isinstance(value, list):
        return "array"
    if isinstance(value, dict):
        return "object"
    return type(value).__name__


def _has_type(value: object, wanted: str) -> bool:
    actual = _type_of(value)
    return actual == wanted or (wanted == "number" and actual == "integer")


def json_equal(a: object, b: object) -> bool:
    """JSON equality: 1 equals 1.0, and a boolean never equals a number."""
    if isinstance(a, bool) or isinstance(b, bool):
        return isinstance(a, bool) and isinstance(b, bool) and a == b
    if _is_number(a) and _is_number(b):
        return a == b
    if isinstance(a, list) and isinstance(b, list):
        return len(a) == len(b) and all(json_equal(x, y) for x, y in zip(a, b))
    if isinstance(a, dict) and isinstance(b, dict):
        return a.keys() == b.keys() and all(json_equal(a[k], b[k]) for k in a)
    return type(a) is type(b) and a == b


def _short(value: object) -> str:
    text = repr(value)
    return text if len(text) <= 60 else text[:57] + "..."


class _Evaluator:
    def __init__(self, root: dict) -> None:
        self.root = root

    def valid(self, schema: object, instance: object, where: str) -> bool:
        scratch: list[tuple[str, str]] = []
        self.evaluate(schema, instance, where, scratch)
        return not scratch

    def evaluate(self, schema: object, instance: object, where: str,
                 out: list[tuple[str, str]]) -> None:
        if schema is True:
            return
        if schema is False:
            out.append((where, "no value is allowed here"))
            return
        assert isinstance(schema, dict)
        if "$ref" in schema:
            self.evaluate(_resolve(self.root, schema["$ref"]), instance, where, out)
        self._generic(schema, instance, where, out)
        self._combinators(schema, instance, where, out)
        if _is_number(instance):
            self._numeric(schema, instance, where, out)
        elif isinstance(instance, str):
            self._string(schema, instance, where, out)
        elif isinstance(instance, list):
            self._array(schema, instance, where, out)
        elif isinstance(instance, dict):
            self._object(schema, instance, where, out)

    def _generic(self, schema: dict, instance: object, where: str, out: list) -> None:
        if "type" in schema:
            wanted = schema["type"]
            options = wanted if isinstance(wanted, list) else [wanted]
            if not any(_has_type(instance, t) for t in options):
                out.append((where, f"{_short(instance)} is not of type {' or '.join(options)}"))
        if "const" in schema and not json_equal(instance, schema["const"]):
            out.append((where, f"{_short(instance)} is not {_short(schema['const'])}"))
        if "enum" in schema and not any(json_equal(instance, v) for v in schema["enum"]):
            out.append((where, f"{_short(instance)} is not one of {_short(schema['enum'])}"))

    def _combinators(self, schema: dict, instance: object, where: str, out: list) -> None:
        for sub in schema.get("allOf", ()):
            self.evaluate(sub, instance, where, out)
        if "anyOf" in schema and not any(self.valid(s, instance, where) for s in schema["anyOf"]):
            out.append((where, f"matches none of the {len(schema['anyOf'])} anyOf alternatives"
                        + self._closest(schema["anyOf"], instance, where)))
        if "oneOf" in schema:
            matched = sum(1 for s in schema["oneOf"] if self.valid(s, instance, where))
            if matched != 1:
                closest = self._closest(schema["oneOf"], instance, where) if matched == 0 else ""
                out.append((where, f"matches {matched} of the {len(schema['oneOf'])} oneOf "
                            "alternatives; exactly one is required" + closest))
        if "not" in schema and self.valid(schema["not"], instance, where):
            out.append((where, f"{_short(instance)} matches a schema it must not match"))
        if "if" in schema:
            branch = "then" if self.valid(schema["if"], instance, where) else "else"
            if branch in schema:
                self.evaluate(schema[branch], instance, where, out)

    def _closest(self, options: list, instance: object, where: str) -> str:
        best: list[tuple[str, str]] | None = None
        for sub in options:
            scratch: list[tuple[str, str]] = []
            self.evaluate(sub, instance, where, scratch)
            if best is None or len(scratch) < len(best):
                best = scratch
        if not best:
            return ""
        pointer, message = best[0]
        return f" (closest: {pointer or '/'}: {message})"

    def _numeric(self, schema: dict, value: float, where: str, out: list) -> None:
        if "minimum" in schema and value < schema["minimum"]:
            out.append((where, f"{value} is less than the minimum {schema['minimum']}"))
        if "maximum" in schema and value > schema["maximum"]:
            out.append((where, f"{value} is greater than the maximum {schema['maximum']}"))
        if "exclusiveMinimum" in schema and value <= schema["exclusiveMinimum"]:
            out.append((where, f"{value} is not greater than {schema['exclusiveMinimum']}"))
        if "exclusiveMaximum" in schema and value >= schema["exclusiveMaximum"]:
            out.append((where, f"{value} is not less than {schema['exclusiveMaximum']}"))
        if "multipleOf" in schema:
            step = schema["multipleOf"]
            if Decimal(str(value)) % Decimal(str(step)) != 0:
                out.append((where, f"{value} is not a multiple of {step}"))

    def _string(self, schema: dict, value: str, where: str, out: list) -> None:
        if "minLength" in schema and len(value) < schema["minLength"]:
            out.append((where, f"{_short(value)} is shorter than {schema['minLength']}"))
        if "maxLength" in schema and len(value) > schema["maxLength"]:
            out.append((where, f"{_short(value)} is longer than {schema['maxLength']}"))
        if "pattern" in schema and not _compile(schema["pattern"]).search(value):
            out.append((where, f"{_short(value)} does not match {schema['pattern']!r}"))

    def _array(self, schema: dict, value: list, where: str, out: list) -> None:
        if "minItems" in schema and len(value) < schema["minItems"]:
            out.append((where, f"has {len(value)} item(s); at least {schema['minItems']} required"))
        if "maxItems" in schema and len(value) > schema["maxItems"]:
            out.append((where, f"has {len(value)} item(s); at most {schema['maxItems']} allowed"))
        if schema.get("uniqueItems"):
            for i, item in enumerate(value):
                if any(json_equal(item, other) for other in value[:i]):
                    out.append((f"{where}/{i}", "repeats an earlier item; items must be unique"))
                    break
        prefix = schema.get("prefixItems", [])
        for i, sub in enumerate(prefix[: len(value)]):
            self.evaluate(sub, value[i], f"{where}/{i}", out)
        if "items" in schema:
            for i in range(len(prefix), len(value)):
                self.evaluate(schema["items"], value[i], f"{where}/{i}", out)
        if "contains" in schema:
            hits = sum(1 for i, item in enumerate(value)
                       if self.valid(schema["contains"], item, f"{where}/{i}"))
            low, high = schema.get("minContains", 1), schema.get("maxContains")
            if hits < low or (high is not None and hits > high):
                out.append((where, f"{hits} item(s) match `contains`"))

    def _object(self, schema: dict, value: dict, where: str, out: list) -> None:
        for name in schema.get("required", ()):
            if name not in value:
                out.append((where, f"{name!r} is a required property"))
        for name, needs in (schema.get("dependentRequired") or {}).items():
            if name in value:
                for other in needs:
                    if other not in value:
                        out.append((where, f"{other!r} is required when {name!r} is present"))
        if "minProperties" in schema and len(value) < schema["minProperties"]:
            out.append((where, f"has fewer than {schema['minProperties']} properties"))
        if "maxProperties" in schema and len(value) > schema["maxProperties"]:
            out.append((where, f"has more than {schema['maxProperties']} properties"))
        properties = schema.get("properties") or {}
        patterns = schema.get("patternProperties") or {}
        for name, item in value.items():
            here = f"{where}/{escape(name)}"
            matched = False
            if name in properties:
                matched = True
                self.evaluate(properties[name], item, here, out)
            for pattern, sub in patterns.items():
                if _compile(pattern).search(name):
                    matched = True
                    self.evaluate(sub, item, here, out)
            if not matched and "additionalProperties" in schema:
                extra = schema["additionalProperties"]
                if extra is False:
                    out.append((here, f"{name!r} is not an allowed property here"))
                else:
                    self.evaluate(extra, item, here, out)
            if "propertyNames" in schema:
                self.evaluate(schema["propertyNames"], name, here, out)
        for name, sub in (schema.get("dependentSchemas") or {}).items():
            if name in value:
                self.evaluate(sub, value, where, out)
