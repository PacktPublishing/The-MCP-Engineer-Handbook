// The server packed into the weather-alerts bundle (Section 5.5).
//
// Plain JavaScript on purpose: a Node bundle ships the entry point and its
// node_modules exactly as they run, so there is no build step to forget
// before `mcpb pack`. The forecast data is canned — the point of this sample
// is the packaging, not the weather.
import { McpServer } from '@modelcontextprotocol/server';
import { serveStdio } from '@modelcontextprotocol/server/stdio';
import { z } from 'zod';

// Both values arrive through the manifest's mcp_config.env. The host resolves
// ${user_config.api_key} from what the user entered at install time.
const apiKey = process.env.WEATHER_API_KEY;
const cacheTtl = Number(process.env.CACHE_TTL ?? 300);

const ALERTS = {
  oslo: ['Wind warning: gusts up to 25 m/s along the coast until 18:00.'],
  miami: ['Heat advisory: feels-like temperatures up to 41 °C this afternoon.'],
};

serveStdio(() => {
  const server = new McpServer({ name: 'weather-alerts', version: '1.0.0' });

  server.registerTool(
    'get_alerts',
    {
      description: 'Returns active weather alerts for a city',
      inputSchema: z.object({
        city: z.string().describe('City name, e.g. "Oslo"'),
      }),
    },
    async ({ city }) => {
      if (!apiKey) {
        // A missing key is a configuration problem the user can fix in the
        // host's extension settings, so say so instead of failing obscurely.
        return {
          content: [{ type: 'text', text: 'No Weather API key configured. Set it in the extension settings.' }],
          isError: true,
        };
      }
      const alerts = ALERTS[city.trim().toLowerCase()] ?? [];
      const text = alerts.length > 0
        ? alerts.join('\n')
        : `No active alerts for ${city}.`;
      return { content: [{ type: 'text', text: `${text}\n(cached for ${cacheTtl}s)` }] };
    },
  );

  return server;
});
