// The SDK-native MRTR path: no hand-written loop.
//
// In v2, `client.callTool()` fulfils `input_required` results AUTOMATICALLY
// by default (ClientOptions.inputRequired.autoFulfill defaults to true): it
// dispatches each embedded request to the handler registered via
// `setRequestHandler('elicitation/create', ...)`, then retries the original
// call with the collected inputResponses and a byte-exact requestState echo,
// on a fresh request id, up to `maxRounds` (default 10) rounds. The
// interactive rounds happen inside the call — callTool resolves with the
// final result. `maxRounds` is the retry budget. Two caveats on the deadline:
// `maxTotalTimeout` is checked between wire legs, so it does not interrupt a
// prompt the user leaves open; for a deadline that covers answering time,
// pass an abort signal too. The driver hands each handler a per-round signal
// linked to it (`ctx.mcpReq.signal`), which reaches the open prompt.
//
// What the SDK does NOT do: render the form, apply policy, or validate the
// user's content against requestedSchema. Those stay in your handler —
// the same handleInputRequest the manual loop uses.
//
// Run: node dist/native.js  (same MRTR_ANSWERS / MRTR_POLICY env as the manual entry)

import { Client } from '@modelcontextprotocol/client';
import { StdioClientTransport } from '@modelcontextprotocol/client/stdio';
import { handleInputRequest } from './input-handler.js';

const client = new Client(
  { name: 'mrtr-native-client', version: '0.1.0' },
  {
    versionNegotiation: { mode: 'auto' },
    // Required for the handler registration below AND so the server sees the
    // elicitation capability in the per-request _meta envelope.
    capabilities: { elicitation: {} },
    inputRequired: { maxRounds: 5 }, // autoFulfill: true is the default
  },
);

// The driver dispatches embedded elicitation requests to this handler; the
// per-round signal fires on the caller's abort (deadline or user cancel).
client.setRequestHandler('elicitation/create', async (request, ctx) =>
  handleInputRequest(request, ctx.mcpReq.signal),
);

// Whole-flow deadline (monotonic AbortSignal.timeout) plus the user's Ctrl+C.
const timeoutMs = Number(process.env['MRTR_TIMEOUT_MS'] ?? '120000');
const canceller = new AbortController();
process.on('SIGINT', () => canceller.abort());
if (process.env['MRTR_CANCEL_AFTER_MS']) {
  setTimeout(() => canceller.abort(), Number(process.env['MRTR_CANCEL_AFTER_MS'])).unref();
}
const deadline = AbortSignal.timeout(timeoutMs);

await client.connect(new StdioClientTransport({ command: 'node', args: ['dist/demo-server.js'] }));

console.log('-> tools/call book_meeting (SDK drives the MRTR rounds)');
try {
  const result = await client.callTool(
    { name: 'book_meeting', arguments: { room: '4B' } },
    {
      maxTotalTimeout: timeoutMs,
      signal: AbortSignal.any([canceller.signal, deadline]),
      // The driver reports each fulfilment round as progress.
      onprogress: (p) => console.log(`  [driver] ${p.message}`),
    },
  );
  const text = result.content.find((c) => c.type === 'text');
  console.log(`<- final result: ${text?.type === 'text' ? text.text : JSON.stringify(result.content)}`);
} catch (error) {
  const reason = deadline.aborted
    ? `tool call still gathering input after ${timeoutMs} ms`
    : canceller.signal.aborted
      ? 'tool call cancelled by the user'
      : error instanceof Error
        ? error.message
        : String(error);
  console.log(`<- failed: ${reason}`);
  process.exitCode = 1;
} finally {
  await client.close();
}
