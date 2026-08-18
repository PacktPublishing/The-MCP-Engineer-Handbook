import sys

from mcp.server import MCPServer

server = MCPServer(name="echo-http-server", version="0.1.0")


@server.tool()
def echo(message: str, uppercase: bool = False) -> str:
    """A tool that echoes back the input message.

    Args:
        message: The message to echo back.
        uppercase: Whether to uppercase the message.
    """
    # Log the call so a reader can verify the tool was invoked (§1.8).
    print(f'echo tool called: message="{message}", uppercase={uppercase}', file=sys.stderr)
    if not message:
        return "No message provided"
    result = message.upper() if uppercase else message
    return f"Echo: {result}"


if __name__ == "__main__":
    # Serves the MCP endpoint at http://localhost:5000/mcp
    server.run(transport="streamable-http", port=5000)
