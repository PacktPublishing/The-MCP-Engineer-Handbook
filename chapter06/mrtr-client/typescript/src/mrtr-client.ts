// The manual gather-and-retry loop from Chapter 6, Section 6.2.5, plus the
// production bounds the chapter calls for (retry budget, timeout, cancel).
//
// Run against the companion demo server (built alongside this file):
//   node dist/mrtr-client.js [book_meeting|never_satisfied]
//
// Scripted mode (no terminal interaction): see input-handler.ts.

import {
  Client,
  isInputRequiredResult,
  type CallToolRequest,
  type CallToolResult,
} from '@modelcontextprotocol/client';
import { StdioClientTransport } from '@modelcontextprotocol/client/stdio';
import { handleInputRequest } from './input-handler.js';

// The book's loop (§6.2.5). Deviations from the printed snippet, forced by
// the v2 SDK: the client class is `Client` (not `McpClient`); the params
// type is `CallToolRequest['params']` (there is no `CallToolParams`); the
// call must opt in with `{ allowInputRequired: true }` or the SDK handles
// input_required itself (see native.ts); and the discriminator is the
// exported `isInputRequiredResult()` guard, because the public
// `CallToolResult` type does not carry `resultType` (it is wire-only).
async function callToolGatheringInput(
  client: Client,
  params: CallToolRequest['params'],
): Promise<CallToolResult> {
  let mrtr = {};
  for (;;) {
    // each iteration is a brand-new request with a new JSON-RPC id
    const result = await client.callTool(
      { ...params, ...mrtr },
      { allowInputRequired: true },
    );
    if (!isInputRequiredResult(result)) {
      return result;
    }
    const inputResponses: Record<string, unknown> = {};
    for (const [key, request] of Object.entries(result.inputRequests ?? {})) {
      // render a form, open a URL, or apply policy — see input-handler.ts
      inputResponses[key] = await handleInputRequest(request);
    }
    mrtr = {
      inputResponses,
      // echo verbatim; never inspect, parse, or modify
      ...(result.requestState !== undefined
        ? { requestState: result.requestState }
        : {}),
    };
  }
}

// Production version: the same loop, bounded. A misbehaving server could
// otherwise keep the host gathering input forever.
interface GatherBounds {
  maxRounds?: number; // retry budget
  timeoutMs?: number; // deadline for the whole flow, all rounds included
  signal?: AbortSignal; // user-facing cancel
}

async function callToolGatheringInputBounded(
  client: Client,
  params: CallToolRequest['params'],
  bounds: GatherBounds = {},
): Promise<CallToolResult> {
  const { maxRounds = 10, timeoutMs = 120_000, signal } = bounds;
  const deadline = Date.now() + timeoutMs;
  let mrtr = {};
  for (let round = 1; ; round++) {
    if (round > maxRounds) {
      throw new Error(`input_required retry budget exhausted after ${maxRounds} rounds`);
    }
    if (signal?.aborted) {
      throw new Error('tool call cancelled by the user');
    }
    const remaining = deadline - Date.now();
    if (remaining <= 0) {
      throw new Error(`tool call still gathering input after ${timeoutMs} ms`);
    }
    const result = await client.callTool(
      { ...params, ...mrtr },
      { allowInputRequired: true, timeout: remaining, signal },
    );
    if (!isInputRequiredResult(result)) {
      return result;
    }
    console.log(
      `<- input_required (round ${round}): keys [${Object.keys(result.inputRequests ?? {}).join(', ')}]` +
        (result.requestState !== undefined ? ', requestState present' : ''),
    );
    const inputResponses: Record<string, unknown> = {};
    for (const [key, request] of Object.entries(result.inputRequests ?? {})) {
      inputResponses[key] = await handleInputRequest(request);
    }
    mrtr = {
      inputResponses,
      ...(result.requestState !== undefined ? { requestState: result.requestState } : {}),
    };
  }
}

// --- demo driver -----------------------------------------------------------

const toolName = process.argv[2] ?? 'book_meeting';
const maxRounds = Number(process.env['MRTR_MAX_ROUNDS'] ?? '10');

const client = new Client(
  { name: 'mrtr-client', version: '0.1.0' },
  {
    // Negotiate the modern (2026-07-28) era; MRTR does not exist on legacy.
    versionNegotiation: { mode: 'auto' },
    // The server refuses to embed elicitation requests unless the request's
    // client capabilities declare support (-32021 otherwise).
    capabilities: { elicitation: {} },
  },
);

// User-facing cancel: Ctrl+C aborts the in-flight call and the loop.
const canceller = new AbortController();
process.on('SIGINT', () => canceller.abort());

await client.connect(new StdioClientTransport({ command: 'node', args: ['dist/demo-server.js'] }));

console.log(`-> tools/call ${toolName}`);
try {
  const params = { name: toolName, arguments: toolName === 'book_meeting' ? { room: '4B' } : {} };
  // MRTR_UNBOUNDED=1 runs the chapter's bare loop instead of the bounded one.
  const result =
    process.env['MRTR_UNBOUNDED'] === '1'
      ? await callToolGatheringInput(client, params)
      : await callToolGatheringInputBounded(client, params, {
          maxRounds,
          timeoutMs: 120_000,
          signal: canceller.signal,
        });
  const text = result.content.find((c) => c.type === 'text');
  console.log(`<- final result: ${text?.type === 'text' ? text.text : JSON.stringify(result.content)}`);
} catch (error) {
  console.log(`<- failed: ${error instanceof Error ? error.message : String(error)}`);
  process.exitCode = 1;
} finally {
  await client.close();
}

// Exported for reference; the demo driver uses the bounded variant.
export { callToolGatheringInput, callToolGatheringInputBounded };
