import asyncio

from jsonschema import Draft202012Validator
from jsonschema.exceptions import best_match
from mcp import MCPError
from mcp.server import Server
from mcp.server.context import ServerRequestContext
from mcp.server.stdio import stdio_server
from mcp_types import (
    INVALID_PARAMS,
    CallToolRequestParams,
    CallToolResult,
    ListToolsResult,
    PaginatedRequestParams,
    TextContent,
    Tool,
)


def message_schema(description: str) -> dict:
    return {
        "type": "object",
        "properties": {"message": {"type": "string", "description": description}},
        "required": ["message"],
    }


# One list feeds both tools/list and argument validation in tools/call
TOOLS = [
    Tool(
        name="echo",
        description="Echo back the provided message",
        inputSchema=message_schema("The message to echo back"),
    ),
    Tool(
        name="reverse",
        description="Reverse the provided message",
        inputSchema=message_schema("The message to reverse"),
    ),
    Tool(
        name="shout",
        description="Return the provided message in uppercase",
        inputSchema=message_schema("The message to shout"),
    ),
]
VALIDATORS = {tool.name: Draft202012Validator(tool.input_schema) for tool in TOOLS}


def tool_error(text: str) -> CallToolResult:
    return CallToolResult(content=[TextContent(type="text", text=text)], is_error=True)


# Handler for tools/list
async def handle_list_tools(
    ctx: ServerRequestContext, params: PaginatedRequestParams | None
) -> ListToolsResult:
    return ListToolsResult(tools=TOOLS)


# Handler for tools/call. The low-level Server dispatches whatever arrives:
# rejecting unknown tools and arguments that don't match the advertised
# schema is now this handler's job.
async def handle_call_tool(
    ctx: ServerRequestContext, params: CallToolRequestParams
) -> CallToolResult:
    validator = VALIDATORS.get(params.name)
    if validator is None:
        # Unknown tool: a protocol error, as the high-level API reports it
        raise MCPError(code=INVALID_PARAMS, message=f"Unknown tool: {params.name}")
    arguments = params.arguments or {}
    if error := best_match(validator.iter_errors(arguments)):
        # Invalid arguments: a tool error the model can read and correct
        return tool_error(f"Invalid arguments for tool {params.name}: {error.message}")

    message: str = arguments["message"]
    if params.name == "echo":
        return CallToolResult(content=[TextContent(type="text", text=f"Echo: {message}")])
    if params.name == "reverse":
        return CallToolResult(content=[TextContent(type="text", text=message[::-1])])
    return CallToolResult(content=[TextContent(type="text", text=message.upper())])


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
