"""The Platform API leg of the conformance gate: the `platform` and `client` corpus groups.

Those two groups hold exchanges with the Platform API rather than documents a parser reads: one
request a host sends and the answer the platform gives (`corpus/README.md`, "The Platform API
groups"). A host suite replays each one against its client. This module holds the corpus to the
spec the platform serves at https://mantle.place/api/v1/openapi.json, so a case can never pin a
behaviour the platform does not have:

- the operation exists, and the case's method and path are that operation's;
- every query parameter and header the request sends is one the operation declares, every
  required one is sent unless the case declares it left out (`omits`), and a case that leaves one
  out is answered with a refusal;
- the request body and the response body validate against the operation's own schemas, and the
  status is one the operation declares;
- the case's `status` and `code` expectations say what the response says.

Two copies of the spec are read. Pull requests read the vendored copy beside the generated .NET
client (`revit/src/MantlePlace.PlatformApi/Generated/openapi.json`), which `ci-platform-api` keeps
equal to the served one, so a pull request fetches nothing. The scheduled live run fetches the
served copy too.

**The record.** The spec's identity (its `info.version` and the sha256 of its canonical text) is
recorded in `coverage-baseline.json` (`platformApi`) and in each claiming host's
`verifiedAgainstPlatformApi`. The gate fails when either names another spec than the vendored one:
the spec moved, so the cases are re-checked and the record raised in the same change.

The schema validator is the subset of JSON Schema the spec uses, stdlib only like the rest of the
gate. A keyword it does not know fails loudly rather than passing unread.
"""

from __future__ import annotations

import hashlib
import json
import re
import urllib.error
import urllib.request
from pathlib import Path

_REPO_ROOT = Path(__file__).resolve().parents[2]

#: The corpus groups that hold Platform API exchanges.
PLATFORM_GROUPS = frozenset({"platform", "client"})

#: The spec the generated .NET client was generated from, vendored by
#: revit/tools/platform-api/sync_platform_api.py and kept current by ci-platform-api.
VENDORED_SPEC = _REPO_ROOT / "revit" / "src" / "MantlePlace.PlatformApi" / "Generated" / "openapi.json"

SERVED_SPEC_URL = "https://mantle.place/api/v1/openapi.json"

#: Python's default user agent is refused by the edge in front of mantle.place; name the caller.
_USER_AGENT = "mantleplace-dcc conformance gate"

_VERBS = ("get", "put", "post", "delete", "patch")

#: Headers every request may carry whatever the operation declares: they are HTTP's, not the API's.
_TRANSPORT_HEADERS = frozenset({"accept", "content-type"})

#: Keywords that describe rather than constrain.
_ANNOTATIONS = frozenset({"description", "title", "default", "examples", "format", "deprecated",
                          "readOnly", "writeOnly", "$comment"})


def canonical(spec: dict) -> str:
    """The spec as vendored: re-written with a stable indent, key order kept, LF line ends."""
    return json.dumps(spec, indent=2, ensure_ascii=False) + "\n"


def spec_identity(spec: dict) -> dict:
    """`{version, sha256}`: what the record names. The digest is over the canonical text, so line
    endings and whitespace do not move it and any change of content does."""
    version = spec.get("info", {}).get("version") if isinstance(spec.get("info"), dict) else None
    digest = hashlib.sha256(canonical(spec).encode("utf-8")).hexdigest()
    return {"version": version, "sha256": digest}


def load_vendored_spec(path: Path = VENDORED_SPEC) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def fetch_served_spec(timeout: int = 30) -> dict | None:
    """The spec as the platform serves it now, or None when it cannot be read."""
    request = urllib.request.Request(SERVED_SPEC_URL, headers={"Accept": "application/json",
                                                               "User-Agent": _USER_AGENT})
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:  # noqa: S310 - fixed https URL
            return json.loads(response.read().decode("utf-8"))
    except (urllib.error.URLError, TimeoutError, json.JSONDecodeError, UnicodeDecodeError):
        return None


def _resolve(node: object, spec: dict) -> object:
    """Follows a local `$ref` (`#/components/...`) until the node is not one."""
    seen = 0
    while isinstance(node, dict) and isinstance(node.get("$ref"), str):
        ref = node["$ref"]
        if not ref.startswith("#/"):
            raise KeyError(f"non-local $ref {ref!r}")
        target: object = spec
        for part in ref[2:].split("/"):
            part = part.replace("~1", "/").replace("~0", "~")
            if not isinstance(target, dict) or part not in target:
                raise KeyError(f"$ref {ref!r} does not resolve")
            target = target[part]
        node = target
        seen += 1
        if seen > 32:
            raise KeyError(f"$ref {ref!r} loops")
    return node


def _type_matches(value: object, wanted: str) -> bool:
    if wanted == "null":
        return value is None
    if wanted == "boolean":
        return isinstance(value, bool)
    if wanted == "integer":
        return isinstance(value, int) and not isinstance(value, bool) or (
            isinstance(value, float) and value.is_integer())
    if wanted == "number":
        return isinstance(value, (int, float)) and not isinstance(value, bool)
    if wanted == "string":
        return isinstance(value, str)
    if wanted == "array":
        return isinstance(value, list)
    if wanted == "object":
        return isinstance(value, dict)
    return False


def validate_schema(value: object, schema: object, spec: dict, where: str) -> list[str]:
    """Validates `value` against `schema` (resolved within `spec`). Returns one line per breach,
    each naming the JSON path (`where`). An unknown keyword is itself a breach."""
    if schema is True or schema == {}:
        return []
    if schema is False:
        return [f"{where}: no value is allowed here"]
    try:
        schema = _resolve(schema, spec)
    except KeyError as exc:
        return [f"{where}: {exc}"]
    if not isinstance(schema, dict):
        return [f"{where}: the schema is not an object"]

    problems: list[str] = []
    for keyword in schema:
        if keyword not in _KEYWORDS and keyword not in _ANNOTATIONS:
            problems.append(f"{where}: the schema uses `{keyword}`, which this validator does not "
                            "know; teach platform_cases.py the keyword rather than skip it")
    if problems:
        return problems

    wanted = schema.get("type")
    if wanted is not None:
        types = wanted if isinstance(wanted, list) else [wanted]
        if not any(_type_matches(value, t) for t in types):
            return [f"{where}: {json.dumps(value)[:60]} is not of type {' or '.join(types)}"]

    if "const" in schema and value != schema["const"]:
        problems.append(f"{where}: {json.dumps(value)[:60]} is not the const {json.dumps(schema['const'])}")
    if "enum" in schema and value not in schema["enum"]:
        problems.append(f"{where}: {json.dumps(value)[:60]} is not one of {json.dumps(schema['enum'])}")
    if "anyOf" in schema:
        if all(validate_schema(value, branch, spec, where) for branch in schema["anyOf"]):
            problems.append(f"{where}: {json.dumps(value)[:60]} matches no branch of anyOf")

    if isinstance(value, (int, float)) and not isinstance(value, bool):
        if "minimum" in schema and value < schema["minimum"]:
            problems.append(f"{where}: {value} is below the minimum {schema['minimum']}")
        if "maximum" in schema and value > schema["maximum"]:
            problems.append(f"{where}: {value} is above the maximum {schema['maximum']}")

    if isinstance(value, str):
        if "minLength" in schema and len(value) < schema["minLength"]:
            problems.append(f"{where}: shorter than minLength {schema['minLength']}")
        if "maxLength" in schema and len(value) > schema["maxLength"]:
            problems.append(f"{where}: longer than maxLength {schema['maxLength']}")
        if "pattern" in schema and not re.search(schema["pattern"], value):
            problems.append(f"{where}: {value!r} does not match the pattern {schema['pattern']!r}")

    if isinstance(value, list):
        if "minItems" in schema and len(value) < schema["minItems"]:
            problems.append(f"{where}: fewer than minItems {schema['minItems']}")
        if "maxItems" in schema and len(value) > schema["maxItems"]:
            problems.append(f"{where}: more than maxItems {schema['maxItems']}")
        if "items" in schema:
            for position, item in enumerate(value):
                problems.extend(validate_schema(item, schema["items"], spec, f"{where}[{position}]"))

    if isinstance(value, dict):
        properties = schema.get("properties", {})
        for name in schema.get("required", []):
            if name not in value:
                problems.append(f"{where}: the required member `{name}` is missing")
        for name, member in value.items():
            if name in properties:
                problems.extend(validate_schema(member, properties[name], spec, f"{where}.{name}"))
            elif "additionalProperties" in schema:
                extra = schema["additionalProperties"]
                if extra is False:
                    problems.append(f"{where}: `{name}` is not a member the schema allows")
                else:
                    problems.extend(validate_schema(member, extra, spec, f"{where}.{name}"))
            if "propertyNames" in schema:
                problems.extend(validate_schema(name, schema["propertyNames"], spec, f"{where} (name {name!r})"))
    return problems


_KEYWORDS = frozenset({"$ref", "type", "const", "enum", "anyOf", "minimum", "maximum", "minLength",
                       "maxLength", "pattern", "minItems", "maxItems", "items", "properties",
                       "required", "additionalProperties", "propertyNames"})


def _find_operation(spec: dict, operation_id: str) -> tuple[str, str, dict] | None:
    for path, item in spec.get("paths", {}).items():
        for verb in _VERBS:
            operation = item.get(verb)
            if isinstance(operation, dict) and operation.get("operationId") == operation_id:
                return verb.upper(), path, operation
    return None


def _path_matches(template: str, path: str) -> bool:
    pattern = "^" + re.sub(r"\\\{[^}]+\\\}", "[^/]+", re.escape(template)) + "$"
    return re.match(pattern, path) is not None


def _allows_bearer(operation: dict, spec: dict) -> bool:
    security = operation.get("security", spec.get("security", []))
    return any("bearerAuth" in requirement for requirement in security if isinstance(requirement, dict))


def check_exchange(entry: dict, exchange: object, spec: dict, label: str) -> list[str]:
    """Holds one case to the spec. `entry` is its index entry, `exchange` its file's content,
    `label` names the spec copy in messages. Returns failure lines."""
    case_id = entry.get("id", "?")

    def fail(detail: str) -> str:
        return f"FAIL: corpus case '{case_id}' is not an exchange {label} allows: {detail}"

    if not isinstance(exchange, dict):
        return [fail("the file is not an object with operationId, request and response")]
    operation_id = exchange.get("operationId")
    request = exchange.get("request")
    response = exchange.get("response")
    if not isinstance(operation_id, str) or not isinstance(request, dict) or not isinstance(response, dict):
        return [fail("the file needs operationId (a string), request and response (objects)")]

    found = _find_operation(spec, operation_id)
    if found is None:
        return [fail(f"no operation has operationId {operation_id!r}")]
    verb, template, operation = found
    failures: list[str] = []

    if request.get("method") != verb:
        failures.append(fail(f"{operation_id} is {verb}, the request says {request.get('method')!r}"))
    if not isinstance(request.get("path"), str) or not _path_matches(template, request["path"]):
        failures.append(fail(f"{operation_id} is at {template}, the request says {request.get('path')!r}"))

    try:
        parameters = [_resolve(p, spec) for p in operation.get("parameters", [])]
    except KeyError as exc:
        return [fail(str(exc))]
    declared = {(p["in"], p["name"].lower()): p for p in parameters if isinstance(p, dict)}
    query = request.get("query", {})
    headers = request.get("headers", {})
    omits = request.get("omits", [])
    if not isinstance(query, dict) or not isinstance(headers, dict) or not isinstance(omits, list):
        return failures + [fail("query and headers are objects, omits a list")]

    for name, value in query.items():
        parameter = declared.get(("query", name.lower()))
        if parameter is None:
            failures.append(fail(f"the query parameter `{name}` is not one {operation_id} declares"))
        else:
            failures.extend(fail(p) for p in validate_schema(value, parameter.get("schema", {}), spec, f"query.{name}"))
    for name, value in headers.items():
        lowered = name.lower()
        if lowered == "authorization":
            if not _allows_bearer(operation, spec):
                failures.append(fail(f"the request sends Authorization, but {operation_id} takes no bearer"))
            elif not isinstance(value, str) or not value.startswith("Bearer "):
                failures.append(fail("Authorization is a `Bearer <token>`"))
            continue
        if lowered in _TRANSPORT_HEADERS:
            continue
        parameter = declared.get(("header", lowered))
        if parameter is None:
            failures.append(fail(f"the header `{name}` is not one {operation_id} declares"))
        else:
            failures.extend(fail(p) for p in validate_schema(value, parameter.get("schema", {}), spec, f"headers.{name}"))

    sent = {("query", n.lower()) for n in query} | {("header", n.lower()) for n in headers}
    left_out = {name.lower() for name in omits if isinstance(name, str)}
    for (where, name), parameter in declared.items():
        if parameter.get("required") and where in ("query", "header") and (where, name) not in sent:
            if name not in left_out:
                failures.append(fail(f"the required {where} parameter `{parameter['name']}` is not sent; "
                                     "a case that leaves it out on purpose says so in request.omits"))
    for name in left_out:
        parameter = declared.get(("header", name)) or declared.get(("query", name))
        if parameter is None or not parameter.get("required"):
            failures.append(fail(f"request.omits names `{name}`, which is not a required parameter"))

    status = response.get("status")
    body_schema_holder = operation.get("requestBody")
    if body_schema_holder is not None:
        holder = _resolve(body_schema_holder, spec)
        schema = holder.get("content", {}).get("application/json", {}).get("schema")
        if "body" not in request:
            if holder.get("required"):
                failures.append(fail(f"{operation_id} requires a request body"))
        elif schema is not None:
            failures.extend(fail(p) for p in validate_schema(request["body"], schema, spec, "request.body"))
    elif "body" in request:
        failures.append(fail(f"{operation_id} takes no request body"))

    if not isinstance(status, int) or isinstance(status, bool):
        return failures + [fail("response.status is an integer")]
    answer = operation.get("responses", {}).get(str(status))
    if answer is None:
        failures.append(fail(f"{operation_id} declares no {status} response "
                             f"(it declares {', '.join(operation.get('responses', {}))})"))
    else:
        answer = _resolve(answer, spec)
        schema = answer.get("content", {}).get("application/json", {}).get("schema")
        if schema is not None:
            if "body" not in response:
                failures.append(fail(f"the {status} response carries a JSON body"))
            else:
                failures.extend(fail(p) for p in validate_schema(response["body"], schema, spec, "response.body"))

    success = 200 <= status < 300
    if left_out and success:
        failures.append(fail("a request that omits a required parameter is answered with a refusal, "
                             f"not {status}"))
    expect = entry.get("expect")
    if expect == "accept" and not success:
        failures.append(fail(f"an accept case is a success; the response is {status}"))
    if expect == "reject" and success:
        failures.append(fail(f"a reject case is a refusal; the response is {status}"))

    expectations = entry.get("expectations") or {}
    body = response.get("body")
    if "status" in expectations and expectations["status"] != status:
        failures.append(fail(f"expectations.status is {expectations['status']}, the response {status}"))
    if "code" in expectations and (not isinstance(body, dict) or body.get("code") != expectations["code"]):
        actual = body.get("code") if isinstance(body, dict) else None
        failures.append(fail(f"expectations.code is {expectations['code']!r}, the response {actual!r}"))
    return failures


def check_platform_cases(raw_cases: list[dict], corpus: Path, spec: dict, label: str) -> list[str]:
    """Every `platform`/`client` case held to `spec`. Unreadable files are check_corpus's to report."""
    failures: list[str] = []
    for entry in raw_cases:
        if entry.get("group") not in PLATFORM_GROUPS or not entry.get("file"):
            continue
        try:
            exchange = json.loads((corpus / entry["file"]).read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            continue
        failures.extend(check_exchange(entry, exchange, spec, label))
    return failures


def check_records(identity: dict, baseline_doc: dict, entries: list[tuple[str, dict]]) -> list[str]:
    """The baseline and every host claiming a Platform API group name `identity`."""
    failures: list[str] = []
    named = f"version {identity['version']!r}, sha256 {identity['sha256'][:12]}..."
    if baseline_doc.get("platformApi") != identity:
        failures.append(
            "FAIL: coverage-baseline.json does not name the Platform API spec the cases were checked "
            f"against ({named}). The spec moved or the record is stale: re-check the platform and "
            "client cases, then rerun with --update-baseline and commit the diff."
        )
    for host, entry in entries:
        groups = entry.get("groups") if isinstance(entry.get("groups"), list) else []
        if not PLATFORM_GROUPS & set(groups):
            continue
        record = entry.get("verifiedAgainstPlatformApi")
        if record != identity:
            failures.append(
                f"FAIL [{host}]: verifiedAgainstPlatformApi is {json.dumps(record)}, not the vendored "
                f"spec ({named}). A host claiming the platform or client group records the spec its "
                "suite was run against; run it against this one and raise the record."
            )
    return failures
