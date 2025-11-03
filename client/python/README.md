# MCP Client with Ollama Integration - Python (HTTP Transport)

A simple MCP (Model Context Protocol) client implemented in Python that connects to an MCP server and integrates with Ollama to provide AI-powered chat with tool calling capabilities.

## Description

This client demonstrates how to:
- Connect to an MCP server via HTTP transport
- List available tools from the server
- Integrate with Ollama for AI-powered conversations
- Handle streaming responses from the LLM
- Execute tool calls through the MCP server
- Maintain conversation history

## Prerequisites

- Python 3.10 or higher
- pip (Python package installer)
- Ollama installed and running locally (http://127.0.0.1:11434)
- An MCP server running on http://localhost:5000 (e.g., the Chapter 1 HTTP server)

## Installation

1. Install the required dependencies:

```bash
pip install -r requirements.txt
```

2. Make sure Ollama is installed and running:

```bash
# Install Ollama from https://ollama.ai if not already installed
ollama serve
```

3. Pull the required model:

```bash
ollama pull qwen2.5:1.5b
```

Note: The C# client sample uses `qwen3:1.7b`. You can use any compatible Ollama model by changing the `model` variable in `client.py`.

## Usage

1. Start the MCP server (from the chapter01/http-server/python directory):

```bash
cd ../http-server/python
python server.py
```

2. In a new terminal, run the client:

```bash
python client.py
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

You can modify the following settings in `client.py`:

- **MCP Server URL**: Change `http://localhost:5000` to your server's URL
- **Ollama Host**: Change `http://127.0.0.1:11434` to your Ollama instance
- **Model**: Change `qwen2.5:1.5b` to any Ollama model you have installed

## Architecture

This client uses:
- **MCP Python SDK** (`mcp` package) for MCP protocol implementation
- **HTTP transport** for communication with the MCP server
- **Ollama Python SDK** for LLM integration
- **Async/await** pattern for handling requests
- **Streaming responses** for real-time AI output

## Tool Calling Flow

1. Client receives user prompt
2. Client sends prompt to Ollama with available tools
3. Ollama decides if a tool call is needed
4. If needed, client calls the tool through MCP server
5. Tool result is sent back to Ollama
6. Ollama generates final response with tool result context
7. Response is streamed to the user

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
ollama pull qwen2.5:1.5b
```

### Import errors
Make sure all dependencies are installed:
```bash
pip install -r requirements.txt
```
