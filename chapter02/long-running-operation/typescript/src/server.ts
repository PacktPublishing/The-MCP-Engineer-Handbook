// LongRunningOperation — splitting a slow domain operation across tools (Chapter 2, Section 2.6.4).
//
// start_data_processing and start_search return immediately with an operation ID; the
// model threads that ID through check_operation_status and can recover lost IDs with
// list_all_operations. Completion is derived from the clock — no background workers.

import { randomBytes } from 'node:crypto';

import { McpServer } from '@modelcontextprotocol/server';
import { serveStdio } from '@modelcontextprotocol/server/stdio';
import { z } from 'zod';

// How long a started operation takes before it is "done".
// Scaled down for the demo; the tool descriptions below are scaled to match.
const delaySeconds = Number.parseFloat(process.env.OPERATION_DELAY_SECONDS ?? '');
const completionDelayMs = 1000 * (Number.isFinite(delaySeconds) ? delaySeconds : 4);

interface Operation {
  operationId: string;
  kind: string;
  input: string;
  startedAt: number;
  completesAt: number;
  result: string;
}

const isCompleted = (op: Operation) => Date.now() >= op.completesAt;

// In-memory store keyed by the operation handle. In production this would be
// durable storage (a database or task queue): the stateless design rules from
// Chapter 5 mean any replica may receive the status poll, so the state must
// live somewhere every replica can reach — not in process memory. (Module-level
// here, so the state is shared even if serveStdio pins separate era instances.)
const operations = new Map<string, Operation>();

// Bounded retention, applied lazily on each call (still no background workers):
// completed operations expire 10 minutes after they finish, and the store never
// holds more than 1,000 operations. Without a bound, a long-lived server grows
// forever and eventually runs out of IDs.
const retentionMs = 10 * 60 * 1000;
const maxOperations = 1000;
const maxIdAttempts = 8;

// Drops operations that completed more than retentionMs ago.
function prune(): void {
  const cutoff = Date.now() - retentionMs;
  for (const op of [...operations.values()]) {
    if (op.completesAt <= cutoff) operations.delete(op.operationId);
  }
}

function startOperation(kind: string, input: string, resultTemplate: string): Operation {
  prune();
  if (operations.size >= maxOperations) {
    // Still full: make room by dropping the oldest completed operation.
    // If every operation is still running, refuse and say what to do instead.
    const completed = [...operations.values()].filter(isCompleted);
    if (completed.length === 0) {
      throw new Error(
        `This server is already running ${maxOperations} operations, its limit. ` +
          'Check on them with check_operation_status (list_all_operations shows them all) ' +
          'and start new work once some have completed.',
      );
    }
    const oldest = completed.reduce((a, b) => (b.completesAt < a.completesAt ? b : a));
    operations.delete(oldest.operationId);
  }

  // Short, distinctive handles: the model has to reproduce them verbatim,
  // so op_3f9c1a2b beats a 36-character UUID (Chapter 2, Section 2.6.3).
  // Eight hex characters from a CSPRNG make collisions vanishingly rare at
  // the cap above, and the retry loop is bounded anyway.
  for (let attempt = 0; attempt < maxIdAttempts; attempt++) {
    const operationId = `op_${randomBytes(4).toString('hex')}`;
    if (operations.has(operationId)) continue;
    const op: Operation = {
      operationId,
      kind,
      input,
      startedAt: Date.now(),
      completesAt: Date.now() + completionDelayMs,
      result: resultTemplate,
    };
    operations.set(operationId, op);
    return op;
  }

  throw new Error('Could not allocate an operation ID. Try again.');
}

const text = (t: string) => ({ content: [{ type: 'text' as const, text: t }] });

// Status report returned by check_operation_status (also published as the tool's
// output schema). result and guidance are always present and null when absent, the
// same contract as the C# and Python ports (an omitted field and a null one are
// different contracts to a client that validates).
const OperationStatusSchema = z.object({
  operationId: z.string(),
  kind: z.string(),
  status: z.string(),
  result: z.string().nullable(),
  guidance: z.string().nullable(),
});

serveStdio(() => {
  const server = new McpServer({ name: 'long-running-operation', version: '0.1.0' });

  server.registerTool(
    'start_data_processing',
    {
      description:
        'Start an asynchronous data-processing job on the named dataset. ' +
        'Returns immediately with an operation ID — it does NOT wait for the job to finish. ' +
        'Processing typically takes 3-6 seconds. Poll check_operation_status with the ' +
        'returned operation ID; do not poll more than once every 2 seconds.',
      inputSchema: z.object({
        dataset: z.string().describe('Name of the dataset to process.'),
      }),
    },
    async ({ dataset }) => {
      const op = startOperation(
        'data_processing',
        dataset,
        `Dataset '${dataset}' processed: 1204 rows read, 1187 rows transformed, 17 rows rejected (schema mismatch).`,
      );
      return text(
        `Started data processing for dataset '${dataset}'. Operation ID: ${op.operationId}. ` +
          'Typically completes in 3-6 seconds. Check progress with check_operation_status, ' +
          'waiting at least 2 seconds between checks.',
      );
    },
  );

  server.registerTool(
    'start_search',
    {
      description:
        'Start an asynchronous deep search across the archive for the given query. ' +
        'Returns immediately with an operation ID — it does NOT wait for results. ' +
        'Searches typically take 3-6 seconds. Poll check_operation_status with the ' +
        'returned operation ID; do not poll more than once every 2 seconds.',
      inputSchema: z.object({
        query: z.string().describe('Search query text.'),
      }),
    },
    async ({ query }) => {
      const op = startOperation(
        'search',
        query,
        `Search for '${query}' finished: 3 matching documents — 'Q3 capacity plan' (0.92), 'Incident 4411 retro' (0.87), 'Archive index 2024' (0.71).`,
      );
      return text(
        `Started search for '${query}'. Operation ID: ${op.operationId}. ` +
          'Typically completes in 3-6 seconds. Check progress with check_operation_status, ' +
          'waiting at least 2 seconds between checks.',
      );
    },
  );

  server.registerTool(
    'check_operation_status',
    {
      description:
        'Check the status of an operation previously started with start_data_processing ' +
        "or start_search. Requires the operation ID those tools returned. Reports 'running' or " +
        "'completed', and includes the result once completed. If still running, wait at least " +
        '2 seconds before checking again.',
      inputSchema: z.object({
        operationId: z
          .string()
          .describe('Operation ID returned by start_data_processing or start_search.'),
      }),
      outputSchema: OperationStatusSchema,
    },
    async ({ operationId }) => {
      prune();
      const op = operations.get(operationId);
      let status: z.infer<typeof OperationStatusSchema>;
      if (op === undefined) {
        // Instructive text for unknown handles: tell the model how to recover,
        // not just that it failed.
        status = {
          operationId,
          kind: 'unknown',
          status: 'not_found',
          result: null,
          guidance:
            `No operation with ID '${operationId}' exists on this server; completed ` +
            'operations expire after 10 minutes. Operation IDs are returned by ' +
            'start_data_processing and start_search. Call list_all_operations to ' +
            'see every operation this server knows about.',
        };
      } else if (isCompleted(op)) {
        status = {
          operationId: op.operationId,
          kind: op.kind,
          status: 'completed',
          result: op.result,
          guidance: null,
        };
      } else {
        status = {
          operationId: op.operationId,
          kind: op.kind,
          status: 'running',
          result: null,
          guidance: 'Still running. Wait at least 2 seconds before checking again.',
        };
      }
      return {
        content: [{ type: 'text' as const, text: JSON.stringify(status) }],
        structuredContent: status,
      };
    },
  );

  server.registerTool(
    'list_all_operations',
    {
      description:
        'List every operation this server knows about, with its current status. ' +
        'Use this to recover an operation ID you no longer have, or to get an overview ' +
        'of running and completed operations.',
      inputSchema: z.object({}),
    },
    async () => {
      prune();
      if (operations.size === 0) {
        return text(
          'No operations right now; completed operations expire after 10 minutes. ' +
            'Start one with start_data_processing or start_search.',
        );
      }
      const lines = [...operations.values()]
        .sort((a, b) => a.startedAt - b.startedAt)
        .map(
          (op) =>
            `${op.operationId}  kind=${op.kind}  status=${isCompleted(op) ? 'completed' : 'running'}  input='${op.input}'`,
        );
      return text(lines.join('\n'));
    },
  );

  return server;
});
