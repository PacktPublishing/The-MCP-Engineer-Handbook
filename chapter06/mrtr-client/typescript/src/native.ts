// The SDK-native MRTR path: no hand-written loop.
//
// In v2, `client.callTool()` fulfils `input_required` results AUTOMATICALLY
// by default (ClientOptions.inputRequired.autoFulfill defaults to true): it
// dispatches each embedded request to the handler registered via
// `setRequestHandler('elicitation/create', ...)`, then retries the original
// call with the collected inputResponses and a byte-exact requestState echo,
// on a fresh request id, up to `maxRounds` (default 10) rounds. The
// interactive rounds happen inside the call — callTool resolves with the
// final result. `maxTotalTimeout` bounds the whole flow, and an abort signal
// cancels it, so the chapter's retry budget / timeout / cancel obligations
// map directly onto SDK options here.
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

// The driver dispatches embedded elicitation requests to this handler.
client.setRequestHandler('elicitation/create', async (request) => handleInputRequest(request));

await client.connect(new StdioClientTransport({ command: 'node', args: ['dist/demo-server.js'] }));

console.log('-> tools/call book_meeting (SDK drives the MRTR rounds)');
try {
  const result = await client.callTool(
    { name: 'book_meeting', arguments: { room: '4B' } },
    {
      maxTotalTimeout: 120_000,
      // The driver reports each fulfilment round as progress.
      onprogress: (p) => console.log(`  [driver] ${p.message}`),
    },
  );
  const text = result.content.find((c) => c.type === 'text');
  console.log(`<- final result: ${text?.type === 'text' ? text.text : JSON.stringify(result.content)}`);
} catch (error) {
  console.log(`<- failed: ${error instanceof Error ? error.message : String(error)}`);
  process.exitCode = 1;
} finally {
  await client.close();
}
