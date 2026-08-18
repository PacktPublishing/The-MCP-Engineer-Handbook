"""An agent exposed as an MCP server — the one-liner from Chapter 12, §12.3.

Microsoft Agent Framework's `as_mcp_server()` turns any agent into an MCP server
exposing the agent as a single tool (name and description come from the agent).
Any MCP host — an IDE, a chat platform, another framework's harness — can now call
this agent without knowing or caring what framework it's built on.

Model: OpenAI via OPENAI_API_KEY (and optionally OPENAI_MODEL). Listing the tool
needs no model; calling it does.
"""

import os

import anyio
from agent_framework import Agent
from agent_framework.openai import OpenAIChatClient
from mcp.server.stdio import stdio_server

agent = Agent(
    client=OpenAIChatClient(model=os.environ.get("OPENAI_MODEL", "gpt-4.1-mini")),
    name="haiku_writer",
    description="Writes a haiku about the given topic.",
    instructions="You write haiku. Respond with only the haiku: three lines, nothing else.",
)

# The whole pattern is this call.
server = agent.as_mcp_server(server_name="haiku-agent", version="0.1.0")


async def main() -> None:
    async with stdio_server() as (read, write):
        await server.run(read, write, server.create_initialization_options())


anyio.run(main)
