# Companion demo server for the MRTR client sample (Chapter 6, Section 6.2.5).
#
# `book_meeting` answers its first call with `input_required`, asking for
# meeting details (a form exercising titles, descriptions, and defaults), and
# answers the retry with a SECOND elicitation (a final confirmation) before
# completing — so the client's gather-and-retry loop genuinely iterates.
#
# `never_satisfied` asks forever; it exists so the client can demonstrate its
# retry budget tripping against a misbehaving server.
#
# Python SDK notes (mcp 2.0.0):
#   - A tool function returns an `InputRequiredResult` (from `mcp_types`)
#     directly; there is no wrapper helper like the TS SDK's `inputRequired()`.
#   - Retried answers arrive on `ctx.input_responses` (a dict of ElicitResult
#     etc., keyed like `input_requests`) and `ctx.request_state`.
#   - `server.run()` serves stdio through `serve_dual_era_loop`: a client that
#     opens with `server/discover` or the modern `_meta` envelope gets the
#     2026-07-28 era (where MRTR exists); an `initialize` handshake locks the
#     connection to the legacy era, where returning `InputRequiredResult`
#     is a server error (there is NO legacy downgrade shim, unlike TS).
#   - Nothing in the framework enforces the client's `elicitation` capability
#     before a tool embeds an elicit request (the TS server refuses with
#     -32021); the capability check below is the tool's own job.

import json
import sys

from mcp.server.mcpserver import Context, MCPServer
from mcp_types import ElicitRequest, ElicitRequestFormParams, ElicitResult, InputRequiredResult
from pydantic import BaseModel, Field, ValidationError

server = MCPServer(name="mrtr-demo-server", version="0.1.0")


# Validates the untrusted form content the client sends back.
class Details(BaseModel):
    title: str = Field(min_length=1, max_length=80)
    duration: str  # "15" | "30" | "60" — checked below; enum via schema on the wire
    notify: bool = True


def _text(t: str) -> str:
    return t


# The tool mints plain-JSON state so this code stays readable. Unlike the TS
# SDK, MCPServer seals every outgoing requestState by DEFAULT before it
# reaches the wire (`RequestStateBoundary` middleware, AES-256-GCM with an
# ephemeral process-local key, 600 s TTL, fail-closed) and unseals the echo —
# the handler only ever sees plaintext it minted. Multi-instance deployments
# must supply shared keys via `MCPServer(request_state_security=...)`.
def _read_state(raw: str | None) -> dict | None:
    if raw is None:
        return None
    try:
        state = json.loads(raw)
    except ValueError:
        return None
    return state if isinstance(state, dict) else None


def _client_supports_form_elicitation(ctx: Context) -> bool:
    caps = ctx.client_capabilities
    return caps is not None and caps.elicitation is not None


def _accepted_content(ctx: Context, key: str) -> dict | None:
    """Schema-aware read of one retried answer: accept-action content or None."""
    responses = ctx.input_responses or {}
    view = responses.get(key)
    if not isinstance(view, ElicitResult) or view.action != "accept":
        return None
    return dict(view.content or {})


@server.tool(name="book_meeting")
async def book_meeting(room: str, ctx: Context) -> str | InputRequiredResult:
    """Books a meeting room. Elicits meeting details, then a final confirmation, before booking.

    Args:
        room: Room to book.
    """
    if not _client_supports_form_elicitation(ctx):
        return _text("This tool needs a client that supports form elicitation.")
    state = _read_state(ctx.request_state)

    # Round 1: no state yet — ask for the meeting details.
    if state is None:
        return InputRequiredResult(
            input_requests={
                "details": ElicitRequest(
                    params=ElicitRequestFormParams(
                        message=f"Booking room {room}. What should the meeting look like?",
                        requested_schema={
                            "type": "object",
                            "properties": {
                                "title": {
                                    "type": "string",
                                    "title": "Meeting title",
                                    "description": "Shown on the room display.",
                                    "minLength": 1,
                                    "maxLength": 80,
                                },
                                "duration": {
                                    "type": "string",
                                    "title": "Duration (minutes)",
                                    "description": "How long to hold the room.",
                                    "enum": ["15", "30", "60"],
                                    "default": "30",
                                },
                                "notify": {
                                    "type": "boolean",
                                    "title": "Notify attendees",
                                    "description": "Send a calendar notification when booked.",
                                    "default": True,
                                },
                            },
                            "required": ["title", "duration"],
                        },
                    )
                ),
            },
            request_state=json.dumps({"step": "awaiting-details", "room": room}),
        )

    # Round 2: the retry carrying the details form's answers.
    if state.get("step") == "awaiting-details":
        the_room = state.get("room", room)
        content = _accepted_content(ctx, "details")
        if content is None:
            return _text(f"Room {the_room} not booked (declined or missing). Ask me again anytime.")
        # Schema-aware read: validates the untrusted content before use.
        try:
            details = Details.model_validate(content)
            if details.duration not in ("15", "30", "60"):
                raise ValueError("duration out of range")
        except (ValidationError, ValueError):
            return _text(f"Room {the_room} not booked: the details did not match the requested schema.")
        # Ask once more — a retry is allowed to come back input_required.
        return InputRequiredResult(
            input_requests={
                "confirm": ElicitRequest(
                    params=ElicitRequestFormParams(
                        message=f'Book {the_room} for "{details.title}" ({details.duration} min)?',
                        requested_schema={
                            "type": "object",
                            "properties": {
                                "confirm": {
                                    "type": "boolean",
                                    "title": "Confirm booking",
                                    "description": "The room is charged to your team once booked.",
                                    "default": True,
                                },
                            },
                            "required": ["confirm"],
                        },
                    )
                ),
            },
            request_state=json.dumps(
                {"step": "awaiting-confirm", "room": the_room, "details": details.model_dump()}
            ),
        )

    # Round 3: the retry carrying the confirmation.
    the_room = state.get("room", room)
    confirmation = _accepted_content(ctx, "confirm")
    if confirmation is None or confirmation.get("confirm") is not True:
        return _text(f"Room {the_room} not booked: confirmation was withheld.")
    details = Details.model_validate(state.get("details") or {})
    return _text(
        f'Booked {the_room} for "{details.title}" ({details.duration} min). '
        + ("Attendees notified." if details.notify else "No notification sent.")
    )


@server.tool(name="never_satisfied")
async def never_satisfied(ctx: Context) -> InputRequiredResult:
    """Misbehaving tool: keeps requesting input forever. For retry-budget demos."""
    try:
        round_no = int(ctx.request_state or "0") + 1
    except ValueError:
        round_no = 1
    return InputRequiredResult(
        input_requests={
            "again": ElicitRequest(
                params=ElicitRequestFormParams(
                    message=f"Still not satisfied (round {round_no}). Once more?",
                    requested_schema={
                        "type": "object",
                        "properties": {
                            "again": {"type": "boolean", "title": "Go again", "default": True},
                        },
                        "required": ["again"],
                    },
                )
            ),
        },
        request_state=str(round_no),
    )


if __name__ == "__main__":
    if "--http" in sys.argv:
        # Streamable HTTP entry (the modern era's primary transport).
        server.run(transport="streamable-http")
    else:
        server.run()  # stdio; dual-era (modern for envelope/discover openings)
