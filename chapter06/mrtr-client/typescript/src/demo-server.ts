// Companion demo server for the MRTR client sample (Chapter 6, Section 6.2.5).
//
// `book_meeting` answers its first call with `input_required`, asking for
// meeting details (a form exercising titles, descriptions, and defaults), and
// answers the retry with a SECOND elicitation (a final confirmation) before
// completing — so the client's gather-and-retry loop genuinely iterates.
//
// `never_satisfied` asks forever; it exists so the client can demonstrate its
// retry budget tripping against a misbehaving server.
//
// NOTE: hand-wiring `server.connect(new StdioServerTransport())` serves the
// LEGACY (2025-11-25) era only — `server/discover` answers -32601 and
// input_required is downgraded to the deprecated server->client elicitation
// shim. `serveStdio(factory)` (bottom of this file) is the stdio entry that
// serves the 2026-07-28 era, where MRTR exists.

import {
  McpServer,
  inputRequired,
  inputResponse,
  acceptedContent,
} from '@modelcontextprotocol/server';
import { serveStdio } from '@modelcontextprotocol/server/stdio';
import { z } from 'zod';

// Validates the untrusted form content the client sends back.
const DetailsSchema = z.object({
  title: z.string().min(1).max(80),
  duration: z.enum(['15', '30', '60']),
  notify: z.boolean().optional(),
});

const ConfirmSchema = z.object({ confirm: z.boolean() });

function text(t: string) {
  return { content: [{ type: 'text' as const, text: t }] };
}

// The demo requestState is plain JSON so transcripts stay readable. It
// round-trips through the client as attacker-controlled input: a production
// server must integrity-protect it (see createRequestStateCodec in the SDK).
type BookingState =
  | { step: 'awaiting-details'; room: string }
  | { step: 'awaiting-confirm'; room: string; details: z.infer<typeof DetailsSchema> };

function readState(raw: string | undefined): BookingState | undefined {
  if (raw === undefined) return undefined;
  try {
    return JSON.parse(raw) as BookingState;
  } catch {
    return undefined;
  }
}

function buildServer(): McpServer {
  const server = new McpServer({ name: 'mrtr-demo-server', version: '0.1.0' });

  server.registerTool(
    'book_meeting',
    {
      description:
        'Books a meeting room. Elicits meeting details, then a final confirmation, before booking.',
      inputSchema: z.object({
        room: z.string().describe('Room to book.'),
      }),
    },
    async ({ room }, ctx) => {
      const responses = ctx.mcpReq.inputResponses;
      const state = readState(ctx.mcpReq.requestState<string>());

      // Round 1: no state yet — ask for the meeting details.
      if (state === undefined) {
        return inputRequired({
          inputRequests: {
            details: inputRequired.elicit({
              message: `Booking room ${room}. What should the meeting look like?`,
              requestedSchema: {
                type: 'object',
                properties: {
                  title: {
                    type: 'string',
                    title: 'Meeting title',
                    description: 'Shown on the room display.',
                    minLength: 1,
                    maxLength: 80,
                  },
                  duration: {
                    type: 'string',
                    title: 'Duration (minutes)',
                    description: 'How long to hold the room.',
                    enum: ['15', '30', '60'],
                    default: '30',
                  },
                  notify: {
                    type: 'boolean',
                    title: 'Notify attendees',
                    description: 'Send a calendar notification when booked.',
                    default: true,
                  },
                },
                required: ['title', 'duration'],
              },
            }),
          },
          requestState: JSON.stringify({ step: 'awaiting-details', room }),
        });
      }

      // Round 2: the retry carrying the details form's answers.
      if (state.step === 'awaiting-details') {
        const view = inputResponse(responses, 'details');
        if (view.kind !== 'elicit' || view.action !== 'accept') {
          const action = view.kind === 'elicit' ? view.action : 'missing response';
          return text(`Room ${state.room} not booked (${action}). Ask me again anytime.`);
        }
        // Schema-aware read: validates the untrusted content before use.
        const details = acceptedContent(responses, 'details', DetailsSchema);
        if (details === undefined) {
          return text(`Room ${state.room} not booked: the details did not match the requested schema.`);
        }
        // Ask once more — a retry is allowed to come back input_required.
        return inputRequired({
          inputRequests: {
            confirm: inputRequired.elicit({
              message: `Book ${state.room} for "${details.title}" (${details.duration} min)?`,
              requestedSchema: {
                type: 'object',
                properties: {
                  confirm: {
                    type: 'boolean',
                    title: 'Confirm booking',
                    description: 'The room is charged to your team once booked.',
                    default: true,
                  },
                },
                required: ['confirm'],
              },
            }),
          },
          requestState: JSON.stringify({ step: 'awaiting-confirm', room: state.room, details }),
        });
      }

      // Round 3: the retry carrying the confirmation.
      const confirmation = acceptedContent(responses, 'confirm', ConfirmSchema);
      if (confirmation === undefined || !confirmation.confirm) {
        return text(`Room ${state.room} not booked: confirmation was withheld.`);
      }
      const { details } = state;
      const notify = details.notify ?? true;
      return text(
        `Booked ${state.room} for "${details.title}" (${details.duration} min). ` +
          (notify ? 'Attendees notified.' : 'No notification sent.'),
      );
    },
  );

  server.registerTool(
    'never_satisfied',
    {
      description: 'Misbehaving tool: keeps requesting input forever. For retry-budget demos.',
      inputSchema: z.object({}),
    },
    async (_args, ctx) => {
      const round = Number(ctx.mcpReq.requestState<string>() ?? '0') + 1;
      return inputRequired({
        inputRequests: {
          again: inputRequired.elicit({
            message: `Still not satisfied (round ${round}). Once more?`,
            requestedSchema: {
              type: 'object',
              properties: {
                again: { type: 'boolean', title: 'Go again', default: true },
              },
              required: ['again'],
            },
          }),
        },
        requestState: String(round),
      });
    },
  );

  return server;
}

serveStdio(buildServer, { onerror: (error) => console.error('[demo-server]', error) });
