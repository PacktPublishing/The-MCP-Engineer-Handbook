// A stand-in for any long-running backend: start_report returns immediately with a
// handle, the actual work finishes a few seconds later. With the Tasks extension the
// server would return a CreateTaskResult instead of a handle-in-content, but the
// host-side injection problem this sample demonstrates is identical either way.
import { McpServer } from '@modelcontextprotocol/server';
import { serveStdio } from '@modelcontextprotocol/server/stdio';
import { z } from 'zod';

// null = still running; string = the finished report. Module-level, so the state is
// shared even if serveStdio pins separate era instances to the connection.
const results = new Map<string, string | null>();
let counter = 0;

const delayMs = 1000 * Number(process.env.REPORT_DELAY_SECONDS ?? '3');

const jsonResult = (value: object) => ({
  content: [{ type: 'text' as const, text: JSON.stringify(value) }],
});

// serveStdio owns the era decision: modern (2026-07-28) openings get a modern
// instance, legacy openings a legacy one — the same factory serves both.
serveStdio(() => {
  const server = new McpServer({ name: 'report-server', version: '0.1.0' });

  server.registerTool(
    'start_report',
    {
      description:
        'Start generating a report. Returns immediately with an operation handle; ' +
        'the report completes a few seconds later.',
      inputSchema: z.object({
        topic: z.string().describe('What the report should cover.'),
      }),
    },
    async ({ topic }) => {
      const operationId = `op-${String(++counter).padStart(3, '0')}`;
      results.set(operationId, null);
      setTimeout(() => {
        results.set(
          operationId,
          `Report on ${topic}: revenue up 14% quarter over quarter, ` +
            'driven by the new subscription tier. Two regions missed target; details in the appendix.',
        );
      }, delayMs);
      return jsonResult({ operationId, status: 'running' });
    },
  );

  server.registerTool(
    'get_report_result',
    {
      description:
        "Fetch the result of a previously started report. Returns status 'running' until it completes.",
      inputSchema: z.object({
        operationId: z.string().describe('Handle returned by start_report.'),
      }),
    },
    async ({ operationId }) => {
      if (!results.has(operationId)) return jsonResult({ operationId, status: 'unknown' });
      const report = results.get(operationId);
      return report == null
        ? jsonResult({ operationId, status: 'running' })
        : jsonResult({ operationId, status: 'completed', report });
    },
  );

  return server;
});
