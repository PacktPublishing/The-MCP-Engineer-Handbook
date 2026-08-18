# A minimal chat-model abstraction (the role Microsoft.Extensions.AI plays in the C#
# canonical) plus a deterministic scripted implementation — no API key needed. The
# scripted model plays the model's side of Pattern 3: discover what resources exist via
# list_resources, then route a read to the right server via read_resource. A real
# provider plugs in by implementing the same ChatModel protocol over its own SDK.
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


class ScriptedChatModel:
    def __init__(self) -> None:
        self._call_counter = 0

    async def respond(
        self, history: Sequence[ChatMessage], tools: Sequence[ToolSpec]
    ) -> ChatMessage:
        last = history[-1]

        if last.role == "tool" and last.tool_results:
            tool_result = last.tool_results[-1]
            call_name = _find_call_name(history, tool_result.call_id)
            result_text = tool_result.result

            # The aggregated catalog came back; every entry carries its serverName.
            # The user asked for the wiki server's release notes — route the read there.
            if call_name == "list_resources" and '"wiki"' in result_text:
                return self._call(
                    "read_resource",
                    {"serverName": "wiki", "uri": "file:///release_notes.md"},
                )

            if call_name == "read_resource":
                return _text(
                    "Both servers expose the same catalog: a user guide, release notes, a tip of "
                    "the day, plus telemetry and podcast material. From the wiki server's release "
                    "notes: offline vaults can now be converted to synced vaults in place, search "
                    "now indexes attachments up to 25 MB, and the legacy nimbus:// scheme is deprecated."
                )

            return _text(f"Tool '{call_name}' returned: {result_text}")

        # Opening user request: discover the catalog first.
        if last.role == "user":
            return self._call("list_resources", {})

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


def _text(text: str) -> ChatMessage:
    return ChatMessage("assistant", text=text)
