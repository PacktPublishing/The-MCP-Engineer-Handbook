# Multi-provider schema mapping (§6.2.2) and output schema lifting (§6.2.3).
# Connects to the demo server over stdio, fetches its tools, and runs them
# through a registry of provider adapters — OpenAI Chat Completions, OpenAI
# Responses, Anthropic, Gemini, and Ollama-style — printing what each adapter
# kept, stripped, lifted, or refused. No model API keys involved: this is the
# mapping layer only. Finishes by reverse-mapping a simulated model tool call
# back to the server and executing it (§6.2.4).
import asyncio
import copy
import json
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Literal

from mcp.client import Client
from mcp.client.stdio import StdioServerParameters, stdio_client
from mcp_types import Tool


def to_json(value: Any) -> str:
    """Compact JSON, matching what a wire serializer produces."""
    return json.dumps(value, separators=(",", ":"))


# -----------------------------------------------------------------------------
# The minimal implementation printed in §6.2.2, targeting OpenAI's Responses
# API. Reproduced verbatim except the parameter type: the book prints
# `McpTool`, the b1 SDK's type is `Tool` (with a snake_case `input_schema`
# attribute for the wire's `inputSchema`).
# -----------------------------------------------------------------------------
def mcp_tool_to_openai(tool: Tool, server_prefix: str) -> dict[str, Any]:
    return {
        "type": "function",
        "name": f"{server_prefix}__{tool.name}",
        "description": tool.description or "",
        "parameters": tool.input_schema,
    }


# -----------------------------------------------------------------------------
# Everything below is what that minimal function grows into: a registry of
# provider adapters, each with a per-keyword strategy — strip, lift, or fail
# fast — for the JSON Schema keywords its API doesn't accept, plus a
# per-provider output-schema decision.
# -----------------------------------------------------------------------------

JsonObject = dict[str, Any]

# What to do with a JSON Schema keyword the provider's API doesn't accept.
KeywordStrategy = Literal["strip", "lift", "fail"]


@dataclass
class MappingAction:
    action: Literal["stripped", "lifted", "inlined-ref"]
    keyword: str
    path: str
    detail: str | None = None


class MappingError(Exception):
    pass


@dataclass
class ProviderAdapter:
    provider: str
    # Per-keyword strategy for keywords this provider's API rejects or
    # ignores. Unlisted keywords pass through untouched. This table is data,
    # not code, because provider support matrices shift release to release —
    # the assignments below are illustrative; verify against current provider
    # docs before relying on them.
    keyword_strategies: dict[str, KeywordStrategy]
    # Inline local $refs for APIs that don't understand references.
    inline_local_refs: bool
    # §6.2.3: pass the output schema through natively, lift it into the
    # description, or drop it (when the model doesn't need it, lifting only
    # adds token cost). As of this writing no major API takes the field on
    # tool definitions — 'native' here illustrates the pass-through path for
    # when support lands.
    output_schema: Literal["native", "lift", "drop"]
    # The provider's wrapper format around name/description/schema.
    wrap: Callable[[str, str, JsonObject, JsonObject | None], JsonObject]


def _wrap_openai_chat(
    name: str, description: str, parameters: JsonObject, output_schema: JsonObject | None
) -> JsonObject:
    return {
        "type": "function",
        "function": {"name": name, "description": description, "parameters": parameters},
    }


def _wrap_openai_responses(
    name: str, description: str, parameters: JsonObject, output_schema: JsonObject | None
) -> JsonObject:
    # The flattened shape mcp_tool_to_openai produces, grown up.
    wrapped: JsonObject = {
        "type": "function",
        "name": name,
        "description": description,
        "parameters": parameters,
    }
    if output_schema is not None:
        wrapped["output_schema"] = output_schema
    return wrapped


def _wrap_anthropic(
    name: str, description: str, parameters: JsonObject, output_schema: JsonObject | None
) -> JsonObject:
    return {"name": name, "description": description, "input_schema": parameters}


def _wrap_gemini(
    name: str, description: str, parameters: JsonObject, output_schema: JsonObject | None
) -> JsonObject:
    # One entry in tools[].functionDeclarations.
    return {"name": name, "description": description, "parameters": parameters}


ADAPTERS = [
    ProviderAdapter(
        provider="openai-chat",
        # Chat Completions passes schemas through fairly permissively, but
        # documents `default` as unsupported.
        keyword_strategies={"default": "lift"},
        inline_local_refs=False,
        output_schema="lift",
        wrap=_wrap_openai_chat,
    ),
    ProviderAdapter(
        provider="openai-responses",
        # Strict function calling accepts only a narrow schema subset: lift the
        # validation keywords, fail fast on compositions it can't represent.
        keyword_strategies={
            "minimum": "lift", "maximum": "lift",
            "minLength": "lift", "maxLength": "lift",
            "pattern": "lift", "format": "lift", "default": "lift",
            "oneOf": "fail",
        },
        inline_local_refs=False,
        output_schema="native",
        wrap=_wrap_openai_responses,
    ),
    ProviderAdapter(
        provider="anthropic",
        # Full JSON Schema 2020-12 support: nothing to strip or lift on input.
        keyword_strategies={},
        inline_local_refs=False,
        output_schema="lift",
        wrap=_wrap_anthropic,
    ),
    ProviderAdapter(
        provider="gemini",
        # Function declarations take an OpenAPI-flavored subset: no $ref, no
        # pattern. oneOf gets a crude lift — structural keywords are the hard
        # case §6.2.2 calls out; the alternatives survive only as prose.
        keyword_strategies={"pattern": "lift", "default": "lift", "oneOf": "lift"},
        inline_local_refs=True,
        output_schema="lift",
        wrap=_wrap_gemini,
    ),
    ProviderAdapter(
        provider="ollama",
        # OpenAI Chat Completions wire format, but small local models handle
        # constraints unevenly — this adapter strips them, trading lost
        # information for a schema the model won't trip over.
        keyword_strategies={
            "pattern": "strip", "format": "strip", "default": "strip", "const": "strip",
        },
        inline_local_refs=True,
        output_schema="drop",
        wrap=_wrap_openai_chat,
    ),
]

# --- Namespace prefixing (§6.2.2) --------------------------------------------
# Server prefix + double underscore, so the separator can't be confused with
# the word-divider underscores inside tool names.


def namespaced_name(server_prefix: str, tool_name: str) -> str:
    return f"{server_prefix}__{tool_name}"


def split_namespaced_name(name: str) -> tuple[str, str]:
    """Reverse mapping: prefixed model tool call -> originating server + tool."""
    sep = name.find("__")
    if sep < 1:
        raise MappingError(f"Tool name has no server prefix: {name}")
    return name[:sep], name[sep + 2 :]


# --- Schema transformation ----------------------------------------------------


def assert_local_ref(ref: str, path: str) -> None:
    """2026-07-28 obligation: never dereference $refs that resolve to a network
    URI — a schema is data from a partially trusted source. This sample
    supports local (#/...) references only and rejects everything else."""
    if not ref.startswith("#"):
        raise MappingError(f'Refusing non-local $ref "{ref}" at {path}')


MAX_DEPTH = 32  # Bound the walk: hostile schemas are a DoS vector.


def inline_local_refs(
    node: Any, root: JsonObject, actions: list[MappingAction], path: str = "#", depth: int = 0
) -> Any:
    """Inline #/$defs/... references so the schema is self-contained."""
    if depth > MAX_DEPTH:
        raise MappingError(f"Schema exceeds depth {MAX_DEPTH} at {path}")
    if isinstance(node, list):
        return [
            inline_local_refs(item, root, actions, f"{path}/{i}", depth + 1)
            for i, item in enumerate(node)
        ]
    if not isinstance(node, dict):
        return node
    ref = node.get("$ref")
    if isinstance(ref, str):
        assert_local_ref(ref, path)
        target: Any = root
        for segment in ref[2:].split("/"):
            target = target.get(segment) if isinstance(target, dict) else None
        if target is None:
            raise MappingError(f'Unresolvable $ref "{ref}" at {path}')
        actions.append(MappingAction("inlined-ref", "$ref", path, ref))
        # 2020-12 allows keywords alongside $ref; merge them over the target.
        siblings = {k: v for k, v in node.items() if k != "$ref"}
        merged = {**target, **siblings}
        return inline_local_refs(merged, root, actions, path, depth + 1)
    return {
        key: inline_local_refs(value, root, actions, f"{path}/{key}", depth + 1)
        for key, value in node.items()
        if key != "$defs"  # Consumed by inlining.
    }


def lift_phrase(keyword: str, node: JsonObject) -> str:
    """Human phrasing for a lifted keyword — what the model sees instead."""
    if keyword == "minimum":
        return (
            f"Must be between {node['minimum']} and {node['maximum']}."
            if "maximum" in node
            else f"Must be at least {node['minimum']}."
        )
    if keyword == "maximum":
        return f"Must be at most {node['maximum']}."
    if keyword == "minLength":
        return (
            f"Must be {node['minLength']} to {node['maxLength']} characters."
            if "maxLength" in node
            else f"Must be at least {node['minLength']} characters."
        )
    if keyword == "maxLength":
        return f"Must be at most {node['maxLength']} characters."
    if keyword == "pattern":
        return f"Must match the regular expression {node['pattern']}."
    if keyword == "format":
        return f"Format: {node['format']}."
    if keyword == "default":
        return f"Defaults to {to_json(node['default'])}."
    if keyword == "oneOf":
        # The crude lift for a structural keyword: alternatives as prose.
        return f"Exactly one of these shapes: {to_json(node['oneOf'])}."
    return f"{keyword}: {to_json(node[keyword])}."


def append_to_description(node: JsonObject, text: str) -> None:
    node["description"] = f"{node['description']} {text}" if node.get("description") else text


# minimum+maximum and minLength+maxLength lift as one sentence; when the
# first of a pair is lifted, the second is silently removed with it.
LIFT_PAIRS = {"minimum": "maximum", "minLength": "maxLength"}


def apply_keyword_strategies(
    node: Any,
    strategies: dict[str, KeywordStrategy],
    actions: list[MappingAction],
    path: str = "#",
    depth: int = 0,
) -> None:
    """Walk the schema applying the adapter's per-keyword strategy. Mutates the
    (already copied) schema in place and records every action taken."""
    if depth > MAX_DEPTH:
        raise MappingError(f"Schema exceeds depth {MAX_DEPTH} at {path}")
    if isinstance(node, list):
        for i, item in enumerate(node):
            apply_keyword_strategies(item, strategies, actions, f"{path}/{i}", depth + 1)
        return
    if not isinstance(node, dict):
        return
    if isinstance(node.get("$ref"), str):
        assert_local_ref(node["$ref"], path)  # Even when passed through.

    for keyword, strategy in strategies.items():
        if keyword not in node:
            continue
        if strategy == "fail":
            raise MappingError(
                f'Schema uses "{keyword}" at {path}, which this provider cannot represent'
            )
        if strategy == "strip":
            actions.append(MappingAction("stripped", keyword, path, to_json(node[keyword])))
            del node[keyword]
        elif strategy == "lift":
            phrase = lift_phrase(keyword, node)
            append_to_description(node, phrase)
            actions.append(MappingAction("lifted", keyword, path, phrase))
            del node[keyword]
            partner = LIFT_PAIRS.get(keyword)
            if partner and partner in node and strategies.get(partner) == "lift":
                del node[partner]
            if keyword == "oneOf" and "type" not in node:
                node["type"] = "object"

    for key, value in list(node.items()):
        if key in ("description", "enum", "required"):
            continue
        apply_keyword_strategies(value, strategies, actions, f"{path}/{key}", depth + 1)


# --- Forward mapping: one MCP tool through one adapter --------------------------


@dataclass
class MappedTool:
    definition: JsonObject
    actions: list[MappingAction] = field(default_factory=list)
    output_schema_decision: str = "none declared"


def map_tool(adapter: ProviderAdapter, tool: Tool, server_prefix: str) -> MappedTool:
    actions: list[MappingAction] = []
    schema: JsonObject = copy.deepcopy(tool.input_schema)
    if adapter.inline_local_refs:
        schema = inline_local_refs(schema, schema, actions)
    apply_keyword_strategies(schema, adapter.keyword_strategies, actions)

    description = tool.description or ""
    output_schema_decision = "none declared"
    native_output: JsonObject | None = None
    if tool.output_schema is not None:
        if adapter.output_schema == "native":
            native_output = tool.output_schema
            output_schema_decision = "passed through natively"
        elif adapter.output_schema == "lift":
            description += f" Returns JSON matching this schema: {to_json(tool.output_schema)}"
            output_schema_decision = "lifted into description"
        elif adapter.output_schema == "drop":
            output_schema_decision = (
                "dropped (model does not need it; lifting would only add token cost)"
            )

    definition = adapter.wrap(
        namespaced_name(server_prefix, tool.name), description, schema, native_output
    )
    return MappedTool(definition, actions, output_schema_decision)


# --- Reverse mapping: model tool call -> MCP tools/call -------------------------


@dataclass
class ModelToolCall:
    """The shape a model API hands back: prefixed name, arguments as JSON text."""

    name: str
    arguments: str


async def route_tool_call(call: ModelToolCall, clients: dict[str, Client]):
    server_prefix, tool_name = split_namespaced_name(call.name)
    client = clients.get(server_prefix)
    if client is None:
        raise MappingError(f'No connected server for prefix "{server_prefix}"')
    return await client.call_tool(tool_name, json.loads(call.arguments))


# --- Demo -----------------------------------------------------------------------


def approx_tokens(value: Any) -> int:
    return (len(to_json(value)) + 2) // 4


def tool_as_wire_dict(tool: Tool) -> JsonObject:
    return tool.model_dump(by_alias=True, exclude_none=True)


async def main() -> None:
    server_path = Path(__file__).resolve().parent / "demo_server.py"
    transport = stdio_client(
        StdioServerParameters(command=sys.executable, args=[str(server_path)])
    )
    async with Client(transport) as client:
        print(f"Negotiated protocol version: {client.protocol_version}")

        server_prefix = "demo"  # Short and meaningful: every token of it rides along on every tool name.
        tools = (await client.list_tools()).tools
        print(f"Fetched {len(tools)} tools: {', '.join(t.name for t in tools)}\n")

        print("--- Minimal mapping (the §6.2.2 snippet, Responses API shape) ---")
        print(to_json(mcp_tool_to_openai(tools[0], server_prefix)))

        for adapter in ADAPTERS:
            print(f"\n=== {adapter.provider} ===")
            strategies = ", ".join(
                f"{keyword}->{strategy}"
                for keyword, strategy in adapter.keyword_strategies.items()
            )
            print(f"keyword strategies: {strategies or '(none — full JSON Schema accepted)'}")
            print(
                f"local $refs: {'inlined' if adapter.inline_local_refs else 'passed through'}; "
                f"output schema: {adapter.output_schema}"
            )

            for tool in tools:
                print(f"\n  {tool.name} (MCP definition ~{approx_tokens(tool_as_wire_dict(tool))} tokens)")
                try:
                    mapped = map_tool(adapter, tool, server_prefix)
                    for a in mapped.actions:
                        detail = f" -> {a.detail}" if a.detail else ""
                        print(f"    {a.action} {a.keyword} at {a.path}{detail}")
                    if tool.output_schema is not None:
                        print(f"    output schema: {mapped.output_schema_decision}")
                    print(
                        f"    mapped (~{approx_tokens(mapped.definition)} tokens): "
                        f"{to_json(mapped.definition)}"
                    )
                except MappingError as error:
                    print(f"    FAILED FAST: {error}")

        print("\n--- Reverse mapping: routing a model tool call (§6.2.4) ---")
        dispatch = {server_prefix: client}
        model_call = ModelToolCall(
            name="demo__get_booking",
            arguments='{"booking_ref":"BK-7Q2M4X"}',
        )
        print(f"model emitted: {model_call.name}({model_call.arguments})")
        result = await route_tool_call(model_call, dispatch)
        print(f'routed to server "{server_prefix}", tool "get_booking"')
        print(f"result: {to_json(result.structured_content)}")


if __name__ == "__main__":
    asyncio.run(main())
