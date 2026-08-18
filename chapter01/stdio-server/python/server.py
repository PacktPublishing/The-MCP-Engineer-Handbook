from mcp.server import MCPServer

server = MCPServer(name="echo-server", version="0.1.0")


@server.tool()
def echo(message: str, uppercase: bool = False) -> str:
    """A tool that echoes back the input message.

    Args:
        message: The message to echo back.
        uppercase: Whether to uppercase the message.
    """
    if not message:
        return "No message provided"
    result = message.upper() if uppercase else message
    return f"Echo: {result}"


if __name__ == "__main__":
    server.run()
