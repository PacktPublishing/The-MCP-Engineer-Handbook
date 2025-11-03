# MCP Echo Server - Python (Stdio Transport)

A simple MCP (Model Context Protocol) server implemented in Python that provides an "echo" tool via stdio transport.

## Description

This server demonstrates the basic structure of an MCP server using the Python SDK. It provides a single tool called "echo" that echoes back the input message, with an optional uppercase transformation.

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

The server will start and communicate via stdio (standard input/output). It's designed to be used by MCP clients that communicate over stdio transport.

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

To use this server with an MCP client, configure it as a stdio server. For example, in Claude Desktop's configuration:

```json
{
  "mcpServers": {
    "echo-server": {
      "command": "python",
      "args": ["/path/to/server.py"]
    }
  }
}
```

## Architecture

This server uses:
- **MCP Python SDK** (`mcp` package) for the MCP protocol implementation
- **Stdio transport** for communication with clients
- **Async/await** pattern for handling requests

The server implements two main handlers:
1. `list_tools()` - Returns the list of available tools
2. `call_tool()` - Executes the requested tool
