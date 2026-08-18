from mcp.server import MCPServer

server = MCPServer(name="hello-server", version="0.1.0")


@server.tool()
def say_hello(name: str) -> str:
    """Greets the caller by name.

    Args:
        name: Name to greet.
    """
    return f"Hello, {name}! This server runs on the 2026-07-28 MCP revision."


if __name__ == "__main__":
    server.run()
