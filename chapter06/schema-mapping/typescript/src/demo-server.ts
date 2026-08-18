// Demo MCP server whose tool input schemas deliberately exercise JSON Schema
// 2020-12 features that stress provider mapping (§6.2.2): numeric and string
// constraints, defaults, an enum, a oneOf composition, $defs/$ref, and one
// tool with an output schema (§6.2.3). The low-level Server API is used so
// the schemas can be written as raw JSON Schema rather than derived from zod.
import { Server, type Tool } from '@modelcontextprotocol/server';
import { serveStdio } from '@modelcontextprotocol/server/stdio';

// serveStdio owns the era decision: modern (2026-07-28) openings get a modern
// instance, legacy openings a legacy one — the same factory serves both.
serveStdio(() => {
  const server = new Server(
    { name: 'demo-booking-server', version: '0.1.0' },
    { capabilities: { tools: {} } },
  );

  const tools: Tool[] = [
    {
      name: 'search_stays',
      description: 'Search for hotel stays in a city.',
      // Constraints, defaults, an enum, format — the keywords whose provider
      // support is most uneven.
      inputSchema: {
        type: 'object' as const,
        properties: {
          city: {
            type: 'string',
            description: 'Destination city.',
            minLength: 2,
          },
          check_in: {
            type: 'string',
            description: 'Check-in date.',
            format: 'date',
            pattern: '^\\d{4}-\\d{2}-\\d{2}$',
          },
          nights: { type: 'integer', minimum: 1, maximum: 30, default: 1 },
          guests: { type: 'integer', minimum: 1, maximum: 8, default: 2 },
          sort: {
            type: 'string',
            description: 'Result ordering.',
            enum: ['price', 'rating', 'distance'],
            default: 'price',
          },
        },
        required: ['city', 'check_in'],
      },
    },
    {
      name: 'book_stay',
      description: 'Book a stay found via search_stays.',
      // Composition and references: oneOf over two payment shapes defined in
      // $defs, discriminated by a const. First-class in 2026-07-28 tool
      // schemas; several model APIs have never heard of them.
      inputSchema: {
        type: 'object' as const,
        $defs: {
          Guest: {
            type: 'object',
            properties: {
              full_name: { type: 'string', minLength: 1 },
              email: { type: 'string', format: 'email' },
            },
            required: ['full_name', 'email'],
          },
          CardPayment: {
            type: 'object',
            properties: {
              method: { const: 'card' },
              card_token: {
                type: 'string',
                description: 'Tokenized card reference.',
                pattern: '^tok_[A-Za-z0-9]{8,}$',
              },
            },
            required: ['method', 'card_token'],
          },
          InvoicePayment: {
            type: 'object',
            properties: {
              method: { const: 'invoice' },
              po_number: { type: 'string', minLength: 3 },
            },
            required: ['method', 'po_number'],
          },
        },
        properties: {
          stay_id: { type: 'string', pattern: '^stay_[a-z0-9]+$' },
          lead_guest: { $ref: '#/$defs/Guest' },
          payment: {
            description: 'How the stay is paid.',
            oneOf: [
              { $ref: '#/$defs/CardPayment' },
              { $ref: '#/$defs/InvoicePayment' },
            ],
          },
        },
        required: ['stay_id', 'lead_guest', 'payment'],
      },
    },
    {
      name: 'get_booking',
      description: 'Retrieve a booking by reference.',
      inputSchema: {
        type: 'object' as const,
        properties: {
          booking_ref: { type: 'string', pattern: '^BK-[A-Z0-9]{6}$' },
        },
        required: ['booking_ref'],
      },
      // Output schema (§6.2.3): most model APIs have no slot for this, so
      // the client-side adapter decides whether to pass, lift, or drop it.
      outputSchema: {
        type: 'object' as const,
        properties: {
          booking_ref: { type: 'string' },
          status: { type: 'string', enum: ['confirmed', 'pending', 'cancelled'] },
          nights: { type: 'integer', minimum: 1 },
          total: { type: 'number', minimum: 0 },
          currency: { type: 'string', pattern: '^[A-Z]{3}$' },
        },
        required: ['booking_ref', 'status', 'total', 'currency'],
      },
    },
  ];

  server.setRequestHandler('tools/list', async () => ({ tools }));

  server.setRequestHandler('tools/call', async (request) => {
    const { name, arguments: args } = request.params;
    const a = (args ?? {}) as Record<string, unknown>;

    switch (name) {
      case 'search_stays':
        return {
          content: [
            {
              type: 'text' as const,
              text: `2 stays in ${a.city}: stay_a1b2c3 (Hotel Borealis, 142 EUR/night), stay_d4e5f6 (Pension Vega, 88 EUR/night).`,
            },
          ],
        };
      case 'book_stay':
        return {
          content: [
            {
              type: 'text' as const,
              text: `Booked ${a.stay_id}. Reference: BK-7Q2M4X.`,
            },
          ],
        };
      case 'get_booking': {
        const booking = {
          booking_ref: String(a.booking_ref),
          status: 'confirmed',
          nights: 3,
          total: 426,
          currency: 'EUR',
        };
        return {
          content: [{ type: 'text' as const, text: JSON.stringify(booking) }],
          structuredContent: booking,
        };
      }
      default:
        throw new Error(`Unknown tool: ${name}`);
    }
  });

  return server;
});
