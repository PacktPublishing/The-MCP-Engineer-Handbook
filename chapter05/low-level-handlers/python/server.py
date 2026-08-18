import asyncio

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


# Handler for tools/list
async def handle_list_tools(
    ctx: ServerRequestContext, params: PaginatedRequestParams | None
) -> ListToolsResult:
    return ListToolsResult(
        tools=[
            Tool(
                name="echo",
                description="Echo back the provided message",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "message": {
                            "type": "string",
                            "description": "The message to echo back",
                        },
                    },
                    "required": ["message"],
                },
            ),
            Tool(
                name="reverse",
                description="Reverse the provided message",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "message": {
                            "type": "string",
                            "description": "The message to reverse",
                        },
                    },
                    "required": ["message"],
                },
            ),
            Tool(
                name="shout",
                description="Return the provided message in uppercase",
                inputSchema={
                    "type": "object",
                    "properties": {
                        "message": {
                            "type": "string",
                            "description": "The message to shout",
                        },
                    },
                    "required": ["message"],
                },
            ),
        ]
    )


# Handler for tools/call
async def handle_call_tool(
    ctx: ServerRequestContext, params: CallToolRequestParams
) -> CallToolResult:
    arguments = params.arguments or {}
    if params.name == "echo":
        return CallToolResult(
            content=[TextContent(type="text", text=f"Echo: {arguments['message']}")]
        )
    if params.name == "reverse":
        return CallToolResult(
            content=[TextContent(type="text", text=arguments["message"][::-1])]
        )
    if params.name == "shout":
        return CallToolResult(
            content=[TextContent(type="text", text=arguments["message"].upper())]
        )
    raise ValueError(f"Unknown tool: {params.name}")


server = Server(
    "low-level-handlers-server",
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
