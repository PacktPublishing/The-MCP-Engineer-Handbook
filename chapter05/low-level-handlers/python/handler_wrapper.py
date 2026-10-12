"""Cross-cutting concerns via manual handler wrapping (decorator composition).

Reuses server.py's handlers (tools, schemas, and argument validation), but the
tools/call handler is wrapped in logging, rate-limiting, and error-handling
decorators before it is registered.
Log output goes to stderr; stdout belongs to the protocol.
"""

import asyncio
import functools
import logging
import time

from mcp import MCPError
from mcp.server import Server
from mcp.server.context import ServerRequestContext
from mcp.server.stdio import stdio_server
from mcp_types import CallToolRequestParams, CallToolResult, TextContent

from server import handle_call_tool, handle_list_tools

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
    """Convert unexpected handler exceptions into is_error tool results.

    MCPError is a deliberate protocol error (such as an unknown tool), so it
    passes through. Anything else is logged in full here, and the client only
    sees a generic message: exception text can carry internal details.
    """

    @functools.wraps(handler)
    async def wrapped(ctx: ServerRequestContext, params: CallToolRequestParams):
        try:
            return await handler(ctx, params)
        except MCPError:
            raise
        except Exception:
            logger.exception("Tool %s failed", params.name)
            return CallToolResult(
                content=[TextContent(type="text", text=f"Tool {params.name} failed.")],
                is_error=True,
            )

    return wrapped


# Compose the decorators (outermost runs first)
_decorated = with_logging(
    with_rate_limit(max_calls=10, window_seconds=60)(
        with_error_handling(handle_call_tool)
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
