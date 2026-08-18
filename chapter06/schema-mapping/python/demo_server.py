# Demo MCP server whose tool input schemas deliberately exercise JSON Schema
# 2020-12 features that stress provider mapping (§6.2.2): numeric and string
# constraints, defaults, an enum, a oneOf composition, $defs/$ref, and one
# tool with an output schema (§6.2.3). The low-level Server API is used so
# the schemas can be written as raw JSON Schema rather than derived from
# Python type hints.
import asyncio
import json

from mcp.server import Server
from mcp.server.context import ServerRequestContext
from mcp.server.stdio import stdio_server
from mcp_types import (
    CallToolRequestParams,
    CallToolResult,
    ListToolsResult,
    PaginatedRequestParams,
    TextContent,
    Tool,
)

TOOLS = [
    Tool(
        name="search_stays",
        description="Search for hotel stays in a city.",
        # Constraints, defaults, an enum, format — the keywords whose provider
        # support is most uneven.
        inputSchema={
            "type": "object",
            "properties": {
                "city": {
                    "type": "string",
                    "description": "Destination city.",
                    "minLength": 2,
                },
                "check_in": {
                    "type": "string",
                    "description": "Check-in date.",
                    "format": "date",
                    "pattern": "^\\d{4}-\\d{2}-\\d{2}$",
                },
                "nights": {"type": "integer", "minimum": 1, "maximum": 30, "default": 1},
                "guests": {"type": "integer", "minimum": 1, "maximum": 8, "default": 2},
                "sort": {
                    "type": "string",
                    "description": "Result ordering.",
                    "enum": ["price", "rating", "distance"],
                    "default": "price",
                },
            },
            "required": ["city", "check_in"],
        },
    ),
    Tool(
        name="book_stay",
        description="Book a stay found via search_stays.",
        # Composition and references: oneOf over two payment shapes defined in
        # $defs, discriminated by a const. First-class in 2026-07-28 tool
        # schemas; several model APIs have never heard of them.
        inputSchema={
            "type": "object",
            "$defs": {
                "Guest": {
                    "type": "object",
                    "properties": {
                        "full_name": {"type": "string", "minLength": 1},
                        "email": {"type": "string", "format": "email"},
                    },
                    "required": ["full_name", "email"],
                },
                "CardPayment": {
                    "type": "object",
                    "properties": {
                        "method": {"const": "card"},
                        "card_token": {
                            "type": "string",
                            "description": "Tokenized card reference.",
                            "pattern": "^tok_[A-Za-z0-9]{8,}$",
                        },
                    },
                    "required": ["method", "card_token"],
                },
                "InvoicePayment": {
                    "type": "object",
                    "properties": {
                        "method": {"const": "invoice"},
                        "po_number": {"type": "string", "minLength": 3},
                    },
                    "required": ["method", "po_number"],
                },
            },
            "properties": {
                "stay_id": {"type": "string", "pattern": "^stay_[a-z0-9]+$"},
                "lead_guest": {"$ref": "#/$defs/Guest"},
                "payment": {
                    "description": "How the stay is paid.",
                    "oneOf": [
                        {"$ref": "#/$defs/CardPayment"},
                        {"$ref": "#/$defs/InvoicePayment"},
                    ],
                },
            },
            "required": ["stay_id", "lead_guest", "payment"],
        },
    ),
    Tool(
        name="get_booking",
        description="Retrieve a booking by reference.",
        inputSchema={
            "type": "object",
            "properties": {
                "booking_ref": {"type": "string", "pattern": "^BK-[A-Z0-9]{6}$"},
            },
            "required": ["booking_ref"],
        },
        # Output schema (§6.2.3): most model APIs have no slot for this, so
        # the client-side adapter decides whether to pass, lift, or drop it.
        outputSchema={
            "type": "object",
            "properties": {
                "booking_ref": {"type": "string"},
                "status": {"type": "string", "enum": ["confirmed", "pending", "cancelled"]},
                "nights": {"type": "integer", "minimum": 1},
                "total": {"type": "number", "minimum": 0},
                "currency": {"type": "string", "pattern": "^[A-Z]{3}$"},
            },
            "required": ["booking_ref", "status", "total", "currency"],
        },
    ),
]


async def handle_list_tools(
    ctx: ServerRequestContext, params: PaginatedRequestParams | None
) -> ListToolsResult:
    return ListToolsResult(tools=TOOLS)


async def handle_call_tool(
    ctx: ServerRequestContext, params: CallToolRequestParams
) -> CallToolResult:
    args = params.arguments or {}

    if params.name == "search_stays":
        return CallToolResult(
            content=[
                TextContent(
                    type="text",
                    text=f"2 stays in {args['city']}: stay_a1b2c3 (Hotel Borealis, "
                    "142 EUR/night), stay_d4e5f6 (Pension Vega, 88 EUR/night).",
                )
            ]
        )
    if params.name == "book_stay":
        return CallToolResult(
            content=[
                TextContent(
                    type="text",
                    text=f"Booked {args['stay_id']}. Reference: BK-7Q2M4X.",
                )
            ]
        )
    if params.name == "get_booking":
        booking = {
            "booking_ref": str(args["booking_ref"]),
            "status": "confirmed",
            "nights": 3,
            "total": 426,
            "currency": "EUR",
        }
        return CallToolResult(
            content=[TextContent(type="text", text=json.dumps(booking, separators=(",", ":")))],
            structuredContent=booking,
        )
    raise ValueError(f"Unknown tool: {params.name}")


server = Server(
    "demo-booking-server",
    on_list_tools=handle_list_tools,
    on_call_tool=handle_call_tool,
)


async def main() -> None:
    async with stdio_server() as (read_stream, write_stream):
        await server.run(
            read_stream, write_stream, server.create_initialization_options()
        )


if __name__ == "__main__":
    asyncio.run(main())
