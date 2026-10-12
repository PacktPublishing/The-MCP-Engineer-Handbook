"""SDK middleware (new in v2): the low-level Server's `middleware` list.

The Python SDK gives the low-level Server a public `middleware` list
(default: `[OpenTelemetryMiddleware()]`). Each entry is an async
callable `(ctx, call_next) -> result` that wraps EVERY inbound request and
notification — including `initialize`, params validation, and unknown-method
dispatch — not just tool calls. The list is ordered outermost-first.
"""

import asyncio
import logging
import time

from mcp.server import Server
from mcp.server.context import CallNext, ServerRequestContext
from mcp.server.stdio import stdio_server

from server import handle_call_tool, handle_list_tools

logger = logging.getLogger("middleware_example")


class TimingMiddleware:
    """Log every inbound message's method and elapsed time to stderr."""

    async def __call__(self, ctx: ServerRequestContext, call_next: CallNext):
        start = time.perf_counter()
        try:
            return await call_next(ctx)
        finally:
            elapsed = time.perf_counter() - start
            logger.info("%s handled in %.3fs", ctx.method, elapsed)


server = Server(
    "middleware-example-server",
    on_list_tools=handle_list_tools,
    on_call_tool=handle_call_tool,
)
# Runs inside the default OpenTelemetryMiddleware (the list is outermost-first).
server.middleware.append(TimingMiddleware())


async def main() -> None:
    logging.basicConfig(level=logging.INFO)  # the default handler writes to stderr
    async with stdio_server() as (read_stream, write_stream):
        await server.run(
            read_stream, write_stream, server.create_initialization_options()
        )


if __name__ == "__main__":
    asyncio.run(main())
