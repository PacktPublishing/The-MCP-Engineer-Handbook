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

## Era and the temporary git pin

The server `as_mcp_server()` returns is an `mcp` v2 low-level `Server`, so this sample
speaks the stateless **2026-07-28** revision (`server/discover`, per-request `_meta`
envelope, no `initialize`). Like every Python v2 stdio server, it still answers a legacy
client that opens with `initialize` (negotiating `2025-11-25`); the first message on the
connection decides the era.

That needs Microsoft Agent Framework code that isn't on PyPI yet: the latest release
(agent-framework 1.22.0) still requires `mcp>=1.24.0,<2`, and MAF moved to
`mcp>=2.2.0,<3` in [PR #9236](https://github.com/microsoft/agent-framework/pull/9236).
So `pyproject.toml` temporarily pins `agent-framework-core` and `agent-framework-openai`
to that PR's merge commit (`28e9844`) through `[tool.uv.sources]`, with `mcp==2.3.0`.
**When a release ships with mcp 2.x (expected: agent-framework 1.23.0), replace the
git sources with plain PyPI pins.** Depending on just those two packages (rather than the
`agent-framework` meta-package) also drops the `prerelease = "allow"` the old pin needed.

If `uv sync` fails with a git-lfs "smudge filter" error, your uv predates its default of
skipping Git LFS (seen with uv 0.5.x; uv 0.13 is fine): upgrade uv, or run
`GIT_LFS_SKIP_SMUDGE=1 uv sync`.

MAF still registers the logging handler, so `server/discover` advertises `logging` and
stderr shows an `MCPDeprecationWarning` (logging is deprecated as of 2026-07-28,
SEP-2577). Both are harmless.

## Run

```bash
cd python
uv sync
OPENAI_API_KEY=... uv run python server.py     # optionally OPENAI_MODEL=... (default gpt-4.1-mini)
```

Connect with the MCP Inspector (`npx @modelcontextprotocol/inspector`, STDIO,
command `uv`, args `run python server.py`, cwd `python/`) — or smoke it raw. Handshake-less:
each request carries the client envelope in `_meta`:

```bash
M='"_meta":{"io.modelcontextprotocol/clientInfo":{"name":"smoke","version":"0"},"io.modelcontextprotocol/clientCapabilities":{},"io.modelcontextprotocol/protocolVersion":"2026-07-28"}'
{
printf '%s\n' \
'{"jsonrpc":"2.0","id":0,"method":"server/discover","params":{'"$M"'}}' \
'{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{'"$M"'}}' \
'{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"haiku_writer","arguments":{"task":"write a haiku about protocols"},'"$M"'}}'
sleep 20
} | uv run python server.py
```

Expect `server/discover` to return `supportedVersions` `["2026-07-28"]` and
`serverInfo` `haiku-agent` in `_meta`, and `tools/list` to return `haiku_writer` with a
single `task` string as its input schema.

`tools/list` makes no model call (a dummy key is enough to start the server, since
`OpenAIChatClient` reads it at construction); `tools/call` invokes the agent for real, so
calling the tool needs a real OpenAI API key, and each call is a metered OpenAI request
billed per token (check OpenAI's pricing).

**Verified 2026-10-19 (MAF `28e9844`, mcp 2.3.0)**: the Python v2 `Client` negotiates
`2026-07-28` pinned and in `mode='auto'`, and the TypeScript v2 client (2.2.0) does with
`versionNegotiation` `'auto'` or a pin; all list `haiku_writer`. Legacy clients still work:
the Python v1 SDK (1.28.1), Python v2 `mode='legacy'` and the TypeScript client's default
all get `2025-11-25`. A modern-era `tools/call` with a dummy key reaches OpenAI (401), so
the call path is wired. The last full round trip against `gpt-4.1-mini` was on 2026-08-19
(agent-framework 1.14.0, legacy era) and returned a haiku. Note the flattening the
chapter describes: from the caller's side this is just a tool — the agent's model,
instructions, and loop are invisible.
