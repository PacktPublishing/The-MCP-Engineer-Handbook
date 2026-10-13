import { App } from "@modelcontextprotocol/ext-apps";
import type { CallToolResult } from "@modelcontextprotocol/client";

const STATUSES = ["healthy", "degraded", "down"] as const;

interface ServiceHealth {
  name: string;
  status: (typeof STATUSES)[number];
  uptimePct: number;
  cpuPct: number;
  memoryMb: number;
  responseTimeMs: number;
}

interface HealthData {
  services: ServiceHealth[];
  timestamp: number;
}

const app = new App({ name: "Health Dashboard", version: "1.0.0" });

// Tool results can arrive from the initial call and from refreshes in any
// order; only the most recently requested one may update the dashboard.
let latest = 0;

// Handle initial tool result — render the dashboard
app.addEventListener("toolresult", (result) => {
  renderDashboard(result, ++latest);
});

// Wire up the refresh button
document.getElementById("refresh-btn")!
  .addEventListener("click", async () => {
    const seq = ++latest;
    try {
      const result = await app.callServerTool({
        name: "refresh_health",
        arguments: {},
      });
      renderDashboard(result, seq);
    } catch (err) {
      if (seq === latest) showError(`Refresh failed: ${String(err)}`);
    }
  });

// Connect to the host
await app.connect();

function renderDashboard(result: CallToolResult, seq: number): void {
  if (seq !== latest) return; // a newer request is in flight or already rendered

  const data = parseHealthData(result);
  if (typeof data === "string") {
    showError(data);
    return;
  }

  const healthy = data.services.filter(s => s.status === "healthy").length;
  document.getElementById("summary")!.textContent =
    `${healthy}/${data.services.length} services healthy — ` +
    `updated ${new Date(data.timestamp).toLocaleTimeString()}`;

  document.getElementById("cards")!.replaceChildren(...data.services.map(service => {
    const card = document.createElement("div");
    card.className = `card ${service.status}`;
    const metrics = document.createElement("dl");
    for (const [label, value] of [
      ["Uptime", `${service.uptimePct}%`],
      ["CPU", `${service.cpuPct}%`],
      ["Memory", `${service.memoryMb} MB`],
      ["Latency", `${service.responseTimeMs} ms`],
    ]) {
      metrics.append(element("dt", label), element("dd", value));
    }
    card.append(element("h2", service.name), element("p", service.status, "status"), metrics);
    return card;
  }));
}

// structuredContent is untyped JSON from the server: check it at the boundary
// instead of trusting the interface above. Returns an error message on failure.
function parseHealthData(result: CallToolResult): HealthData | string {
  if (result.isError) return "The server reported an error fetching health data.";
  const data = result.structuredContent as Partial<HealthData> | undefined;
  if (!data || !Array.isArray(data.services) || !Number.isFinite(data.timestamp)) {
    return "No health data received.";
  }
  const valid = data.services.every((s: Partial<ServiceHealth>) =>
    typeof s?.name === "string" &&
    STATUSES.includes(s.status as ServiceHealth["status"]) &&
    [s.uptimePct, s.cpuPct, s.memoryMb, s.responseTimeMs].every(Number.isFinite));
  return valid ? (data as HealthData) : "Received malformed health data.";
}

function showError(message: string): void {
  document.getElementById("summary")!.textContent = message;
  document.getElementById("cards")!.replaceChildren();
}

function element(tag: string, text: string, className?: string): HTMLElement {
  const el = document.createElement(tag);
  el.textContent = text;
  if (className) el.className = className;
  return el;
}
