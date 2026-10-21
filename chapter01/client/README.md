# client (chapter 1 model-wired MCP client)

Companion sample for **Chapter 1, §1.8 (Wiring Models to MCP)** — the C# MCP client from the printed extracts, wired to a local qwen3 model via Ollama so no API key or subscription is needed. It connects to the `chapter01/http-server` sample over Streamable HTTP, lists its tools, hands them to the model (`McpClientTool` inherits from `AITool`, so a cast is the only mapping needed), and runs a streaming chat loop in which the model can call the `echo` tool.

## Prerequisites

- The `chapter01/http-server` sample running on port 5000. The C# and TypeScript servers listen at the root URI the client uses; for the Python server, change the endpoint in `Program.cs` to `http://localhost:5000/mcp`
- [Ollama](https://ollama.com) running locally with the model pulled: `ollama pull qwen3:1.7b`

## Tested with

.NET SDK 10.0.101 (net10.0), on 2026-10-21. Packages, pinned exactly in `EchoClient.csproj`:

| Package | Version |
|---|---|
| `ModelContextProtocol` | 2.0.0 |
| `Microsoft.Extensions.AI` | 10.7.0 |
| `OllamaSharp` | 5.4.8 |
| `Anthropic`, `Microsoft.Extensions.AI.OpenAI` (alternatives only) | 12.33.0, 10.7.0 |

The model side was checked live with Ollama 0.12.7 and `qwen3:1.7b`.

## Run

```
cd csharp && dotnet run
```

Then prompt the model to call the tool:

```
Connected to server with tools: echo
Your prompt:
Please call the echo tool with "FOO"
AI Response:
...
```

Look in the console output of the `http-server` for `echo tool called: message="FOO", …` to verify the tool call was made. (Small local models phrase the reply differently from run to run — what's deterministic is the tool call showing up in the server log.)

The chat loop keeps two representations of each response apart: it writes the streamed text to the console, and it adds the full response to the conversation history with `chatHistory.AddMessages(updates)`. With `UseFunctionInvocation()`, the streamed updates carry the tool calls and tool results as well as the text, so the next turn still sends the model what the tool returned. Appending only the concatenated text would drop that exchange.

The loop ends when standard input closes (Ctrl+D, or Ctrl+Z then Enter on Windows), when `Console.ReadLine()` returns `null`.

Note: the first request after Ollama starts can exceed the client's HTTP timeout while the model loads; warm it up first with `ollama run qwen3:1.7b "hi"`.

## Using an LLM API instead

The chat loop only needs an `IChatClient`, so switching to an LLM API is a one-line change: replace the `chatClient` assignment in `Program.cs` with one of the factory methods in `AlternativeProviders.cs` (Claude via the official Anthropic SDK, or OpenAI via its Microsoft.Extensions.AI adapter). Both read their API key from the conventional environment variable.
