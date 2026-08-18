using System.ComponentModel;
using ModelContextProtocol.Server;

/// <summary>
/// A small in-memory report store. The tools deliberately span two permission
/// levels: reading needs the "reports:read" scope, deleting needs
/// "reports:admin". The scope checks live in the tools/call filter in
/// Program.cs — the tools themselves stay policy-free.
/// </summary>
[McpServerToolType]
public class ReportTools
{
    private static readonly Dictionary<string, string> Reports = new()
    {
        ["q1-sales"] = "Q1 sales: revenue up 12% quarter over quarter, driven by the APAC region.",
        ["q2-sales"] = "Q2 sales: revenue flat; churn in the SMB segment offset enterprise growth.",
        ["incident-42"] = "Incident 42 post-mortem: cache stampede after a deploy; mitigated by jittered TTLs.",
    };

    [McpServerTool(Name = "list_reports")]
    [Description("Lists the IDs of all available reports.")]
    public static string ListReports() => string.Join("\n", Reports.Keys);

    [McpServerTool(Name = "read_report")]
    [Description("Returns the contents of a report by ID.")]
    public static string ReadReport([Description("The report ID")] string id) =>
        Reports.TryGetValue(id, out var body)
            ? body
            : throw new KeyNotFoundException($"No report with ID '{id}'.");

    [McpServerTool(Name = "delete_report")]
    [Description("Permanently deletes a report by ID.")]
    public static string DeleteReport([Description("The report ID")] string id) =>
        Reports.Remove(id)
            ? $"Deleted report '{id}'."
            : throw new KeyNotFoundException($"No report with ID '{id}'.");
}
