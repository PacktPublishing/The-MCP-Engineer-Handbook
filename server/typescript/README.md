# MCP Echo Server - TypeScript (Stdio)

A simple Model Context Protocol (MCP) server implementation in TypeScript that provides an "echo" tool via stdio transport.

## Features

- **Echo Tool**: Echoes back the input message with optional uppercase transformation
- **Stdio Transport**: Communicates via standard input/output for integration with MCP clients
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

## Integration

This server can be integrated with any MCP client that supports stdio transport. Add it to your client configuration:

```json
{
  "mcpServers": {
    "echo": {
      "command": "node",
      "args": ["/path/to/dist/server.js"]
    }
  }
}
```

## Development

- `npm run build` - Compile TypeScript to JavaScript
- `npm run dev` - Run in development mode with tsx
- `npm run clean` - Remove compiled files

## License

MIT
