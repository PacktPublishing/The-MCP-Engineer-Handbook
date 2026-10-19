# Agent as MCP Server (`as_mcp_server()`)

**Book ref:** Chapter 12, §12.3 (Agents as Tools). Mechanics background: Chapter 8, §8.5.

The ecosystem's biggest interop lever in one call: Microsoft Agent Framework's
`agent.as_mcp_server()` exposes a framework-native agent as an MCP server with a single
tool. Any compatible MCP host can now call the agent — no coordination, no framework
knowledge. "Compatible" is specific here: see the version-skew note below.

## Layout

- `python/` — a haiku-writing agent (MAF `Agent` + `OpenAIChatClient`) exposed over stdio.

There is deliberately no C#/TS port: the exposure API is framework-specific, and MAF
Python is the purest form the chapter cites (the C# route is
`McpServerTool.Create(agent.AsAIFunction())` through the MCP C# SDK — two lines, same move).

## Version-skew note (like `chapter07/health-dashboard/`)

`agent-framework` still pins **`mcp<2` — the v1 SDK line**, so this is one of the samples
that does *not* run on the repo's v2 GA pins. The sample pins 1.14.0, which resolves to
`mcp 1.28.1`; re-checked 2026-10-19, the latest release (1.22.0) still requires
`mcp>=1.24.0,<2`. The exposed server therefore speaks the legacy (2025-era) MCP revision:
the `initialize` handshake, `2025-06-18` in the smoke below, and `2025-11-25` at most (a
client asking for `2026-07-28` is answered with `2025-11-25`). It works with hosts that
still support that revision, which dual-era clients with version negotiation do
transparently — itself a live demonstration of Chapter 4's dual-era reality. A host that
speaks only the stateless 2026-07-28 contract can't use it. Re-check when MAF adopts the
v2 `mcp` line.

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

`tools/list` works without a model; `tools/call` invokes the agent for real, so calling the
tool needs an OpenAI API key, and each call is a metered OpenAI request billed per token
(check OpenAI's pricing).

**Verified 2026-08-19 (GA pass, agent-framework 1.14.0)**: full round trip against
`gpt-4.1-mini` — the listed tool carries
the agent's name/description, the input schema is a single `task` string, and the call
returned a haiku. Note the flattening the chapter describes: from the caller's side this
is just a tool — the agent's model, instructions, and loop are invisible.
