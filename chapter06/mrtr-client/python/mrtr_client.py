# The manual gather-and-retry loop from Chapter 6, Section 6.2.5, plus the
# production bounds the chapter calls for (retry budget, timeout, cancel).
#
# Run against the companion demo server (same directory):
#   uv run mrtr_client.py [book_meeting|never_satisfied]
#
# Scripted mode (no terminal interaction): see input_handler.py.
#
# Python SDK notes (mcp 2.0.0):
#   - `client.call_tool()` runs the MRTR loop itself (see native.py). The
#     manual loop lives one level down: `client.session.call_tool(...,
#     allow_input_required=True)` returns the interim `InputRequiredResult`
#     instead of raising, and the retry carries `input_responses` /
#     `request_state` as keyword arguments — there is no spread-into-params
#     like the TS `{ ...params, ...mrtr }`.
#   - The discriminator is `isinstance(result, InputRequiredResult)`; the
#     wire `resultType` tag is parsed into the type, not exposed as a field
#     to check by hand.
#   - `Client(...)` declares the elicitation capability only when an
#     `elicitation_callback` is set. The manual loop never dispatches to the
#     callback, but it must still be registered, or the server sees a client
#     without the capability.

import json
import os
import sys
import time

import anyio
from mcp.client import Client
from mcp.client.stdio import StdioServerParameters, stdio_client
from mcp_types import CallToolResult, InputRequiredResult, TextContent

from input_handler import handle_elicitation, handle_input_request


# The book's loop (§6.2.5): retry until the result is no longer input_required.
async def call_tool_gathering_input(client: Client, name: str, arguments: dict) -> CallToolResult:
    input_responses = None
    request_state = None
    while True:
        # each iteration is a brand-new request with a new JSON-RPC id
        result = await client.session.call_tool(
            name,
            arguments,
            input_responses=input_responses,
            request_state=request_state,
            allow_input_required=True,
        )
        if not isinstance(result, InputRequiredResult):
            return result
        input_responses = {}
        for key, request in (result.input_requests or {}).items():
            # render a form, open a URL, or apply policy — see input_handler.py
            input_responses[key] = await handle_input_request(request)
        # echo verbatim; never inspect, parse, or modify
        request_state = result.request_state


# Production version: the same loop, bounded. A misbehaving server could
# otherwise keep the host gathering input forever.
async def call_tool_gathering_input_bounded(
    client: Client,
    name: str,
    arguments: dict,
    *,
    max_rounds: int = 10,  # retry budget
    timeout_s: float = 120.0,  # deadline for the whole flow, all rounds included
) -> CallToolResult:
    deadline = time.monotonic() + timeout_s
    input_responses = None
    request_state = None
    round_no = 0
    while True:
        round_no += 1
        if round_no > max_rounds:
            raise RuntimeError(f"input_required retry budget exhausted after {max_rounds} rounds")
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError(f"tool call still gathering input after {timeout_s} s")
        with anyio.fail_after(remaining):
            result = await client.session.call_tool(
                name,
                arguments,
                input_responses=input_responses,
                request_state=request_state,
                allow_input_required=True,
            )
        if not isinstance(result, InputRequiredResult):
            return result
        keys = ", ".join((result.input_requests or {}).keys())
        state_note = ", requestState present" if result.request_state is not None else ""
        print(f"<- input_required (round {round_no}): keys [{keys}]{state_note}")
        input_responses = {}
        for key, request in (result.input_requests or {}).items():
            input_responses[key] = await handle_input_request(request)
        request_state = result.request_state


# --- demo driver -------------------------------------------------------------


async def main() -> int:
    tool_name = sys.argv[1] if len(sys.argv) > 1 else "book_meeting"
    max_rounds = int(os.environ.get("MRTR_MAX_ROUNDS", "10"))

    here = os.path.dirname(os.path.abspath(__file__))
    transport = stdio_client(
        StdioServerParameters(command=sys.executable, args=[os.path.join(here, "demo_server.py")])
    )
    client = Client(
        transport,
        # Registers the callback so the client ADVERTISES the elicitation
        # capability; the manual loop below never lets the driver reach it.
        elicitation_callback=lambda context, params: handle_elicitation(params),
        # mode="auto" (the default) probes server/discover and lands on the
        # modern 2026-07-28 era, where MRTR exists.
    )

    async with client:
        print(f"-> tools/call {tool_name} (negotiated {client.protocol_version})")
        arguments = {"room": "4B"} if tool_name == "book_meeting" else {}
        try:
            # MRTR_UNBOUNDED=1 runs the chapter's bare loop instead of the bounded one.
            # User-facing cancel: Ctrl+C cancels the task scope and the in-flight call.
            if os.environ.get("MRTR_UNBOUNDED") == "1":
                result = await call_tool_gathering_input(client, tool_name, arguments)
            else:
                result = await call_tool_gathering_input_bounded(
                    client, tool_name, arguments, max_rounds=max_rounds, timeout_s=120.0
                )
        except (RuntimeError, TimeoutError) as error:
            print(f"<- failed: {error}")
            return 1
        text = next((c.text for c in result.content if isinstance(c, TextContent)), None)
        print(f"<- final result: {text if text is not None else result.content}")
        return 0


if __name__ == "__main__":
    try:
        sys.exit(anyio.run(main))
    except KeyboardInterrupt:
        print("<- failed: tool call cancelled by the user")
        sys.exit(1)
