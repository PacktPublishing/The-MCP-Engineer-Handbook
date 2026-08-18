# A minimal chat-model abstraction (the role Microsoft.Extensions.AI plays in the C#
# canonical) plus a deterministic scripted implementation — no API key needed. The
# scripted model inspects where the resource landed in the context and answers the way
# a capable model plausibly would, so the three injection approaches can be compared
# on identical inputs. A real provider plugs in by implementing the same ChatModel
# protocol over its own SDK.
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Literal, Protocol, Sequence

Role = Literal["system", "user", "assistant"]


# Content parts are objects (not bare strings) so the host can remember WHICH parts
# carry resource data by identity and mark them in the printed context dump.
@dataclass(eq=False)
class TextPart:
    text: str


@dataclass
class ChatMessage:
    role: Role
    parts: list[TextPart] = field(default_factory=list)

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
    return "".join(part.text for message in history if message.role == role for part in message.parts)
