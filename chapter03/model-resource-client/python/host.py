# ModelResourceClient — Pattern 3 (§3.3.3): model-controlled resource access via
# tool wrappers.
#
# Two host-side tools — list_resources and read_resource — give resources the
# model-native integration point the protocol doesn't define. The list tool aggregates
# across ALL connected servers, tagging each entry with a host-assigned server name;
# the read tool routes a read to the right server by that name. To prove the
# aggregation, this host spawns the SAME demo server twice under different labels
# ("docs" and "wiki") — same catalog, distinct routing keys.
#
# Usage: uv run host.py
import asyncio
import json
import os
import sys
from pathlib import Path

from mcp.client.stdio import StdioServerParameters

from resource_tools import ClientManager, ResourceTools
from scripted_chat_model import (
    ChatMessage,
    ScriptedChatModel,
    ToolResult,
    ToolSpec,
)


async def main() -> None:
    client_manager = ClientManager()
    try:
        for server_name in ("docs", "wiki"):
            # server_name is the host-assigned label; routing never trusts the
            # server's self-reported name.
            await client_manager.connect(
                server_name,
                StdioServerParameters(command=sys.executable, args=[resolve_server_script()]),
            )

        # Expose the two methods as model-callable tools. Describing what an MCP
        # *resource* is (not a file, not an HTTP URL) is what makes the model use
        # these well — see §3.3.3.
        resource_tools = ResourceTools(client_manager)
        tools = [
            ToolSpec(
                "list_resources",
                "Lists available MCP resources from all connected servers. Resources are curated "
                "context items (documents, reference material, configuration) identified by a URI "
                "that is only meaningful to the server that owns it — it is not a file path or web URL. "
                "Each entry includes the serverName needed to read it.",
            ),
            ToolSpec(
                "read_resource",
                "Reads one MCP resource and returns its content. Pass the serverName and uri exactly "
                "as returned by list_resources; a URI is only valid on the server it was listed from.",
            ),
        ]

        chat = ScriptedChatModel()
        history = [
            ChatMessage(
                "user",
                "What reference material do we have across the connected servers? "
                "Then show me the release notes from the wiki server.",
            )
        ]

        # The standard tool loop: run the model, execute its tool calls (locally — these
        # are host tools, not MCP server tools), feed results back, repeat until it
        # answers in text.
        while True:
            reply = await chat.respond(history, tools)
            history.append(reply)
            if not reply.tool_calls:
                break

            results: list[ToolResult] = []
            for call in reply.tool_calls:
                if call.name == "list_resources":
                    result = json.dumps(await resource_tools.list_resources())
                elif call.name == "read_resource":
                    result = await resource_tools.read_resource(
                        call.arguments["serverName"], call.arguments["uri"]
                    )
                else:
                    raise ValueError(f"Unknown tool: {call.name}")
                results.append(ToolResult(call.call_id, result))
            history.append(ChatMessage("tool", tool_results=results))

        print_history(history)
    finally:
        await client_manager.aclose()


def print_history(history: list[ChatMessage]) -> None:
    print("=" * 78)
    print("CONVERSATION — resources reached the model through the wrapper tools")
    print("=" * 78)
    continuation = "\n" + " " * 18
    for i, message in enumerate(history):
        role = message.role.upper().ljust(9)
        lines: list[str] = []
        if message.text:
            lines.append(truncate(message.text, 320))
        for call in message.tool_calls:
            lines.append(f"(tool call {call.call_id}) {call.name}({json.dumps(call.arguments)})")
        for tool_result in message.tool_results:
            lines.append(f"(result for {tool_result.call_id}) {truncate(tool_result.result, 320)}")
        for line in lines:
            print(f"  [{i + 1}] {role} " + line.replace("\n", continuation))


def truncate(text: str, max_length: int) -> str:
    return text if len(text) <= max_length else f"{text[:max_length]}… ({len(text)} chars total)"


# Locate the demo server next to this sample, or wherever DEMO_RESOURCE_SERVER_PY points.
def resolve_server_script() -> str:
    if override := os.environ.get("DEMO_RESOURCE_SERVER_PY"):
        return override
    candidate = (
        Path(__file__).resolve().parent.parent.parent
        / "demo-resource-server" / "python" / "server.py"
    )
    if not candidate.exists():
        raise FileNotFoundError(
            f"server.py not found at {candidate}. Point DEMO_RESOURCE_SERVER_PY at "
            "chapter03/demo-resource-server/python/server.py."
        )
    return str(candidate)


if __name__ == "__main__":
    asyncio.run(main())
