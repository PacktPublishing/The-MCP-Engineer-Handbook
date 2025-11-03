# MCP Echo Server - TypeScript (Streaming HTTP)

A simple Model Context Protocol (MCP) server implementation in TypeScript that provides an "echo" tool via Streaming HTTP transport.

## Features

- **Echo Tool**: Echoes back the input message with optional uppercase transformation
- **Streaming HTTP Transport**: Uses MCP Streaming HTTP specification for communication over HTTP
- **Session Management**: Manages multiple client sessions with unique session IDs
- **SSE Streaming**: Supports Server-Sent Events (SSE) for streaming responses
- **Type-Safe**: Written in idiomatic TypeScript with strict type checking (no `any` types)

## Prerequisites

- Node.js 18 or higher
- npm or yarn

## Installation

```bash
npm install
```

## Usage

### Build the server

```bash
npm run build
```

### Run the server

```bash
npm start
```

### Development mode (with auto-reload)

```bash
npm run dev
```

The server will start on `http://127.0.0.1:5000`

## Tool Documentation

### echo

Echoes back the input message.

**Parameters:**
- `message` (string, required): The message to echo back
- `uppercase` (boolean, optional, default: false): Whether to uppercase the message

**Examples:**

```json
{
  "message": "Hello, World!"
}
```

Returns: `Echo: Hello, World!`

```json
{
  "message": "Hello, World!",
  "uppercase": true
}
```

Returns: `Echo: HELLO, WORLD!`

## API Endpoints

### POST /mcp
Main endpoint for MCP JSON-RPC messages. Session management is handled automatically:
- First request (initialization) creates a new session with a generated UUID
- Subsequent requests must include the `mcp-session-id` header

### GET /mcp
Establishes an SSE (Server-Sent Events) stream for receiving server notifications.
Requires `mcp-session-id` header with a valid session ID.

### DELETE /mcp
Terminates an active session.
Requires `mcp-session-id` header with a valid session ID.

## Integration

This server can be integrated with any MCP client that supports Streaming HTTP transport. Example client configuration:

```json
{
  "mcpServers": {
    "echo-http": {
      "transport": "streamableHttp",
      "url": "http://127.0.0.1:5000/mcp"
    }
  }
}
```

## Development

- `npm run build` - Compile TypeScript to JavaScript
- `npm run dev` - Run in development mode with tsx
- `npm run clean` - Remove compiled files

## Transport Details

This implementation uses the MCP Streaming HTTP transport specification, which:
- Manages stateful sessions with unique session IDs
- Supports both request/response and streaming patterns
- Uses SSE for server-to-client streaming
- Follows the JSON-RPC 2.0 protocol

Note: This replaces the deprecated SSE transport with the newer Streaming HTTP transport.

## License

MIT
