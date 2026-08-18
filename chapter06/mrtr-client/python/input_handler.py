# handle_input_request: the client host's side of an elicitation.
#
# Two concerns from Chapter 6, Section 6.2.5:
#   1. Form rendering — a schema-driven console form with labels, help text,
#      defaults, the accept/decline/cancel three-action model, and validation
#      of the user's input against the requested schema before responding.
#   2. Policy hooks — pre-user policies that can auto-answer or deny a
#      request without it ever reaching the user.
#
# Modes:
#   - Interactive (default): renders the form on the terminal.
#   - Scripted: MRTR_ANSWERS holds a JSON array of predetermined answers,
#     consumed in order — one per elicitation. An entry is either a content
#     object (meaning accept) or { "action": "decline" | "cancel" }.
#     Example: MRTR_ANSWERS='[{"title":"Sync","duration":"30"},{"confirm":true}]'
#
# MRTR_POLICY=autoconfirm enables the demo auto-answer policy.

import json
import os
import re
from typing import Any

import anyio
from mcp_types import (
    ElicitRequestFormParams,
    ElicitRequestParams,
    ElicitRequestURLParams,
    ElicitResult,
    InputRequest,
)

# ---------------------------------------------------------------------------
# Policy hooks: run before the user ever sees the request. A policy returns a
# full response to short-circuit the form, or None to pass.
# ---------------------------------------------------------------------------

_SUSPICIOUS = re.compile(r"password|api[ -]?key|token|secret|credential", re.IGNORECASE)


def deny_credential_solicitation(params: ElicitRequestFormParams) -> ElicitResult | None:
    """Form mode must never collect credentials; deny requests that look like it."""
    for key, prop in (params.requested_schema.get("properties") or {}).items():
        texts = " ".join([key, prop.get("title") or "", prop.get("description") or ""])
        if prop.get("format") == "password" or _SUSPICIOUS.search(texts):
            print(f'  [policy] declined: field "{key}" appears to solicit credentials')
            return ElicitResult(action="decline")
    return None


def auto_confirm(params: ElicitRequestFormParams) -> ElicitResult | None:
    """Demo auto-answer policy: accept pure-confirmation forms (all-boolean) with their defaults."""
    if os.environ.get("MRTR_POLICY") != "autoconfirm":
        return None
    props = (params.requested_schema.get("properties") or {}).items()
    if not props or not all(p.get("type") == "boolean" for _, p in props):
        return None
    content = {key: prop.get("default") if isinstance(prop.get("default"), bool) else True for key, prop in props}
    print(f"  [policy] auto-answered confirmation form: {json.dumps(content)}")
    return ElicitResult(action="accept", content=content)


POLICIES = [deny_credential_solicitation, auto_confirm]

# ---------------------------------------------------------------------------
# Validation: check content against the requested schema before responding —
# a correctness concern (the server expects this shape) and a security one.
# ---------------------------------------------------------------------------


def validate_field(key: str, prop: dict[str, Any], value: Any) -> str | None:
    if (options := prop.get("enum")) is not None:
        if not isinstance(value, str) or value not in options:
            return f'"{key}" must be one of: {", ".join(options)}'
        return None
    match prop.get("type"):
        case "string":
            if not isinstance(value, str):
                return f'"{key}" must be a string'
            if (n := prop.get("minLength")) is not None and len(value) < n:
                return f'"{key}" must be at least {n} characters'
            if (n := prop.get("maxLength")) is not None and len(value) > n:
                return f'"{key}" must be at most {n} characters'
            return None
        case "number" | "integer" as t:
            if isinstance(value, bool) or not isinstance(value, (int, float)):
                return f'"{key}" must be a number'
            if t == "integer" and not float(value).is_integer():
                return f'"{key}" must be an integer'
            if (n := prop.get("minimum")) is not None and value < n:
                return f'"{key}" must be >= {n}'
            if (n := prop.get("maximum")) is not None and value > n:
                return f'"{key}" must be <= {n}'
            return None
        case "boolean":
            return None if isinstance(value, bool) else f'"{key}" must be a boolean'
        case t:
            return f'"{key}" has an unsupported schema type: {t}'


def validate_content(params: ElicitRequestFormParams, content: dict[str, Any]) -> list[str]:
    errors: list[str] = []
    properties = params.requested_schema.get("properties") or {}
    for key in params.requested_schema.get("required") or []:
        if key not in content:
            errors.append(f'"{key}" is required')
    for key, value in content.items():
        prop = properties.get(key)
        if prop is None:
            errors.append(f'"{key}" is not in the requested schema')
            continue
        if (error := validate_field(key, prop, value)) is not None:
            errors.append(error)
    return errors


def defaults_of(params: ElicitRequestFormParams) -> dict[str, Any]:
    """Pre-populate defaults, per the schema-driven rendering rules."""
    properties = params.requested_schema.get("properties") or {}
    return {key: prop["default"] for key, prop in properties.items() if "default" in prop}


# ---------------------------------------------------------------------------
# Scripted answers (for smoke tests / headless runs).
# ---------------------------------------------------------------------------

_scripted_answers: list[Any] | None = (
    json.loads(os.environ["MRTR_ANSWERS"]) if os.environ.get("MRTR_ANSWERS") else None
)


def next_scripted_answer(params: ElicitRequestFormParams) -> ElicitResult:
    assert _scripted_answers is not None
    if not _scripted_answers:
        raise RuntimeError("MRTR_ANSWERS ran out of scripted answers")
    entry = _scripted_answers.pop(0)
    if entry.get("action") in ("decline", "cancel"):
        print(f"  [scripted] {entry['action']}")
        return ElicitResult(action=entry["action"])
    content = {**defaults_of(params), **entry}
    if errors := validate_content(params, content):
        raise RuntimeError(f"Scripted answer failed validation: {'; '.join(errors)}")
    print(f"  [scripted] accept: {json.dumps(content)}")
    return ElicitResult(action="accept", content=content)


# ---------------------------------------------------------------------------
# Interactive form rendering.
# ---------------------------------------------------------------------------


def _parse_input(prop: dict[str, Any], raw: str) -> tuple[bool, Any]:
    if prop.get("enum") is not None or prop.get("type") == "string":
        return True, raw
    if prop.get("type") in ("number", "integer"):
        try:
            return True, int(raw) if prop["type"] == "integer" else float(raw)
        except ValueError:
            return False, None
    if prop.get("type") == "boolean":
        if raw.lower() in ("y", "yes", "true"):
            return True, True
        if raw.lower() in ("n", "no", "false"):
            return True, False
    return False, None


def _render_form_sync(params: ElicitRequestFormParams) -> ElicitResult:
    print(f"\n{params.message}")
    print("(enter a value, or !decline / !cancel; blank accepts the default)")
    content: dict[str, Any] = {}
    required = params.requested_schema.get("required") or []
    for key, prop in (params.requested_schema.get("properties") or {}).items():
        label = prop.get("title") or key  # title is the display label
        if prop.get("description"):
            print(f"  {prop['description']}")  # description is help text
        hints = [
            "options: " + "/".join(prop["enum"]) if prop.get("enum") else str(prop.get("type")),
        ]
        if "default" in prop:
            hints.append(f"default: {prop['default']}")
        while True:
            raw = input(f"  {label} ({', '.join(hints)}): ").strip()
            if raw == "!decline":
                return ElicitResult(action="decline")
            if raw == "!cancel":
                return ElicitResult(action="cancel")
            if raw == "":
                if "default" in prop:
                    content[key] = prop["default"]
                elif key in required:
                    print(f'  "{label}" is required.')
                    continue
                break
            ok, value = _parse_input(prop, raw)
            error = validate_field(key, prop, value) if ok else f'"{label}" is not a valid {prop.get("type")}'
            if error is not None:
                print(f"  {error}")
                continue
            content[key] = value
            break
    if errors := validate_content(params, content):
        # Should not happen after per-field checks, but never send invalid data.
        print(f"  Input failed validation: {'; '.join(errors)}")
        return ElicitResult(action="cancel")
    return ElicitResult(action="accept", content=content)


# ---------------------------------------------------------------------------
# The handler itself. `handle_elicitation` is what the native driver's
# `elicitation_callback` runs; `handle_input_request` adapts a bare embedded
# request from an `input_requests` map (an `ElicitRequest` with
# method/params) for the manual loop.
# ---------------------------------------------------------------------------


async def handle_elicitation(params: ElicitRequestParams) -> ElicitResult:
    if isinstance(params, ElicitRequestURLParams):
        # URL mode: display the full URL, never pre-fetch, never auto-navigate.
        # This console host only reports consent; a real host opens the system
        # browser after explicit user approval.
        print(f"  [url-mode] {params.message}")
        print(f"  [url-mode] target: {params.url} — open it in your browser, then continue.")
        return ElicitResult(action="accept")

    print(f"  elicitation: {params.message}")

    for policy in POLICIES:
        if (verdict := policy(params)) is not None:
            return verdict  # answered or denied by policy

    if _scripted_answers is not None:
        return next_scripted_answer(params)
    # input() blocks; keep the event loop responsive.
    return await anyio.to_thread.run_sync(_render_form_sync, params)


async def handle_input_request(request: InputRequest) -> ElicitResult:
    if request.method != "elicitation/create":
        # This host supports elicitation only (sampling is deprecated; roots n/a).
        raise RuntimeError(f"Unsupported input request: {request.method}")
    return await handle_elicitation(request.params)
