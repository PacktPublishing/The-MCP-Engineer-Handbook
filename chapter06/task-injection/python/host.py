# TaskInjectionHost — the model integration problem for long-running operations (§6.2.11).
#
# Chat-model APIs require every tool call to be immediately followed by its result, so when
# an operation outlives the model turn the host synthesizes an immediate "started" result to
# unblock the model. When the REAL result arrives later, it has nowhere natural to go. This
# host demonstrates the four known injection strategies side by side and prints the exact
# conversation-history shape each one produces.
#
# Usage: uv run host.py [prepend|synthetic-turn|meta-tool|fresh-invocation|--all]
import asyncio
import json
import os
import sys
from pathlib import Path

from mcp.client import Client
from mcp.client.stdio import StdioServerParameters, stdio_client
from mcp_types import CallToolResult, TextContent

from scripted_chat_model import (
    ChatMessage,
    ChatModel,
    ScriptedChatModel,
    ToolResult,
    ToolSpec,
)

KNOWN = ["prepend", "synthetic-turn", "meta-tool", "fresh-invocation"]


async def main() -> int:
    args = sys.argv[1:]
    if not args:
        strategies = ["prepend"]
    elif args == ["--all"]:
        strategies = KNOWN
    elif len(args) == 1 and args[0] in KNOWN:
        strategies = [args[0]]
    else:
        print(f"Usage: uv run host.py [{'|'.join(KNOWN)}|--all]", file=sys.stderr)
        return 1

    summaries = [await run_scenario(strategy) for strategy in strategies]

    if len(summaries) > 1:
        print("=" * 78)
        print("COMPARISON — history shape produced when the real result landed")
        print("=" * 78)
        for strategy, injected, shape in summaries:
            print(f"  {strategy:<17} +{injected} messages  {shape}")
    return 0


async def run_scenario(strategy: str) -> tuple[str, int, str]:
    print("=" * 78)
    print(f"STRATEGY: {strategy}")
    print("=" * 78)

    # Connect to the demo server as a stdio child process. The b1 Client defaults to
    # mode="auto": it probes server/discover (2026-07-28) and falls back to the legacy initialize
    # handshake — which is what the Python stdio server still speaks in 2.0.0b1.
    transport = stdio_client(
        StdioServerParameters(command=sys.executable, args=[resolve_server_script()])
    )
    async with Client(transport) as mcp:
        listing = await mcp.list_tools()
        tools = [ToolSpec(t.name, t.description or "", t.input_schema) for t in listing.tools]

        # Host-side state and meta-tool for the meta-tool strategy. This tool is implemented
        # by the HOST, not the MCP server: it hands the model whatever finished while it
        # wasn't looking. run_model_turn dispatches it locally.
        completed_ops: list[dict[str, str]] = []
        if strategy == "meta-tool":
            tools.append(
                ToolSpec(
                    "check_completed_operations",
                    "Returns the results of long-running operations that completed since the last check.",
                )
            )

        chat = create_chat_model()
        history = [ChatMessage("user", "Please generate the quarterly sales report.")]

        # Act 1 — the model calls start_report; the tool returns IMMEDIATELY with a handle.
        # That immediate "operation started" tool result is what keeps the conversation
        # legal: the API would reject a history in which the tool call dangles unanswered.
        # The model acknowledges and the turn ends — with the real work still running.
        operation_id = await run_model_turn(chat, history, tools, mcp, completed_ops)
        if operation_id is None:
            raise RuntimeError("The model did not start the report operation.")

        # The host polls for the real result on the model's behalf. (With the Tasks extension
        # this would be tasks/get instead of a status tool; the injection problem below is
        # unchanged.)
        report = await poll_for_result(mcp, operation_id)
        completed_ops.append({"operationId": operation_id, "result": report})
        before = len(history)
        print(f"\n>>> Real result for {operation_id} arrived on the host. Injecting via '{strategy}'.\n")

        # Act 2 — the real result lands and must re-enter the conversation somehow.
        if strategy == "prepend":
            # Hold the result until the NEXT real user message, then prepend it. Nothing
            # reaches the model until the user speaks again — cheap, legal, but not
            # proactive, and the result arrives wearing the user's voice.
            next_user_text = "Thanks. Anything in it I should flag for the board?"
            history.append(
                ChatMessage(
                    "user",
                    f"[Result of completed operation {operation_id}]\n{report}\n---\n{next_user_text}",
                )
            )
            await run_model_turn(chat, history, tools, mcp, completed_ops)
            shape = "result piggybacks inside the next real user message"

        elif strategy == "synthetic-turn":
            # Fabricate a turn right away and run the model on it. Proactive — the assistant
            # can announce the result unprompted — but the history now contains a user-role
            # message no user ever wrote.
            history.append(
                ChatMessage(
                    "user",
                    f"<operation-update>Operation {operation_id} completed. Result:\n{report}</operation-update>",
                )
            )
            await run_model_turn(chat, history, tools, mcp, completed_ops)
            shape = "fabricated user-role turn appended immediately"

        elif strategy == "meta-tool":
            # The model pulls the result itself through a host-side tool, keeping every
            # tool call legally paired with its result — but only after something prompts
            # it to look. Here the nudge is the next user message.
            history.append(ChatMessage("user", "Any update on the report?"))
            await run_model_turn(chat, history, tools, mcp, completed_ops)
            shape = "user nudge -> assistant tool call -> tool result -> assistant summary"

        else:  # fresh-invocation
            # Headless-agent style: abandon the old conversation and start a new invocation
            # with the result in its initial context. Clean history, no synthesis — at the
            # cost of everything the previous conversation knew.
            history = [
                ChatMessage(
                    "system",
                    "You are a reporting agent. A long-running operation you started earlier "
                    "has completed; summarize its result for the record.",
                ),
                ChatMessage(
                    "user",
                    f"Operation {operation_id} (quarterly sales report) has completed. Result:\n{report}",
                ),
            ]
            before = 0
            await run_model_turn(chat, history, tools, mcp, completed_ops)
            shape = "new invocation seeded with the result; original history abandoned"

        print_history(strategy, history, injected_from=before)
        return (strategy, len(history) - before, shape)


# One model "turn": call the chat model, execute any tool calls it makes (MCP tools via the
# Client, the meta-tool locally), feed the results back, and repeat until it answers in text.
# Returns the operationId if a start_report call happened during the turn.
async def run_model_turn(
    chat: ChatModel,
    history: list[ChatMessage],
    tools: list[ToolSpec],
    mcp: Client,
    completed_ops: list[dict[str, str]],
) -> str | None:
    operation_id: str | None = None
    while True:
        reply = await chat.respond(history, tools)
        history.append(reply)
        if not reply.tool_calls:
            return operation_id

        results: list[ToolResult] = []
        for call in reply.tool_calls:
            if call.name == "check_completed_operations":
                result_text = (
                    "No completed operations." if not completed_ops else json.dumps(completed_ops)
                )
            else:
                result = await mcp.call_tool(call.name, call.arguments)
                result_text = text_of(result)
                if call.name == "start_report":
                    operation_id = json.loads(result_text)["operationId"]
            results.append(ToolResult(call.call_id, result_text))
        history.append(ChatMessage("tool", tool_results=results))


async def poll_for_result(mcp: Client, operation_id: str) -> str:
    while True:
        result = await mcp.call_tool("get_report_result", {"operationId": operation_id})
        root = json.loads(text_of(result))
        if root["status"] == "completed":
            return root["report"]
        await asyncio.sleep(0.5)


def text_of(result: CallToolResult) -> str:
    return "".join(block.text for block in result.content if isinstance(block, TextContent))


def print_history(label: str, history: list[ChatMessage], injected_from: int) -> None:
    print(
        f"--- Conversation history after '{label}' "
        f"(messages {injected_from + 1}+ appeared when the result landed) ---"
    )
    continuation = "\n" + " " * 18
    for i, message in enumerate(history):
        role = message.role.upper().ljust(9)
        lines: list[str] = []
        if message.text:
            lines.append(message.text)
        for call in message.tool_calls:
            lines.append(f"(tool call {call.call_id}) {call.name}({json.dumps(call.arguments)})")
        for tool_result in message.tool_results:
            lines.append(f"(result for {tool_result.call_id}) {tool_result.result}")
        for line in lines:
            print(f"  [{i + 1}] {role} " + line.replace("\n", continuation))
    print()


# Default: the deterministic scripted model (no key needed, identical output every run).
# A real provider plugs in here: return any object implementing the ChatModel protocol —
# its respond() maps history to the provider's message format and ToolSpec entries to the
# provider's tool declarations.
def create_chat_model() -> ChatModel:
    return ScriptedChatModel()


# Locate the server script next to this file, or wherever REPORT_SERVER_PY points.
def resolve_server_script() -> str:
    if override := os.environ.get("REPORT_SERVER_PY"):
        return override
    return str(Path(__file__).with_name("report_server.py"))


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
