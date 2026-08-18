"""Cross-cutting concerns via manual handler wrapping (decorator composition).

Same tools as server.py, but the tools/call handler is wrapped in logging,
rate-limiting, and error-handling decorators before it is registered.
Log output goes to stderr; stdout belongs to the protocol.
"""

import asyncio
import functools
import logging
import time

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

logger = logging.getLogger("handler_wrapper")


def with_logging(handler):
    """Log entry, exit, and elapsed time for each call."""

    @functools.wraps(handler)
    async def wrapped(ctx: ServerRequestContext, params: CallToolRequestParams):
        start = time.time()
        logger.info("Handling tool call: %s", params.name)
        result = await handler(ctx, params)
        elapsed = time.time() - start
        logger.info("Handled %s in %.3fs", params.name, elapsed)
        return result

    return wrapped


def with_rate_limit(max_calls: int = 10, window_seconds: int = 60):
    """Enforce a sliding-window rate limit."""
    calls: list[float] = []

    def decorator(handler):
        @functools.wraps(handler)
        async def wrapped(ctx: ServerRequestContext, params: CallToolRequestParams):
            now = time.time()
            calls[:] = [t for t in calls if now - t < window_seconds]
            if len(calls) >= max_calls:
                return CallToolResult(
                    content=[TextContent(type="text", text="Rate limit exceeded.")],
                    is_error=True,
                )
            calls.append(now)
            return await handler(ctx, params)

        return wrapped

    return decorator


def with_error_handling(handler):
    """Convert handler exceptions into is_error tool results."""

    @functools.wraps(handler)
    async def wrapped(ctx: ServerRequestContext, params: CallToolRequestParams):
        try:
            return await handler(ctx, params)
        except Exception as exc:
            logger.exception("Tool %s failed", params.name)
            return CallToolResult(
                content=[TextContent(type="text", text=f"Error: {exc}")],
                is_error=True,
            )

    return wrapped


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
        ]
    )


async def _handle_call_tool_impl(
    ctx: ServerRequestContext, params: CallToolRequestParams
) -> CallToolResult:
    arguments = params.arguments or {}
    if params.name == "echo":
        return CallToolResult(
            content=[TextContent(type="text", text=f"Echo: {arguments['message']}")]
        )
    raise ValueError(f"Unknown tool: {params.name}")


# Compose the decorators (outermost runs first)
_decorated = with_logging(
    with_rate_limit(max_calls=10, window_seconds=60)(
        with_error_handling(_handle_call_tool_impl)
    )
)

server = Server(
    "handler-wrapper-server",
    on_list_tools=handle_list_tools,
    on_call_tool=_decorated,
)


async def main() -> None:
    logging.basicConfig(level=logging.INFO)  # the default handler writes to stderr
    async with stdio_server() as (read_stream, write_stream):
        await server.run(
            read_stream, write_stream, server.create_initialization_options()
        )


if __name__ == "__main__":
    asyncio.run(main())
