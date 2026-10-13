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

// The book's loop (§6.2.5), line for line apart from the comment pointing at
// input-handler.ts. Notes on the v2 SDK: the call must opt in with
// `{ allowInputRequired: true }` or the SDK handles input_required itself (see
// native.ts), and the discriminator is the exported `isInputRequiredResult()`
// guard, because the public `CallToolResult` type does not carry `resultType`
// (it is wire-only). MAX_ROUNDS matches the default cap in all three SDKs.
async function callToolGatheringInput(
  client: Client,
  params: CallToolRequest['params'],
): Promise<CallToolResult> {
  let mrtr = {};
  const MAX_ROUNDS = 10; // the SDKs' own default cap
  for (let round = 0; round < MAX_ROUNDS; round++) {
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
  throw new Error(`still input_required after ${MAX_ROUNDS} rounds`);
}

// Production version: the capped loop plus what the chapter says production
// code also needs. One AbortSignal combines a whole-flow deadline with the
// user-facing cancel, and it reaches *both* phases of every round: the network
// call and the input handler. So the deadline covers the time a user spends
// answering a form, not just the round trips, and an unanswered prompt is
// abandoned the moment the signal fires. AbortSignal.timeout runs on a
// monotonic clock, as does performance.now() for the remaining-time budget.
interface GatherBounds {
  maxRounds?: number; // retry budget
  timeoutMs?: number; // deadline for the whole flow: every round trip and every prompt
  signal?: AbortSignal; // user-facing cancel
}

async function callToolGatheringInputBounded(
  client: Client,
  params: CallToolRequest['params'],
  bounds: GatherBounds = {},
): Promise<CallToolResult> {
  const { maxRounds = 10, timeoutMs = 120_000, signal } = bounds;
  const deadline = AbortSignal.timeout(timeoutMs);
  const abort = signal ? AbortSignal.any([signal, deadline]) : deadline;
  const started = performance.now();
  const failure = () =>
    deadline.aborted
      ? new Error(`tool call still gathering input after ${timeoutMs} ms`)
      : new Error('tool call cancelled by the user');
  let mrtr = {};
  try {
    for (let round = 1; round <= maxRounds; round++) {
      abort.throwIfAborted();
      const remaining = Math.max(1, timeoutMs - (performance.now() - started));
      const result = await client.callTool(
        { ...params, ...mrtr },
        { allowInputRequired: true, timeout: remaining, signal: abort },
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
        inputResponses[key] = await handleInputRequest(request, abort);
      }
      mrtr = {
        inputResponses,
        ...(result.requestState !== undefined ? { requestState: result.requestState } : {}),
      };
    }
  } catch (error) {
    if (abort.aborted) throw failure(); // report the cause, not the AbortError
    throw error;
  }
  throw new Error(`input_required retry budget exhausted after ${maxRounds} rounds`);
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

// User-facing cancel: Ctrl+C aborts the in-flight call, an open prompt, and
// the loop. MRTR_CANCEL_AFTER_MS simulates the user pressing it (for tests).
const canceller = new AbortController();
process.on('SIGINT', () => canceller.abort());
if (process.env['MRTR_CANCEL_AFTER_MS']) {
  setTimeout(() => canceller.abort(), Number(process.env['MRTR_CANCEL_AFTER_MS'])).unref();
}
const timeoutMs = Number(process.env['MRTR_TIMEOUT_MS'] ?? '120000');

await client.connect(new StdioClientTransport({ command: 'node', args: ['dist/demo-server.js'] }));

console.log(`-> tools/call ${toolName}`);
try {
  const params = { name: toolName, arguments: toolName === 'book_meeting' ? { room: '4B' } : {} };
  // MRTR_UNBOUNDED=1 runs the chapter's printed loop (round cap only, no deadline
  // or cancel) instead of the production one.
  const result =
    process.env['MRTR_UNBOUNDED'] === '1'
      ? await callToolGatheringInput(client, params)
      : await callToolGatheringInputBounded(client, params, {
          maxRounds,
          timeoutMs,
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
