# A minimal chat-model abstraction (the role Microsoft.Extensions.AI plays in the C#
# canonical) plus a deterministic scripted implementation — no API key needed. The
# scripted model pattern-matches the incoming history and replies with the turn a
# capable tool-calling model would plausibly produce, so the four injection strategies
# can be compared on identical conversations. A real provider plugs in by implementing
# the same ChatModel protocol over its own SDK (see create_chat_model in host.py).
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Literal, Protocol, Sequence

Role = Literal["system", "user", "assistant", "tool"]


@dataclass
class ToolCall:
    call_id: str
    name: str
    arguments: dict[str, Any]


@dataclass
class ToolResult:
    call_id: str
    result: str


@dataclass
class ChatMessage:
    role: Role
    text: str = ""
    tool_calls: list[ToolCall] = field(default_factory=list)
    tool_results: list[ToolResult] = field(default_factory=list)


@dataclass
class ToolSpec:
    name: str
    description: str = ""
    input_schema: dict[str, Any] | None = None


class ChatModel(Protocol):
    async def respond(
        self, history: Sequence[ChatMessage], tools: Sequence[ToolSpec]
    ) -> ChatMessage: ...


_SUMMARY_LINE = (
    "Revenue is up 14% quarter over quarter, driven by the new subscription tier; "
    "two regions missed target (details in the appendix)."
)


class ScriptedChatModel:
    def __init__(self) -> None:
        self._call_counter = 0

    async def respond(
        self, history: Sequence[ChatMessage], tools: Sequence[ToolSpec]
    ) -> ChatMessage:
        last = history[-1]

        # A tool result just came back — react to it.
        if last.role == "tool" and last.tool_results:
            tool_result = last.tool_results[-1]
            call_name = _find_call_name(history, tool_result.call_id)
            result_text = tool_result.result
            if call_name == "start_report":
                # The synthesized-immediate "operation started" result: acknowledge and move on.
                return _text(
                    "I've started generating the report. It's running in the background — "
                    "I'll share the results as soon as they're available."
                )
            if call_name == "check_completed_operations":
                # meta-tool strategy: the meta-tool returned completed work (or nothing).
                if "No completed" in result_text:
                    return _text("Nothing has finished yet — I'll keep an eye on it.")
                return _text(f"Yes — the report just finished. {_SUMMARY_LINE}")
            return _text(f"Tool '{call_name}' returned: {result_text}")

        text = last.text

        # prepend strategy: the completed result is riding along inside a real user message.
        if "[Result of completed operation" in text:
            return _text(
                f"Good news first: the quarterly sales report finished. {_SUMMARY_LINE} "
                "To answer your question: I'd flag the two underperforming regions for the board."
            )

        # synthetic-turn strategy: a fabricated turn is announcing the result.
        if "<operation-update>" in text:
            return _text(f"Update: the quarterly sales report is ready. {_SUMMARY_LINE}")

        # fresh-invocation strategy: a brand-new run seeded with the result.
        if "has completed. Result:" in text:
            return _text(f"For the record: the operation completed successfully. {_SUMMARY_LINE}")

        # meta-tool strategy: the user nudges, so check for finished work.
        if "any update" in text.lower() and _has_tool(tools, "check_completed_operations"):
            return self._call("check_completed_operations", {})

        # Opening user request: kick off the long-running report.
        if last.role == "user" and "report" in text.lower() and _has_tool(tools, "start_report"):
            return self._call("start_report", {"topic": "quarterly sales"})

        return _text("(scripted model has no line for this input)")

    def _call(self, name: str, arguments: dict[str, Any]) -> ChatMessage:
        self._call_counter += 1
        return ChatMessage(
            "assistant", tool_calls=[ToolCall(f"call-{self._call_counter}", name, arguments)]
        )


def _find_call_name(history: Sequence[ChatMessage], call_id: str) -> str:
    for message in reversed(history):
        for call in reversed(message.tool_calls):
            if call.call_id == call_id:
                return call.name
    return "(unknown)"


def _has_tool(tools: Sequence[ToolSpec], name: str) -> bool:
    return any(tool.name == name for tool in tools)


def _text(text: str) -> ChatMessage:
    return ChatMessage("assistant", text=text)
