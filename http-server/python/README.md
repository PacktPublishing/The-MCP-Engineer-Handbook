# MCP Echo Server - Python (Streaming HTTP Transport)

A simple MCP (Model Context Protocol) server implemented in Python that provides an "echo" tool via HTTP using Streaming HTTP transport.

## Description

This server demonstrates how to create an MCP server that can be accessed over HTTP. It uses Streaming HTTP for bi-directional communication between the client and server. The server provides a single tool called "echo" that echoes back the input message, with an optional uppercase transformation.

## Prerequisites

- Python 3.10 or higher
- pip (Python package installer)

## Installation

1. Install the required dependencies:

```bash
pip install -r requirements.txt
```

## Usage

Run the server:

```bash
python server.py
```

The server will start on `http://127.0.0.1:5000` with the following endpoint:
- Streaming HTTP endpoint: `http://127.0.0.1:5000/` (for all MCP communication)

## Tool: Echo

The server provides one tool:

### `echo`

Echoes back the input message with optional uppercase transformation.

**Parameters:**
- `message` (string, required): The message to echo back
- `uppercase` (boolean, optional, default: false): Whether to uppercase the message

**Example:**
```json
{
  "message": "Hello, World!",
  "uppercase": false
}
```

**Returns:**
```
Echo: Hello, World!
```

## Integration with MCP Clients

To use this server with an MCP client that supports HTTP transport, configure it to connect to:
- Base URL: `http://127.0.0.1:5000`

Example configuration for Claude Desktop:
```json
{
  "mcpServers": {
    "echo-server": {
      "url": "http://127.0.0.1:5000"
    }
  }
}
```

## Architecture

This server uses:
- **MCP Python SDK** (`mcp` package) for the MCP protocol implementation
- **Streaming HTTP transport** for bi-directional communication over HTTP
- **StreamableHTTPSessionManager** for managing HTTP sessions
- **Starlette** web framework for HTTP handling
- **Uvicorn** ASGI server for running the application
- **Async/await** pattern for handling requests

The server implements two main handlers:
1. `list_tools()` - Returns the list of available tools
2. `call_tool()` - Executes the requested tool

## Development

To run the server in development mode with auto-reload:

```bash
uvicorn server:starlette_app --reload --host 127.0.0.1 --port 5000
```

Note: You'll need to modify the server.py file slightly to expose the `starlette_app` variable at module level for this to work.

## Testing

You can test the server using curl or any HTTP client:

```bash
# Send a request to list tools
curl -X POST http://127.0.0.1:5000/ \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc": "2.0", "id": 1, "method": "tools/list"}'
```
