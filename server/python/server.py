#!/usr/bin/env python3
"""
MCP Server with Echo Tool - Stdio Transport

This is a simple MCP server that provides an "echo" tool via stdio transport.
It demonstrates the basic structure of an MCP server in Python.
"""

import asyncio
from mcp.server import Server
from mcp.server.stdio import stdio_server
from mcp.types import Tool, TextContent


# Create the server instance
app = Server("echo-server")


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
    """Main entry point for the server."""
    async with stdio_server() as (read_stream, write_stream):
        await app.run(
            read_stream,
            write_stream,
            app.create_initialization_options()
        )


if __name__ == "__main__":
    asyncio.run(main())
