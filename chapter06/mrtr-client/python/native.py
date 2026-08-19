# The SDK-native MRTR path: no hand-written loop.
#
# In mcp 2.0.0, `client.call_tool()` (and `get_prompt` / `read_resource`)
# resolves `InputRequiredResult`s AUTOMATICALLY: each embedded request is
# dispatched to the matching callback (`elicitation_callback` here; sampling
# and roots have their own), all requests in one round run concurrently, and
# the call is retried with the collected `input_responses` and a byte-exact
# `request_state` echo, on a fresh request id, up to `input_required_max_rounds`
# (default 10, `InputRequiredRoundsExceededError` beyond it). A result carrying
# only `request_state` — no requests — is retried after a short exponential
# backoff (50 ms doubling to a 250 ms cap): the load-shedding leg. The
# interactive rounds happen inside the call — `call_tool` returns the final
# result.
#
# What the SDK does NOT do: render the form, apply policy, or validate the
# user's content against requested_schema. Those stay in your callback —
# the same handle_elicitation the manual loop's handler uses.
#
# Run: uv run native.py  (same MRTR_ANSWERS / MRTR_POLICY env as the manual entry)

import os
import sys

import anyio
from mcp.client import Client
from mcp.client.stdio import StdioServerParameters, stdio_client
from mcp_types import TextContent

from input_handler import handle_elicitation


async def main() -> int:
    tool_name = sys.argv[1] if len(sys.argv) > 1 else "book_meeting"
    here = os.path.dirname(os.path.abspath(__file__))
    transport = stdio_client(
        StdioServerParameters(command=sys.executable, args=[os.path.join(here, "demo_server.py")])
    )
    client = Client(
        transport,
        # Registering the callback also advertises the elicitation capability.
        elicitation_callback=lambda context, params: handle_elicitation(params),
        input_required_max_rounds=int(os.environ.get("MRTR_MAX_ROUNDS", "5")),
    )

    async with client:
        print(f"-> tools/call {tool_name} (SDK drives the MRTR rounds; negotiated {client.protocol_version})")
        arguments = {"room": "4B"} if tool_name == "book_meeting" else {}
        try:
            result = await client.call_tool(tool_name, arguments)
        except Exception as error:  # includes InputRequiredRoundsExceededError
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
