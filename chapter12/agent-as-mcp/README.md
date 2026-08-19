# Agent as MCP Server (`as_mcp_server()`)

**Book ref:** Chapter 12, §12.3 (Agents as Tools). Mechanics background: Chapter 8, §8.5.

The ecosystem's biggest interop lever in one call: Microsoft Agent Framework's
`agent.as_mcp_server()` exposes a framework-native agent as an MCP server with a single
tool. Any MCP host can now call the agent — no coordination, no framework knowledge.

## Layout

- `python/` — a haiku-writing agent (MAF `Agent` + `OpenAIChatClient`) exposed over stdio.

There is deliberately no C#/TS port: the exposure API is framework-specific, and MAF
Python is the purest form the chapter cites (the C# route is
`McpServerTool.Create(agent.AsAIFunction())` through the MCP C# SDK — two lines, same move).

## Version-skew note (like `chapter07/health-dashboard/`)

`agent-framework` still pins **`mcp<2` — the v1 SDK line** (re-checked at the repo's GA
pass, 2026-08-19: 1.14.0, the latest release, resolves to `mcp 1.28.1`), so this is one
of the samples that does *not* run on the repo's v2 GA pins. The exposed server speaks
the legacy era (`initialize` handshake, protocolVersion `2025-06-18` in the smoke below).
Modern clients with auto version negotiation fall back to it transparently — which is
itself a live demonstration of Chapter 4's dual-era reality. Re-check when MAF adopts
the v2 `mcp` line.

This project also keeps `[tool.uv] prerelease = "allow"` — unlike the rest of the repo,
where it was a beta-era `mcp` artifact, here it is required by agent-framework's own
dependency tree (several `agent-framework-*` subpackages and Azure deps only ship
pre-releases).

## Run

```bash
cd python
uv sync
OPENAI_API_KEY=... uv run python server.py     # optionally OPENAI_MODEL=... (default gpt-4.1-mini)
```

Connect with the MCP Inspector (`npx @modelcontextprotocol/inspector`, STDIO,
command `uv`, args `run python server.py`, cwd `python/`) — or smoke it raw:

```bash
(
echo '{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"0"}}}'
echo '{"jsonrpc":"2.0","method":"notifications/initialized"}'
echo '{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}'
echo '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"haiku_writer","arguments":{"task":"write a haiku about protocols"}}}'
sleep 20
) | uv run python server.py
```

`tools/list` works without a model; `tools/call` invokes the agent for real.

**Verified 2026-08-19 (GA pass, agent-framework 1.14.0)**: full round trip against
`gpt-4.1-mini` — the listed tool carries
the agent's name/description, the input schema is a single `task` string, and the call
returned a haiku. Note the flattening the chapter describes: from the caller's side this
is just a tool — the agent's model, instructions, and loop are invisible.
