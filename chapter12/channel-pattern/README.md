# The Channel Pattern — Talking to Users Through Tools

**Book ref:** Chapter 12, §12.4 (Channels — When Users Live Behind Tools). Also a working
miniature of §12.2's harness framing.

An agent whose user conversation is *tool traffic*: an MCP server is the communication
channel, inbound messages arrive by polling it, and the only way to reach the user is the
channel's `reply` tool. This is the portable shape of what Claude Code Channels does with
an experimental push capability (`claude/channel`) — polling tools work with every host
today; push is a research preview, not a standard.

## Layout (C# canonical; ports pending)

- `csharp/ChannelServer/` — the channel: a stdio MCP server with two tools,
  `check_messages` and `reply`, bridging a demo chat surface through two JSONL files.
  A real channel server bridges Telegram/Discord/Slack/email the same way — the tool
  surface is the pattern, the backend is a detail. The unread-cursor lives in process
  memory: a channel is a stdio server, and the process *is* the stream position (ch10's
  stdio-affinity point).
- `csharp/Chat/` — the user's "phone": a console app that appends to the inbox file and
  tails the outbox. Knows nothing about MCP.
- `csharp/ChannelHost/` — a minimal harness: owns the loop (poll every 2s, one model turn
  per inbound batch, stop policy via `MAX_TURNS`), and prints every `reply` tool call —
  communication as observable, gateable tool traffic. Scripted responder by default
  (no key needed); set `OPENAI_API_KEY` (plus optionally `OPENAI_MODEL`,
  `OPENAI_BASE_URL` for any OpenAI-compatible endpoint, Ollama included) for a real model.

## Run (three terminals, or two if you skip watching the server)

```bash
export CHANNEL_DIR=$(mktemp -d)          # same value in both terminals

# terminal 1 — the user's phone
cd csharp/Chat && dotnet run

# terminal 2 — the agent
cd csharp/ChannelHost && dotnet run      # spawns ChannelServer itself
```

Type into terminal 1; the agent's replies come back there. Terminal 2 shows the harness
view: inbound text, then `tools/call reply({...})` — the message to the user *is* the
tool call.

## Smoke (no interactivity, no model)

```bash
export CHANNEL_DIR=$(mktemp -d)
echo '{"from":"user","text":"what time is it?","ts":"2026-07-12T12:00:00Z"}' > $CHANNEL_DIR/inbox.jsonl
cd csharp/ChannelHost && MAX_TURNS=1 dotnet run
cat $CHANNEL_DIR/outbox.jsonl            # the scripted responder's reply, sent via the reply tool
```

**Verified 2026-07-12**: scripted path end-to-end, and the same run against a real model
(`gpt-4.1-mini`) — the model answered the user exclusively through the `reply` tool, as
the system prompt demands ("plain output goes nowhere").

## The inversion (§12.4 → §12.3)

Outbound user communication is tools; inbound is the same claim mirrored — expose the
agent itself as an MCP tool (`../agent-as-mcp/`) and you've published a way for the world
to talk *to* it. Channel server out, `as_mcp_server()` in: MCP carrying the conversation
in both directions, while the loop stays the harness's job.
