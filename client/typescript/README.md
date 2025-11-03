# MCP Client with Ollama Integration - TypeScript (HTTP Transport)

A simple MCP (Model Context Protocol) client implemented in TypeScript that connects to an MCP server and integrates with Ollama to provide AI-powered chat with tool calling capabilities.

## Description

This client demonstrates how to:
- Connect to an MCP server via HTTP transport
- List available tools from the server
- Integrate with Ollama for AI-powered conversations
- Handle streaming responses from the LLM
- Execute tool calls through the MCP server
- Maintain conversation history

## Prerequisites

- Node.js 18 or higher
- npm (Node package manager)
- Ollama installed and running locally (http://127.0.0.1:11434)
- An MCP server running on http://localhost:5000 (e.g., the Chapter 1 HTTP server)

## Installation

1. Install the required dependencies:

```bash
npm install
```

2. Make sure Ollama is installed and running:

```bash
# Install Ollama from https://ollama.ai if not already installed
ollama serve
```

3. Pull the required model:

```bash
ollama pull qwen3:1.7b
```

Note: This uses the same model as the C# sample (`qwen3:1.7b`). The Python sample uses `qwen2.5:1.5b`. You can use any compatible Ollama model by changing the `model` variable in `src/client.ts`.

## Usage

1. Start the MCP server (from the chapter01/http-server/typescript directory):

```bash
cd ../../http-server/typescript
npm install
npm run dev
```

2. In a new terminal, build and run the client:

```bash
# Build the TypeScript code
npm run build

# Run the built client
npm start

# Or run directly in development mode
npm run dev
```

3. Start chatting! The client will:
   - Connect to the MCP server
   - Display available tools
   - Accept your prompts
   - Stream AI responses
   - Automatically call tools when needed
   - Display tool results

## Example Conversation

```
Connected to MCP server with tools:
  - echo: A tool that echoes back the input message

Your prompt:
Echo "Hello, World!" in uppercase

AI Response:
[Calling tool: echo]
[Tool result: Echo: HELLO, WORLD!]
The message has been echoed in uppercase: "HELLO, WORLD!"
```

## Configuration

You can modify the following settings in `src/client.ts`:

- **MCP Server URL**: Change `http://localhost:5000/mcp` to your server's URL
- **Ollama Host**: Change `http://127.0.0.1:11434` to your Ollama instance
- **Model**: Change `qwen3:1.7b` to any Ollama model you have installed

## Project Structure

```
typescript/
├── src/
│   └── client.ts       # Main client implementation
├── package.json        # Dependencies and scripts
├── tsconfig.json       # TypeScript configuration
├── .gitignore          # Git ignore patterns
└── README.md           # This file
```

## Architecture

This client uses:
- **MCP TypeScript SDK** (`@modelcontextprotocol/sdk`) for MCP protocol implementation
- **Streaming HTTP transport** for communication with the MCP server
- **Ollama TypeScript SDK** for LLM integration
- **Async/await** pattern for handling requests
- **Streaming responses** for real-time AI output
- **Modern TypeScript** with ES modules and strict type checking

## Tool Calling Flow

1. Client receives user prompt
2. Client sends prompt to Ollama with available tools
3. Ollama decides if a tool call is needed
4. If needed, client calls the tool through MCP server
5. Tool result is sent back to Ollama
6. Ollama generates final response with tool result context
7. Response is streamed to the user

## Development

### Building

```bash
npm run build
```

This compiles TypeScript files from `src/` to JavaScript in `dist/`.

### Running in Development Mode

```bash
npm run dev
```

This uses `tsx` to run TypeScript files directly without compilation.

### Cleaning Build Artifacts

```bash
npm run clean
```

## Troubleshooting

### Connection refused to MCP server
Make sure the MCP server is running on http://localhost:5000

### Connection refused to Ollama
Make sure Ollama is installed and running:
```bash
ollama serve
```

### Model not found
Pull the required model:
```bash
ollama pull qwen3:1.7b
```

### Import errors or module not found
Make sure all dependencies are installed:
```bash
npm install
```

### TypeScript compilation errors
Make sure you're using Node.js 18 or higher:
```bash
node --version
```

## Comparison with Other Implementations

This TypeScript implementation is functionally equivalent to the Python and C# implementations:

- **Python**: Uses `mcp` package and `ollama` package with async/await
- **C#**: Uses `ModelContextProtocol.Client` and `OllamaSharp` with Microsoft.Extensions.AI
- **TypeScript**: Uses `@modelcontextprotocol/sdk` and `ollama` package with async/await

All three implementations:
- Use HTTP transport to connect to MCP servers
- Support streaming responses
- Handle tool calling through the MCP protocol
- Maintain conversation history
- Provide interactive chat interfaces

## License

MIT
