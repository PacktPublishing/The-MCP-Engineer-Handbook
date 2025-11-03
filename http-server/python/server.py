#!/usr/bin/env python3
"""
MCP Server with Echo Tool - Streaming HTTP Transport

This is a simple MCP server that provides an "echo" tool via HTTP using Streaming HTTP transport.
It demonstrates how to create an MCP server that can be accessed over HTTP.
"""

import asyncio
import contextlib
from mcp.server import Server
from mcp.server.streamable_http_manager import StreamableHTTPSessionManager
from mcp.types import Tool, TextContent
from starlette.applications import Starlette
from starlette.routing import Mount
import uvicorn


# Create the server instance
app = Server("echo-http-server")


@app.list_tools()
async def list_tools() -> list[Tool]:
    """
    List available tools.
    
    Returns:
        A list of Tool objects that this server provides.
    """
    return [
        Tool(
            name="echo",
            description="A tool that echoes back the input message",
            inputSchema={
                "type": "object",
                "properties": {
                    "message": {
                        "type": "string",
                        "description": "The message to echo back"
                    },
                    "uppercase": {
                        "type": "boolean",
                        "description": "Whether to uppercase the message",
                        "default": False
                    }
                },
                "required": ["message"]
            }
        )
    ]


@app.call_tool()
async def call_tool(name: str, arguments: dict) -> list[TextContent]:
    """
    Handle tool calls.
    
    Args:
        name: The name of the tool to call
        arguments: The arguments for the tool call
        
    Returns:
        A list of TextContent objects with the tool result.
        
    Raises:
        ValueError: If the tool name is unknown
    """
    if name == "echo":
        message = arguments.get("message", "")
        uppercase = arguments.get("uppercase", False)
        
        if not message:
            result = "No message provided"
        else:
            result = message.upper() if uppercase else message
            result = f"Echo: {result}"
        
        return [TextContent(type="text", text=result)]
    
    raise ValueError(f"Unknown tool: {name}")


async def main():
    """Main entry point for the HTTP server."""
    # Create session manager for streamable HTTP
    session_manager = StreamableHTTPSessionManager(
        app=app,
        json_response=False,
        stateless=False
    )
    
    # Create lifespan context manager
    @contextlib.asynccontextmanager
    async def lifespan(app_ctx):
        async with session_manager.run():
            yield
    
    # Create Starlette app with Mount for the ASGI app
    starlette_app = Starlette(
        routes=[
            Mount("/", app=session_manager.handle_request),
        ],
        lifespan=lifespan
    )
    
    # Run server
    print("Starting MCP HTTP server on http://127.0.0.1:5000")
    print("Streaming HTTP endpoint: http://127.0.0.1:5000/")
    
    config = uvicorn.Config(
        starlette_app,
        host="127.0.0.1",
        port=5000,
        log_level="info"
    )
    server = uvicorn.Server(config)
    await server.serve()


if __name__ == "__main__":
    asyncio.run(main())
