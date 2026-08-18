import { App } from "@modelcontextprotocol/ext-apps";

interface ServiceHealth {
  name: string;
  status: "healthy" | "degraded" | "down";
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

// Handle initial tool result — render the dashboard
// (the single-handler app.ontoolresult property is deprecated in 1.7.4)
app.addEventListener("toolresult", (result) => {
  renderDashboard(result.structuredContent as HealthData | undefined);
});

// Wire up the refresh button
document.getElementById("refresh-btn")!
  .addEventListener("click", async () => {
    const result = await app.callServerTool({
      name: "refresh_health",
      arguments: {},
    });
    renderDashboard(result.structuredContent as HealthData | undefined);
  });

// Connect to the host
await app.connect();

function renderDashboard(data: HealthData | undefined): void {
  const cards = document.getElementById("cards")!;
  const summary = document.getElementById("summary")!;
  if (!data) {
    summary.textContent = "No health data received.";
    return;
  }

  const healthy = data.services.filter(s => s.status === "healthy").length;
  summary.textContent =
    `${healthy}/${data.services.length} services healthy — ` +
    `updated ${new Date(data.timestamp).toLocaleTimeString()}`;

  cards.replaceChildren(...data.services.map(service => {
    const card = document.createElement("div");
    card.className = `card ${service.status}`;
    card.innerHTML = `
      <h2></h2>
      <p class="status"></p>
      <dl>
        <dt>Uptime</dt><dd>${service.uptimePct}%</dd>
        <dt>CPU</dt><dd>${service.cpuPct}%</dd>
        <dt>Memory</dt><dd>${service.memoryMb} MB</dd>
        <dt>Latency</dt><dd>${service.responseTimeMs} ms</dd>
      </dl>`;
    card.querySelector("h2")!.textContent = service.name;
    card.querySelector(".status")!.textContent = service.status;
    return card;
  }));
}
