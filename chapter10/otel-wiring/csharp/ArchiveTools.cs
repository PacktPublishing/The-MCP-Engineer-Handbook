using System.ComponentModel;
using ModelContextProtocol.Server;

[McpServerToolType]
public class ArchiveTools
{
    private static readonly string[] Incidents =
    [
        "2026-03-14 retry storm: a host release broke recovery behavior; burst signature caught it.",
        "2026-05-02 context flood: forecast tool p95 doubled after a backend change added raw JSON.",
        "2026-06-21 dud wave: schema rename made the model quote whole questions into the query field.",
    ];

    [McpServerTool(Name = "get_forecast"), Description("Returns a weather forecast for a city.")]
    public static string GetForecast(
        [Description("City name.")] string city,
        [Description("Number of days, 1-7.")] int days = 3)
    {
        days = Math.Clamp(days, 1, 7);
        var lines = Enumerable.Range(1, days).Select(d =>
            $"Day {d}: {city} — high {18 + (d * 3) % 9}C, low {9 + (d * 2) % 5}C, " +
            $"{(d % 2 == 0 ? "scattered showers" : "partly cloudy")}, wind {5 + d} m/s.");
        return string.Join("\n", lines);
    }

    [McpServerTool(Name = "search_incidents"), Description("Searches the incident archive. Returns matching entries.")]
    public static string SearchIncidents([Description("Search terms.")] string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = Incidents
            .Where(i => terms.Any(t => i.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return hits.Count > 0 ? string.Join("\n", hits) : "(no matches)";
    }

    // Simulates a downstream dependency failure: the tool runs and fails, which the
    // SDK reports as CallToolResult.IsError = true inside an HTTP 200 — rung 3 of
    // the error ladder, invisible to transport-level metrics.
    [McpServerTool(Name = "check_dependency"), Description("Checks the health of the upstream archive service.")]
    public static string CheckDependency()
        => throw new InvalidOperationException("Upstream archive service returned 503.");
}
