import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  registerAppTool,
  registerAppResource,
  RESOURCE_MIME_TYPE,
} from "@modelcontextprotocol/ext-apps/server";

// The View bundle is produced by `npm run build` (esbuild) and inlined into
// the HTML template here, so the ui:// resource is a single self-contained
// document.
const here = dirname(fileURLToPath(import.meta.url));
const dashboardHtml = readFileSync(join(here, "view/dashboard.html"), "utf-8")
  .replace(
    "<!-- BUNDLE -->",
    `<script type="module">\n${readFileSync(join(here, "view/dashboard.js"), "utf-8")}</script>`,
  );

interface ServiceHealth {
  name: string;
  status: "healthy" | "degraded" | "down";
  uptimePct: number;
  cpuPct: number;
  memoryMb: number;
  responseTimeMs: number;
}

// Fabricated health data with per-call jitter so the refresh button
// visibly changes the dashboard.
async function getHealthData(): Promise<ServiceHealth[]> {
  const jitter = (base: number, spread: number) =>
    Math.round((base + (Math.random() - 0.5) * spread) * 10) / 10;

  const services: ServiceHealth[] = [
    { name: "api-gateway", base: 12 },
    { name: "auth-service", base: 8 },
    { name: "billing-service", base: 45 },
    { name: "search-index", base: 30 },
  ].map(({ name, base }) => {
    const roll = Math.random();
    const status: ServiceHealth["status"] =
      roll < 0.75 ? "healthy" : roll < 0.95 ? "degraded" : "down";
    return {
      name,
      status,
      uptimePct: status === "down" ? jitter(82, 4) : jitter(99.9, 0.2),
      cpuPct: jitter(status === "degraded" ? 78 : 24, 12),
      memoryMb: Math.round(jitter(512, 128)),
      responseTimeMs: status === "healthy" ? jitter(base, 6) : jitter(base * 8, 40),
    };
  });
  return services;
}

function createServer(): McpServer {
  const server = new McpServer({
    name: "System Health Monitor",
    version: "1.0.0",
  });

  const resourceUri = "ui://health/dashboard.html";

  // Primary tool — visible to both model and app
  registerAppTool(
    server,
    "get_system_health",
    {
      title: "System Health",
      description: "Returns current health status for all services.",
      inputSchema: {},
      _meta: { ui: { resourceUri } },
    },
    async () => {
      const services = await getHealthData();
      const healthy = services.filter(s => s.status === "healthy").length;
      return {
        // Text summary for the model's context
        content: [{
          type: "text",
          text: `${healthy}/${services.length} services healthy.`
        }],
        // Structured data for the View to render
        structuredContent: { services, timestamp: Date.now() },
      };
    },
  );

  // Refresh tool — app-only, hidden from the model
  registerAppTool(
    server,
    "refresh_health",
    {
      title: "Refresh Health Data",
      description: "Fetches latest health data for the dashboard.",
      inputSchema: {},
      _meta: {
        ui: {
          resourceUri,
          visibility: ["app"],  // Hidden from the agent
        },
      },
    },
    async () => {
      const services = await getHealthData();
      return {
        content: [{ type: "text", text: "Refreshed" }],
        structuredContent: { services, timestamp: Date.now() },
      };
    },
  );

  // UI resource — the HTML template
  registerAppResource(
    server,
    "Health Dashboard",
    resourceUri,
    { description: "Interactive service health dashboard" },
    async () => ({
      contents: [{
        uri: resourceUri,
        mimeType: RESOURCE_MIME_TYPE,
        text: dashboardHtml,  // Your bundled HTML
      }],
    }),
  );

  return server;
}

const server = createServer();
await server.connect(new StdioServerTransport());
