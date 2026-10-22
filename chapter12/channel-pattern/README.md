# The Channel Pattern — Talking to Users Through Tools

**Book ref:** Chapter 12, §12.4 (Channels — When Users Live Behind Tools). Also a working
miniature of §12.2's harness framing.

An agent whose user conversation is *tool traffic*: an MCP server is the communication
channel, inbound messages arrive by polling it, and the only way to reach the user is the
channel's `reply` tool. This is the portable shape of what Claude Code Channels does with
an experimental push capability (`claude/channel`). Polling needs nothing beyond plain
`tools/call`, so it works today with any host whose harness runs the poll loop (a generic
host won't poll on its own); push is a research preview, not a standard.

## Layout (C# canonical; ports pending)

- `csharp/ChannelServer/` — the channel: a stdio MCP server with two tools,
  `check_messages` and `reply`, bridging a demo chat surface through two JSONL files.
  A real channel server bridges Telegram/Discord/Slack/email the same way — the tool
  surface is the pattern, the backend is a detail. `check_messages` returns a structured
  batch, `{"messages":[{"id","from","text"}],"rejected":[...]}`: the message ID is the
  inbox line number, and `rejected` lists malformed inbox lines. The unread-cursor lives in
  process memory: a channel is a stdio server, and the process *is* the stream position
  (ch10's stdio-affinity point). A `tools/list` filter restores declaration order: C# SDK
  releases before 2.3.0 list tools in hash order (stable, but `reply` before `check_messages`);
  the pinned 2.3.0 keeps registration order, so the filter is now redundant.
- `csharp/Chat/` — the user's "phone": a console app that appends to the inbox file and
  tails the outbox. Knows nothing about MCP.
- `csharp/ChannelHost/` — a minimal harness: owns the loop (poll every 2s, one model turn
  per inbound batch, the stop policy below), and prints every `reply` tool call with its
  outcome — communication as observable, gateable tool traffic. Polling stays in the
  harness: the model is given only `reply`, never `check_messages`, so it can't pull more
  inbox records outside the harness's batch accounting. Scripted responder by default (no
  key needed); see "Real model" below.

## Harness policy

- **One reply per batch.** Everything new since the last poll is one batch and one user
  turn, sent to the model as the JSON batch (IDs, senders and texts stay separate fields,
  so a newline in a message can't blur where one ends). The system prompt asks for exactly
  one `reply` per batch, and the harness enforces it: a second `reply` in the same batch is
  refused without touching the channel. Plain assistant text never counts as delivered;
  the harness logs a batch that ended without a successful `reply`.
- **Control flow never reads message text.** An empty `messages` array means idle (no
  model call). A failed poll (`isError`, a protocol error, or a malformed batch) is a
  harness event, never model input: the harness backs off and stops after three failures
  in a row.
- **Stop policy.** `MAX_TURNS` caps *processed batches* (idle polls don't count; unset
  means run until Ctrl+C; anything but a positive integer is rejected). Each batch also
  has a 60-second deadline and a cap of two tool round trips in the function-invocation
  middleware (`MaximumIterationsPerRequest = 2`, set explicitly rather than left at its
  default of 40). History grows for the life of the process: the demo has no retention
  or compaction policy, which a long-running host needs.

## Delivery guarantee (and what this demo doesn't give you)

The server only reads complete lines: a last line without its newline is left for the next
poll, so a half-appended record is never parsed. Each complete line is then either delivered
or reported in `rejected` (a valid record followed by `{broken` delivers the valid one and
reports the broken one), and only then does the cursor move past it.

That cursor moves when the batch is *returned*, not when the agent has replied. So delivery
is **at-most-once per server process**: a host crash, timeout, or model failure between poll
and reply loses that batch, and nothing retries it. The cursor is also process memory, so a
restarted server starts again at line 1 and replays the whole inbox (duplicates across
restarts). There is no acknowledgement after a reply. A reliable channel needs stable message
IDs, a persistent acknowledgement or checkpoint written *after* processing, and an
idempotent reply (keyed by message ID) so a retry can't double-send. Persisting the read
offset alone doesn't give you exactly-once.

## Trust boundary

The JSONL backend is **one trusted local conversation**. The `from` field is whatever the
inbox file says, not an authenticated sender, and every reply goes to the one shared outbox.
That's why the channel needs a private directory: `ChannelServer` and `Chat` refuse to start
without `CHANNEL_DIR`, and `ChannelHost` creates a private one (owner-only on Unix) and
prints it if you didn't set one. A real adapter needs verified platform sender and
conversation IDs, explicit pairing or allowlisting, replay handling, and reply routing bound
in host context rather than reconstructed by the model. Sender allowlists limit *who* can
start a conversation, not what an allowed sender's message contains: forwarded content can
still carry prompt injection.

## Run (three terminals, or two if you skip watching the server)

```bash
export CHANNEL_DIR=$(mktemp -d)          # same value in both terminals

# terminal 1 — the user's phone
cd csharp/Chat && dotnet run

# terminal 2 — the agent
cd csharp/ChannelHost && dotnet run      # spawns ChannelServer itself
```

Type into terminal 1; the agent's replies come back there. Terminal 2 shows the harness
view: inbound messages, then `tools/call reply({...}) -> Sent.` — the message to the user
*is* the tool call.

## Smoke (no interactivity, no model)

```bash
export CHANNEL_DIR=$(mktemp -d)
echo '{"from":"user","text":"what time is it?","ts":"2026-07-12T12:00:00Z"}' > $CHANNEL_DIR/inbox.jsonl
cd csharp/ChannelHost && MAX_TURNS=1 dotnet run
cat $CHANNEL_DIR/outbox.jsonl            # the scripted responder's reply, sent via the reply tool
```

## Real model

The host speaks the OpenAI Chat Completions API through `Microsoft.Extensions.AI.OpenAI`,
configured by environment variables in the ChannelHost terminal:

- **OpenAI:** `OPENAI_API_KEY` (an API key from your OpenAI platform account) and
  optionally `OPENAI_MODEL` (default `gpt-4.1-mini`).
- **Any OpenAI-compatible endpoint, Ollama included:** also set `OPENAI_BASE_URL`. For a
  local Ollama, `OPENAI_BASE_URL=http://localhost:11434/v1`, `OPENAI_API_KEY=ollama` (any
  non-empty value; Ollama ignores it), and `OPENAI_MODEL` set to a pulled model that
  supports tool calling.

```bash
export OPENAI_API_KEY=sk-...               # or the Ollama settings above
cd csharp/ChannelHost && MAX_TURNS=3 dotnet run
```

A metered API like OpenAI bills per token for every batch (check your provider's pricing);
a local Ollama model doesn't.

**Verified 2026-08-19 (GA pass, ModelContextProtocol 2.0.0)**: scripted path end-to-end,
and the same run against a real model
(`gpt-4.1-mini`) — the model answered the user exclusively through the `reply` tool, as
the system prompt demands ("plain output goes nowhere"). Re-run 2026-10-21 at
ModelContextProtocol 2.3.0 and Microsoft.Extensions.AI 10.11.0, scripted and against
`gpt-4.1-mini`, with the same results.

**Re-verified 2026-10-19 (structured batch and harness policy)**, scripted and against a
local fake OpenAI-compatible endpoint that logs requests: an inbox message "Why does my app
say No new messages?" gets a normal reply; a valid record, `{broken`, a valid record, and a
half-written line deliver the two valid records, report line 2 as rejected, and pick up the
half-written record once its newline lands; an empty inbox makes no model request; a
failing `check_messages` makes no model request and stops the host after three tries; a
responder that emits two `reply` calls gets the second refused; one that keeps calling
tools is cut off after two round trips; a model request slower than the 60-second deadline
is abandoned; and a restarted server replays the inbox from line 1.

## The inversion (§12.4 → §12.3)

Outbound user communication is tools; inbound is the same claim mirrored — expose the
agent itself as an MCP tool (`../agent-as-mcp/`) and you've published a way for the world
to talk *to* it. Channel server out, `as_mcp_server()` in: MCP carrying the conversation
in both directions, while the loop stays the harness's job.
