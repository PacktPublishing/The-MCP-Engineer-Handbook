# client (chapter 1 model-wired MCP client)

Companion sample for **Chapter 1, §1.8 (Wiring Models to MCP)** — the C# MCP client from the printed extracts, wired to a local qwen3 model via Ollama so no API key or subscription is needed. It connects to the `chapter01/http-server` sample over Streamable HTTP, lists its tools, hands them to the model (`McpClientTool` inherits from `AITool`, so a cast is the only mapping needed), and runs a streaming chat loop in which the model can call the `echo` tool.

## Prerequisites

- The `chapter01/http-server` sample running on port 5000 (the C# one listens at the root URI the client uses)
- [Ollama](https://ollama.com) running locally with the model pulled: `ollama pull qwen3:1.7b`

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

Note: the first request after Ollama starts can exceed the client's HTTP timeout while the model loads; warm it up first with `ollama run qwen3:1.7b "hi"`.

## Using an LLM API instead

The chat loop only needs an `IChatClient`, so switching to an LLM API is a one-line change: replace the `chatClient` assignment in `Program.cs` with one of the factory methods in `AlternativeProviders.cs` (Claude via the official Anthropic SDK, or OpenAI via its Microsoft.Extensions.AI adapter). Both read their API key from the conventional environment variable.
