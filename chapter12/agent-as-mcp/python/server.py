"""An agent exposed as an MCP server — the one-liner from Chapter 12, §12.3.

Microsoft Agent Framework's `as_mcp_server()` turns any agent into an MCP server
exposing the agent as a single tool (name and description come from the agent).
Any compatible MCP host — an IDE, a chat platform, another framework's harness — can
now call this agent without knowing or caring what framework it's built on. The
server it returns is an `mcp` v2 low-level `Server`, so it speaks the stateless
2026-07-28 revision (and still answers legacy `initialize` clients); see the README.

Model: OpenAI via OPENAI_API_KEY (and optionally OPENAI_MODEL). Listing the tool
needs no model call (any non-empty key lets the client construct); calling it does.
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
    # Server.run() serves both eras: the client's first message decides which.
    async with stdio_server() as (read, write):
        await server.run(read, write, server.create_initialization_options())


anyio.run(main)
